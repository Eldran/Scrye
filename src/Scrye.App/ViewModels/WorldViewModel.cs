using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using Scrye.Core.Automation;
using Scrye.Core.Logging;
using Scrye.Core.Model;
using Scrye.Core.Plugins;
using Scrye.Core.Profiles;
using Scrye.Core.Session;
using Scrye.Core.Text;
using Scrye.Scripting;
using Scrye.Scripting.Plugins;

namespace Scrye.App.ViewModels;

/// <summary>
/// Wraps a live <see cref="MudSession"/> for one world tab. Engine lines land on
/// the session loop (background); we enqueue them and drain to the
/// <see cref="ScrollbackBuffer"/> on a UI-thread timer, so the renderer sees at
/// most one update per frame instead of one per line (firehose-safe). The same
/// timer drains the <see cref="Debugger"/>'s event queue.
/// </summary>
public sealed class WorldViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly Rgb EchoColour = new(0x60, 0xC0, 0xF0);   // cyan-ish for local echo
    private static readonly Rgb SystemColour = new(0xF0, 0xC0, 0x40); // amber for system notices
    private static readonly Rgb PluginColour = new(0x90, 0xE0, 0x90); // green for plugin output

    private readonly MudSession _session;
    private readonly LuaScriptHost _scriptHost;
    private readonly PluginManager _plugins;
    private readonly CommandHistory _history = new();
    private readonly CompletionEngine _completion = new();
    private bool _logging;
    private readonly ConcurrentQueue<Line> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly List<Line> _drainBuffer = new(256);

    public string Title { get; }

    /// <summary>Which profile chain (mud/account/character) this tab was resolved from,
    /// so layer edits can be re-resolved and live-applied. Null for quick-connect tabs.</summary>
    public ProfileRef? Ref { get; init; }

    public ScrollbackBuffer Scrollback { get; } = new();
    public RelayCommand SubmitCommand { get; }

    /// <summary>Output-pane font, resolved from the profile cascade (global default,
    /// per-world override). Falls back to the terminal monospace stack.</summary>
    public Avalonia.Media.FontFamily OutputFontFamily { get; private set; } =
        new("Cascadia Mono, Consolas, Menlo, monospace");
    public double OutputFontSize { get; private set; } = 14d;

    /// <summary>The trigger debugger / event timeline for this world's session.</summary>
    public DebuggerViewModel Debugger { get; }

    /// <summary>Replay transport: load/step recordings and re-run them against the current triggers.</summary>
    public ReplayViewModel Replay { get; }

    /// <summary>Declarative HUD panels contributed by plugins (Foundation D).</summary>
    public HudViewModel Hud { get; }

    /// <summary>Running-sequence status strip (Foundation E command sequences).</summary>
    public SequenceViewModel Sequence { get; }

    /// <summary>Find-in-scrollback bar (Ctrl+F): searches this world's output.</summary>
    public FindViewModel Find { get; }

    /// <summary>Plugins-manager panel: list / reload / enable-disable this world's plugins.</summary>
    public PluginsViewModel Plugins { get; }

    /// <summary>Game-state inspector: live filterable view of the StateStore (idea #9).</summary>
    public StateViewModel StateInspector { get; }

    private string _input = "";
    public string Input { get => _input; set => SetField(ref _input, value); }

    private bool _showDebugger;
    public bool ShowDebugger
    {
        get => _showDebugger;
        set
        {
            if (SetField(ref _showDebugger, value))
                DebuggerColWidth = value ? new GridLength(400) : new GridLength(0);
        }
    }

    // Width of the debugger's grid column. Bound two-way in spirit: the GridSplitter
    // overrides it live while dragging; toggling the panel resets it (0 hidden / 400 shown).
    private GridLength _debuggerColWidth = new(0);
    public GridLength DebuggerColWidth { get => _debuggerColWidth; set => SetField(ref _debuggerColWidth, value); }

    public WorldViewModel(WorldProfile profile)
    {
        Title = profile.Name;
        _session = new MudSession(profile);
        _session.LineReady += line => _pending.Enqueue(line);
        _session.StateChanged += s => _pending.Enqueue(Line.FromText($"[{s}]", SystemColour));
        _session.MsspReceived += mssp =>
        {
            string name = mssp.TryGetValue("NAME", out var n) ? n : Title;
            string players = mssp.TryGetValue("PLAYERS", out var pc) ? $" - {pc} players online" : "";
            AppendSystem($"server: {name}{players}");
        };

        // scripting: trigger/alias/timer script callbacks run on the session loop
        _scriptHost = new LuaScriptHost(new SessionWorldApi(_session));
        _session.ScriptDispatcher = (fn, wildcards) => _scriptHost.CallFunction(fn, wildcards.ToArray());
        _session.ScriptExecutor = code => _scriptHost.Execute(code);

        // debugger: the event bus fires on the session loop; enqueue there, drain on the UI timer.
        Debugger = new DebuggerViewModel(_session, AppendSystem);
        _session.Events.Emitted += Debugger.Enqueue;

        // replay: analysis re-runs recordings against this session's current rule set.
        Replay = new ReplayViewModel(() => _session.Automation, AppendSystem);

        // command sequences: status strip driven by the session; controls route back to it.
        Sequence = new SequenceViewModel(_session.PauseSequence, _session.ResumeSequence, _session.StopSequence);
        _session.SequenceStatusChanged += Sequence.Update;

        // HUD: plugins add declarative panels during load (below); this owns them.
        // Panel-button clicks are marshalled onto the session loop before hitting plugin Lua.
        // _plugins is assigned just below; the lambda only runs on a click, well after that.
        Hud = new HudViewModel(_session.GameState,
            (pluginId, actionId) => _session.Post(() => _plugins!.InvokeAction(pluginId, actionId)));

        // find-in-scrollback: searches the rendered output buffer.
        Find = new FindViewModel(Scrollback);

        // game-state inspector: subscribes to StateStore.Changed here (pre-loop),
        // queues on the loop, drains on the UI flush timer below.
        StateInspector = new StateViewModel(_session.GameState);

        // plugins: discover the ones for this world, load them, and fan session events to them.
        var pluginRoots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "plugins"),                                 // bundled (next to exe)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // user plugins
                "Scrye", "plugins"),
        };
        var host = new SessionPluginHost(_session,
            (id, text) => _pending.Enqueue(Line.FromText($"[{id}] {text}", PluginColour)),
            (id, spec) => Hud.AddPanel(id, spec));
        string userPluginRoot = pluginRoots[1];   // %APPDATA%/Scrye/plugins — writable, removable
        _plugins = new PluginManager(PluginCatalog.ForMud(profile.Name, pluginRoots), host, AppendSystem,
            id => Hud.RemovePanels(id),                                  // drop a plugin's HUD panels on unload
            () => PluginCatalog.ForMud(profile.Name, pluginRoots),       // rescan disk for add/remove
            userPluginRoot);
        Plugins = new PluginsViewModel(
            () => _plugins.ListPlugins(),
            (id, done) => _session.Post(() => { _plugins.Reload(id); Dispatcher.UIThread.Post(done); }),
            (id, enable, done) => _session.Post(() =>
            {
                if (enable) _plugins.Enable(id); else _plugins.Disable(id);
                Dispatcher.UIThread.Post(done);
            }),
            (id, done) => _session.Post(() => { _plugins.Remove(id); Dispatcher.UIThread.Post(done); }),
            done => _session.Post(() => { _plugins.Rescan(); Dispatcher.UIThread.Post(done); }),
            done => { ScaffoldNewPlugin(userPluginRoot); _session.Post(() => { _plugins.Rescan(); Dispatcher.UIThread.Post(done); }); },
            () => OpenPluginsFolder(userPluginRoot));
        // Plugins process each server line (onLine gag/rewrite + triggers) and user input
        // (aliases) via the session's filter hooks — so gagging actually suppresses display.
        _session.LineDisplayFilter = _plugins.ProcessLine;    // gag/rewrite + triggers + prompt hook
        _session.InputFilter = _plugins.ProcessInput;         // plugin aliases (a match consumes input)
        _session.GmcpReceived += (pkg, json) => _plugins.DispatchGmcp(pkg, json);
        _session.Ticked += _plugins.Tick;                     // plugin timers (scrye.after/every)
        _session.StateChanged += s =>                          // plugin lifecycle hooks
        {
            if (s == ConnectionState.Connected) _plugins.DispatchConnect();
            else if (s == ConnectionState.Disconnected) _plugins.DispatchDisconnect();
        };

        SubmitCommand = new RelayCommand(Submit);

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    /// <summary>Construct from a resolved profile: loads its triggers/aliases/timers/vars.</summary>
    public WorldViewModel(EffectiveProfile eff) : this(eff.World)
    {
        _session.LoadProfileData(eff);
        if (!string.IsNullOrWhiteSpace(eff.FontFamily)) OutputFontFamily = new Avalonia.Media.FontFamily(eff.FontFamily);
        if (eff.FontSize is > 0) OutputFontSize = eff.FontSize.Value;
    }

    public Task ConnectAsync() => _session.ConnectAsync();

    /// <summary>Live-apply a re-resolved profile's rules to this connected session
    /// (triggers/aliases/timers replaced; runtime state untouched).</summary>
    public void ReloadRules(EffectiveProfile eff) => _session.ReloadAutomation(eff);

    public void AppendSystem(string text) => _pending.Enqueue(Line.FromText("* " + text, SystemColour));

    private void Flush()
    {
        if (!_pending.IsEmpty)
        {
            _drainBuffer.Clear();
            while (_pending.TryDequeue(out Line? line))
                _drainBuffer.Add(line);
            Scrollback.AddRange(_drainBuffer);
            // harvest words for tab-completion on the UI thread (engine isn't thread-safe)
            foreach (Line l in _drainBuffer) _completion.Observe(l.PlainText);
        }
        Debugger.Drain();   // always drain events, even on a frame with no output lines
        StateInspector.Drain();
    }

    /// <summary>Up-arrow recall. <paramref name="current"/> is the box text (saved as draft).</summary>
    public string? HistoryPrevious(string current) => _history.Previous(current);
    /// <summary>Down-arrow recall / draft restore.</summary>
    public string? HistoryNext() => _history.Next();

    /// <summary>Tab-completion candidates for <paramref name="prefix"/> (most-recent first).</summary>
    public IReadOnlyList<string> Complete(string prefix) => _completion.Complete(prefix);

    /// <summary>Open the find bar (Ctrl+F).</summary>
    public void OpenFind() => Find.Open();

    private void Submit()
    {
        string text = Input ?? "";
        Input = "";
        _history.Add(text);        // record for up/down recall
        _completion.Observe(text); // typed words feed completion too

        if (text == "mipstart") { _pending.Enqueue(Line.FromText("> " + text, EchoColour)); _session.StartMip(); return; }

        // client "." commands (sequences); unknown dot-input falls through to the MUD.
        if (TryClientCommand(text)) { _pending.Enqueue(Line.FromText(text, EchoColour)); return; }

        // "/..." is a local Lua console — runs on the session loop, not sent to the MUD.
        // e.g.  /world.AddAlias("greet", "hi *", "say hello %1")
        if (text.StartsWith('/') && text.Length > 1)
        {
            _pending.Enqueue(Line.FromText(text, EchoColour));
            _session.RunScript(text[1..]);
            return;
        }

        _pending.Enqueue(Line.FromText("> " + text, EchoColour));   // local echo
        _session.Submit(text);
    }

    /// <summary>Handle client "." commands. Returns false for unrecognized ones so they
    /// pass through to the MUD (some MUDs use "." commands too).</summary>
    private bool TryClientCommand(string text)
    {
        if (!text.StartsWith('.')) return false;
        string[] parts = text.Split(' ', 2);
        string arg = parts.Length > 1 ? parts[1].Trim() : "";
        switch (parts[0].ToLowerInvariant())
        {
            case ".walk":
                if (arg.Length > 0) _session.RunWalk(arg);
                else AppendSystem("usage: .walk north;north;east x3;wait 2");
                return true;
            case ".seq":
                if (arg.Length > 0) _session.RunSequence(arg);
                else AppendSystem("usage: .seq <name>");
                return true;
            case ".stop": _session.StopSequence(); return true;
            case ".pause": _session.PauseSequence(); return true;
            case ".resume": _session.ResumeSequence(); return true;
            case ".log": HandleLogCommand(arg); return true;
            default: return false;
        }
    }

    /// <summary>Scaffold a new, editable plugin (plugin.json + starter main.lua) under the user
    /// plugins folder, so the user can edit it and Reload. Rescan (by the caller) picks it up.</summary>
    private void ScaffoldNewPlugin(string userRoot)
    {
        try
        {
            Directory.CreateDirectory(userRoot);
            string id = "my-plugin";
            for (int n = 2; Directory.Exists(Path.Combine(userRoot, id)); n++) id = "my-plugin-" + n;
            string dir = Path.Combine(userRoot, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"),
                "{\n  \"id\": \"" + id + "\",\n  \"name\": \"" + id + "\",\n  \"version\": \"0.1.0\",\n  \"mudIds\": [\"*\"]\n}\n");
            File.WriteAllText(Path.Combine(dir, "main.lua"),
                "-- New Scrye plugin. Edit this file, then click Reload in the Plugins panel.\n" +
                "scrye.print(\"" + id + " loaded\")\n\n" +
                "scrye.onLine(function(line)\n" +
                "    -- react to output here; return false to gag a line, a string to rewrite it\n" +
                "end)\n");
            AppendSystem($"created plugin '{id}' — edit {Path.Combine(dir, "main.lua")}, then Reload");
        }
        catch (Exception ex) { AppendSystem("could not create plugin: " + ex.Message); }
    }

    /// <summary>Open the user plugins folder in the OS file manager (to drop in downloaded plugins).</summary>
    private void OpenPluginsFolder(string userRoot)
    {
        try
        {
            Directory.CreateDirectory(userRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = userRoot, UseShellExecute = true });
        }
        catch (Exception ex) { AppendSystem("could not open plugins folder: " + ex.Message); }
    }

    /// <summary>Handle <c>.log</c> / <c>.log html</c> / <c>.log off</c>. Bare <c>.log</c> toggles.</summary>
    private void HandleLogCommand(string arg)
    {
        string a = arg.Trim().ToLowerInvariant();
        if (a is "off" or "stop")
        {
            if (_logging) { _session.StopLogging(); _logging = false; AppendSystem("logging stopped"); }
            else AppendSystem("not currently logging");
            return;
        }
        if (_logging && a.Length == 0)   // bare .log while logging → toggle off
        {
            _session.StopLogging(); _logging = false; AppendSystem("logging stopped"); return;
        }
        LogFormat fmt = a.Contains("htm") ? LogFormat.Html : LogFormat.Text;
        string path = _session.StartLogging(fmt);
        _logging = true;
        AppendSystem($"logging ({fmt.ToString().ToLowerInvariant()}) to {path}");
    }

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Stop();
        Replay.Stop();
        _plugins.Dispose();
        Hud.Dispose();
        _session.Events.Emitted -= Debugger.Enqueue;
        await _session.DisposeAsync();
    }
}

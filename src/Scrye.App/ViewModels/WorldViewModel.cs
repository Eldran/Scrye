using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
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
    private readonly ConcurrentQueue<Line> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly List<Line> _drainBuffer = new(256);

    public string Title { get; }
    public ScrollbackBuffer Scrollback { get; } = new();
    public RelayCommand SubmitCommand { get; }

    /// <summary>The trigger debugger / event timeline for this world's session.</summary>
    public DebuggerViewModel Debugger { get; }

    /// <summary>Replay transport: load/step recordings and re-run them against the current triggers.</summary>
    public ReplayViewModel Replay { get; }

    /// <summary>Declarative HUD panels contributed by plugins (Foundation D).</summary>
    public HudViewModel Hud { get; }

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

        // HUD: plugins add declarative panels during load (below); this owns them.
        Hud = new HudViewModel(_session.GameState);

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
        _plugins = new PluginManager(PluginCatalog.ForMud(profile.Name, pluginRoots), host, AppendSystem);
        _session.LineReady += line => _plugins.DispatchLine(line.PlainText);
        _session.GmcpReceived += (pkg, json) => _plugins.DispatchGmcp(pkg, json);

        SubmitCommand = new RelayCommand(Submit);

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    /// <summary>Construct from a resolved profile: loads its triggers/aliases/timers/vars.</summary>
    public WorldViewModel(EffectiveProfile eff) : this(eff.World)
    {
        _session.LoadProfileData(eff);
    }

    public Task ConnectAsync() => _session.ConnectAsync();

    public void AppendSystem(string text) => _pending.Enqueue(Line.FromText("* " + text, SystemColour));

    private void Flush()
    {
        if (!_pending.IsEmpty)
        {
            _drainBuffer.Clear();
            while (_pending.TryDequeue(out Line? line))
                _drainBuffer.Add(line);
            Scrollback.AddRange(_drainBuffer);
        }
        Debugger.Drain();   // always drain events, even on a frame with no output lines
    }

    private void Submit()
    {
        string text = Input ?? "";
        Input = "";

        if (text == "mipstart") { _pending.Enqueue(Line.FromText("> " + text, EchoColour)); _session.StartMip(); return; }

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

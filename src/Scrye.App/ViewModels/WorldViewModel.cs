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
    private readonly ConcurrentQueue<(string Pane, Line Line)> _pendingRouted = new();
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

    /// <summary>All capture panes regardless of zone (bottom tabs, right tabs, floating
    /// windows). Auto-created on first routed line or restored from the saved layout.</summary>
    private readonly List<CapturePaneViewModel> _allPanes = new();
    private readonly Dictionary<CapturePaneViewModel, Window> _floatWindows = new();
    private bool _restoringLayout;   // suppress saves while applying a loaded layout

    public System.Collections.ObjectModel.ObservableCollection<CapturePaneViewModel> BottomPanes { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<CapturePaneViewModel> RightPanes { get; } = new();

    private CapturePaneViewModel? _selectedBottomPane;
    public CapturePaneViewModel? SelectedBottomPane
    {
        get => _selectedBottomPane;
        set
        {
            if (SetField(ref _selectedBottomPane, value) && value is not null)
                value.Unread = 0;   // viewing the tab clears its badge
        }
    }

    private CapturePaneViewModel? _selectedRightPane;
    public CapturePaneViewModel? SelectedRightPane
    {
        get => _selectedRightPane;
        set
        {
            if (SetField(ref _selectedRightPane, value) && value is not null)
                value.Unread = 0;
        }
    }

    private bool _showPanes;
    /// <summary>Bottom pane-zone visibility ("Panes" toggle). Auto-opens when the
    /// first bottom pane appears; the row height collapses when hidden.</summary>
    public bool ShowPanes
    {
        get => _showPanes;
        set
        {
            if (SetField(ref _showPanes, value))
                PanesRowHeight = value ? new GridLength(190) : new GridLength(0);
        }
    }

    private GridLength _panesRowHeight = new(0);
    public GridLength PanesRowHeight { get => _panesRowHeight; set => SetField(ref _panesRowHeight, value); }

    private bool _rightVisible;
    /// <summary>Right pane-zone visibility (auto: shown while any pane is docked right).</summary>
    public bool RightVisible
    {
        get => _rightVisible;
        private set
        {
            if (SetField(ref _rightVisible, value))
                RightPanesColWidth = value ? new GridLength(300) : new GridLength(0);
        }
    }

    private GridLength _rightPanesColWidth = new(0);
    public GridLength RightPanesColWidth { get => _rightPanesColWidth; set => SetField(ref _rightPanesColWidth, value); }

    // ---- pane creation / placement / persistence -------------------------------

    private CapturePaneViewModel CreatePane(string name, PaneDock dock)
    {
        var pane = new CapturePaneViewModel(name, OutputFontFamily, OutputFontSize)
        {
            ShowTimestamps = ShowTimestamps,
            MoveRequested = MovePane,
            CloseRequested = ClosePane,
        };
        _allPanes.Add(pane);
        PlacePane(pane, dock);
        return pane;
    }

    /// <summary>Move a pane into a zone: detach it everywhere, then attach to the target.
    /// Closing a floating pane's window re-docks it to the bottom zone.</summary>
    private void PlacePane(CapturePaneViewModel pane, PaneDock dock)
    {
        BottomPanes.Remove(pane);
        RightPanes.Remove(pane);
        if (_floatWindows.Remove(pane, out Window? open)) open.Close();   // removed first: Closed won't re-dock
        if (ReferenceEquals(SelectedBottomPane, pane)) SelectedBottomPane = BottomPanes.Count > 0 ? BottomPanes[0] : null;
        if (ReferenceEquals(SelectedRightPane, pane)) SelectedRightPane = RightPanes.Count > 0 ? RightPanes[0] : null;

        pane.Dock = dock;
        switch (dock)
        {
            case PaneDock.Bottom:
                BottomPanes.Add(pane);
                SelectedBottomPane ??= pane;
                if (!ShowPanes) ShowPanes = true;
                break;
            case PaneDock.Right:
                RightPanes.Add(pane);
                SelectedRightPane ??= pane;
                break;
            case PaneDock.Floating:
                OpenFloatWindow(pane);
                break;
        }
        if (BottomPanes.Count == 0 && ShowPanes) ShowPanes = false;
        RightVisible = RightPanes.Count > 0;
    }

    private void MovePane(CapturePaneViewModel pane, PaneDock dock)
    {
        if (pane.Dock == dock && dock != PaneDock.Floating) return;
        PlacePane(pane, dock);
        SaveLayout();
    }

    private void ClosePane(CapturePaneViewModel pane)
    {
        BottomPanes.Remove(pane);
        RightPanes.Remove(pane);
        if (_floatWindows.Remove(pane, out Window? open)) open.Close();
        if (ReferenceEquals(SelectedBottomPane, pane)) SelectedBottomPane = BottomPanes.Count > 0 ? BottomPanes[0] : null;
        if (ReferenceEquals(SelectedRightPane, pane)) SelectedRightPane = RightPanes.Count > 0 ? RightPanes[0] : null;
        _allPanes.Remove(pane);
        if (BottomPanes.Count == 0 && ShowPanes) ShowPanes = false;
        RightVisible = RightPanes.Count > 0;
        SaveLayout();
    }

    private void OpenFloatWindow(CapturePaneViewModel pane)
    {
        // properties set directly (no bindings): only ShowTimestamps changes after
        // creation, synced via PropertyChanged below.
        var terminal = new Controls.TerminalPane
        {
            Source = pane.Buffer,
            FontFamily = pane.FontFamily,
            FontSize = pane.FontSize,
            ShowTimestamps = pane.ShowTimestamps,
        };
        void SyncTimestamps(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CapturePaneViewModel.ShowTimestamps))
                terminal.ShowTimestamps = pane.ShowTimestamps;
        }
        pane.PropertyChanged += SyncTimestamps;

        var win = new Window
        {
            Title = $"{Title} — {pane.Name}",
            Width = 560,
            Height = 400,
            Content = terminal,
            Background = Avalonia.Media.Brushes.Black,
        };
        _floatWindows[pane] = win;
        win.Closed += (_, _) =>
        {
            pane.PropertyChanged -= SyncTimestamps;
            // user closed the window: bring the pane home to the bottom zone
            if (_floatWindows.Remove(pane) && _allPanes.Contains(pane))
            {
                PlacePane(pane, PaneDock.Bottom);
                SaveLayout();
            }
        };
        win.Show();
        pane.Unread = 0;   // it's visible now
    }

    private void SaveLayout()
    {
        if (_restoringLayout) return;
        var layout = new Services.WorldLayout { ShowTimestamps = ShowTimestamps };
        foreach (CapturePaneViewModel p in _allPanes)
            layout.Panes.Add(new Services.PaneLayoutEntry { Name = p.Name, Dock = p.Dock.ToString() });
        if (Hud is not null)
            foreach (HudPanelViewModel hp in Hud.Panels)
                if (!double.IsNaN(hp.X) && !double.IsNaN(hp.Y))
                    layout.HudPanels.Add(new Services.HudPanelLayout { Name = hp.Key, X = hp.X, Y = hp.Y });
        Services.PaneLayoutStore.Save(Title, layout);
    }

    /// <summary>Recreate the saved pane setup (docks + timestamp toggle) for this world.</summary>
    private void RestoreLayout()
    {
        Services.WorldLayout? layout = Services.PaneLayoutStore.Load(Title);
        if (layout is null) return;
        _restoringLayout = true;
        try
        {
            ShowTimestamps = layout.ShowTimestamps;
            foreach (Services.PaneLayoutEntry entry in layout.Panes)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                if (FindPane(entry.Name) is not null) continue;
                PaneDock dock = Enum.TryParse(entry.Dock, out PaneDock d) ? d : PaneDock.Bottom;
                CreatePane(entry.Name, dock);
            }
        }
        finally { _restoringLayout = false; }
    }

    private CapturePaneViewModel? FindPane(string name)
    {
        foreach (CapturePaneViewModel p in _allPanes)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>Plugins-manager panel: list / reload / enable-disable this world's plugins.</summary>
    public PluginsViewModel Plugins { get; }

    /// <summary>Game-state inspector: live filterable view of the StateStore (idea #9).</summary>
    public StateViewModel StateInspector { get; }

    private string _input = "";
    public string Input { get => _input; set => SetField(ref _input, value); }

    // ---- multi-session tab state ----------------------------------------------

    /// <summary>Set by the main window when this becomes the visible tab; clears the badge.</summary>
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetField(ref _isActive, value) && value) Unread = 0;
        }
    }

    private int _unread;
    /// <summary>Lines that arrived while another tab was active (the tab-header badge).</summary>
    public int Unread
    {
        get => _unread;
        private set
        {
            if (SetField(ref _unread, value))
            {
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadText));
            }
        }
    }
    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString();

    private ConnectionState _connState = ConnectionState.Disconnected;
    /// <summary>Connection state mirrored onto the UI thread (drives the tab's status dot).</summary>
    public ConnectionState ConnState
    {
        get => _connState;
        private set
        {
            if (SetField(ref _connState, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusTip));
            }
        }
    }

    private static readonly Avalonia.Media.IBrush ConnectedBrush =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0x4C, 0xC3, 0x8A));
    private static readonly Avalonia.Media.IBrush BusyBrush =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xF0, 0xC0, 0x40));
    private static readonly Avalonia.Media.IBrush DownBrush =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Avalonia.Media.IBrush IdleBrush =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0x6E, 0x76, 0x81));

    public Avalonia.Media.IBrush StatusBrush => _connState switch
    {
        ConnectionState.Connected => ConnectedBrush,
        ConnectionState.Connecting or ConnectionState.Disconnecting => BusyBrush,
        ConnectionState.Failed => DownBrush,
        _ => IdleBrush,
    };
    public string StatusTip => $"{Title}: {_connState}";

    /// <summary>Set by the main window: sends a command to every connected world
    /// (input-broadcast). Null when this tab stands alone.</summary>
    public Action<string>? Broadcast { get; set; }

    /// <summary>Set by the main window: raise an app-level toast (title, body).</summary>
    public Action<string, string>? Toast { get; set; }

    private bool _isBroadcast;
    /// <summary>When on, plain input from this tab goes to ALL worlds ("All" toggle).
    /// Client "." and "/" commands stay local.</summary>
    public bool IsBroadcast { get => _isBroadcast; set => SetField(ref _isBroadcast, value); }

    private bool _showTimestamps;
    /// <summary>HH:mm:ss gutter in the output and capture panes (".ts" toggle; persisted).</summary>
    public bool ShowTimestamps
    {
        get => _showTimestamps;
        set
        {
            if (SetField(ref _showTimestamps, value))
            {
                foreach (CapturePaneViewModel p in _allPanes) p.ShowTimestamps = value;
                SaveLayout();
            }
        }
    }

    // ---- text-to-speech (accessibility) ----------------------------------------

    private readonly Services.SpeechService _speech = new();

    private bool _ttsEnabled;
    /// <summary>Speak incoming lines aloud while this tab is active (".tts" / TTS toggle).</summary>
    public bool TtsEnabled
    {
        get => _ttsEnabled;
        set
        {
            if (SetField(ref _ttsEnabled, value) && !value) _speech.Stop();
        }
    }

    /// <summary>A broadcast command arriving at this world: echo it distinctly and send.</summary>
    public void ReceiveBroadcast(string text)
    {
        _pending.Enqueue(Line.FromText("» " + text, EchoColour));
        _session.Submit(text);
    }

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

    /// <summary>Persist a plugin enable/disable to the connected node's profile (set by
    /// MainWindowViewModel for profile worlds; null for quick-connect = session-only).</summary>
    public Action<string, bool>? PersistPluginEnable { get; set; }

    public WorldViewModel(WorldProfile profile, IReadOnlyList<string>? enabledPlugins = null)
    {
        Title = profile.Name;
        _session = new MudSession(profile);
        _session.LineReady += line => _pending.Enqueue(line);
        _session.StateChanged += s =>
        {
            _pending.Enqueue(Line.FromText($"[{s}]", SystemColour));
            Dispatcher.UIThread.Post(() =>
            {
                ConnState = s;   // status dot on the UI thread
                if (s is ConnectionState.Connected or ConnectionState.Disconnected or ConnectionState.Failed)
                    Toast?.Invoke(Title, s switch
                    {
                        ConnectionState.Connected => "connected",
                        ConnectionState.Failed => "connection failed",
                        _ => "disconnected",
                    });
            });
        };

        // trigger notifications + sounds (session loop → UI/audio)
        _session.NotifyRequested += line =>
            Dispatcher.UIThread.Post(() => Toast?.Invoke(Title, line.PlainText));
        _session.SoundRequested += sound => Services.SoundService.Play(sound, Title);
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
            (pluginId, actionId) => _session.Post(() => _plugins!.InvokeAction(pluginId, actionId)),
            (pluginId, actionId, col, row, ch) =>
                _session.Post(() => _plugins!.InvokeCellAction(pluginId, actionId, col, row, ch)),
            (pluginId, actionId, text) =>
                _session.Post(() => _plugins!.InvokeSubmit(pluginId, actionId, text)));

        // Restore dragged HUD-panel positions (loaded up-front: plugins add their panels
        // during construction below, before RestoreLayout runs), and persist on drag.
        var savedHud = new System.Collections.Generic.Dictionary<string, (double, double)>(StringComparer.Ordinal);
        if (Services.PaneLayoutStore.Load(profile.Name) is { } savedLayout)
            foreach (Services.HudPanelLayout h in savedLayout.HudPanels)
                if (!string.IsNullOrEmpty(h.Name)) savedHud[h.Name] = (h.X, h.Y);
        Hud.LoadPosition = key => savedHud.TryGetValue(key, out (double, double) p) ? p : null;
        Hud.PanelMoved = SaveLayout;

        // find-in-scrollback: searches the rendered output buffer.
        Find = new FindViewModel(Scrollback);

        // capture panes: trigger-routed lines arrive on the session loop; enqueue,
        // drain on the UI flush timer alongside the main scrollback.
        _session.LineRouted += (pane, line) => _pendingRouted.Enqueue((pane, line));

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
        // persistent scrye.store data, scoped per world: %APPDATA%/Scrye/plugin-data/<world>/<pluginId>.json
        var pluginData = new PluginDataStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "plugin-data"),
            profile.Name, AppendSystem);
        var host = new SessionPluginHost(_session,
            (id, text) => _pending.Enqueue(Line.FromText($"[{id}] {text}", PluginColour)),
            (id, spec) => Hud.AddPanel(id, spec),
            pluginData);
        string userPluginRoot = pluginRoots[1];   // %APPDATA%/Scrye/plugins — writable, removable
        // Plugins are opt-in per character: the manager offers everything discovered for this
        // MUD but loads only what this character enabled (empty for quick-connect). Toggling in
        // the manager persists to the connected node's profile via PersistPluginEnable.
        _plugins = new PluginManager(
            PluginCatalog.AvailableForMud(profile.Name, pluginRoots),
            enabledPlugins ?? Array.Empty<string>(),
            host, AppendSystem,
            id => Hud.RemovePanels(id),                                  // drop a plugin's HUD panels on unload
            () => PluginCatalog.AvailableForMud(profile.Name, pluginRoots),   // rescan disk for add/remove
            userPluginRoot,
            (id, enabled) => Dispatcher.UIThread.Post(() => PersistPluginEnable?.Invoke(id, enabled)));
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
        _session.ChannelMessage += (ch, msg) => _plugins.DispatchChannel(ch, msg);   // MIP chat → scrye.onChannel
        _session.Ticked += _plugins.Tick;                     // plugin timers (scrye.after/every)
        _session.StateChanged += s =>                          // plugin lifecycle hooks
        {
            if (s == ConnectionState.Connected) _plugins.DispatchConnect();
            else if (s == ConnectionState.Disconnected) _plugins.DispatchDisconnect();
        };

        SubmitCommand = new RelayCommand(Submit);

        // bring back this world's saved pane setup (docks + timestamp toggle)
        RestoreLayout();

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    /// <summary>Construct from a resolved profile: loads its triggers/aliases/timers/vars.</summary>
    public WorldViewModel(EffectiveProfile eff) : this(eff.World, eff.EnabledPlugins)
    {
        _session.LoadProfileData(eff);
        LoadMacros(eff.Macros);
        if (!string.IsNullOrWhiteSpace(eff.FontFamily)) OutputFontFamily = new Avalonia.Media.FontFamily(eff.FontFamily);
        if (eff.FontSize is > 0) OutputFontSize = eff.FontSize.Value;
    }

    // ---- keyboard macros -----------------------------------------------------

    // Normalised gesture ("Ctrl+K", "F1", "NumPad1") → macro. Rebuilt on rule reload.
    private readonly Dictionary<string, Scrye.Core.Automation.MacroDef> _macros = new(StringComparer.Ordinal);

    private void LoadMacros(IReadOnlyList<Scrye.Core.Automation.MacroDef> macros)
    {
        _macros.Clear();
        foreach (Scrye.Core.Automation.MacroDef m in macros)
        {
            string key = Services.MacroKeys.Normalize(m.Key);
            if (key.Length > 0) _macros[key] = m;
        }
    }

    /// <summary>If the pressed key matches a macro, send its command(s) and return true.
    /// Called from the window's key handler; only eligible gestures reach the map.</summary>
    public bool TryFireMacro(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods)
    {
        if (_macros.Count == 0 || !Services.MacroKeys.IsEligible(key, mods)) return false;
        string? gesture = Services.MacroKeys.FromEvent(key, mods);
        if (gesture is null || !_macros.TryGetValue(gesture, out Scrye.Core.Automation.MacroDef? m) || !m.Enabled)
            return false;

        string expanded = Scrye.Core.Automation.Template.Expand(m.Send, null, _session.Variables);
        Scrye.Core.Automation.AutomationEngine.ForEachLine(expanded, line =>
        {
            _pending.Enqueue(Line.FromText("> " + line, EchoColour));   // local echo, like typing
            _session.Submit(line);
        });
        return true;
    }

    public Task ConnectAsync() => _session.ConnectAsync();

    /// <summary>Live-apply a re-resolved profile's rules to this connected session
    /// (triggers/aliases/timers replaced; runtime state untouched).</summary>
    public void ReloadRules(EffectiveProfile eff)
    {
        _session.ReloadAutomation(eff);
        LoadMacros(eff.Macros);   // keybindings re-resolve with the rest of the cascade
    }

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
            if (!IsActive) Unread += _drainBuffer.Count;   // badge while another tab is up
            if (TtsEnabled && IsActive)
                foreach (Line l in _drainBuffer)
                    if (!l.IsPrompt && l.PlainText.Trim().Length > 0)
                        _speech.Speak(l.PlainText);
        }
        DrainRouted();
        Debugger.Drain();   // always drain events, even on a frame with no output lines
        StateInspector.Drain();
    }

    /// <summary>Deliver trigger-routed lines to their capture panes (UI thread).
    /// Panes are created on first use; unselected panes accumulate unread counts.</summary>
    private void DrainRouted()
    {
        if (_pendingRouted.IsEmpty) return;
        bool created = false;
        while (_pendingRouted.TryDequeue(out (string Pane, Line Line) item))
        {
            CapturePaneViewModel? pane = FindPane(item.Pane);
            if (pane is null)
            {
                pane = CreatePane(item.Pane, PaneDock.Bottom);
                created = true;
            }
            pane.Buffer.Add(item.Line);
            bool visible = pane.Dock == PaneDock.Floating
                || ReferenceEquals(pane, SelectedBottomPane)
                || ReferenceEquals(pane, SelectedRightPane);
            if (!visible) pane.Unread++;
        }
        if (created) SaveLayout();
    }

    /// <summary>Up-arrow recall. <paramref name="current"/> is the box text (saved as draft).</summary>
    public string? HistoryPrevious(string current) => _history.Previous(current);
    /// <summary>Down-arrow recall / draft restore.</summary>
    public string? HistoryNext() => _history.Next();

    /// <summary>Tab-completion candidates for <paramref name="prefix"/> (most-recent first).</summary>
    public IReadOnlyList<string> Complete(string prefix) => _completion.Complete(prefix);

    /// <summary>Open the find bar (Ctrl+F).</summary>
    public void OpenFind() => Find.Open();

    /// <summary>An MXP command link was clicked: send it (with local echo), or put it
    /// in the input box when the link asked for a prompt.</summary>
    public void HandleCommandLink(string command, bool prompt)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (prompt) { Input = command; return; }
        _pending.Enqueue(Line.FromText("> " + command, EchoColour));
        _session.Submit(command);
    }

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

        // "All" toggle: plain commands fan out to every connected world (each echoes "» cmd").
        if (IsBroadcast && Broadcast is not null && text.Length > 0)
        {
            Broadcast(text);
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
            case ".all":
                if (arg.Length == 0) { AppendSystem("usage: .all <command> — send to every connected world"); return true; }
                if (Broadcast is null) { AppendSystem("broadcast unavailable"); return true; }
                Broadcast(arg);
                return true;
            case ".tts": HandleTtsCommand(arg); return true;
            case ".ts" or ".timestamps":
                ShowTimestamps = !ShowTimestamps;
                AppendSystem(ShowTimestamps ? "timestamps on" : "timestamps off");
                return true;
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

    /// <summary>Handle <c>.tts</c> (toggle) / <c>.tts off</c> / <c>.tts stop</c> / <c>.tts rate N</c>.</summary>
    private void HandleTtsCommand(string arg)
    {
        if (!Services.SpeechService.Supported) { AppendSystem("TTS is not available on this platform yet"); return; }
        string[] parts = arg.Trim().ToLowerInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length == 0 ? "" : parts[0])
        {
            case "":
                TtsEnabled = !TtsEnabled;
                AppendSystem(TtsEnabled ? "TTS on — speaking incoming lines" : "TTS off");
                break;
            case "on": TtsEnabled = true; AppendSystem("TTS on — speaking incoming lines"); break;
            case "off": TtsEnabled = false; AppendSystem("TTS off"); break;
            case "stop": _speech.Stop(); AppendSystem("TTS: stopped speaking"); break;
            case "rate":
                if (parts.Length > 1 && int.TryParse(parts[1], out int r))
                {
                    _speech.Rate = r;
                    AppendSystem($"TTS rate = {_speech.Rate} (-10 slow … +10 fast)");
                }
                else AppendSystem("usage: .tts rate <-10..10>");
                break;
            default: AppendSystem("usage: .tts [on|off|stop|rate N]"); break;
        }
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
        _speech.Dispose();
        var floats = new List<Window>(_floatWindows.Values);
        _floatWindows.Clear();   // cleared first so Closed handlers don't re-dock during teardown
        foreach (Window w in floats) { try { w.Close(); } catch { } }
        Replay.Stop();
        _plugins.Dispose();
        Hud.Dispose();
        _session.Events.Emitted -= Debugger.Enqueue;
        await _session.DisposeAsync();
    }
}

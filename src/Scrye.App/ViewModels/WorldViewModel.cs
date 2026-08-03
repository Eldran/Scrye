using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using Scrye.App.Companion;
using Scrye.Companion.Protocol;
using Scrye.Companion.Server.Hub;
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

    private string? _sessionId;

    /// <summary>Stable identity for the companion protocol. Derived from <see cref="Ref"/>
    /// so it survives reconnects and desktop restarts; quick-connect tabs (no profile) get a
    /// random ephemeral id instead.
    ///
    /// <para>Computed lazily rather than in the constructor because <see cref="Ref"/> is an
    /// <c>init</c> property — it is not assigned until the object initializer has run.</para></summary>
    public string SessionId => _sessionId ??= Ref is { } r
        ? CompanionSessionId.FromProfile(r.Mud, r.Account, r.Character)
        : CompanionSessionId.NewEphemeral();

    /// <summary>The companion fan-out point, or null when the server is not running.
    /// Set by <c>MainWindowViewModel</c> when the server starts.</summary>
    public CompanionHub? Companion { get; set; }

    /// <summary>Web Push fan-out, or null when the companion server is not running.
    /// Set alongside <see cref="Companion"/>.</summary>
    public Scrye.Companion.Server.Push.PushNotifier? Notifier { get; set; }

    /// <summary>The session's shared state tree. Owned by the session loop — read it from
    /// another thread only through a snapshot.</summary>
    public Scrye.Core.State.StateStore GameState => _session.GameState;

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

        // Companion state feed. Fires on the session loop, which is where StateStore lives —
        // publishing is a channel write per subscriber, so nothing crosses back to the UI
        // thread (companion design §4.1). Changed is preferred over Watch: it covers every
        // leaf without a subscription per subtree, and already carries the Removed flag.
        _session.GameState.Changed += change =>
            Companion?.PublishState(StateUpdateMessage.From(SessionId, change));
        _session.StateChanged += s =>
        {
            _pending.Enqueue(Line.FromText($"[{s}]", SystemColour));
            Dispatcher.UIThread.Post(() =>
            {
                ConnState = s;   // status dot on the UI thread

                // Keep every device's session picker honest about connect/disconnect.
                Companion?.PublishSessionState(new SessionStateMessage(
                    SessionId, s == ConnectionState.Connected,
                    Ref?.Character ?? Title, Ref?.Mud ?? Title));

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
        {
            Dispatcher.UIThread.Post(() => Toast?.Invoke(Title, line.PlainText));

            // The same event also reaches the phone, as a Web Push if any device opted in
            // (companion design §7.2). No new concept for the user: whatever they already
            // flagged Notify on — a tell, low health, a finished route — now travels.
            // Fire-and-forget, so a slow push service cannot stall the session loop.
            Notifier?.NotifyInBackground(
                Ref?.Character ?? Title, line.PlainText, SessionId, DateTimeOffset.UtcNow);
        };
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
            PublishToCompanion();
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

    /// <summary>Capture panes and their scrollback, for companion snapshots. UI-thread
    /// state — read it through <c>AppSessionSource</c>, which marshals.</summary>
    public IReadOnlyList<CapturePaneViewModel> CapturePanes => _allPanes;

    /// <summary>Fire a plugin panel action, exactly as a click on the desktop HUD would.
    /// Posts onto the session loop first: plugin script is loop-thread-only.</summary>
    public bool InvokeHudAction(string pluginId, string actionId)
    {
        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(actionId)) return false;
        _session.Post(() => _plugins.InvokeAction(pluginId, actionId));
        return true;
    }

    /// <summary>Fire an input widget's submit callback with text entered on a companion
    /// device — the same path a desktop Enter takes.</summary>
    public bool InvokeHudSubmit(string pluginId, string actionId, string text)
    {
        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(actionId)) return false;
        _session.Post(() => _plugins.InvokeSubmit(pluginId, actionId, text ?? ""));
        return true;
    }

    /// <summary>Fire a colorgrid cell callback from a companion tap.</summary>
    public bool InvokeHudCell(string pluginId, string actionId, int col, int row, string ch)
    {
        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(actionId)) return false;
        _session.Post(() => _plugins.InvokeCellAction(pluginId, actionId, col, row, ch ?? ""));
        return true;
    }

    /// <summary>Send the lines just drained into scrollback on to any connected companion
    /// devices. Called from <see cref="Flush"/>, on the UI thread, immediately after
    /// <c>Scrollback.AddRange</c> — the 33 ms flush already is the batch window, so there is
    /// deliberately no second batcher here (companion design §3.1).
    ///
    /// <para>The first sequence is derived from <c>NextSequence</c> <em>after</em> the add,
    /// so it stays correct even when this same flush pushed the buffer past its cap and
    /// triggered a trim.</para></summary>
    private void PublishToCompanion()
    {
        if (Companion is not { } hub || _drainBuffer.Count == 0) return;

        long firstSequence = Scrollback.NextSequence - _drainBuffer.Count;
        var builder = new OutputBatchBuilder();
        builder.AddRange(_drainBuffer, firstSequence);
        hub.PublishOutput(builder.Build(SessionId));
    }

    /// <summary>Deliver trigger-routed lines to their capture panes (UI thread).
    /// Panes are created on first use; unselected panes accumulate unread counts.</summary>
    private void DrainRouted()
    {
        if (_pendingRouted.IsEmpty) return;
        bool created = false;
        // Group this tick's routed lines by pane so the companion gets one frame per pane
        // rather than one per line, matching how the main output stream batches (§3.1).
        Dictionary<string, OutputBatchBuilder>? companionBatches =
            Companion is null ? null : new Dictionary<string, OutputBatchBuilder>(StringComparer.Ordinal);

        while (_pendingRouted.TryDequeue(out (string Pane, Line Line) item))
        {
            CapturePaneViewModel? pane = FindPane(item.Pane);
            if (pane is null)
            {
                pane = CreatePane(item.Pane, PaneDock.Bottom);
                created = true;
            }
            pane.Buffer.Add(item.Line);

            if (companionBatches is not null)
            {
                if (!companionBatches.TryGetValue(item.Pane, out OutputBatchBuilder? b))
                    companionBatches[item.Pane] = b = new OutputBatchBuilder();
                // Each pane's buffer carries its own sequence space; read it after the add.
                b.Add(item.Line, pane.Buffer.SequenceAt(pane.Buffer.Count - 1));
            }
            bool visible = pane.Dock == PaneDock.Floating
                || ReferenceEquals(pane, SelectedBottomPane)
                || ReferenceEquals(pane, SelectedRightPane);
            if (!visible) pane.Unread++;
        }

        if (companionBatches is not null && Companion is { } hub)
        {
            foreach (KeyValuePair<string, OutputBatchBuilder> kv in companionBatches)
            {
                OutputBatchMessage built = kv.Value.Build(SessionId);
                hub.PublishPaneOutput(new PaneOutputMessage(SessionId, kv.Key, built.Styles, built.Lines));
            }
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
    /// in the input box when the link asked for a prompt.
    ///
    /// <para>Deliberately does NOT go through <see cref="SubmitText"/>. The action here was
    /// authored by the <em>MUD</em>, not the user, so it must never be prefix-dispatched:
    /// routing it through the normal pipeline would let a hostile MXP <c>&lt;SEND&gt;</c>
    /// smuggle a <c>/</c> Lua command into the console. It goes straight to the session as
    /// literal text, and a leading <c>/</c> is sent to the MUD rather than executed.
    /// A companion server handling a link tap must send it raw for the same reason.</para></summary>
    public void HandleCommandLink(string command, bool prompt)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (prompt) { Input = command; return; }
        _pending.Enqueue(Line.FromText("> " + command, EchoColour));
        _session.Submit(command);
    }

    /// <summary>The command line's Enter handler: take what is in the input box, record it
    /// for recall, and run it through the pipeline as local input.</summary>
    private void Submit()
    {
        string text = Input ?? "";
        Input = "";
        _history.Add(text);        // record for up/down recall
        _completion.Observe(text); // typed words feed completion too

        SubmitText(text, CommandOrigin.Local);
    }

    /// <summary>
    /// The single entry point for user-entered command text, whatever produced it. Applies
    /// the client-command and Lua-console prefixes, then aliases/triggers/logging by way of
    /// <c>MudSession.Submit</c> — so a command from a phone behaves exactly as one typed
    /// here (companion design §4).
    ///
    /// <para><paramref name="origin"/> carries the privilege decision, enforced here at the
    /// one place input enters the pipeline rather than in the UI — so a second entry point
    /// added later cannot bypass the check by forgetting about it (§7.3).</para>
    ///
    /// <para>Deliberately does NOT touch command history or tab completion: those belong to
    /// <em>this</em> input box, and a companion device keeps its own. Callers that represent
    /// a real input box (see <see cref="Submit"/>) record them first.</para>
    /// </summary>
    public CommandSubmitResult SubmitText(string text, CommandOrigin origin)
    {
        text ??= "";

        // "/..." is a local Lua console — arbitrary script on the session loop, not sent to
        // the MUD. e.g.  /world.AddAlias("greet", "hi *", "say hello %1")
        // This is the ONE privileged prefix and the only thing origin gates; everything
        // below is either ordinary MUD input or a pre-authored client command. Checked
        // before any echo, so a refused command leaves no trace of having half-run.
        bool isScript = CommandPrivilege.IsScriptConsole(text);
        if (isScript && !origin.MayRunScripts)
            return CommandSubmitResult.RejectedScriptingNotPermitted;

        if (text == "mipstart")
        {
            _pending.Enqueue(Line.FromText("> " + text, EchoColour));
            _session.StartMip();
            return CommandSubmitResult.Accepted;
        }

        // client "." commands (sequences, logging, tts); unknown dot-input falls through to
        // the MUD. Deliberately NOT gated: a sequence is a command list this desktop already
        // authored, and firing a walk route from a phone is a core companion use case.
        if (TryClientCommand(text))
        {
            _pending.Enqueue(Line.FromText(text, EchoColour));
            return CommandSubmitResult.Accepted;
        }

        if (isScript)
        {
            _pending.Enqueue(Line.FromText(text, EchoColour));
            _session.RunScript(text[1..]);
            return CommandSubmitResult.Accepted;
        }

        // "All" toggle: plain commands fan out to every connected world (each echoes "» cmd").
        // Local input only — a companion device must not silently inherit a toggle it cannot
        // see, so "north" from a phone always means *this* world. Broadcasting from a
        // companion is a separate, explicit action (§4).
        if (origin.Source == CommandSource.Local
            && IsBroadcast && Broadcast is not null && text.Length > 0)
        {
            Broadcast(text);
            return CommandSubmitResult.Accepted;
        }

        _pending.Enqueue(Line.FromText("> " + text, EchoColour));   // local echo
        _session.Submit(text);
        return CommandSubmitResult.Accepted;
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
            case ".companion": HandleCompanionCommand(arg); return true;
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

    /// <summary>Hook the app's companion controller up to this world, so <c>.companion</c>
    /// can drive it. Set by <c>MainWindowViewModel</c>; null in tests or if the feature is
    /// compiled out.</summary>
    public CompanionController? CompanionControl { get; set; }

    /// <summary><c>.companion [on|off|status]</c> — start or stop the mobile companion
    /// server and print its address and token.
    ///
    /// <para>A client command rather than a menu item for now: it needs no XAML, follows the
    /// same shape as <c>.log</c> and <c>.tts</c>, and prints the credential straight into the
    /// output pane where it can be copied. A proper panel replaces it once the protocol has
    /// been proven from a browser (companion design §10 step 3).</para>
    ///
    /// <para>Note the token is echoed into scrollback, which means session logging can
    /// capture it. That is acceptable for a loopback-only bring-up credential that is
    /// regenerated on every start, and is another reason this is temporary.</para></summary>
    private void HandleCompanionCommand(string arg)
    {
        if (CompanionControl is not CompanionController c)
        {
            AppendSystem("companion server unavailable");
            return;
        }

        string a = arg.Trim().ToLowerInvariant();

        if (a is "off" or "stop")
        {
            if (!c.IsRunning) { AppendSystem("companion server is not running"); return; }
            _ = StopCompanionAsync(c);
            return;
        }

        if (a is "status")
        {
            AppendSystem(c.IsRunning
                ? $"companion server running at {c.Url}"
                : "companion server stopped");
            if (c.IsRunning && c.TrustedLogin is { } who)
                AppendSystem($"  tailnet login {who} connects without a token");
            if (c.IsRunning) AppendSystem($"  token (only needed off-tailnet): {c.Token}");
            if (c.IsRunning) AppendSystem($"  push devices registered: {c.PushSubscriberCount}");
            AppendSystem($"  this world's sessionId: {SessionId}");
            _ = ReportTailscaleAsync(c);
            return;
        }

        if (a is "tailscale" or "remote")
        {
            _ = ReportTailscaleAsync(c, verbose: true);
            return;
        }

        if (a is "notify" or "push")
        {
            ReportNotifySources(c);
            return;
        }

        if (a is "notify test" or "push test" or "test")
        {
            if (!c.IsRunning) { AppendSystem("companion server is not running"); return; }
            if (c.PushSubscriberCount == 0)
            {
                AppendSystem("no devices registered for notifications");
                AppendSystem("  on the phone: ⋯ menu → Enable notifications");
                AppendSystem("  (iOS only allows this once Scrye is on the home screen)");
                return;
            }
            _ = TestNotifyAsync(c);
            return;
        }

        if (c.IsRunning)
        {
            AppendSystem($"companion server already running at {c.Url}");
            AppendSystem($"  token: {c.Token}");
            AppendSystem($"  this world's sessionId: {SessionId}");
            return;
        }

        _ = StartCompanionAsync(c);
    }

    private async Task StartCompanionAsync(CompanionController c)
    {
        try
        {
            await c.StartAsync();
            AppendSystem($"companion server started at {c.Url}");
            if (c.TrustedLogin is { } login)
                AppendSystem($"  tailnet login {login} may connect WITHOUT a token");
            AppendSystem($"  token (only needed off-tailnet): {c.Token}");
            AppendSystem($"  this world's sessionId: {SessionId}");
            AppendSystem("  usage: .companion status | tailscale | notify | notify test | off");
        }
        catch (Exception ex)
        {
            // Most likely the port is already taken. Report it rather than failing silently.
            AppendSystem($"companion server failed to start: {ex.Message}");
        }
    }

    /// <summary>List everything that can raise a notification for this world.
    ///
    /// <para>Worth having as a command rather than only a per-trigger checkbox: once
    /// notifications reach a phone, "what will buzz my pocket?" is a question you want
    /// answered in one place, not by clicking through every rule.</para></summary>
    private void ReportNotifySources(CompanionController c)
    {
        IReadOnlyList<(Scrye.Core.Automation.TriggerDef Def, bool Enabled)> notifying =
            _session.Automation.NotifyingTriggers;

        if (notifying.Count == 0)
        {
            AppendSystem("no triggers in this world are set to Notify");
        }
        else
        {
            AppendSystem($"{notifying.Count} trigger(s) set to Notify:");
            foreach ((Scrye.Core.Automation.TriggerDef def, bool enabled) in notifying)
            {
                string name = string.IsNullOrWhiteSpace(def.Name) ? "(unnamed)" : def.Name;
                string group = string.IsNullOrWhiteSpace(def.Group) ? "" : $" [{def.Group}]";
                string state = enabled ? "" : "  (DISABLED)";
                AppendSystem($"  {name}{group}: {def.Pattern}{state}");
            }
        }

        // Plugin code is arbitrary, so there is no honest way to enumerate its notify calls
        // — but saying nothing would make this list look complete when it is not.
        AppendSystem("plugins may also notify via scrye.notify(); those are not listed here");

        if (c.IsRunning)
        {
            AppendSystem($"phone devices registered: {c.PushSubscriberCount}");
            AppendSystem("  '.companion notify test' sends a test notification");
        }
        else
        {
            AppendSystem("companion server is stopped — notifications stay on this PC");
        }
    }

    private async Task TestNotifyAsync(CompanionController c)
    {
        AppendSystem($"sending a test notification to {c.PushSubscriberCount} device(s)...");
        try
        {
            int delivered = await c.TestNotifyAsync();
            AppendSystem(delivered > 0
                ? $"delivered to {delivered} device(s)"
                : "no device accepted it — check the phone's notification settings");
        }
        catch (Exception ex)
        {
            AppendSystem($"notification failed: {ex.Message}");
        }
    }

    private async Task StopCompanionAsync(CompanionController c)
    {
        try
        {
            await c.StopAsync();
            AppendSystem("companion server stopped");
        }
        catch (Exception ex)
        {
            AppendSystem($"companion server failed to stop cleanly: {ex.Message}");
        }
    }

    /// <summary>Report how a phone would reach this server from outside the machine.
    ///
    /// <para>Scrye stays bound to loopback and Tailscale's own proxy terminates TLS in front
    /// of it (companion design §5, §7.1). That keeps certificate <em>renewal</em> out of this
    /// codebase entirely — Let's Encrypt certs last 90 days, and a manually installed one
    /// would break the phone silently the day it lapsed.</para>
    ///
    /// <para>The serve command is printed, never run: it changes the user's tailnet
    /// configuration and its first invocation opens a browser consent page.</para></summary>
    private async Task ReportTailscaleAsync(CompanionController c, bool verbose = false)
    {
        Scrye.Companion.Server.Tailscale.TailscaleStatus ts =
            await Scrye.Companion.Server.Tailscale.TailscaleInfo.QueryAsync();

        if (!ts.Installed)
        {
            if (verbose)
            {
                AppendSystem("tailscale is not installed — it is what lets a phone reach this");
                AppendSystem("  see docs/Scrye-Companion-Setup.md for the walkthrough");
            }
            return;
        }

        if (!ts.Running || ts.DnsName is null)
        {
            AppendSystem($"tailscale: {ts.Detail}");
            return;
        }

        AppendSystem($"tailscale node: {ts.DnsName}");
        AppendSystem($"  phone URL (once serving): {ts.PublicUrl}");
        AppendSystem("  to start the TLS proxy, run in a terminal:");
        AppendSystem($"    {Scrye.Companion.Server.Tailscale.TailscaleInfo.ServeCommand(4747)}");
        if (verbose)
            AppendSystem($"    (stop it with: {Scrye.Companion.Server.Tailscale.TailscaleInfo.ServeOffCommand(4747)})");
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

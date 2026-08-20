using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;   // Avalonia 12: SetTextAsync is an extension method (ClipboardExtensions)
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
    private static readonly Rgb RelayColour  = new(0xC0, 0x90, 0xE0); // violet for another world's chat
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
        _closedPanes.Add(pane.Name);      // so a declaring plugin does not re-create it next start
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
        layout.ClosedPanes.AddRange(_closedPanes);
        foreach (CapturePaneViewModel p in _allPanes)
            layout.Panes.Add(new Services.PaneLayoutEntry { Name = p.Name, Dock = p.Dock.ToString() });
        if (Hud is not null)
            foreach (HudPanelViewModel hp in Hud.Panels)
                if (!double.IsNaN(hp.X) && !double.IsNaN(hp.Y))
                    layout.HudPanels.Add(new Services.HudPanelLayout
                    {
                        Name = hp.Key, X = hp.X, Y = hp.Y,
                        W = double.IsNaN(hp.UserWidth) ? 0 : hp.UserWidth,
                        H = double.IsNaN(hp.UserHeight) ? 0 : hp.UserHeight,
                    });
        // Collapsed panels go in their own list: a panel can be rolled up before it has ever
        // been dragged, and the entries above are only written once it has a real position.
        if (Hud is not null)
            foreach (HudPanelViewModel hp in Hud.Panels)
                if (hp.IsCollapsed) layout.CollapsedHudPanels.Add(hp.Key);
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
            foreach (string closed in layout.ClosedPanes) _closedPanes.Add(closed);
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

    /// <summary>Names the user closed by hand; see <c>WorldLayout.ClosedPanes</c>.</summary>
    private readonly HashSet<string> _closedPanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Create any capture pane a loaded plugin declares in its manifest, so the pane
    /// is there as soon as the plugin is enabled rather than the first time something is
    /// routed into it. Re-run after enable/reload/rescan, and idempotent.
    ///
    /// <para>Skips a pane the user closed by hand — that choice outlives a restart. A line
    /// actually routed to it still recreates it through the normal path, which is right: at
    /// that point there is something in it to read.</para></summary>
    private void EnsureDeclaredPanes()
    {
        bool made = false;
        foreach (PluginInfo p in _plugins.ListPlugins())
        {
            if (!p.Loaded || p.Panes is null) continue;
            foreach (string name in p.Panes)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                string pane = name.Trim();
                if (_closedPanes.Contains(pane) || FindPane(pane) is not null) continue;
                CreatePane(pane, PaneDock.Bottom);      // docking bottom also opens the zone
                made = true;
            }
        }
        if (made) SaveLayout();
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

    /// <summary>Companion panel: start/stop the phone server, show how to reach it, and list
    /// what can notify. Named <c>CompanionPanel</c> rather than <c>Companion</c> because that
    /// name is already the protocol fan-out hub on this class.</summary>
    public CompanionViewModel CompanionPanel { get; }

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

    /// <summary>Persist a trigger's Notify flag to the connected node's profile layer. Assigned
    /// by the shell, which owns the profile store; null for a quick-connect world, and the
    /// companion panel greys the tick boxes out when it is.</summary>
    public Action<TriggerDef, bool>? PersistTriggerNotify { get; set; }

    /// <summary>Write a parsed MUSHclient import into this world's own profile layer.
    /// Set by the shell, which owns the profile store; null on a quick-connect tab, which has
    /// no layer to write to. Returns false if the save failed (it has already toasted).</summary>
    public Func<Scrye.Core.Automation.MushclientImport, bool>? ImportRules { get; set; }

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
        Sequence = new SequenceViewModel(_session.PauseSequence, _session.ResumeSequence,
                                         _session.StopSequence, _session.RunSequence);
        _session.SequenceStatusChanged += Sequence.Update;

        // HUD: plugins add declarative panels during load (below); this owns them.
        // Panel-button clicks are marshalled onto the session loop before hitting plugin Lua.
        // _plugins is assigned just below; the lambda only runs on a click, well after that.
        Hud = new HudViewModel(_session.GameState,
            (pluginId, actionId) => _session.Post(() => _plugins!.InvokeAction(pluginId, actionId)),
            (pluginId, actionId, col, row, ch) =>
                _session.Post(() => _plugins!.InvokeCellAction(pluginId, actionId, col, row, ch)),
            (pluginId, actionId, text) =>
                _session.Post(() => _plugins!.InvokeSubmit(pluginId, actionId, text)),
            (pluginId, actionId, label, index) =>
                _session.Post(() => _plugins!.InvokeChoice(pluginId, actionId, label, index)),
            // click= in widget text lands on the same handler as an MXP link from the MUD
            (command, prompt) => HandleCommandLink(command, prompt));

        // Restore dragged HUD-panel positions (loaded up-front: plugins add their panels
        // during construction below, before RestoreLayout runs), and persist on drag.
        var savedHud = new System.Collections.Generic.Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);
        var savedCollapsed = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        if (Services.PaneLayoutStore.Load(profile.Name) is { } savedLayout)
        {
            foreach (Services.HudPanelLayout h in savedLayout.HudPanels)
                if (!string.IsNullOrEmpty(h.Name)) savedHud[h.Name] = (h.X, h.Y, h.W, h.H);
            foreach (string c in savedLayout.CollapsedHudPanels) savedCollapsed.Add(c);
        }
        Hud.LoadPosition = key => savedHud.TryGetValue(key, out (double, double, double, double) p) ? p : null;
        Hud.LoadCollapsed = key => savedCollapsed.Contains(key);
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
        // The host renders the line itself: it applies the plugin's inline colour markup and
        // prepends the "[id] " tag in PluginColour, so uncoloured plugins look exactly as before
        // and coloured ones arrive ready to enqueue. Token names resolve through HudColor, the
        // same table the HUD widget specs use.
        var host = new SessionPluginHost(_session,
            (id, line) => _pending.Enqueue(line),
            (id, spec) => Hud.AddPanel(id, spec),
            pluginData,
            HudColor.ResolveRgb,
            PluginColour);
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
            (id, done) => _session.Post(() => { _plugins.Reload(id); Dispatcher.UIThread.Post(() => { EnsureDeclaredPanes(); done(); }); }),
            (id, enable, done) => _session.Post(() =>
            {
                if (enable) _plugins.Enable(id); else _plugins.Disable(id);
                Dispatcher.UIThread.Post(() => { EnsureDeclaredPanes(); done(); });
            }),
            (id, done) => _session.Post(() => { _plugins.Remove(id); Dispatcher.UIThread.Post(done); }),
            done => _session.Post(() => { _plugins.Rescan(); Dispatcher.UIThread.Post(() => { EnsureDeclaredPanes(); done(); }); }),
            done => { ScaffoldNewPlugin(userPluginRoot); _session.Post(() => { _plugins.Rescan(); Dispatcher.UIThread.Post(done); }); },
            () => OpenPluginsFolder(userPluginRoot),
            () => _plugins.Diagnostics.Snapshot());   // immutable snapshot — safe to read from the UI thread

        CompanionPanel = new CompanionViewModel(
            () => CompanionControl,
            () => SessionId,
            () => _session.Automation.AllTriggers,
            CopyToClipboard,
            // Collect plugin.<id>.notify rows ON the session loop (the state store is
            // single-threaded there) and deliver back on the UI thread.
            deliver => _session.Post(() =>
            {
                var found = new System.Collections.Generic.List<(string, string)>();
                foreach (var kv in _session.GameState.Snapshot())
                {
                    const string prefix = "plugin.";
                    const string suffix = ".notify";
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                        !kv.Key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    string id = kv.Key[prefix.Length..^suffix.Length];
                    // exactly plugin.<id>.notify — a nested path like plugin.x.notify.y is
                    // that plugin's own business, not a sources blob
                    if (id.Length == 0 || id.Contains('.')) continue;
                    if (kv.Value.Text.Length > 0) found.Add((id, kv.Value.Text));
                }
                Dispatcher.UIThread.Post(() => deliver(found));
            }),
            // Flipping a trigger's Notify: persist through the shell (it owns the profile
            // store), then re-apply so the change takes effect without a reconnect. Null for a
            // quick-connect world, which has no layer to write into — the panel greys those out.
            (def, on) =>
            {
                PersistTriggerNotify?.Invoke(def, on);
                _session.Post(() => _session.Automation.SetTriggerNotify(def, on));
            },
            () => PersistTriggerNotify is not null,
            // Toggle commands run the way typing them would (plugin aliases first). This is
            // panel-authored text from a local plugin, not MUD-authored, so the echo is honest.
            cmd => HandleCommandLink(cmd, prompt: false));
        // Plugins process each server line (onLine gag/rewrite + triggers) and user input
        // (aliases) via the session's filter hooks — so gagging actually suppresses display.
        _session.LineDisplayFilter = _plugins.ProcessLine;    // gag/rewrite + triggers + prompt hook
        _session.InputFilter = _plugins.ProcessInput;         // plugin aliases (a match consumes input)
        _session.GmcpReceived += (pkg, json) => _plugins.DispatchGmcp(pkg, json);
        _session.ChannelMessage += (ch, msg) =>
        {
            _plugins.DispatchChannel(ch, msg);                        // MIP chat → scrye.onChannel
            // …and, when this world is allowed to, offer it to whichever tab is in front. The
            // shell decides whether anyone wants it; this only says "here is one, from me".
            if (!_session.Profile.ShouldRelay(ch)) return;
            if (ch.Equals("Tell", StringComparison.OrdinalIgnoreCase)
                && msg.StartsWith(Scrye.Core.Mip.MipProcessor.OutgoingTellPrefix, StringComparison.Ordinal))
                return;                                               // your own outgoing tell
            ChannelRelayed?.Invoke(this, ch, msg);
        };
        _session.Ticked += _plugins.Tick;                     // plugin timers (scrye.after/every)
        _session.CommandSent += _plugins.DispatchCommand;     // every outgoing command → scrye.onCommand (1.6)
        // scrye.emit lands back on the manager, which fans it out to every plugin's scrye.on
        // handlers (1.6). Set after the manager exists; both run on the session loop.
        host.PluginEventSink = _plugins.DispatchPluginEvent;
        _session.StateChanged += s =>                          // plugin lifecycle hooks
        {
            if (s == ConnectionState.Connected)
            {
                _plugins.DispatchConnect();
                // Hopped to the UI thread on purpose: _logging and _loggingDeclined are otherwise
                // only touched by the ".log" command, and keeping every write on one thread is
                // cheaper than reasoning about a race whose prize is a duplicate notice.
                Dispatcher.UIThread.Post(MaybeAutoLog);
            }
            else if (s == ConnectionState.Disconnected) _plugins.DispatchDisconnect();
        };

        // The idle guard. The session has already suspended its own timers and sequence by the
        // time this runs; all that is left is to say so where the user will see it and to hand
        // the news to plugins, which stop whatever they are driving.
        _session.IdleSignal += signal =>
        {
            if (signal == IdleGuardSignal.Warning)
            {
                AppendSystem($"idle guard: nothing from you for {IdleGuard.Describe(_session.IdleGuard.IdleSeconds)}"
                    + $" — automation stops in {IdleGuard.Describe(_session.IdleGuard.SecondsRemaining)}."
                    + " Type anything to reset.");
                return;
            }
            AppendSystem($"idle guard: idle {IdleGuard.Describe(_session.IdleGuard.IdleSeconds)}"
                + " — automation stopped. Timers restart when you type; anything a plugin was"
                + " driving needs starting again yourself.");
            _plugins.DispatchIdle();
        };

        SubmitCommand = new RelayCommand(Submit);

        // bring back this world's saved pane setup (docks + timestamp toggle), then add any
        // pane a loaded plugin declares but the saved layout does not have yet — the fresh-
        // machine case, where there is no layout at all.
        RestoreLayout();
        EnsureDeclaredPanes();

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    /// <summary>Construct from a resolved profile: loads its triggers/aliases/timers/vars.</summary>
    public WorldViewModel(EffectiveProfile eff) : this(eff.World, eff.EnabledPlugins)
    {
        _session.LoadProfileData(eff);
        LoadMacros(eff.Macros);
        // Names come from the resolved profile rather than the live SequenceEngine: the engine's
        // registry is loop-owned, and this runs on the UI thread.
        Sequence.SetAvailable(eff.Sequences.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
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
        Sequence.SetAvailable(eff.Sequences.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
    }

    /// <summary>The idle guard, as a bottom-bar toggle. It stops all automation when it fires,
    /// which is worth being able to see and switch without remembering ".idle" — the command
    /// stays for setting a limit, which a toggle cannot express.</summary>
    public bool IdleGuardEnabled
    {
        get => _session.IdleGuard.Enabled;
        set
        {
            if (_session.IdleGuard.Enabled == value) return;
            _session.IdleGuard.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IdleGuardTip));
            AppendSystem(value
                ? $"idle guard on — automation stops after {IdleGuard.Describe(_session.IdleGuard.Seconds)} with nothing from you"
                : "idle guard off");
        }
    }

    /// <summary>Tooltip for that toggle: the limit is the part a checkbox cannot show.</summary>
    public string IdleGuardTip =>
        $"Idle guard: stop automation after {IdleGuard.Describe(_session.IdleGuard.Seconds)} "
      + "with no input from you. Set the limit with .idle <seconds|Nm>.";

    public void AppendSystem(string text) => _pending.Enqueue(Line.FromText("* " + text, SystemColour));

    /// <summary>
    /// <c>.mip</c> — report whether the viking feed still has the shape the parsers assume.
    /// <c>.mip fields</c> — report what this character actually receives, which is the question
    /// you have to answer before writing a plugin for a guild nobody here plays.
    /// <c>.mip fields save</c> — the same, written to a markdown file you can hand to whoever
    /// is writing it.
    ///
    /// <para>The audit runs on the session loop as messages arrive, so this only reads its
    /// findings; the hop is because the report is a snapshot of loop-owned state.</para>
    /// </summary>
    private void HandleMipCommand(string arg)
    {
        string a = arg.Trim().ToLowerInvariant();
        if (a.Length == 0) { PostMipLines(s => s.MipAudit.Report()); return; }
        if (a is not ("fields" or "fields save" or "fields file"))
        {
            AppendSystem("usage: .mip | .mip fields | .mip fields save");
            return;
        }

        bool save = a != "fields";
        _session.Post(() =>
        {
            IReadOnlyList<(string, string?)> vitals = _session.MipVitalsSnapshot();
            IReadOnlyList<string> lines = _session.MipAudit.FieldReport(vitals, markdown: save);
            string? written = null, error = null;
            if (save)
            {
                try
                {
                    string dir = MudSession.DefaultLogDirectory();
                    Directory.CreateDirectory(dir);
                    // The world name is in it because the whole point is running this on several
                    // characters and comparing; a bare "mip-fields.md" would overwrite the last one.
                    string safe = string.Join("_", Title.Split(Path.GetInvalidFileNameChars()));
                    written = Path.Combine(dir,
                        $"mip-fields-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                    File.WriteAllLines(written, lines);
                }
                catch (Exception ex) { error = ex.Message; written = null; }
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (!save) { foreach (string l in lines) AppendSystem(l); return; }
                if (written is not null) AppendSystem($"MIP field report written to {written}");
                else AppendSystem($"could not write the MIP field report: {error}");
            });
        });
    }

    private void PostMipLines(Func<MudSession, IReadOnlyList<string>> build)
    {
        _session.Post(() =>
        {
            IReadOnlyList<string> lines = build(_session);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (string l in lines) AppendSystem(l);
            });
        });
    }

    /// <summary>A chat line from ANOTHER world, shown here because this is the tab in front.
    /// Raised by <see cref="ChannelRelayed"/> on the source world and routed by the shell.</summary>
    public event Action<WorldViewModel, string, string>? ChannelRelayed;

    /// <summary>Draw another world's chat line in this pane. It goes straight onto the pending
    /// queue, like <see cref="AppendSystem"/>: it is NOT MUD output, so it must not reach this
    /// world's triggers, its capture panes, or its session transcript — a foreign line matching
    /// a local trigger and firing automation would be a genuinely bad surprise.</summary>
    public void AppendRelay(string sourceWorld, string channel, string text)
    {
        string label = channel.Equals("Tell", StringComparison.OrdinalIgnoreCase)
            ? sourceWorld                                   // "[Aardwolf] Bob: hi" reads as a tell already
            : $"{sourceWorld}/{channel}";
        _pending.Enqueue(Line.FromText($"[{label}] {text}", RelayColour));
    }

    /// <summary>Take a chat line relayed from another world: draw it inline AND offer it to this
    /// world's plugins as <c>scrye.onRelay</c>, which is how it reaches a chat pane instead of
    /// scrolling away. The plugin half is posted to the session loop, because that is the only
    /// thread a plugin runtime may be entered on.</summary>
    public void ReceiveRelay(string sourceWorld, string channel, string text)
    {
        AppendRelay(sourceWorld, channel, text);
        _session.Post(() => _plugins.DispatchRelay(sourceWorld, channel, text));
    }

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
    /// <summary>Capture-pane wrap measure, in characters — the width the MUD wraps its
    /// own output to, so the panes read like the main output instead of monitor-wide.</summary>
    private const int PaneWrapCols = 100;

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
                _closedPanes.Remove(item.Pane);   // traffic overrules an earlier close
                created = true;
            }
            // Wrap long chat lines to a readable measure BEFORE they hit the buffer: the
            // terminal renderer is one-buffer-line-per-row, so on a widescreen an unwrapped
            // tell runs the full monitor. ~100 columns matches the width the MUD wraps its
            // own output to; continuations carry a hanging indent and no timestamp.
            foreach (Line seg in item.Line.Wrap(PaneWrapCols))
            {
                pane.Buffer.Add(seg);
                if (companionBatches is not null)
                {
                    if (!companionBatches.TryGetValue(item.Pane, out OutputBatchBuilder? b))
                        companionBatches[item.Pane] = b = new OutputBatchBuilder();
                    // Each pane's buffer carries its own sequence space; read it after the add.
                    b.Add(seg, pane.Buffer.SequenceAt(pane.Buffer.Count - 1));
                }
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
    /// <summary>Up arrow. <paramref name="prefix"/> filters the walk — the text before the
    /// caret, or empty for the whole history.</summary>
    public string? HistoryPrevious(string current, string? prefix = null) => _history.Previous(current, prefix);

    /// <summary>The newest command starting with <paramref name="prefix"/> (inline suggestion).</summary>
    public string? HistorySuggest(string? prefix) => _history.Suggest(prefix);

    /// <summary>The user edited the input: the next Up should filter on what is there now.</summary>
    public void HistoryResync() => _history.Resync();
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
    /// A companion server handling a link tap must send it raw for the same reason.</para>
    ///
    /// <para>For the same reason it submits <em>literally</em>: the ';' command separator is
    /// something a person asks for by typing it, not something a link gets to claim on their
    /// behalf.</para></summary>
    public void HandleCommandLink(string command, bool prompt)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (prompt) { Input = command; return; }
        _pending.Enqueue(Line.FromText("> " + command, EchoColour));
        _session.SubmitLiteral(command);   // one command, whatever separators the MUD put in it
    }

    /// <summary>The command line's Enter handler: take what is in the input box, record it
    /// for recall, and run it through the pipeline as local input.</summary>
    private void Submit()
    {
        string text = Input ?? "";
        // "Keep the last command" leaves it in the box instead of clearing; the view then
        // selects it, so Enter alone repeats it and the next keystroke replaces it. An empty
        // submit has nothing to keep. Set BEFORE the command goes out, as the clear always was,
        // so nothing downstream can observe a box that still holds what is already on the wire.
        Input = Services.InputPreferences.KeepAfterSend && text.Length > 0 ? text : "";
        _history.Add(text);        // record for up/down recall (consecutive repeats collapse)
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
            case ".idle": HandleIdleCommand(arg); return true;
            case ".tts": HandleTtsCommand(arg); return true;
            case ".companion": HandleCompanionCommand(arg); return true;
            case ".mip": HandleMipCommand(arg); return true;
            case ".import": HandleImportCommand(arg); return true;
            case ".ts" or ".timestamps":
                ShowTimestamps = !ShowTimestamps;
                AppendSystem(ShowTimestamps ? "timestamps on" : "timestamps off");
                return true;
            default: return false;
        }
    }

    /// <summary>
    /// <c>.import &lt;file&gt;</c> — read a MUSHclient world file (or an exported plugin) and
    /// say what would come across; <c>.import &lt;file&gt; apply</c> keeps it.
    ///
    /// <para>Two steps on purpose. A world file can hold hundreds of rules and several of them
    /// will not survive the crossing — script actions especially — so reading the list is
    /// cheaper than undoing the import afterwards. What is kept lands in a group named after
    /// the file, which makes it one collapsed header in Settings, and one thing to delete if
    /// you change your mind.</para>
    /// </summary>
    private void HandleImportCommand(string arg)
    {
        bool apply = arg.EndsWith(" apply", StringComparison.OrdinalIgnoreCase);
        string path = (apply ? arg[..^" apply".Length] : arg).Trim().Trim('"');
        if (path.Length == 0)
        {
            AppendSystem("usage: .import <path to a MUSHclient .mcl or plugin .xml> [apply]");
            return;
        }
        if (!System.IO.File.Exists(path)) { AppendSystem("no such file: " + path); return; }

        string group = System.IO.Path.GetFileNameWithoutExtension(path);
        Scrye.Core.Automation.MushclientImport import;
        try
        {
            import = Scrye.Core.Automation.MushclientImport.Parse(System.IO.File.ReadAllText(path), group);
        }
        catch (Exception ex)
        {
            AppendSystem($"could not read {path}: {ex.Message}");
            return;
        }

        foreach (string line in import.Report().Split('\n')) AppendSystem(line.TrimEnd());

        if (!apply)
        {
            AppendSystem(import.Count > 0
                ? $"nothing has changed yet — run  .import {path} apply  to keep this"
                : "nothing to import");
            return;
        }
        if (import.Count == 0) { AppendSystem("nothing to import"); return; }
        if (ImportRules is null)
        {
            AppendSystem("this tab has no saved profile to import into (quick-connect)");
            return;
        }
        AppendSystem(ImportRules(import)
            ? $"imported — look for the '{group}' group in Settings"
            : "the import could not be saved (see logs)");
    }

    /// <summary>
    /// <c>.idle</c> — read or change the dead-man's switch for this session. Takes effect at
    /// once; the profile's <c>idleGuard</c>/<c>idleGuardSeconds</c> supply the value it starts
    /// with, so this is the knob you reach for mid-session rather than the place it is stored.
    /// </summary>
    private void HandleIdleCommand(string arg)
    {
        IdleGuard guard = _session.IdleGuard;
        arg = arg.Trim().ToLowerInvariant();

        if (arg.Length == 0)
        {
            AppendSystem(guard.Enabled
                ? $"idle guard on, limit {IdleGuard.Describe(guard.Seconds)}"
                  + (guard.HasFired
                      ? " — fired; automation is stopped until you send something"
                      : $", {IdleGuard.Describe(guard.SecondsRemaining)} left")
                : $"idle guard off (limit would be {IdleGuard.Describe(guard.Seconds)})");
            return;
        }

        if (arg is "on" or "off")
        {
            guard.Enabled = arg == "on";
            OnPropertyChanged(nameof(IdleGuardEnabled));
            AppendSystem(guard.Enabled
                ? $"idle guard on — automation stops after {IdleGuard.Describe(guard.Seconds)} with nothing from you"
                : "idle guard off");
            return;
        }

        // "10m" and "600" both read naturally for a limit of this size
        int mult = arg.EndsWith('m') ? 60 : 1;
        if (int.TryParse(arg.TrimEnd('m', 's'), out int n) && n > 0)
        {
            guard.Seconds = n * mult;
            guard.Enabled = true;
            OnPropertyChanged(nameof(IdleGuardEnabled));
            OnPropertyChanged(nameof(IdleGuardTip));
            AppendSystem($"idle guard on, limit {IdleGuard.Describe(guard.Seconds)}"
                + (n * mult != guard.Seconds
                    ? $" (clamped to {IdleGuard.MinSeconds}-{IdleGuard.MaxSeconds}s)" : ""));
            return;
        }

        AppendSystem("usage: .idle | .idle on | .idle off | .idle <seconds> | .idle <n>m");
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
            // The scaffold is the documentation most authors actually read, so it declares an API
            // range and its permissions rather than leaving both to be discovered later.
            File.WriteAllText(Path.Combine(dir, "plugin.json"),
                "{\n" +
                "  \"id\": \"" + id + "\",\n" +
                "  \"name\": \"" + id + "\",\n" +
                "  \"version\": \"0.1.0\",\n" +
                "  \"mudIds\": [\"*\"],\n" +
                "  \"requires\": { \"scryeApi\": \">=" + Scrye.Core.Plugins.ScryeApi.CurrentText + " <2.0\" },\n" +
                "  \"permissions\": [\"output.read\", \"ui.panels\", \"state.write\"]\n" +
                "}\n");
            File.WriteAllText(Path.Combine(dir, "main.lua"),
                "-- New Scrye plugin. Edit this file, then click Reload in the Plugins panel.\n" +
                "scrye.print(\"" + id + " loaded\")\n\n" +
                "scrye.onLine(function(line)\n" +
                "    -- react to output here; return false to gag a line, a string to rewrite it\n" +
                "end)\n\n" +
                "-- A panel is data, not drawing: describe widgets and bind them to state paths.\n" +
                "-- Colours accept a #RRGGBB literal or a theme token (accent, dim, success,\n" +
                "-- warning, error, info, ...) -- prefer the token so your panel follows the\n" +
                "-- user's colour scheme and renders correctly on the mobile companion.\n" +
                "local P = \"plugin.\" .. scrye.id .. \".\"\n\n" +
                "scrye.setState(P .. \"rows\", \"Example\\tready\")\n\n" +
                "scrye.addPanel{\n" +
                "    title = \"" + id + "\", accent = \"accent\",\n" +
                "    widgets = {\n" +
                "        { type = \"label\", text = \"Hello from " + id + "\", color = \"dim\" },\n" +
                "        -- a list grows and shrinks with its bound value, unlike the fixed widget set\n" +
                "        { type = \"list\", bind = P .. \"rows\" },\n" +
                "    },\n" +
                "}\n");
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

    /// <summary><c>.companion [on|off|status|tailscale|notify]</c> — start or stop the mobile
    /// companion server and report its address.
    ///
    /// <para>The Companion panel in the bottom bar is now the main way in; this stays for
    /// muscle memory and because a keyboard-only path is genuinely quicker mid-session.</para>
    ///
    /// <para>It no longer prints the access token. It used to, and that was a real problem:
    /// scrollback is what session logging writes to disk, so every <c>.companion</c> put a
    /// live credential in a log file. The panel can show it without it ever entering
    /// scrollback, so the command points there instead.</para></summary>
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
            if (c.IsRunning) AppendSystem("  token: see the Companion panel (bottom bar)");
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
            AppendSystem($"  this world's sessionId: {SessionId}");
            CompanionPanel.Open();
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
            AppendSystem($"  this world's sessionId: {SessionId}");
            AppendSystem("  token and QR code: the Companion panel, opening now");
            AppendSystem("  usage: .companion status | tailscale | notify | notify test | off");
            CompanionPanel.Open();
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
            var outcome = await c.TestNotifyAsync();
            AppendSystem(outcome.ToString());
            if (outcome.Delivered == 0 && outcome.Failed == 0 && outcome.Expired == 0)
                AppendSystem("no devices are registered — tap 'Enable notifications' in the companion app on the phone");
        }
        catch (Exception ex)
        {
            AppendSystem($"notification failed: {ex.Message}");
        }
    }

    /// <summary>Put text on the system clipboard.
    ///
    /// <para>Reached through the application lifetime rather than a visual, because a view
    /// model has no control to walk up from. Failures are swallowed: a clipboard that is
    /// locked by another process is a normal Windows occurrence and not worth an error
    /// dialog over a copy button.</para></summary>
    private static void CopyToClipboard(string text)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.Clipboard is { } clipboard)
            {
                _ = clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Nothing useful to do; the value is still visible in the panel to copy by hand.
        }
    }

    private async Task StopCompanionAsync(CompanionController c)
    {
        try
        {
            await c.StopAsync();
            AppendSystem("companion server stopped");
            CompanionPanel.NotifyStateChanged();
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
    /// <summary>Start the automatic transcript on connect, when the profile asks for one.
    ///
    /// <para>Two deliberate restraints. It never restarts a log that is already running, so an
    /// auto-reconnect continues the same file rather than littering the folder with a fragment
    /// per dropped connection. And <c>.log off</c> stays off for the rest of the session —
    /// switching logging back on because the link blipped would ignore an explicit instruction,
    /// on a feature whose entire point is the user deciding what gets written to disk.</para></summary>
    private void MaybeAutoLog()
    {
        if (!_session.Profile.AutoLog || _logging || _loggingDeclined) return;
        try
        {
            string path = _session.StartLogging(_session.AutoLogFormat(), fileStem: _session.AutoLogStem());
            _logging = true;
            AppendSystem($"logging to {path}");
        }
        catch (System.Exception ex)
        {
            // A full disk or a read-only logs folder must not stop you playing.
            AppendSystem($"could not start the session log: {ex.Message}");
        }
    }

    /// <summary>Set once ".log off" is used, so auto-logging does not undo that on reconnect.</summary>
    private bool _loggingDeclined;

    private void HandleLogCommand(string arg)
    {
        string a = arg.Trim().ToLowerInvariant();
        if (a is "off" or "stop")
        {
            _loggingDeclined = true;          // and stay off, even across a reconnect
            if (_logging) { _session.StopLogging(); _logging = false; AppendSystem("logging stopped"); }
            else AppendSystem("not currently logging");
            return;
        }
        if (_logging && a.Length == 0)   // bare .log while logging → toggle off
        {
            _loggingDeclined = true;
            _session.StopLogging(); _logging = false; AppendSystem("logging stopped"); return;
        }
        LogFormat fmt = a.Contains("htm") ? LogFormat.Html : LogFormat.Text;
        _loggingDeclined = false;         // asking for it back re-arms auto-logging too
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
        _scriptHost.Dispose();   // frees the world-script Lua state (native since Phase 5)
        Hud.Dispose();
        _session.Events.Emitted -= Debugger.Enqueue;
        await _session.DisposeAsync();
    }
}

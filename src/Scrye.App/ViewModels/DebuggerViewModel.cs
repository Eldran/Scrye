using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Scrye.Core.Automation;
using Scrye.Core.Events;
using Scrye.Core.Session;

namespace Scrye.App.ViewModels;

/// <summary>
/// The trigger debugger / execution timeline for one world. Consumes the session's
/// event stream (M8 Foundation A): a live, filterable list of everything that
/// happened — lines, input, sends, rule fires, variable changes, protocol messages —
/// plus record-to-<c>.scryerec</c> and a side-effect-free "simulate a line" box.
///
/// Threading: <see cref="Enqueue"/> runs on the session loop thread (event bus);
/// <see cref="Drain"/> runs on the UI thread from the world's flush timer and is the
/// only writer of the observable collection.
/// </summary>
public sealed class DebuggerViewModel : ViewModelBase
{
    private const int Cap = 3000;   // max rows retained / shown
    // Trimming happens in batches: the lists may run this far past Cap before being cut back,
    // so a busy session pays one bulk trim every Slack events instead of an O(n) RemoveAt(0)
    // (plus a collection notification) on every one.
    private const int Slack = Cap / 4;

    private readonly MudSession _session;
    private readonly Action<string> _notify;                 // push a system line to the world output
    private readonly ConcurrentQueue<SessionEvent> _incoming = new();
    private readonly List<EventRowViewModel> _all = new();    // backing store (all kinds)
    // Raw events that arrived while the panel was hidden. No row view models are built for
    // them until the panel is shown, and then only for the newest Cap.
    private readonly List<SessionEvent> _backlog = new();
    private bool _recording;   // UI-side mirror of the recorder, which the session loop owns

    /// <summary>The filtered rows currently shown in the timeline.</summary>
    public ObservableCollection<EventRowViewModel> Rows { get; } = new();

    private EventRowViewModel? _selectedRow;
    /// <summary>The row selected in the list; its <c>Full</c> text fills the detail pane.</summary>
    public EventRowViewModel? SelectedRow { get => _selectedRow; set => SetField(ref _selectedRow, value); }

    public RelayCommand RecordCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand SimulateCommand { get; }

    public DebuggerViewModel(MudSession session, Action<string> notify)
    {
        _session = session;
        _notify = notify;
        RecordCommand = new RelayCommand(ToggleRecord);
        ClearCommand = new RelayCommand(Clear);
        SimulateCommand = new RelayCommand(Simulate);
    }

    // ---- filters (category buckets) -----------------------------------------
    private bool _showOutput = true;
    public bool ShowOutput { get => _showOutput; set { if (SetField(ref _showOutput, value)) Rebuild(); } }
    private bool _showInput = true;
    public bool ShowInput { get => _showInput; set { if (SetField(ref _showInput, value)) Rebuild(); } }
    private bool _showAutomation = true;
    public bool ShowAutomation { get => _showAutomation; set { if (SetField(ref _showAutomation, value)) Rebuild(); } }
    private bool _showState = true;
    public bool ShowState { get => _showState; set { if (SetField(ref _showState, value)) Rebuild(); } }
    private bool _showProtocol = true;
    public bool ShowProtocol { get => _showProtocol; set { if (SetField(ref _showProtocol, value)) Rebuild(); } }
    private bool _showSystem = true;
    public bool ShowSystem { get => _showSystem; set { if (SetField(ref _showSystem, value)) Rebuild(); } }

    private bool _paused;
    /// <summary>When true, new events still accumulate in the backing store but the
    /// visible list is frozen (so you can read without it scrolling away).</summary>
    public bool Paused { get => _paused; set { if (SetField(ref _paused, value) && !value) Rebuild(); } }

    private string _recordLabel = "Record";
    public string RecordLabel { get => _recordLabel; set => SetField(ref _recordLabel, value); }

    private string _simulateInput = "";
    public string SimulateInput { get => _simulateInput; set => SetField(ref _simulateInput, value); }

    // ---- event intake --------------------------------------------------------

    /// <summary>Session-loop thread: just hand the event off; the UI drains it.</summary>
    public void Enqueue(SessionEvent ev) => _incoming.Enqueue(ev);

    private bool _visible;
    /// <summary>Whether the debugger panel is on screen (set by the world with ShowDebugger).
    /// While hidden, <see cref="Drain"/> only parks raw events; showing it folds them in and
    /// rebuilds the list once.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (!value) return;
            FoldBacklog();
            if (!_paused) Rebuild();   // paused keeps its frozen list, as it always has
        }
    }

    /// <summary>UI thread: fold queued events into the backing store and the visible list.</summary>
    public void Drain()
    {
        if (!_visible)
        {
            // Hidden: no row view models, no collection notifications. Keep the newest only.
            while (_incoming.TryDequeue(out SessionEvent? hidden)) _backlog.Add(hidden);
            if (_backlog.Count > Cap + Slack) _backlog.RemoveRange(0, _backlog.Count - Cap);
            return;
        }
        while (_incoming.TryDequeue(out SessionEvent? ev))
        {
            var row = new EventRowViewModel(ev);
            _all.Add(row);
            if (!_paused && Passes(row)) Rows.Add(row);
        }
        if (_all.Count > Cap + Slack) _all.RemoveRange(0, _all.Count - Cap);
        if (Rows.Count > Cap + Slack)
        {
            // One reset + re-add from the (already trimmed) backing store, rather than
            // hundreds of front removals; the selected row survives if it is still kept.
            EventRowViewModel? selected = SelectedRow;
            Rebuild();
            if (selected is not null && Rows.Contains(selected)) SelectedRow = selected;
        }
    }

    /// <summary>Turn events parked while hidden into rows (the newest Cap at most).</summary>
    private void FoldBacklog()
    {
        for (int i = Math.Max(0, _backlog.Count - Cap); i < _backlog.Count; i++)
            _all.Add(new EventRowViewModel(_backlog[i]));
        _backlog.Clear();
        if (_all.Count > Cap) _all.RemoveRange(0, _all.Count - Cap);
    }

    private bool Passes(EventRowViewModel r) => r.Category switch
    {
        "Output" => _showOutput,
        "Input" => _showInput,
        "Automation" => _showAutomation,
        "State" => _showState,
        "Protocol" => _showProtocol,
        _ => _showSystem,
    };

    private void Rebuild()
    {
        Rows.Clear();
        foreach (EventRowViewModel r in _all)
            if (Passes(r)) Rows.Add(r);
    }

    private void Clear()
    {
        _all.Clear();
        _backlog.Clear();
        Rows.Clear();
    }

    // ---- record --------------------------------------------------------------

    // The recorder subscribes to the event bus and appends on the session loop, so starting,
    // counting, saving and stopping all run there too; the label flips on the UI thread at
    // once. _notify (the world's AppendSystem) only enqueues, so it is safe from the loop.
    private void ToggleRecord()
    {
        if (_recording)
        {
            _recording = false;
            RecordLabel = "Record";
            string path = RecordingPath();
            _session.Post(() =>
            {
                int count = _session.Recorder?.Events.Count ?? 0;
                string? error = null;
                try { _session.SaveRecording(path); }   // save while the recorder is still alive
                catch (Exception ex) { error = ex.Message; }
                _session.StopRecording();
                _notify(error is null
                    ? $"recording saved: {path} ({count} events)"
                    : $"could not save the recording: {error}");
            });
        }
        else
        {
            _recording = true;
            RecordLabel = "Stop";
            _session.Post(() => _session.StartRecording());
            _notify("recording started");
        }
    }

    private string RecordingPath()
    {
        string dir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Scrye", "recordings");
        System.IO.Directory.CreateDirectory(dir);
        string name = _session.Profile.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "world";
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return System.IO.Path.Combine(dir, $"{name}-{stamp}.scryerec");
    }

    // ---- simulate (dry-run) --------------------------------------------------

    private void Simulate()
    {
        string line = SimulateInput ?? "";
        if (line.Length == 0) return;

        // The live engine is loop-owned (its rule lists are replaced there on reload), so the
        // dry run happens on the loop; the results are only enqueued as system lines.
        _session.Post(() =>
        {
            IReadOnlyList<AutomationHit> hits = _session.Automation.Simulate(line);
            if (hits.Count == 0)
            {
                _notify($"simulate \"{line}\": no triggers would match");
                return;
            }
            foreach (AutomationHit h in hits)
                _notify($"simulate \"{line}\": {h.Name} would {h.Action}");
        });
    }
}

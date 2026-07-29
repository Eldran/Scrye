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

    private readonly MudSession _session;
    private readonly Action<string> _notify;                 // push a system line to the world output
    private readonly ConcurrentQueue<SessionEvent> _incoming = new();
    private readonly List<EventRowViewModel> _all = new();    // backing store (all kinds)

    /// <summary>The filtered rows currently shown in the timeline.</summary>
    public ObservableCollection<EventRowViewModel> Rows { get; } = new();

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

    /// <summary>UI thread: fold queued events into the backing store and the visible list.</summary>
    public void Drain()
    {
        while (_incoming.TryDequeue(out SessionEvent? ev))
        {
            var row = new EventRowViewModel(ev);
            _all.Add(row);
            if (_all.Count > Cap) _all.RemoveAt(0);
            if (!_paused && Passes(row)) Rows.Add(row);
        }
        while (Rows.Count > Cap) Rows.RemoveAt(0);
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
        Rows.Clear();
    }

    // ---- record --------------------------------------------------------------

    private void ToggleRecord()
    {
        if (_session.IsRecording)
        {
            string path = RecordingPath();
            int count = _session.Recorder?.Events.Count ?? 0;
            _session.SaveRecording(path);   // save while the recorder is still alive
            _session.StopRecording();
            RecordLabel = "Record";
            _notify($"recording saved: {path} ({count} events)");
        }
        else
        {
            _session.StartRecording();
            RecordLabel = "Stop";
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

        IReadOnlyList<AutomationHit> hits = _session.Automation.Simulate(line);
        if (hits.Count == 0)
        {
            _notify($"simulate \"{line}\": no triggers would match");
            return;
        }
        foreach (AutomationHit h in hits)
            _notify($"simulate \"{line}\": {h.Name} would {h.Action}");
    }
}

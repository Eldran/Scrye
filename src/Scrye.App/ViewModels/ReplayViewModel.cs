using System.Collections.ObjectModel;
using Avalonia.Threading;
using Scrye.Core.Automation;
using Scrye.Core.Events;

namespace Scrye.App.ViewModels;

/// <summary>
/// Replay transport for the debugger's "Replay" tab. Lists <c>.scryerec</c>
/// recordings from the app's recordings folder, loads one, and steps through its
/// events (Play / Pause / Step / Restart) into a timeline view. The payoff feature
/// (roadmap #3) is "Re-run vs current triggers": <see cref="ReplayAnalyzer"/> feeds
/// the recorded lines through the live world's <em>current</em> rules and reports
/// which lines would now behave differently — no reconnect, no side effects.
/// </summary>
public sealed class ReplayViewModel : ViewModelBase
{
    private const int StepMs = 150;   // fixed per-event pace for Play (time-accurate replay deferred)

    private readonly Func<AutomationEngine> _currentEngine;   // supplies today's rules for analysis
    private readonly Action<string> _notify;                  // echo analysis/summary to the world output
    private readonly DispatcherTimer _playTimer;

    private SessionRecording? _recording;
    private int _position;

    public ObservableCollection<string> Recordings { get; } = new();
    public ObservableCollection<EventRowViewModel> Rows { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StepCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand AnalyzeCommand { get; }

    public ReplayViewModel(Func<AutomationEngine> currentEngine, Action<string> notify)
    {
        _currentEngine = currentEngine;
        _notify = notify;

        RefreshCommand = new RelayCommand(Refresh);
        LoadCommand = new RelayCommand(Load);
        PlayPauseCommand = new RelayCommand(TogglePlay);
        StepCommand = new RelayCommand(Step);
        RestartCommand = new RelayCommand(Restart);
        AnalyzeCommand = new RelayCommand(Analyze);

        _playTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(StepMs) };
        _playTimer.Tick += (_, _) => Step();

        Refresh();
    }

    private string? _selectedRecording;
    public string? SelectedRecording { get => _selectedRecording; set => SetField(ref _selectedRecording, value); }

    private string _status = "No recording loaded.";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private string _positionLabel = "0 / 0";
    public string PositionLabel { get => _positionLabel; set => SetField(ref _positionLabel, value); }

    private string _playLabel = "Play";
    public string PlayLabel { get => _playLabel; set => SetField(ref _playLabel, value); }

    public bool IsPlaying => _playTimer.IsEnabled;

    // ---- recordings folder ---------------------------------------------------

    private static string RecordingsDir
    {
        get
        {
            string dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Scrye", "recordings");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void Refresh()
    {
        Recordings.Clear();
        foreach (string f in System.IO.Directory.EnumerateFiles(RecordingsDir, "*.scryerec"))
            Recordings.Add(System.IO.Path.GetFileName(f));
    }

    private void Load()
    {
        if (string.IsNullOrEmpty(SelectedRecording)) { _notify("replay: pick a recording first"); return; }
        Pause();
        try
        {
            _recording = SessionRecorder.Load(System.IO.Path.Combine(RecordingsDir, SelectedRecording));
        }
        catch (System.Exception ex) { _notify($"replay: load failed — {ex.Message}"); return; }

        Restart();
        Status = $"{SelectedRecording} — {_recording.Events.Count} events, {_recording.Duration.TotalSeconds:0.#}s (world {_recording.Header.World})";
    }

    // ---- transport -----------------------------------------------------------

    private void TogglePlay()
    {
        if (_recording is null) { _notify("replay: load a recording first"); return; }
        if (_playTimer.IsEnabled) Pause();
        else
        {
            if (_position >= _recording.Events.Count) Restart();
            _playTimer.Start();
            PlayLabel = "Pause";
        }
    }

    private void Pause()
    {
        _playTimer.Stop();
        PlayLabel = "Play";
    }

    private void Step()
    {
        if (_recording is null || _position >= _recording.Events.Count) { Pause(); return; }
        Rows.Add(new EventRowViewModel(_recording.Events[_position]));
        _position++;
        UpdatePosition();
        if (_position >= _recording.Events.Count) Pause();
    }

    private void Restart()
    {
        _position = 0;
        Rows.Clear();
        UpdatePosition();
    }

    private void UpdatePosition() =>
        PositionLabel = $"{_position} / {_recording?.Events.Count ?? 0}";

    // ---- analysis (re-run vs current triggers) -------------------------------

    private void Analyze()
    {
        if (_recording is null) { _notify("replay: load a recording first"); return; }
        AutomationEngine engine = _currentEngine();
        var diffs = ReplayAnalyzer.Diffs(_recording, engine);
        if (diffs.Count == 0)
        {
            _notify($"replay analysis: current triggers behave identically across {_recording.Events.Count} recorded events");
            return;
        }
        _notify($"replay analysis: {diffs.Count} line(s) would behave differently under current triggers —");
        foreach (ReplayLineAnalysis d in diffs)
        {
            string added = d.Added.Count > 0 ? "  +" + string.Join(",", d.Added) : "";
            string removed = d.Removed.Count > 0 ? "  -" + string.Join(",", d.Removed) : "";
            _notify($"  \"{d.Line}\"{added}{removed}");
        }
    }

    public void Stop() => _playTimer.Stop();
}

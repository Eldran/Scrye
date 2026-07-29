using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using Scrye.Core.Automation;
using Scrye.Core.Events;

namespace Scrye.App.ViewModels;

/// <summary>
/// Replay transport for the debugger's "Replay" tab. Lists <c>.scryerec</c>
/// recordings, loads one, and lets you scrub it — <see cref="Position"/> drives the
/// visible timeline (Slider + Step/Play), Play paces to the recorded timestamps
/// scaled by <see cref="Speed"/>. The payoff (roadmap #3) is
/// <see cref="Analyze"/>: <see cref="ReplayAnalyzer"/> re-runs the recorded lines
/// through the live world's <em>current</em> rules and lists which lines now differ
/// — inline in <see cref="DiffLines"/>, no reconnect, no side effects.
/// </summary>
public sealed class ReplayViewModel : ViewModelBase
{
    private readonly Func<AutomationEngine> _currentEngine;
    private readonly Action<string> _notify;
    private readonly DispatcherTimer _playTimer;

    private SessionRecording? _recording;

    public ObservableCollection<string> Recordings { get; } = new();
    public ObservableCollection<EventRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> DiffLines { get; } = new();
    public ObservableCollection<double> SpeedOptions { get; } = new() { 0.5, 1.0, 2.0, 4.0 };

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
        StepCommand = new RelayCommand(() => Position += 1);
        RestartCommand = new RelayCommand(() => Position = 0);
        AnalyzeCommand = new RelayCommand(Analyze);

        _playTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _playTimer.Tick += (_, _) => PlayTick();

        Refresh();
    }

    private string? _selectedRecording;
    public string? SelectedRecording { get => _selectedRecording; set => SetField(ref _selectedRecording, value); }

    private EventRowViewModel? _selectedRow;
    public EventRowViewModel? SelectedRow { get => _selectedRow; set => SetField(ref _selectedRow, value); }

    private string _status = "No recording loaded.";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private string _playLabel = "Play";
    public string PlayLabel { get => _playLabel; set => SetField(ref _playLabel, value); }

    private double _speed = 1.0;
    public double Speed { get => _speed; set => SetField(ref _speed, value <= 0 ? 1.0 : value); }

    private bool _hasDiffs;
    public bool HasDiffs { get => _hasDiffs; set => SetField(ref _hasDiffs, value); }

    private int _total;
    public int Total { get => _total; private set => SetField(ref _total, value); }

    private int _position;
    /// <summary>Cursor into the recording (0..Total). Setting it reveals/hides recorded
    /// rows so the list always shows events[0..Position). Bound to the scrub Slider.</summary>
    public int Position
    {
        get => _position;
        set
        {
            int v = _recording is null ? 0 : Math.Clamp(value, 0, Total);
            if (v == _position) return;
            ApplyPosition(v);
            _position = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionLabel));
        }
    }

    public string PositionLabel => $"{_position} / {Total}";

    private void ApplyPosition(int target)
    {
        if (_recording is null) return;
        while (Rows.Count < target) Rows.Add(new EventRowViewModel(_recording.Events[Rows.Count]));
        while (Rows.Count > target) Rows.RemoveAt(Rows.Count - 1);
    }

    // ---- recordings folder ---------------------------------------------------

    private static string RecordingsDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Scrye", "recordings");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void Refresh()
    {
        Recordings.Clear();
        foreach (string f in Directory.EnumerateFiles(RecordingsDir, "*.scryerec"))
            Recordings.Add(Path.GetFileName(f));
    }

    private void Load()
    {
        if (string.IsNullOrEmpty(SelectedRecording)) { _notify("replay: pick a recording first"); return; }
        Pause();
        try { _recording = SessionRecorder.Load(Path.Combine(RecordingsDir, SelectedRecording)); }
        catch (Exception ex) { _notify($"replay: load failed — {ex.Message}"); return; }

        Rows.Clear();
        DiffLines.Clear();
        HasDiffs = false;
        _position = 0;
        Total = _recording.Events.Count;
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(PositionLabel));
        Status = $"{SelectedRecording} — {Total} events, {_recording.Duration.TotalSeconds:0.#}s (world {_recording.Header.World})";
    }

    // ---- transport -----------------------------------------------------------

    private void TogglePlay()
    {
        if (_recording is null) { _notify("replay: load a recording first"); return; }
        if (_playTimer.IsEnabled) { Pause(); return; }
        if (_position >= Total) Position = 0;
        _playTimer.Interval = TimeSpan.FromMilliseconds(150);
        _playTimer.Start();
        PlayLabel = "Pause";
    }

    private void Pause()
    {
        _playTimer.Stop();
        PlayLabel = "Play";
    }

    private void PlayTick()
    {
        if (_recording is null || _position >= Total) { Pause(); return; }
        Position = _position + 1;
        if (_position >= Total) { Pause(); return; }

        // pace the next tick to the recorded gap, scaled by speed and clamped so a long
        // idle stretch doesn't stall playback.
        TimeSpan gap = _recording.Events[_position].TimeUtc - _recording.Events[_position - 1].TimeUtc;
        double ms = gap.TotalMilliseconds / Speed;
        _playTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 1, 2000));
    }

    // ---- analysis (re-run vs current triggers) -------------------------------

    private void Analyze()
    {
        if (_recording is null) { _notify("replay: load a recording first"); return; }
        DiffLines.Clear();
        AutomationEngine engine = _currentEngine();
        var diffs = ReplayAnalyzer.Diffs(_recording, engine);
        if (diffs.Count == 0)
        {
            DiffLines.Add($"No differences — current triggers behave identically across {Total} recorded events.");
        }
        else
        {
            DiffLines.Add($"{diffs.Count} line(s) would behave differently under current triggers:");
            foreach (ReplayLineAnalysis d in diffs)
            {
                string added = d.Added.Count > 0 ? "  +" + string.Join(",", d.Added) : "";
                string removed = d.Removed.Count > 0 ? "  -" + string.Join(",", d.Removed) : "";
                DiffLines.Add($"\"{d.Line}\"{added}{removed}");
            }
        }
        HasDiffs = true;
    }

    public void Stop() => _playTimer.Stop();
}

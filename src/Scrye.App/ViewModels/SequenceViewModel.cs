using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Scrye.Core.Automation;

namespace Scrye.App.ViewModels;

/// <summary>
/// Drives the sequence strip: a picker and a Run button while nothing is running, and the
/// live status with Pause/Resume/Stop once something is. Fed by the session's
/// <c>SequenceStatusChanged</c> (which fires on the loop thread, so updates are marshalled to
/// the UI thread); the four commands route back to the session.
///
/// <para>Starting a sequence used to be the one thing here with no UI at all — you could
/// define one in Settings and then had no way to run it except typing <c>.seq &lt;name&gt;</c>.
/// The transport half already existed, so the picker joins it rather than becoming a panel of
/// its own.</para>
/// </summary>
public sealed class SequenceViewModel : ViewModelBase
{
    private readonly Action<string> _run;

    public SequenceViewModel(Action pause, Action resume, Action stop, Action<string> run)
    {
        PauseCommand = new RelayCommand(pause);
        ResumeCommand = new RelayCommand(resume);
        StopCommand = new RelayCommand(stop);
        _run = run;
        RunCommand = new RelayCommand(RunSelected, () => !string.IsNullOrEmpty(Selected));
    }

    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RunCommand { get; }

    /// <summary>Sequence names this world has, for the picker.</summary>
    public ObservableCollection<string> Available { get; } = new();

    private string? _selected;
    public string? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) OnPropertyChanged(nameof(CanRun)); }
    }

    public bool CanRun => !string.IsNullOrEmpty(_selected);

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetField(ref _isActive, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    /// <summary>Picker shown only when nothing is running; the transport replaces it.</summary>
    public bool IsIdle => !_isActive;

    /// <summary>The strip is worth its row when something is running, or when there is
    /// something to run. A world with no sequences never sees it.</summary>
    public bool IsVisible => _isActive || Available.Count > 0;

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    /// <summary>Replace the picker's contents — called at load and whenever the profile is
    /// re-resolved, so a sequence added in Settings appears without a reconnect. Keeps the
    /// current selection when it still exists, otherwise selects the first.</summary>
    public void SetAvailable(IEnumerable<string> names)
    {
        void Apply()
        {
            string? keep = Selected;
            Available.Clear();
            foreach (string n in names) Available.Add(n);
            Selected = keep is not null && Available.Contains(keep) ? keep
                     : Available.Count > 0 ? Available[0] : null;
            OnPropertyChanged(nameof(IsVisible));
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void RunSelected()
    {
        if (!string.IsNullOrEmpty(_selected)) _run(_selected!);
    }

    public void Update(SequenceStatus s)
    {
        void Apply()
        {
            IsActive = s.Active;
            if (!s.Active) { StatusText = ""; return; }
            string name = string.IsNullOrEmpty(s.Name) ? "sequence" : s.Name;
            string paused = s.State == SequenceState.Paused ? "  (paused)" : "";
            StatusText = $"{name}  {s.Sent}/{s.Total}  →  {s.Command}{paused}";
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }
}

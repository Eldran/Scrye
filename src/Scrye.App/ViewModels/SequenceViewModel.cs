using System;
using Avalonia.Threading;
using Scrye.Core.Automation;

namespace Scrye.App.ViewModels;

/// <summary>
/// Drives the running-sequence status strip. Fed by the session's
/// <c>SequenceStatusChanged</c> (which fires on the loop thread, so updates are
/// marshalled to the UI thread). Pause/Resume/Stop route back to the session.
/// </summary>
public sealed class SequenceViewModel : ViewModelBase
{
    public SequenceViewModel(Action pause, Action resume, Action stop)
    {
        PauseCommand = new RelayCommand(pause);
        ResumeCommand = new RelayCommand(resume);
        StopCommand = new RelayCommand(stop);
    }

    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand StopCommand { get; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

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

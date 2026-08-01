using System;
using Scrye.Core.Text;

namespace Scrye.App.ViewModels;

/// <summary>Where a capture pane lives: tabbed under the output, tabbed on the
/// right side, or floating in its own window.</summary>
public enum PaneDock { Bottom, Right, Floating }

/// <summary>
/// One named capture pane (created by a trigger's "capture to pane" or
/// <c>scrye.capture</c>). Owns its own scrollback; renders through the same
/// <c>TerminalPane</c> control wherever it is docked. <see cref="Unread"/> counts
/// lines that arrived while the tab wasn't selected — the badge on the tab header.
/// The move/close commands drive the tab's right-click menu; the world wires
/// <see cref="MoveRequested"/>/<see cref="CloseRequested"/> and does the actual work.
/// </summary>
public sealed class CapturePaneViewModel : ViewModelBase
{
    public string Name { get; }
    public ScrollbackBuffer Buffer { get; } = new();

    /// <summary>Font settings mirrored from the world so the pane matches the main output.</summary>
    public Avalonia.Media.FontFamily FontFamily { get; }
    public double FontSize { get; }

    public Action<CapturePaneViewModel, PaneDock>? MoveRequested { get; set; }
    public Action<CapturePaneViewModel>? CloseRequested { get; set; }

    public RelayCommand MoveBottomCommand { get; }
    public RelayCommand MoveRightCommand { get; }
    public RelayCommand FloatCommand { get; }
    public RelayCommand CloseCommand { get; }

    public CapturePaneViewModel(string name, Avalonia.Media.FontFamily fontFamily, double fontSize)
    {
        Name = name;
        FontFamily = fontFamily;
        FontSize = fontSize;
        MoveBottomCommand = new RelayCommand(() => MoveRequested?.Invoke(this, PaneDock.Bottom));
        MoveRightCommand = new RelayCommand(() => MoveRequested?.Invoke(this, PaneDock.Right));
        FloatCommand = new RelayCommand(() => MoveRequested?.Invoke(this, PaneDock.Floating));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this));
    }

    private PaneDock _dock = PaneDock.Bottom;
    /// <summary>Current zone; set by the world's PlacePane (persisted in the layout).</summary>
    public PaneDock Dock { get => _dock; set => SetField(ref _dock, value); }

    private int _unread;
    public int Unread
    {
        get => _unread;
        set
        {
            if (SetField(ref _unread, value)) OnPropertyChanged(nameof(HasUnread));
        }
    }

    public bool HasUnread => _unread > 0;

    private bool _showTimestamps;
    /// <summary>Mirrors the world's timestamp toggle (".ts").</summary>
    public bool ShowTimestamps { get => _showTimestamps; set => SetField(ref _showTimestamps, value); }
}

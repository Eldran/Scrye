using Scrye.Core.Text;

namespace Scrye.App.ViewModels;

/// <summary>
/// One named capture pane (created by a trigger's "capture to pane" the first time
/// it routes a line). Owns its own scrollback; renders in a tab under the main
/// output through the same <c>TerminalPane</c> control. <see cref="Unread"/> counts
/// lines that arrived while the tab wasn't selected — the badge on the tab header.
/// </summary>
public sealed class CapturePaneViewModel : ViewModelBase
{
    public string Name { get; }
    public ScrollbackBuffer Buffer { get; } = new();

    /// <summary>Font settings mirrored from the world so the pane matches the main output.</summary>
    public Avalonia.Media.FontFamily FontFamily { get; }
    public double FontSize { get; }

    public CapturePaneViewModel(string name, Avalonia.Media.FontFamily fontFamily, double fontSize)
    {
        Name = name;
        FontFamily = fontFamily;
        FontSize = fontSize;
    }

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
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Scrye.Core.Text;

namespace Scrye.App.Controls;

/// <summary>
/// Split-scrollback terminal: a scrollable history <see cref="OutputView"/> plus,
/// while the user is scrolled up, a live tail pane pinned underneath so new output
/// stays visible during reading. A "new lines" chip overlays the history and jumps
/// back to the bottom. Composed entirely in code — one control to drop into XAML,
/// forwarding the same properties/events OutputView exposes.
/// </summary>
public class TerminalPane : Grid
{
    public static readonly StyledProperty<ScrollbackBuffer?> SourceProperty =
        AvaloniaProperty.Register<TerminalPane, ScrollbackBuffer?>(nameof(Source));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalPane, FontFamily>(
            nameof(FontFamily), new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalPane, double>(nameof(FontSize), 14d);

    public static readonly StyledProperty<string?> SearchTermProperty =
        AvaloniaProperty.Register<TerminalPane, string?>(nameof(SearchTerm));

    public static readonly StyledProperty<int> ActiveMatchLineProperty =
        AvaloniaProperty.Register<TerminalPane, int>(nameof(ActiveMatchLine), -1);

    public static readonly StyledProperty<bool> MatchCaseProperty =
        AvaloniaProperty.Register<TerminalPane, bool>(nameof(MatchCase));

    public static readonly StyledProperty<bool> ShowTimestampsProperty =
        AvaloniaProperty.Register<TerminalPane, bool>(nameof(ShowTimestamps));

    /// <summary>How many lines tall the live-tail pane is while split.</summary>
    public static readonly StyledProperty<int> TailLinesProperty =
        AvaloniaProperty.Register<TerminalPane, int>(nameof(TailLines), 5);

    public ScrollbackBuffer? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public string? SearchTerm { get => GetValue(SearchTermProperty); set => SetValue(SearchTermProperty, value); }
    public int ActiveMatchLine { get => GetValue(ActiveMatchLineProperty); set => SetValue(ActiveMatchLineProperty, value); }
    public bool MatchCase { get => GetValue(MatchCaseProperty); set => SetValue(MatchCaseProperty, value); }
    public bool ShowTimestamps { get => GetValue(ShowTimestampsProperty); set => SetValue(ShowTimestampsProperty, value); }
    public int TailLines { get => GetValue(TailLinesProperty); set => SetValue(TailLinesProperty, value); }

    /// <summary>Forwarded from both output views (sender is this pane, so the
    /// world DataContext resolves the same way as before).</summary>
    public event EventHandler<CommandLinkClickedEventArgs>? CommandLinkClicked;

    private static readonly IImmutableBrush SeparatorBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IImmutableBrush ChipBackground = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x1A, 0x2A, 0x33));

    private readonly OutputView _history = new();
    private readonly OutputView _tail = new();
    private readonly ScrollViewer _historyScroll;
    private readonly ScrollViewer _tailScroll;
    private readonly Border _tailBorder;
    private readonly Button _chip;

    public TerminalPane()
    {
        RowDefinitions = new RowDefinitions("*,Auto");

        _historyScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _history,
        };
        SetRow(_historyScroll, 0);
        Children.Add(_historyScroll);

        _chip = new Button
        {
            Content = "▼ back to bottom",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 22, 10),
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(12),
            Background = ChipBackground,
            IsVisible = false,
        };
        _chip.Click += (_, _) => _history.ScrollToEnd();
        SetRow(_chip, 0);
        Children.Add(_chip);

        _tailScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            Content = _tail,
        };
        // the tail is a live feed, not a second scroll surface — swallow wheel input
        _tailScroll.AddHandler(PointerWheelChangedEvent,
            (object? _, PointerWheelEventArgs e) => e.Handled = true,
            RoutingStrategies.Tunnel);
        _tailBorder = new Border
        {
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _tailScroll,
            IsVisible = false,
        };
        SetRow(_tailBorder, 1);
        Children.Add(_tailBorder);

        _history.CommandLinkClicked += (_, e) => CommandLinkClicked?.Invoke(this, e);
        _tail.CommandLinkClicked += (_, e) => CommandLinkClicked?.Invoke(this, e);

        _history.PropertyChanged += (_, e) =>
        {
            if (e.Property == OutputView.IsFollowingTailProperty ||
                e.Property == OutputView.PendingLinesProperty)
                UpdateSplit();
        };

        UpdateTailHeight();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            ScrollbackBuffer? src = change.GetNewValue<ScrollbackBuffer?>();
            _history.Source = src;
            _tail.Source = src;
        }
        else if (change.Property == FontFamilyProperty)
        {
            FontFamily f = change.GetNewValue<FontFamily>();
            _history.FontFamily = f;
            _tail.FontFamily = f;
        }
        else if (change.Property == FontSizeProperty)
        {
            double s = change.GetNewValue<double>();
            _history.FontSize = s;
            _tail.FontSize = s;
            UpdateTailHeight();
        }
        else if (change.Property == SearchTermProperty)
        {
            // highlight in the history view only; the tail stays a clean live feed
            _history.SearchTerm = change.GetNewValue<string?>();
        }
        else if (change.Property == ActiveMatchLineProperty)
        {
            _history.ActiveMatchLine = change.GetNewValue<int>();
        }
        else if (change.Property == MatchCaseProperty)
        {
            _history.MatchCase = change.GetNewValue<bool>();
        }
        else if (change.Property == ShowTimestampsProperty)
        {
            bool ts = change.GetNewValue<bool>();
            _history.ShowTimestamps = ts;
            _tail.ShowTimestamps = ts;
        }
        else if (change.Property == TailLinesProperty)
        {
            UpdateTailHeight();
        }
    }

    private void UpdateTailHeight() =>
        _tailScroll.Height = Math.Ceiling(FontSize * 1.4) * Math.Max(2, TailLines) + 4;

    private void UpdateSplit()
    {
        bool split = !_history.IsFollowingTail;
        if (_tailBorder.IsVisible != split)
        {
            _tailBorder.IsVisible = split;
            if (split)
            {
                // freshly revealed: snap the tail to the newest line once it has laid out
                Dispatcher.UIThread.Post(_tail.ScrollToEnd, DispatcherPriority.Background);
            }
        }

        int pending = _history.PendingLines;
        _chip.IsVisible = split;
        _chip.Content = pending > 0
            ? $"▼ {pending} new line{(pending == 1 ? "" : "s")}"
            : "▼ back to bottom";
    }
}

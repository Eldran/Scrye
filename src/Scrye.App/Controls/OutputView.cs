using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Scrye.Core.Text;

namespace Scrye.App.Controls;

/// <summary>A clicked MXP command link: the command, and whether to put it in the
/// input box (SEND PROMPT) instead of sending immediately.</summary>
public sealed class CommandLinkClickedEventArgs : EventArgs
{
    public string Command { get; }
    public bool Prompt { get; }
    public CommandLinkClickedEventArgs(string command, bool prompt) { Command = command; Prompt = prompt; }
}

/// <summary>
/// Virtualized, colour-aware scrollback renderer. Lives inside a ScrollViewer:
/// it reports its full content height via <see cref="MeasureOverride"/> (so the
/// ScrollViewer supplies a scrollbar), then draws ONLY the lines inside the
/// current viewport each render — reading the ScrollViewer's offset. Auto-follows
/// the tail unless the user has scrolled up. Uses monospace cell metrics for
/// advance, so alignment holds. (Selection and clickable links: a later pass.)
/// </summary>
public class OutputView : Control
{
    public static readonly DirectProperty<OutputView, ScrollbackBuffer?> SourceProperty =
        AvaloniaProperty.RegisterDirect<OutputView, ScrollbackBuffer?>(
            nameof(Source), o => o.Source, (o, v) => o.Source = v);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<OutputView, FontFamily>(
            nameof(FontFamily), new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<OutputView, double>(nameof(FontSize), 14d);

    /// <summary>Case-insensitive term to highlight across the visible band (find-in-scrollback).</summary>
    public static readonly StyledProperty<string?> SearchTermProperty =
        AvaloniaProperty.Register<OutputView, string?>(nameof(SearchTerm));

    /// <summary>Line index to scroll into view (the current find match); -1 = none.</summary>
    public static readonly StyledProperty<int> ActiveMatchLineProperty =
        AvaloniaProperty.Register<OutputView, int>(nameof(ActiveMatchLine), -1);

    private ScrollbackBuffer? _source;
    private ScrollViewer? _scrollViewer;
    private double _lineHeight = 16;
    private double _charWidth = 8;
    private bool _stickToBottom = true;

    // text selection, in (line, column) content coordinates
    private (int line, int col)? _selAnchor;
    private (int line, int col)? _selCaret;
    private bool _selecting;

    // clickable links (MXP SEND/A + auto-detected URLs)
    private (int line, LinkSpan span)? _pressedLink;   // link under the pointer at press time
    private bool _hoveringLink;

    private readonly Dictionary<uint, IImmutableBrush> _brushCache = new();
    private static readonly IImmutableBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1A));
    private static readonly IImmutableBrush SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0x35, 0xC4, 0xD6));
    private static readonly IImmutableBrush MatchBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0xF0, 0xC0, 0x40));
    private static readonly IImmutableBrush ActiveMatchBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xAA, 0xF0, 0xC0, 0x40));
    private static readonly IImmutableBrush LinkBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x35, 0xC4, 0xD6));
    private static readonly Pen LinkPen = new(LinkBrush);

    /// <summary>A command link (MXP SEND) was clicked. URL links open in the browser
    /// directly and do not raise this.</summary>
    public event EventHandler<CommandLinkClickedEventArgs>? CommandLinkClicked;

    public OutputView()
    {
        Focusable = true;   // needed so Ctrl+C reaches OnKeyDown
    }

    static OutputView()
    {
        AffectsMeasure<OutputView>(FontFamilyProperty, FontSizeProperty);
        AffectsRender<OutputView>(FontFamilyProperty, FontSizeProperty, SearchTermProperty, ActiveMatchLineProperty);
    }

    public string? SearchTerm { get => GetValue(SearchTermProperty); set => SetValue(SearchTermProperty, value); }
    public int ActiveMatchLine { get => GetValue(ActiveMatchLineProperty); set => SetValue(ActiveMatchLineProperty, value); }

    public ScrollbackBuffer? Source
    {
        get => _source;
        set
        {
            ScrollbackBuffer? old = _source;
            if (!SetAndRaise(SourceProperty, ref _source, value)) return;
            if (old is not null) old.Changed -= OnSourceChanged;
            if (_source is not null) _source.Changed += OnSourceChanged;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer is not null)
            _scrollViewer.PropertyChanged += ScrollViewerOnPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scrollViewer is not null)
            _scrollViewer.PropertyChanged -= ScrollViewerOnPropertyChanged;
        _scrollViewer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ScrollViewerOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty)
        {
            _stickToBottom = IsAtBottom();
            InvalidateVisual();   // redraw the newly-visible band
        }
    }

    private double ExtentHeight => (_source?.Count ?? 0) * _lineHeight;

    private bool IsAtBottom()
    {
        if (_scrollViewer is null) return true;
        double vp = _scrollViewer.Viewport.Height;
        return _scrollViewer.Offset.Y >= ExtentHeight - vp - 2;
    }

    private void OnSourceChanged()
    {
        InvalidateMeasure();   // extent grew
        if (_stickToBottom)
        {
            // scroll to the tail once the new extent has been laid out
            Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Background);
        }
        InvalidateVisual();
    }

    private void ScrollToBottom()
    {
        if (_scrollViewer is null) return;
        double target = Math.Max(0, ExtentHeight - _scrollViewer.Viewport.Height);
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, target);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActiveMatchLineProperty)
            ScrollLineIntoView(change.GetNewValue<int>());
    }

    /// <summary>Centre a line in the viewport (used to jump to a find match).</summary>
    private void ScrollLineIntoView(int line)
    {
        if (_scrollViewer is null || _lineHeight <= 0 || line < 0) return;
        double vp = _scrollViewer.Viewport.Height;
        double target = Math.Clamp(line * _lineHeight - vp / 2, 0, Math.Max(0, ExtentHeight - vp));
        _stickToBottom = false;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, target);
        InvalidateVisual();
    }

    // ---- selection + copy ----------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var cell = HitTest(e.GetPosition(this));

        // pressing on a link starts a click, not a selection
        LinkSpan? link = LinkAt(cell);
        if (link is not null)
        {
            _pressedLink = (cell.line, link.Value);
            e.Pointer.Capture(this);
            Focus();
            return;
        }

        _pressedLink = null;
        _selAnchor = _selCaret = cell;
        _selecting = true;
        e.Pointer.Capture(this);
        Focus();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _selCaret = HitTest(e.GetPosition(this));
            InvalidateVisual();
            return;
        }
        // hand cursor over links
        bool over = LinkAt(HitTest(e.GetPosition(this))) is not null;
        if (over != _hoveringLink)
        {
            _hoveringLink = over;
            Cursor = over ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting) { _selecting = false; e.Pointer.Capture(null); return; }
        if (_pressedLink is not (int line, LinkSpan span)) return;
        _pressedLink = null;
        e.Pointer.Capture(null);

        // activate only when released over the same link
        var cell = HitTest(e.GetPosition(this));
        if (cell.line != line || !span.Contains(cell.col)) return;
        ActivateLink(span.Link);
    }

    private void ActivateLink(LinkInfo link)
    {
        if (link.IsUrl)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(link.Action) { UseShellExecute = true });
            }
            catch { /* no browser / malformed url — ignore */ }
            return;
        }
        // multi-command SEND ("cmd1|cmd2"): first command is the default action
        string command = link.Action.Split('|')[0];
        CommandLinkClicked?.Invoke(this, new CommandLinkClickedEventArgs(command, link.Prompt));
    }

    private LinkSpan? LinkAt((int line, int col) cell)
    {
        if (_source is null || cell.line < 0 || cell.line >= _source.Count) return null;
        foreach (LinkSpan span in _source[cell.line].Links)
            if (span.Contains(cell.col)) return span;
        return null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            string? text = SelectedText();
            if (!string.IsNullOrEmpty(text))
            {
                TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            _selAnchor = _selCaret = null;
            InvalidateVisual();
        }
    }

    private (int line, int col) HitTest(Point p)
    {
        int line = _lineHeight > 0 ? (int)(p.Y / _lineHeight) : 0;
        int maxLine = Math.Max(0, (_source?.Count ?? 1) - 1);
        line = Math.Clamp(line, 0, maxLine);
        int col = _charWidth > 0 ? (int)Math.Round((p.X - 2) / _charWidth) : 0;
        return (line, Math.Max(0, col));
    }

    private static ((int line, int col) a, (int line, int col) b) Ordered((int line, int col) x, (int line, int col) y) =>
        (x.line < y.line || (x.line == y.line && x.col <= y.col)) ? (x, y) : (y, x);

    private string? SelectedText()
    {
        if (_selAnchor is null || _selCaret is null || _source is null) return null;
        var (a, b) = Ordered(_selAnchor.Value, _selCaret.Value);
        if (a == b) return null;
        var sb = new StringBuilder();
        for (int i = a.line; i <= b.line && i < _source.Count; i++)
        {
            string txt = _source[i].PlainText;
            int s = i == a.line ? Math.Min(a.col, txt.Length) : 0;
            int e = i == b.line ? Math.Min(b.col, txt.Length) : txt.Length;
            if (e > s) sb.Append(txt, s, e - s);
            if (i < b.line) sb.Append('\n');
        }
        return sb.ToString();
    }

    private void MeasureMetrics()
    {
        var ft = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), FontSize, Brushes.White);
        _charWidth = ft.Width <= 0 ? FontSize * 0.6 : ft.Width;
        _lineHeight = Math.Ceiling(ft.Height <= 0 ? FontSize * 1.3 : ft.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureMetrics();
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, ExtentHeight);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        double offsetY = _scrollViewer?.Offset.Y ?? 0;
        double viewportH = _scrollViewer?.Viewport.Height ?? bounds.Height;

        // paint the visible band (content coordinates) with the terminal background
        context.FillRectangle(Background, new Rect(0, offsetY, bounds.Width, viewportH));

        ScrollbackBuffer? src = _source;
        if (src is null || src.Count == 0 || _lineHeight <= 0) return;

        int first = Math.Max(0, (int)(offsetY / _lineHeight));
        int last = Math.Min(src.Count - 1, (int)((offsetY + viewportH) / _lineHeight));

        (int line, int col)? selA = null, selB = null;
        if (_selAnchor is not null && _selCaret is not null && _selAnchor.Value != _selCaret.Value)
        {
            var (a, b) = Ordered(_selAnchor.Value, _selCaret.Value);
            selA = a; selB = b;
        }
        string? term = SearchTerm;

        for (int i = first; i <= last; i++)
        {
            double y = i * _lineHeight;
            string plain = src[i].PlainText;
            if (selA is not null) DrawSelection(context, i, y, plain.Length, selA.Value, selB!.Value);
            if (!string.IsNullOrEmpty(term)) DrawMatches(context, plain, term!, y, i == ActiveMatchLine);
            DrawLine(context, src[i], y);
            DrawLinks(context, src[i], y);
        }
    }

    /// <summary>Cyan underline beneath every clickable region (MXP links + detected URLs).</summary>
    private void DrawLinks(DrawingContext ctx, Line line, double y)
    {
        IReadOnlyList<LinkSpan> links = line.Links;
        if (links.Count == 0) return;
        double uy = y + _lineHeight - 1;
        foreach (LinkSpan span in links)
        {
            double x = 2 + span.Start * _charWidth;
            ctx.DrawLine(LinkPen, new Point(x, uy), new Point(x + span.Length * _charWidth, uy));
        }
    }

    private void DrawSelection(DrawingContext ctx, int line, double y, int len,
                              (int line, int col) a, (int line, int col) b)
    {
        if (line < a.line || line > b.line) return;
        int start = line == a.line ? a.col : 0;
        int end = line == b.line ? b.col : len;
        if (end > len) end = len;
        if (line == a.line && line == b.line && start == end) end = start + 1;   // caret-ish
        if (end <= start) return;
        double x = 2 + start * _charWidth;
        ctx.FillRectangle(SelectionBrush, new Rect(x, y, (end - start) * _charWidth, _lineHeight));
    }

    private void DrawMatches(DrawingContext ctx, string plain, string term, double y, bool active)
    {
        int from = 0;
        while (from <= plain.Length - term.Length)
        {
            int idx = plain.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            double x = 2 + idx * _charWidth;
            ctx.FillRectangle(active ? ActiveMatchBrush : MatchBrush,
                              new Rect(x, y, term.Length * _charWidth, _lineHeight));
            from = idx + term.Length;
        }
    }

    private void DrawLine(DrawingContext context, Line line, double y)
    {
        double x = 2;
        foreach (StyledRun run in line.Runs)
        {
            Rgb fore = run.Fore, back = run.Back;
            if ((run.Flags & RunFlags.Inverse) != 0) (fore, back) = (back, fore);

            double w = run.Text.Length * _charWidth;

            if (!back.Equals(Rgb.DefaultBack))
                context.FillRectangle(BrushFor(back), new Rect(x, y, w, _lineHeight));

            var weight = (run.Flags & RunFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal;
            var slant = (run.Flags & RunFlags.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;
            var typeface = new Typeface(FontFamily, slant, weight);

            var ft = new FormattedText(run.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, FontSize, BrushFor(fore));
            context.DrawText(ft, new Point(x, y));

            if ((run.Flags & RunFlags.Underline) != 0)
            {
                double uy = y + _lineHeight - 1;
                context.DrawLine(new Pen(BrushFor(fore)), new Point(x, uy), new Point(x + w, uy));
            }

            x += w;
        }
    }

    private IImmutableBrush BrushFor(Rgb c)
    {
        uint key = (uint)((c.R << 16) | (c.G << 8) | c.B);
        if (!_brushCache.TryGetValue(key, out IImmutableBrush? brush))
        {
            brush = new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            _brushCache[key] = brush;
        }
        return brush;
    }
}

using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform; // Avalonia 12: SetTextAsync is now an extension method (ClipboardExtensions)
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

    /// <summary>When true, search highlighting is case-sensitive.</summary>
    public static readonly StyledProperty<bool> MatchCaseProperty =
        AvaloniaProperty.Register<OutputView, bool>(nameof(MatchCase));

    /// <summary>Show a HH:mm:ss gutter (local time) before each line.</summary>
    public static readonly StyledProperty<bool> ShowTimestampsProperty =
        AvaloniaProperty.Register<OutputView, bool>(nameof(ShowTimestamps));

    /// <summary>True while the view is glued to the newest line (read-only:
    /// scrolling up clears it, <see cref="ScrollToEnd"/> or scrolling back down sets it).
    /// The split-scrollback pane watches this to decide when to show the live tail.</summary>
    public static readonly DirectProperty<OutputView, bool> IsFollowingTailProperty =
        AvaloniaProperty.RegisterDirect<OutputView, bool>(
            nameof(IsFollowingTail), o => o.IsFollowingTail);

    /// <summary>Lines that arrived while scrolled up (read-only; resets on return to the tail).</summary>
    public static readonly DirectProperty<OutputView, int> PendingLinesProperty =
        AvaloniaProperty.RegisterDirect<OutputView, int>(
            nameof(PendingLines), o => o.PendingLines);

    private ScrollbackBuffer? _source;
    private ScrollViewer? _scrollViewer;
    private double _lineHeight = 16;
    private double _charWidth = 8;
    private bool _stickToBottom = true;
    private int _pendingLines;
    private int _lastCount;

    // text selection, in (line, column) content coordinates
    private (int line, int col)? _selAnchor;
    private (int line, int col)? _selCaret;
    private bool _selecting;

    // clickable links (MXP SEND/A + auto-detected URLs)
    private (int line, LinkSpan span)? _pressedLink;   // link under the pointer at press time
    private bool _hoveringLink;

    private readonly Dictionary<uint, IImmutableBrush> _brushCache = new();
    private readonly Dictionary<(uint fore, uint back), Rgb> _readableCache = new();
    // The terminal surface follows the active scheme (per-scheme since "void"; always
    // dark). Cached per colour; ThemeService.Changed repaints us when the scheme moves.
    private Avalonia.Media.Color _surfaceColor;
    private IImmutableBrush _surfaceBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0C));
    private Rgb _surfaceRgb = new(0x08, 0x0A, 0x0C);

    private IImmutableBrush Background
    {
        get
        {
            Avalonia.Media.Color c = Services.ThemeService.Current.OutputSurface;
            if (c != _surfaceColor)
            {
                _surfaceColor = c;
                _surfaceBrush = new ImmutableSolidColorBrush(c);
                _surfaceRgb = new Rgb(c.R, c.G, c.B);
                _readableCache.Clear();   // the lift floor moved with the surface
            }
            return _surfaceBrush;
        }
    }

    /// <summary>The colour behind default-background text (matches <see cref="Background"/>).</summary>
    private Rgb SurfaceRgb { get { _ = Background; return _surfaceRgb; } }
    private const double MinContrast = 3.0;                          // readability floor (WCAG contrast ratio) for glyph vs surface
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
        BuildContextMenu();
    }

    static OutputView()
    {
        AffectsMeasure<OutputView>(FontFamilyProperty, FontSizeProperty);
        AffectsRender<OutputView>(FontFamilyProperty, FontSizeProperty, SearchTermProperty,
                                  ActiveMatchLineProperty, MatchCaseProperty, ShowTimestampsProperty);
    }

    public string? SearchTerm { get => GetValue(SearchTermProperty); set => SetValue(SearchTermProperty, value); }
    public int ActiveMatchLine { get => GetValue(ActiveMatchLineProperty); set => SetValue(ActiveMatchLineProperty, value); }
    public bool MatchCase { get => GetValue(MatchCaseProperty); set => SetValue(MatchCaseProperty, value); }
    public bool ShowTimestamps { get => GetValue(ShowTimestampsProperty); set => SetValue(ShowTimestampsProperty, value); }

    /// <summary>Left origin of line text: 2px pad plus the timestamp gutter when shown
    /// ("HH:mm:ss" = 8 chars + a space of separation).</summary>
    private double OriginX => 2 + (ShowTimestamps ? 9 * _charWidth : 0);

    public bool IsFollowingTail => _stickToBottom;
    public int PendingLines => _pendingLines;

    private void SetFollowing(bool value)
    {
        if (_stickToBottom == value) return;
        bool old = _stickToBottom;
        _stickToBottom = value;
        RaisePropertyChanged(IsFollowingTailProperty, old, value);
        if (value) SetPendingLines(0);
    }

    private void SetPendingLines(int value)
    {
        if (_pendingLines == value) return;
        int old = _pendingLines;
        _pendingLines = value;
        RaisePropertyChanged(PendingLinesProperty, old, value);
    }

    public ScrollbackBuffer? Source
    {
        get => _source;
        set
        {
            ScrollbackBuffer? old = _source;
            if (!SetAndRaise(SourceProperty, ref _source, value)) return;
            if (old is not null) old.Changed -= OnSourceChanged;
            if (_source is not null) _source.Changed += OnSourceChanged;
            _lastCount = _source?.Count ?? 0;
            SetPendingLines(0);
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
        // scheme switches must repaint the custom-drawn surface even with no new output
        Services.ThemeService.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Services.ThemeService.Changed -= InvalidateVisual;
        if (_scrollViewer is not null)
            _scrollViewer.PropertyChanged -= ScrollViewerOnPropertyChanged;
        _scrollViewer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ScrollViewerOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty)
        {
            SetFollowing(IsAtBottom());
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
        int count = _source?.Count ?? 0;
        int delta = count - _lastCount;
        _lastCount = count;

        InvalidateMeasure();   // extent grew
        if (_stickToBottom)
        {
            // scroll to the tail once the new extent has been laid out
            Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Background);
        }
        else
        {
            // scrolled up: count what streams past below (cleared buffer resets)
            SetPendingLines(delta > 0 ? _pendingLines + delta : 0);
        }
        InvalidateVisual();
    }

    /// <summary>Jump back to the newest line and resume following the tail.</summary>
    public void ScrollToEnd()
    {
        SetFollowing(true);
        ScrollToBottom();
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
        SetFollowing(false);
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
            CopySelection(CopyFormat.Plain);
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _selAnchor = _selCaret = null;
            InvalidateVisual();
        }
    }

    // ---- copy in three formats ------------------------------------------------

    private enum CopyFormat { Plain, Ansi, Html }

    private void BuildContextMenu()
    {
        MenuItem Item(string header, string? gesture, Action action)
        {
            var mi = new MenuItem { Header = header };
            if (gesture is not null) mi.InputGesture = KeyGesture.Parse(gesture);
            mi.Click += (_, _) => action();
            return mi;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy", "Ctrl+C", () => CopySelection(CopyFormat.Plain)));
        menu.Items.Add(Item("Copy as ANSI", null, () => CopySelection(CopyFormat.Ansi)));
        menu.Items.Add(Item("Copy as HTML", null, () => CopySelection(CopyFormat.Html)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select all", "Ctrl+A", SelectAll));
        ContextMenu = menu;
    }

    private void SelectAll()
    {
        if (_source is null || _source.Count == 0) return;
        _selAnchor = (0, 0);
        _selCaret = (_source.Count - 1, _source[_source.Count - 1].PlainText.Length);
        InvalidateVisual();
    }

    /// <summary>The selection as per-line run slices, ready for the exporters.</summary>
    private List<IReadOnlyList<StyledRun>>? SelectedSlices()
    {
        if (_selAnchor is null || _selCaret is null || _source is null) return null;
        var (a, b) = Ordered(_selAnchor.Value, _selCaret.Value);
        if (a == b) return null;
        var slices = new List<IReadOnlyList<StyledRun>>();
        for (int i = a.line; i <= b.line && i < _source.Count; i++)
        {
            Line line = _source[i];
            int s = i == a.line ? a.col : 0;
            int e = i == b.line ? b.col : line.PlainText.Length;
            slices.Add(TextExporter.Slice(line, s, e));
        }
        return slices;
    }

    private void CopySelection(CopyFormat format)
    {
        List<IReadOnlyList<StyledRun>>? slices = SelectedSlices();
        if (slices is null || slices.Count == 0) return;
        string text = format switch
        {
            CopyFormat.Ansi => TextExporter.ToAnsi(slices),
            CopyFormat.Html => TextExporter.ToHtml(slices),
            _ => TextExporter.ToPlain(slices),
        };
        if (text.Length > 0)
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private (int line, int col) HitTest(Point p)
    {
        int line = _lineHeight > 0 ? (int)(p.Y / _lineHeight) : 0;
        int maxLine = Math.Max(0, (_source?.Count ?? 1) - 1);
        line = Math.Clamp(line, 0, maxLine);
        int col = _charWidth > 0 ? (int)Math.Round((p.X - OriginX) / _charWidth) : 0;
        return (line, Math.Max(0, col));
    }

    private static ((int line, int col) a, (int line, int col) b) Ordered((int line, int col) x, (int line, int col) y) =>
        (x.line < y.line || (x.line == y.line && x.col <= y.col)) ? (x, y) : (y, x);

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

        bool stamps = ShowTimestamps;
        for (int i = first; i <= last; i++)
        {
            double y = i * _lineHeight;
            string plain = src[i].PlainText;
            if (selA is not null) DrawSelection(context, i, y, plain.Length, selA.Value, selB!.Value);
            if (!string.IsNullOrEmpty(term)) DrawMatches(context, plain, term!, y, i == ActiveMatchLine);
            if (stamps && plain.Length > 0 && !src[i].Continuation) DrawTimestamp(context, src[i], y);
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
            double x = OriginX + span.Start * _charWidth;
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
        double x = OriginX + start * _charWidth;
        ctx.FillRectangle(SelectionBrush, new Rect(x, y, (end - start) * _charWidth, _lineHeight));
    }

    private void DrawMatches(DrawingContext ctx, string plain, string term, double y, bool active)
    {
        StringComparison cmp = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int from = 0;
        while (from <= plain.Length - term.Length)
        {
            int idx = plain.IndexOf(term, from, cmp);
            if (idx < 0) break;
            double x = OriginX + idx * _charWidth;
            ctx.FillRectangle(active ? ActiveMatchBrush : MatchBrush,
                              new Rect(x, y, term.Length * _charWidth, _lineHeight));
            from = idx + term.Length;
        }
    }

    private static readonly IImmutableBrush TimestampBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0x6E, 0x76, 0x81));

    /// <summary>Dim HH:mm:ss (local time) in the gutter.</summary>
    private void DrawTimestamp(DrawingContext context, Line line, double y)
    {
        var ft = new FormattedText(line.ReceivedUtc.ToLocalTime().ToString("HH:mm:ss"),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), FontSize, TimestampBrush);
        context.DrawText(ft, new Point(2, y));
    }

    private void DrawLine(DrawingContext context, Line line, double y)
    {
        double x = OriginX;
        foreach (StyledRun run in line.Runs)
        {
            Rgb fore = run.Fore, back = run.Back;
            if ((run.Flags & RunFlags.Inverse) != 0) (fore, back) = (back, fore);

            double w = run.Text.Length * _charWidth;

            // the surface behind this run: its own background, or the panel background for default-back runs
            Rgb surface = back.Equals(Rgb.DefaultBack) ? SurfaceRgb : back;
            if (!back.Equals(Rgb.DefaultBack))
                context.FillRectangle(BrushFor(back), new Rect(x, y, w, _lineHeight));

            // lift near-invisible text (e.g. MUD "black" body text) to a readable contrast
            IImmutableBrush foreBrush = BrushFor(ReadableFore(fore, surface));

            var weight = (run.Flags & RunFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal;
            var slant = (run.Flags & RunFlags.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;
            var typeface = new Typeface(FontFamily, slant, weight);

            var ft = new FormattedText(run.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, FontSize, foreBrush);
            context.DrawText(ft, new Point(x, y));

            if ((run.Flags & RunFlags.Underline) != 0)
            {
                double uy = y + _lineHeight - 1;
                context.DrawLine(new Pen(foreBrush), new Point(x, uy), new Point(x + w, uy));
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

    // ---- readability floor -------------------------------------------------
    // The MUD sometimes sends body text in a near-black colour that reads fine on a
    // light terminal but disappears on Scrye's dark output surface. When a run's
    // contrast against the surface falls below MinContrast we lift the foreground
    // toward white by the smallest amount that clears the floor (hue preserved as
    // far as possible). Colours that are already legible are returned untouched, so
    // ordinary theming and bright MUD colours are unaffected.

    private Rgb ReadableFore(Rgb fore, Rgb back)
    {
        var key = (Key(fore), Key(back));
        if (_readableCache.TryGetValue(key, out Rgb cached)) return cached;
        Rgb result = ComputeReadable(fore, back);
        _readableCache[key] = result;
        return result;

        static uint Key(Rgb c) => (uint)((c.R << 16) | (c.G << 8) | c.B);
    }

    private static Rgb ComputeReadable(Rgb fore, Rgb back)
    {
        double lb = Luminance(back);
        if (Contrast(Luminance(fore), lb) >= MinContrast) return fore;   // already legible
        if (lb > 0.5) return fore;                                       // light surface: don't lift toward white
        Rgb best = fore;
        for (int i = 1; i <= 16; i++)
        {
            double t = i / 16.0;
            best = new Rgb(
                (byte)(fore.R + (255 - fore.R) * t),
                (byte)(fore.G + (255 - fore.G) * t),
                (byte)(fore.B + (255 - fore.B) * t));
            if (Contrast(Luminance(best), lb) >= MinContrast) break;
        }
        return best;
    }

    private static double Contrast(double l1, double l2)
    {
        double hi = Math.Max(l1, l2), lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Rgb c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte b)
    {
        double s = b / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}

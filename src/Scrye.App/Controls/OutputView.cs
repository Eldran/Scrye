using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Scrye.Core.Text;

namespace Scrye.App.Controls;

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

    private ScrollbackBuffer? _source;
    private ScrollViewer? _scrollViewer;
    private double _lineHeight = 16;
    private double _charWidth = 8;
    private bool _stickToBottom = true;

    private readonly Dictionary<uint, IImmutableBrush> _brushCache = new();
    private static readonly IImmutableBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    static OutputView()
    {
        AffectsMeasure<OutputView>(FontFamilyProperty, FontSizeProperty);
        AffectsRender<OutputView>(FontFamilyProperty, FontSizeProperty);
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

        for (int i = first; i <= last; i++)
            DrawLine(context, src[i], i * _lineHeight);
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

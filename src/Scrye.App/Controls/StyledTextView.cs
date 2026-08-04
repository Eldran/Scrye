using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Scrye.Core.Text;

namespace Scrye.App.Controls;

/// <summary>
/// The <c>text</c> HUD widget: monospace, multi-line, and — unlike the TextBlock it replaces —
/// coloured per run rather than per widget. <see cref="Text"/> is newline-separated and may
/// contain the plugin colour markup (<see cref="Markup"/>), so a plugin can tint individual
/// tokens the way the MUSHclient originals coloured their report columns.
///
/// <para>Plain text is unaffected: a string with no '@' parses to a single run in
/// <see cref="Foreground"/>, which is exactly what the old TextBlock did.</para>
///
/// <para>Layout is deliberately dumb — no wrapping, no selection, no links. Reports are
/// column-aligned, so every glyph advances by the same measured character width; that keeps
/// columns lined up regardless of what colour a run happens to be. For anything richer than
/// this, the terminal surface (<see cref="OutputView"/>) is the control you want.</para>
/// </summary>
public class StyledTextView : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StyledTextView, string>(nameof(Text), "");

    /// <summary>Colour for text outside any markup. A non-solid brush falls back to the theme body colour.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<StyledTextView, IBrush?>(nameof(Foreground));

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private const string Mono = "Cascadia Mono, Consolas, monospace";
    private const double FontSize = 10.5;

    private readonly Dictionary<uint, IImmutableBrush> _brushes = new();
    private List<IReadOnlyList<StyledRun>>? _lines;    // cached parse, rebuilt when Text changes
    private double _charWidth, _lineHeight;

    static StyledTextView()
    {
        AffectsMeasure<StyledTextView>(TextProperty, ForegroundProperty);
        AffectsRender<StyledTextView>(TextProperty, ForegroundProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Both inputs feed the parse: Text obviously, Foreground because it is the base colour
        // every unmarked run is built with.
        if (change.Property == TextProperty || change.Property == ForegroundProperty) _lines = null;
    }

    /// <summary>The base colour: the bound brush if it is a solid one, else the scheme's body text.</summary>
    private Rgb BaseRgb()
    {
        if (Foreground is ISolidColorBrush s)
        {
            Color c = s.Color;
            return new Rgb(c.R, c.G, c.B);
        }
        Color t = Services.ThemeService.Current.Text;
        return new Rgb(t.R, t.G, t.B);
    }

    private List<IReadOnlyList<StyledRun>> ParsedLines()
    {
        if (_lines is not null) return _lines;
        var outp = new List<IReadOnlyList<StyledRun>>();
        string text = Text ?? "";
        Rgb base_ = BaseRgb();
        // Markup state does not carry across a newline: each line is parsed on its own so one
        // plugin forgetting a closing "@{}" cannot bleed colour down the rest of the report.
        foreach (string line in text.Length == 0 ? Array.Empty<string>() : text.Split('\n'))
            outp.Add(Markup.Parse(line, ViewModels.HudColor.ResolveRgb, base_));
        _lines = outp;
        return outp;
    }

    private void EnsureMetrics()
    {
        if (_charWidth > 0) return;
        var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono), FontSize, Brushes.White);
        _charWidth = ft.Width;
        _lineHeight = Math.Ceiling(ft.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        List<IReadOnlyList<StyledRun>> lines = ParsedLines();
        if (lines.Count == 0) return new Size(0, 0);

        int widest = 0;
        foreach (IReadOnlyList<StyledRun> runs in lines)
        {
            int n = 0;
            foreach (StyledRun r in runs) n += r.Text.Length;
            widest = Math.Max(widest, n);
        }
        // Report the real text width so an enclosing ScrollViewer can scroll a wide report,
        // rather than silently clipping it the way a NoWrap TextBlock would.
        return new Size(widest * _charWidth, lines.Count * _lineHeight);
    }

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();
        List<IReadOnlyList<StyledRun>> lines = ParsedLines();

        for (int i = 0; i < lines.Count; i++)
        {
            double y = i * _lineHeight;
            double x = 0;
            foreach (StyledRun run in lines[i])
            {
                Rgb fore = run.Fore, back = run.Back;
                if ((run.Flags & RunFlags.Inverse) != 0) (fore, back) = (back, fore);

                double w = run.Text.Length * _charWidth;

                if (!back.Equals(Rgb.DefaultBack))
                    context.FillRectangle(BrushFor(back), new Rect(x, y, w, _lineHeight));

                IImmutableBrush brush = BrushFor(fore);
                var weight = (run.Flags & RunFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal;
                var slant = (run.Flags & RunFlags.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;

                var ft = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Mono, slant, weight), FontSize, brush);
                context.DrawText(ft, new Point(x, y));

                if ((run.Flags & RunFlags.Underline) != 0)
                {
                    double uy = y + _lineHeight - 1;
                    context.DrawLine(new Pen(brush), new Point(x, uy), new Point(x + w, uy));
                }

                x += w;
            }
        }
    }

    // Immutable brushes only: HUD content is produced on the session loop thread and drawn on
    // the UI thread, and Avalonia 12 faults if the compositor touches a mutable brush.
    private IImmutableBrush BrushFor(Rgb c)
    {
        uint key = (uint)((c.R << 16) | (c.G << 8) | c.B);
        if (!_brushes.TryGetValue(key, out IImmutableBrush? b))
        {
            b = new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            _brushes[key] = b;
        }
        return b;
    }
}

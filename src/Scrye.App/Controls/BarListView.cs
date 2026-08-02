using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Scrye.App.Controls;

/// <summary>
/// Renders a dynamic list of labelled "fill × quality" bars — one row per line of
/// <see cref="Rows"/>. Each line is tab-separated: <c>label \t caption \t value \t max \t refined</c>.
/// The bar fills to <c>value/max</c>; the filled part is split into a <b>refined</b> (green)
/// segment of width <c>refined/max</c> and the remaining <b>raw</b> (amber) segment, over a dark
/// track. Lines that aren't in that shape are drawn as plain text (so headers/"none" still show).
/// This is the Avalonia successor to the MUSHclient refinery miniwindow bars.
/// </summary>
public class BarListView : Control
{
    public static readonly StyledProperty<string> RowsProperty =
        AvaloniaProperty.Register<BarListView, string>(nameof(Rows), "");

    public string Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    // palette (kept simple + readable on the dark surface)
    private static readonly IImmutableBrush Refined = new ImmutableSolidColorBrush(Color.FromRgb(0x46, 0xB4, 0x5A)); // green
    private static readonly IImmutableBrush Raw     = new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x20)); // amber
    private static readonly IImmutableBrush Track   = new ImmutableSolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x2C)); // empty
    private static readonly IImmutableBrush LabelBr = new ImmutableSolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xDE));
    private static readonly IImmutableBrush CapBr   = new ImmutableSolidColorBrush(Color.FromRgb(0x9F, 0xB0, 0xC0));

    private const string Mono = "Cascadia Mono, Consolas, monospace";
    private const double FontSize = 11;
    private const double RowGap = 3;   // space between rows
    private const double BarH = 11;    // bar height

    static BarListView()
    {
        AffectsMeasure<BarListView>(RowsProperty);
        AffectsRender<BarListView>(RowsProperty);
    }

    private static string[] Lines(string s) =>
        s.Length == 0 ? System.Array.Empty<string>() : s.Split('\n');

    private double _lineH;

    private double LineHeight()
    {
        if (_lineH > 0) return _lineH;
        var ft = new FormattedText("Xg", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono), FontSize, LabelBr);
        _lineH = System.Math.Ceiling(System.Math.Max(ft.Height, BarH)) + RowGap;
        return _lineH;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = Lines(Rows).Length;
        double w = double.IsInfinity(availableSize.Width) ? 420 : availableSize.Width;
        return new Size(w, n * LineHeight());
    }

    private FormattedText Text(string s, IImmutableBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Mono), FontSize, brush);

    public override void Render(DrawingContext context)
    {
        string[] lines = Lines(Rows);
        if (lines.Length == 0) return;
        double rowH = LineHeight();

        // First pass: align the label and caption columns to the widest of each.
        double labelW = 0, capW = 0;
        var parsed = new (string label, string caption, double frac, double gfrac, bool isBar, string raw)[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            string[] f = lines[i].Split('\t');
            if (f.Length >= 5
                && double.TryParse(f[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double val)
                && double.TryParse(f[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double max)
                && double.TryParse(f[4], NumberStyles.Any, CultureInfo.InvariantCulture, out double refined)
                && max > 0)
            {
                double frac = System.Math.Clamp(val / max, 0, 1);
                double gfrac = System.Math.Clamp(refined / max, 0, frac);   // refined never exceeds the fill
                parsed[i] = (f[0], f[1], frac, gfrac, true, lines[i]);
                labelW = System.Math.Max(labelW, Text(f[0], LabelBr).Width);
                capW = System.Math.Max(capW, Text(f[1], CapBr).Width);
            }
            else
            {
                parsed[i] = (lines[i], "", 0, 0, false, lines[i]);
            }
        }

        double gap = 8;
        double barX = labelW + gap + capW + gap;
        double barW = System.Math.Max(24, Bounds.Width - barX - 2);

        for (int i = 0; i < parsed.Length; i++)
        {
            double y = i * rowH;
            var p = parsed[i];
            if (!p.isBar)
            {
                context.DrawText(Text(p.raw, LabelBr), new Point(0, y));
                continue;
            }

            var lft = Text(p.label, LabelBr);
            context.DrawText(lft, new Point(0, y));
            if (p.caption.Length > 0)
                context.DrawText(Text(p.caption, CapBr), new Point(labelW + gap, y));

            double by = y + System.Math.Max(0, (rowH - RowGap - BarH) / 2);
            context.FillRectangle(Track, new Rect(barX, by, barW, BarH));            // empty track
            double greenW = barW * p.gfrac;
            double amberW = barW * (p.frac - p.gfrac);
            if (greenW > 0) context.FillRectangle(Refined, new Rect(barX, by, greenW, BarH));
            if (amberW > 0) context.FillRectangle(Raw, new Rect(barX + greenW, by, amberW, BarH));
        }
    }
}

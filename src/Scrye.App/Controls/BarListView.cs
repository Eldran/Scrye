using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Scrye.App.Controls;

/// <summary>
/// Renders a dynamic list of labelled "fill × quality" bars — one row per line of
/// <see cref="Rows"/>. Each line is tab-separated:
/// <c>label \t caption \t value \t max \t refined [\t tooltip]</c>.
/// The bar fills to <c>value/max</c>; the filled part splits into a <b>raw</b> (amber)
/// segment on the left and the <b>refined</b> (green) segment of width <c>refined/max</c>
/// on the right — raw units enter on the left and come out refined on the right, the
/// direction the refining actually runs. Lines that aren't in that shape are drawn as
/// plain text (so headers/"none" still show).
///
/// <para>The optional sixth field is a hover tooltip (API 1.8) — the per-quality breakdown
/// the MUSHclient refinery miniwindow showed on its hotspots. A literal <c>\n</c> in it
/// becomes a line break (rows are newline-separated, so the field can't carry a real one).
/// Rows without it show no tooltip.</para>
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

        // transparent backdrop so the whole list is hit-testable for the hover tooltips
        // (a bare Control only receives pointer events where it has drawn something)
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

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
            // raw enters on the left, refined comes out on the right
            double greenW = barW * p.gfrac;
            double amberW = barW * (p.frac - p.gfrac);
            if (amberW > 0) context.FillRectangle(Raw, new Rect(barX, by, amberW, BarH));
            if (greenW > 0) context.FillRectangle(Refined, new Rect(barX + amberW, by, greenW, BarH));
        }
    }

    // ---- hover tooltip (the sixth field) -----------------------------------------------

    /// <summary>Row whose tooltip is currently installed; -1 = none.</summary>
    private int _tipRow = -1;

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int row = (int)(e.GetPosition(this).Y / LineHeight());
        if (row == _tipRow) return;
        _tipRow = row;

        string? tip = null;
        string[] lines = Lines(Rows);
        if (row >= 0 && row < lines.Length)
        {
            string[] f = lines[row].Split('\t');
            if (f.Length >= 6 && f[5].Length > 0) tip = f[5].Replace("\\n", "\n");
        }
        ToolTip.SetTip(this, tip);
        if (tip is null) ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tipRow = -1;
        ToolTip.SetTip(this, null);
        ToolTip.SetIsOpen(this, false);
    }
}

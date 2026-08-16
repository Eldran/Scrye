using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Scrye.App.Controls;

/// <summary>
/// Renders a dynamic list of labelled "fill × quality" bars — one row per line of
/// <see cref="Rows"/>. Each line is tab-separated:
/// <c>label \t caption \t value \t max \t refined [\t tooltip [\t stages]]</c>.
/// The bar fills to <c>value/max</c>. Lines that aren't in that shape are drawn as plain
/// text (so headers/"none" still show).
///
/// <para>The optional sixth field is a hover tooltip (API 1.8) — the per-quality breakdown
/// the MUSHclient refinery miniwindow showed on its hotspots. A literal <c>\n</c> in it
/// becomes a line break (rows are newline-separated, so the field can't carry a real one).
/// Rows without it show no tooltip.</para>
///
/// <para>The optional seventh field is the quality breakdown as <c>qty,pct;qty,pct;…</c>,
/// <b>rawest first</b>. When present the fill is drawn as one segment per stage: width is
/// how many units sit at that quality, colour is where that quality lands on the
/// <see cref="Ramp"/> from amber (0% — raw) to green (100% — refined). Refining therefore
/// still reads left to right, but the intermediate stages are visible instead of being
/// averaged away.</para>
///
/// <para>Without that field the bar falls back to the original two-segment split: raw
/// (amber) on the left, <c>refined/max</c> of green on the right. That keeps rows emitted
/// by older plugins rendering exactly as before.</para>
///
/// <para>Position, not just hue, carries the ordering (rawest is always leftmost) and the
/// tooltip carries the numbers, so the amber→green ramp is never the only encoding.</para>
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

    /// <summary>
    /// Quality ramp, raw (0%) → refined (100%). The two ends are the original amber and
    /// green so the panel's existing language is unchanged; the three interior stops turn
    /// the jump between them into a readable progression. Stops are spaced so that
    /// consecutive ones stay distinguishable (OKLab ΔE 4.7–8.0) while lightness holds
    /// roughly flat — hue, not brightness, is what moves, so a half-refined stage doesn't
    /// read as "more important" than a raw one.
    /// </summary>
    private static readonly (double At, Color C)[] Ramp =
    {
        (0.00, Color.FromRgb(0xE0, 0xA0, 0x20)),   // amber   — raw
        (0.25, Color.FromRgb(0xD9, 0xB3, 0x2A)),
        (0.50, Color.FromRgb(0xC6, 0xC4, 0x33)),   // yellow  — half way
        (0.75, Color.FromRgb(0x8F, 0xBE, 0x41)),
        (1.00, Color.FromRgb(0x46, 0xB4, 0x5A)),   // green   — refined
    };

    /// <summary>One brush per whole percent, built once. Render runs on every frame, so
    /// interpolating and allocating a brush per segment per frame would be wasteful.</summary>
    private static readonly IImmutableBrush[] RampBrushes = BuildRamp();

    private static IImmutableBrush[] BuildRamp()
    {
        var brushes = new IImmutableBrush[101];
        for (int p = 0; p <= 100; p++)
        {
            double t = p / 100.0;
            int i = 0;
            while (i < Ramp.Length - 2 && t > Ramp[i + 1].At) i++;
            (double a, Color ca) = Ramp[i];
            (double b, Color cb) = Ramp[i + 1];
            double k = b > a ? System.Math.Clamp((t - a) / (b - a), 0, 1) : 0;
            brushes[p] = new ImmutableSolidColorBrush(Color.FromRgb(
                (byte)System.Math.Round(ca.R + (cb.R - ca.R) * k),
                (byte)System.Math.Round(ca.G + (cb.G - ca.G) * k),
                (byte)System.Math.Round(ca.B + (cb.B - ca.B) * k)));
        }
        return brushes;
    }

    private static IImmutableBrush QualityBrush(double pct) =>
        RampBrushes[(int)System.Math.Round(System.Math.Clamp(pct, 0, 100))];

    private const string Mono = "Cascadia Mono, Consolas, monospace";
    private const double FontSize = 11;
    private const double RowGap = 3;   // space between rows
    private const double BarH = 11;    // bar height
    private const double SegGap = 1;   // hairline of track between stage segments

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
        var parsed = new (string label, string caption, double frac, double gfrac,
                          (double Qty, double Pct)[]? stages, bool isBar, string raw)[lines.Length];
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
                parsed[i] = (f[0], f[1], frac, gfrac, ParseStages(f.Length >= 7 ? f[6] : null), true, lines[i]);
                labelW = System.Math.Max(labelW, Text(f[0], LabelBr).Width);
                capW = System.Math.Max(capW, Text(f[1], CapBr).Width);
            }
            else
            {
                parsed[i] = (lines[i], "", 0, 0, null, false, lines[i]);
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
            double fillW = barW * p.frac;

            if (p.stages is { Length: > 0 } stages)
            {
                // One segment per quality stage, rawest first. Widths come from the stage
                // quantities normalised over their own total rather than over `max`, so the
                // segments always tile the fill exactly even if the quantities and `value`
                // disagree slightly (the MUD rounds them independently).
                double totalQty = 0;
                foreach ((double qty, _) in stages) totalQty += qty;
                if (totalQty > 0)
                {
                    double x = barX;
                    for (int s = 0; s < stages.Length; s++)
                    {
                        (double qty, double pct) = stages[s];
                        double w = fillW * (qty / totalQty);
                        // Leave a hairline of track between segments so two adjacent stages
                        // of similar quality still read as two, not as one wide band. Never
                        // eat a thin segment entirely — a 1-unit stage must stay visible.
                        double draw = s < stages.Length - 1 ? System.Math.Max(w - SegGap, System.Math.Min(w, 1)) : w;
                        if (draw > 0)
                            context.FillRectangle(QualityBrush(pct), new Rect(x, by, draw, BarH));
                        x += w;
                    }
                    continue;
                }
            }

            // Fallback for rows without the stage field: raw enters on the left, refined
            // comes out on the right.
            double greenW = barW * p.gfrac;
            double amberW = fillW - greenW;
            if (amberW > 0) context.FillRectangle(Raw, new Rect(barX, by, amberW, BarH));
            if (greenW > 0) context.FillRectangle(Refined, new Rect(barX + amberW, by, greenW, BarH));
        }
    }

    /// <summary>
    /// Parse the seventh field, <c>qty,pct;qty,pct;…</c> (rawest first). Returns null for a
    /// missing, empty or unparseable field so the caller falls back to the two-colour bar —
    /// a malformed row should look like an old row, not like an empty one.
    /// </summary>
    private static (double Qty, double Pct)[]? ParseStages(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        string[] parts = field.Split(';', System.StringSplitOptions.RemoveEmptyEntries);
        var list = new System.Collections.Generic.List<(double, double)>(parts.Length);
        foreach (string part in parts)
        {
            int comma = part.IndexOf(',');
            if (comma <= 0) continue;
            if (!double.TryParse(part.Substring(0, comma), NumberStyles.Any, CultureInfo.InvariantCulture, out double qty)
                || !double.TryParse(part.Substring(comma + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double pct))
                continue;
            if (qty > 0) list.Add((qty, pct));
        }
        return list.Count > 0 ? list.ToArray() : null;
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

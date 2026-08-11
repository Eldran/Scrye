using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Scrye.App.Controls;

/// <summary>A clicked cell: zero-based column and row plus the character there.</summary>
public readonly record struct GridCell(int Col, int Row, char Ch);

/// <summary>
/// Renders a grid of coloured square cells from a character map: <see cref="GridText"/>
/// is newline-separated rows, <see cref="Palette"/> maps each character to a colour.
/// Cell size adapts to the available width (min 3, max 12 px) with a 1px gap so each
/// cell reads as its own tile — the Avalonia successor to MUSHclient miniwindow maps.
/// Unknown characters render dark; whitespace renders as background.
///
/// <para>Characters listed in <see cref="LabelChars"/> also get drawn as a letter on top of
/// their tile, in black or white depending on how light the tile is. A colour alone says
/// "something is here"; the letter says what — which is what the legend under these charts
/// has always been compensating for. Letters are skipped when the cells are too small to
/// hold one, so a dense grid degrades to plain tiles rather than to mush.</para>
///
/// <para><b>Weave mode</b> (plugin API 1.7, <see cref="Weave"/>). For maps that want visible
/// connections between tiles: cells at EVEN column/row indices are full-size tiles ("nodes"),
/// the odd cells woven between them are narrow and draw the connector characters
/// <c>-</c> <c>|</c> <c>/</c> <c>\</c> <c>x</c> as thin lines in their palette colour rather
/// than as tiles (<c>x</c> is both diagonals — two corridors crossing). A 41x29 character grid
/// thus renders as 21x15 rooms with the exits drawn between them, at nearly the same tile size
/// as an unwoven 21x15 — which is the whole point: doubling the character resolution must not
/// halve the map. Any other character on an odd cell falls back to a small tile. Hit-testing
/// reports the raw (col, row) of the doubled grid, so the bound plugin does the halving.</para>
/// </summary>
public class ColorGridView : Control
{
    public static readonly StyledProperty<string> GridTextProperty =
        AvaloniaProperty.Register<ColorGridView, string>(nameof(GridText), "");

    public static readonly StyledProperty<Dictionary<char, Color>?> PaletteProperty =
        AvaloniaProperty.Register<ColorGridView, Dictionary<char, Color>?>(nameof(Palette));

    /// <summary>Optional command run when a cell is clicked, with a <see cref="GridCell"/>
    /// parameter (col, row, char). When set, the grid becomes hit-testable and clickable.</summary>
    public static readonly StyledProperty<ICommand?> CellCommandProperty =
        AvaloniaProperty.Register<ColorGridView, ICommand?>(nameof(CellCommand));

    /// <summary>Optional command run when the pointer moves onto a DIFFERENT cell (plugin API
    /// 1.6's colorgrid <c>onHover</c>), with a <see cref="GridCell"/> parameter. Fires once per
    /// cell change — never per pixel — and once with <c>(-1, -1, '\0')</c> when the pointer
    /// leaves the grid, so a bound plugin can clear its preview. When set, the grid becomes
    /// hit-testable even without a click command.</summary>
    public static readonly StyledProperty<ICommand?> HoverCommandProperty =
        AvaloniaProperty.Register<ColorGridView, ICommand?>(nameof(HoverCommand));

    /// <summary>Characters that get a letter drawn on their tile (e.g. "SXHWTI&gt;*B").
    /// Empty means tiles only.</summary>
    public static readonly StyledProperty<string> LabelCharsProperty =
        AvaloniaProperty.Register<ColorGridView, string>(nameof(LabelChars), "");

    /// <summary>Weave mode (API 1.7): even cells are tiles, odd cells are thin connector
    /// lines. See the class remarks.</summary>
    public static readonly StyledProperty<bool> WeaveProperty =
        AvaloniaProperty.Register<ColorGridView, bool>(nameof(Weave));

    /// <summary>Micro-icons (plugin API 1.8): maps a grid character to one of the named
    /// glyphs in <see cref="IconGlyphs"/>. An iconed cell draws a muted tile of its palette
    /// colour with the glyph on top in a lightened shade of the same colour — so terrain
    /// looks like terrain instead of a heatmap, and a palette change recolours everything.
    /// Icons win over <see cref="LabelChars"/> letters; below <see cref="MinIconCell"/> the
    /// cell falls back to the plain tile (and then the letter rules apply), so a dense grid
    /// degrades exactly like the letters do. Unknown glyph names render as plain tiles.</summary>
    public static readonly StyledProperty<Dictionary<char, string>?> IconsProperty =
        AvaloniaProperty.Register<ColorGridView, Dictionary<char, string>?>(nameof(Icons));

    /// <summary>Upper clamp for the cell size (plugin API 1.8's colorgrid <c>cell</c>).
    /// The default 12 keeps dense grids compact; a chart that wants readable icons
    /// (the viking sea chart) raises it and the cells grow — still shrinking to fit
    /// the available width, exactly as before, just allowed to be bigger first.</summary>
    public static readonly StyledProperty<double> MaxCellProperty =
        AvaloniaProperty.Register<ColorGridView, double>(nameof(MaxCell), 12);

    public ICommand? CellCommand { get => GetValue(CellCommandProperty); set => SetValue(CellCommandProperty, value); }
    public ICommand? HoverCommand { get => GetValue(HoverCommandProperty); set => SetValue(HoverCommandProperty, value); }
    public string LabelChars { get => GetValue(LabelCharsProperty); set => SetValue(LabelCharsProperty, value); }
    public bool Weave { get => GetValue(WeaveProperty); set => SetValue(WeaveProperty, value); }
    public Dictionary<char, string>? Icons { get => GetValue(IconsProperty); set => SetValue(IconsProperty, value); }
    public double MaxCell { get => GetValue(MaxCellProperty); set => SetValue(MaxCellProperty, value); }

    /// <summary>Last cell reported to <see cref="HoverCommand"/>; (-1,-1) = pointer not over a cell.</summary>
    private int _hoverCol = -1, _hoverRow = -1;

    /// <summary>Below this cell size a letter is illegible, so we draw tiles only.</summary>
    private const double MinLabelCell = 7;

    /// <summary>Below this cell size a glyph is just noise; iconed cells fall back to tiles.</summary>
    private const double MinIconCell = 8;

    private static readonly ImmutableSolidColorBrush LabelDark = new(Color.FromRgb(0x08, 0x06, 0x14));
    private static readonly ImmutableSolidColorBrush LabelLight = new(Color.FromRgb(0xF4, 0xEF, 0xFF));

    private static readonly ImmutableSolidColorBrush Unknown = new(Color.FromRgb(0x28, 0x28, 0x28));
    private readonly Dictionary<char, IImmutableBrush> _brushes = new();

    static ColorGridView()
    {
        AffectsMeasure<ColorGridView>(GridTextProperty, WeaveProperty, MaxCellProperty);
        AffectsRender<ColorGridView>(GridTextProperty, PaletteProperty, LabelCharsProperty, WeaveProperty, IconsProperty, MaxCellProperty);
    }

    // ---- micro-icon glyphs (API 1.8) ---------------------------------------------------
    // Each glyph is designed in a 12-unit box and scaled by u = cellSize / 12. Fill is the
    // main shape; Stroke is line work drawn with a round-capped pen. Either may be empty.
    // The SVG path mini-language keeps these auditable against the design mock.

    /// <summary>The public glyph vocabulary, for docs and validation.</summary>
    public static readonly string[] IconGlyphs =
    {
        "water", "dashes", "grass", "hill", "tree", "pine", "mountain", "house", "tower",
        "gate", "ruin", "star", "person", "ship", "anchor", "flag", "bolt", "crown",
        "hammer", "cross", "dot",
    };

    private static (string Fill, string Stroke) IconPaths(string name, double u) => name switch
    {
        "water" => ("", F("M{0},{1} q{2},{3} {4},0 t{4},0 t{4},0 M{0},{5} q{2},{3} {4},0 t{4},0 t{4},0",
                         1.5 * u, 4.5 * u, 1.5 * u, -2 * u, 3 * u, 8 * u)),
        "dashes" => ("", F("M{0},{1} h{2} M{0},{3} h{4}", 2.5 * u, 4.5 * u, 7 * u, 7.5 * u, 4.5 * u)),
        "grass" => ("", F("M{0},{1} v{2} M{3},{4} v{5} M{6},{1} v{2}",
                          2.5 * u, 8.5 * u, -3 * u, 5.5 * u, 9 * u, -4 * u, 8.5 * u)),
        "hill" => ("", F("M{0},{1} q{2},{3} {4},0 M{5},{1} q{6},{7} {8},0",
                         1 * u, 8.5 * u, 2.5 * u, -5 * u, 5 * u, 5.5 * u, 2.2 * u, -3.6 * u, 4.4 * u)),
        "tree" => (F("M{0},{1} L{2},{3} L{4},{3} Z M{5},{3} h{6} v{7} h-{6} Z",
                     6 * u, 1.2 * u, 9.6 * u, 7 * u, 2.4 * u, 5.1 * u, 1.8 * u, 3.4 * u), ""),
        "pine" => (F("M{0},{1} L{2},{3} L{4},{3} L{5},{6} L{7},{6} L{8},{3} L{9},{3} Z M{10},{6} h{11} v{12} h-{11} Z",
                     6 * u, 1 * u, 8.8 * u, 4.6 * u, 7.6 * u, 10 * u, 8 * u, 2 * u, 4.4 * u, 3.2 * u, 5.2 * u, 1.6 * u, 2.6 * u), ""),
        "mountain" => (F("M{0},{1} L{2},{3} L{4},{5} L{6},{7} L{8},{1} Z",
                         1 * u, 9.5 * u, 4.5 * u, 2.5 * u, 6.5 * u, 6.5 * u, 8 * u, 4 * u, 11 * u), ""),
        "house" => (F("M{0},{1} h{2} v{3} h-{2} Z M{4},{1} L{5},{6} L{7},{1} Z",
                      2.5 * u, 6 * u, 7 * u, 4 * u, 1.5 * u, 6 * u, 2 * u, 10.5 * u), ""),
        "tower" => (F("M{0},{1} h{2} v{3} h{4} v-{3} h{2} v{5} h-{6} Z M{7},{8} h{9} v{10} h-{9} Z",
                      3.5 * u, 3 * u, 1.5 * u, 1.4 * u, 2 * u, 2.8 * u, 5 * u, 4.2 * u, 5.8 * u, 3.6 * u, 4.4 * u), ""),
        "gate" => (F("M{0},{1} v-{2} q{3},-{4} {5},0 v{2} h-{6} v-{7} h-{8} v{7} Z",
                     2.5 * u, 10.5 * u, 5 * u, 3.5 * u, 4 * u, 7 * u, 2 * u, 3.5 * u, 3 * u), ""),
        "ruin" => (F("M{0},{1} h{2} v{3} h-{2} Z M{4},{5} h{2} v{6} h-{2} Z M{7},{8} h{2} v{9} h-{2} Z",
                     2.5 * u, 5 * u, 1.7 * u, 5 * u, 5.2 * u, 3.4 * u, 6.6 * u, 7.9 * u, 6.2 * u, 3.8 * u), ""),
        "star" => (F("M{0},{1} L{2},{3} L{4},{0} L{2},{5} L{0},{6} L{7},{5} L{1},{0} L{7},{3} Z",
                     6 * u, 1.5 * u, 7.2 * u, 4.8 * u, 10.5 * u, 7.2 * u, 10.5 * u, 4.8 * u), ""),
        "person" => (F("M{0},{1} a{2},{2} 0 1 0 0.01,0 Z M{3},{4} q{5},{6} {7},0 Z",
                       6 * u, 2 * u, 1.8 * u, 3.4 * u, 10.5 * u, 2.6 * u, -5 * u, 5.2 * u), ""),
        "ship" => (F("M{0},{1} h{2} l-{3},{4} h-{5} Z M{6},{7} v{8} l-{9},0 Z",
                     2 * u, 7.5 * u, 8 * u, 1.5 * u, 2.5 * u, 5 * u, 6.5 * u, 1.5 * u, 5 * u, 3.6 * u), ""),
        "anchor" => ("", F("M{0},{1} v{2} M{3},{4} h{5} M{6},{7} a{8},{8} 0 0 0 {9},0 M{0},{10} a{11},{11} 0 1 1 0.01,0",
                           6 * u, 3.6 * u, 6 * u, 3.8 * u, 5 * u, 4.4 * u, 2.6 * u, 8 * u, 3.4 * u, 6.8 * u, 2.4 * u, 1.2 * u)),
        "flag" => (F("M{0},{1} L{2},{3} L{0},{4} Z", 5 * u, 1.5 * u, 10 * u, 3.5 * u, 5.5 * u),
                   F("M{0},{1} v{2}", 4.4 * u, 1.5 * u, 9 * u)),
        "bolt" => (F("M{0},{1} L{2},{3} L{4},{3} L{5},{6} L{7},{8} L{0},{8} Z",
                     6.5 * u, 1 * u, 3.5 * u, 6.5 * u, 5.5 * u, 4.5 * u, 10.5 * u, 8.5 * u, 5 * u), ""),
        "crown" => (F("M{0},{1} v-{2} l{3},{4} l{5},-{6} l{5},{6} l{3},-{4} v{2} Z",
                      2 * u, 9 * u, 4 * u, 2.3 * u, 2 * u, 1.7 * u, 3 * u), ""),
        "hammer" => (F("M{0},{1} h{2} v{3} h-{2} Z", 3 * u, 2.5 * u, 6 * u, 2.2 * u),
                     F("M{0},{1} L{0},{2}", 6 * u, 4.7 * u, 10.5 * u)),
        "cross" => ("", F("M{0},{0} L{1},{1} M{1},{0} L{0},{1}", 3 * u, 9 * u)),
        "dot" => (F("M{0},{1} a{2},{2} 0 1 0 0.01,0 Z", 6 * u, 3.8 * u, 2.2 * u), ""),
        _ => ("", ""),
    };

    private static string F(string fmt, params double[] a)
    {
        object[] inv = new object[a.Length];
        for (int i = 0; i < a.Length; i++) inv[i] = a[i].ToString("0.##", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.InvariantCulture, fmt, inv);
    }

    // parsed geometry per (glyph, cell size) — sizes are few (one per grid), so this stays tiny
    private readonly Dictionary<(string, double), (Geometry? Fill, Geometry? Stroke)> _iconGeo = new();
    // muted tile / lightened ink per character, rebuilt on palette swap alongside _brushes
    private readonly Dictionary<char, (IImmutableBrush Bg, IImmutableBrush Ink, ImmutablePen Pen)> _iconInk = new();

    private static Color Mix(Color c, byte target, double k) => Color.FromRgb(
        (byte)(c.R + (target - c.R) * k), (byte)(c.G + (target - c.G) * k), (byte)(c.B + (target - c.B) * k));

    private (Geometry? Fill, Geometry? Stroke) IconGeo(string name, double size)
    {
        if (_iconGeo.TryGetValue((name, size), out var cached)) return cached;
        (string fill, string stroke) = IconPaths(name, size / 12.0);
        var geo = (fill.Length > 0 ? Geometry.Parse(fill) : null,
                   stroke.Length > 0 ? Geometry.Parse(stroke) : null);
        _iconGeo[(name, size)] = geo;
        return geo;
    }

    /// <summary>Muted tile + lightened glyph ink for a palette colour, so every icon
    /// automatically matches its terrain colour and the theme it came from.</summary>
    private (IImmutableBrush Bg, IImmutableBrush Ink, ImmutablePen Pen) IconInk(char ch, Dictionary<char, Color>? palette, double size)
    {
        if (_iconInk.TryGetValue(ch, out var cached) && Math.Abs(cached.Pen.Thickness - PenWidth(size)) < 0.01)
            return cached;
        Color tile = palette is not null && palette.TryGetValue(ch, out Color pc) ? pc : Color.FromRgb(0x28, 0x28, 0x28);
        var ink = new ImmutableSolidColorBrush(Mix(tile, 255, 0.22));
        var made = ((IImmutableBrush)new ImmutableSolidColorBrush(Mix(tile, 0, 0.55)), (IImmutableBrush)ink,
                    new ImmutablePen(ink, PenWidth(size), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
        _iconInk[ch] = made;
        return made;
    }

    private static double PenWidth(double size) => Math.Max(1, size * 0.11);

    /// <summary>Draw the iconed cell at (x, y): muted tile, then the glyph. Returns false when
    /// the glyph name is unknown so the caller can fall back to a plain tile.</summary>
    private bool DrawIcon(DrawingContext context, char ch, string name, double x, double y, double size,
                          Dictionary<char, Color>? palette)
    {
        (Geometry? fill, Geometry? stroke) = IconGeo(name, size);
        if (fill is null && stroke is null) return false;
        (IImmutableBrush bg, IImmutableBrush ink, ImmutablePen pen) = IconInk(ch, palette, size);
        context.FillRectangle(bg, new Rect(x, y, size - 1, size - 1));
        using (context.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            if (fill is not null) context.DrawGeometry(ink, null, fill);
            if (stroke is not null) context.DrawGeometry(null, pen, stroke);
        }
        return true;
    }

    public string GridText { get => GetValue(GridTextProperty); set => SetValue(GridTextProperty, value); }
    public Dictionary<char, Color>? Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }

    private static string[] Rows(string text) =>
        text.Length == 0 ? Array.Empty<string>() : text.Split('\n');

    private double CellFor(string[] rows, double width)
    {
        int cols = 0;
        foreach (string r in rows) cols = Math.Max(cols, r.Length);
        if (cols == 0) return 0;
        double max = Math.Clamp(MaxCell, 3, 64);   // a hostile spec value must not explode layout
        return Math.Clamp(Math.Floor(width / cols), 3, max);
    }

    /// <summary>
    /// Weave cell sizes for the available width: (node, edge), where node is the side of an
    /// even-indexed tile and edge the thickness of the odd cells between them. Edge tracks
    /// node at roughly half so connectors read as passages, not as second-class rooms; node
    /// shrinks (to a floor of 4) until the woven row fits. Returns (0,0) for an empty grid.
    /// </summary>
    private static (double Node, double Edge) WeaveCellsFor(string[] rows, double width)
    {
        int cols = 0;
        foreach (string r in rows) cols = Math.Max(cols, r.Length);
        if (cols == 0) return (0, 0);
        int nodes = (cols + 1) / 2, edges = cols / 2;
        // node + edge/2 per pair is the ideal budget; start there, clamp, then shrink to fit
        double node = Math.Clamp(Math.Floor(width * 2 / (nodes * 2 + edges)), 4, 14);
        double edge = Math.Max(2, Math.Floor(node / 2));
        while (node > 4 && nodes * node + edges * edge > width)
        {
            node -= 1;
            edge = Math.Max(2, Math.Floor(node / 2));
        }
        return (node, edge);
    }

    /// <summary>Pixel offset of column/row index <paramref name="i"/> in weave layout —
    /// the even cells before it are node-sized, the odd ones edge-sized.</summary>
    private static double WeaveOffset(int i, double node, double edge) =>
        (i + 1) / 2 * node + i / 2 * edge;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Report the TIGHT content size, not the offered width: in a vertical stack the
        // grid still gets arranged full-width (alignment stretch), so nothing changes —
        // but inside a horizontal "row" container (API 1.8) the neighbours pack right up
        // against the grid instead of against a phantom full-width slab. With an infinite
        // offer the cells simply take their ceiling size.
        string[] rows = Rows(GridText);
        int cols = 0;
        foreach (string r in rows) cols = Math.Max(cols, r.Length);
        double width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        if (Weave)
        {
            (double node, double edge) = WeaveCellsFor(rows, width);
            return new Size(WeaveOffset(cols, node, edge), WeaveOffset(rows.Length, node, edge));
        }
        double cell = CellFor(rows, width);
        return new Size(cols * cell, rows.Length * cell);
    }

    public override void Render(DrawingContext context)
    {
        string[] rows = Rows(GridText);
        if (rows.Length == 0) return;
        Dictionary<char, Color>? palette = Palette;

        // When interactive, fill a transparent background so the whole grid is hit-testable
        // (a bare Control only receives pointer events where it has drawn something).
        if (CellCommand is not null || HoverCommand is not null)
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (Weave) { RenderWeave(context, rows, palette); return; }

        double cell = CellFor(rows, Bounds.Width);
        if (cell <= 0) return;

        string labels = LabelChars ?? "";
        bool drawLabels = labels.Length > 0 && cell >= MinLabelCell;
        double fontSize = System.Math.Floor(cell * 0.78);
        Dictionary<char, string>? icons = Icons;
        bool drawIcons = icons is not null && icons.Count > 0 && cell >= MinIconCell;

        for (int r = 0; r < rows.Length; r++)
        {
            string row = rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                char ch = row[c];
                if (ch == ' ') continue;

                // an iconed cell replaces both the flat tile and any letter for this char
                if (drawIcons && icons!.TryGetValue(ch, out string? glyph)
                    && DrawIcon(context, ch, glyph, c * cell, r * cell, cell, palette))
                    continue;

                context.FillRectangle(BrushFor(ch, palette),
                    new Rect(c * cell, r * cell, cell - 1, cell - 1));

                if (!drawLabels || labels.IndexOf(ch) < 0) continue;
                Color tile = palette is not null && palette.TryGetValue(ch, out Color pc)
                    ? pc : Color.FromRgb(0x28, 0x28, 0x28);
                FormattedText ft = LabelFor(ch, tile, fontSize);
                // centre in the tile (the tile is cell-1 wide because of the 1px gap)
                context.DrawText(ft, new Point(
                    c * cell + (cell - 1 - ft.Width) / 2,
                    r * cell + (cell - 1 - ft.Height) / 2));
            }
        }
    }

    /// <summary>The characters weave mode draws as lines rather than tiles.</summary>
    private static bool IsConnector(char ch) => ch is '-' or '|' or '/' or '\\' or 'x';

    private void RenderWeave(DrawingContext context, string[] rows, Dictionary<char, Color>? palette)
    {
        (double node, double edge) = WeaveCellsFor(rows, Bounds.Width);
        if (node <= 0) return;

        string labels = LabelChars ?? "";
        bool drawLabels = labels.Length > 0 && node >= MinLabelCell;
        double fontSize = System.Math.Floor(node * 0.78);
        Dictionary<char, string>? icons = Icons;
        bool drawIcons = icons is not null && icons.Count > 0 && node >= MinIconCell;
        // connector line thickness: reads at a glance yet clearly thinner than a tile
        double t = Math.Max(2, Math.Floor(node / 3));

        for (int r = 0; r < rows.Length; r++)
        {
            string row = rows[r];
            double y = WeaveOffset(r, node, edge);
            double h = r % 2 == 0 ? node : edge;
            for (int c = 0; c < row.Length; c++)
            {
                char ch = row[c];
                if (ch == ' ') continue;
                double x = WeaveOffset(c, node, edge);
                double w = c % 2 == 0 ? node : edge;
                bool isNode = c % 2 == 0 && r % 2 == 0;

                if (!isNode && IsConnector(ch))
                {
                    // Lines overshoot the cell by 1px each side to bridge the tiles'
                    // 1px gap — a connector must TOUCH the rooms it connects.
                    switch (ch)
                    {
                        case '-':
                            context.FillRectangle(BrushFor(ch, palette),
                                new Rect(x - 1, y + (h - t) / 2, w + 2, t));
                            break;
                        case '|':
                            context.FillRectangle(BrushFor(ch, palette),
                                new Rect(x + (w - t) / 2, y - 1, t, h + 2));
                            break;
                        case '/':
                            context.DrawLine(PenFor(ch, palette, t),
                                new Point(x - 1, y + h + 1), new Point(x + w + 1, y - 1));
                            break;
                        case '\\':
                            context.DrawLine(PenFor(ch, palette, t),
                                new Point(x - 1, y - 1), new Point(x + w + 1, y + h + 1));
                            break;
                        default:   // 'x' — both diagonals cross here
                            context.DrawLine(PenFor(ch, palette, t),
                                new Point(x - 1, y + h + 1), new Point(x + w + 1, y - 1));
                            context.DrawLine(PenFor(ch, palette, t),
                                new Point(x - 1, y - 1), new Point(x + w + 1, y + h + 1));
                            break;
                    }
                    continue;
                }

                // node cells — and any non-connector character that strays onto an odd cell —
                // draw as tiles, exactly like the unwoven grid (icons on nodes only: an odd
                // cell is too narrow for a glyph even when a stray tile character lands there)
                if (isNode && drawIcons && icons!.TryGetValue(ch, out string? glyph)
                    && DrawIcon(context, ch, glyph, x, y, node, palette))
                    continue;

                context.FillRectangle(BrushFor(ch, palette), new Rect(x, y, w - 1, h - 1));

                if (!isNode || !drawLabels || labels.IndexOf(ch) < 0) continue;
                Color tile = palette is not null && palette.TryGetValue(ch, out Color pc)
                    ? pc : Color.FromRgb(0x28, 0x28, 0x28);
                FormattedText ft = LabelFor(ch, tile, fontSize);
                context.DrawText(ft, new Point(
                    x + (w - 1 - ft.Width) / 2,
                    y + (h - 1 - ft.Height) / 2));
            }
        }
    }

    // FormattedText is not cheap to build, and a 16x16 chart redraws on every feed tick, so
    // cache per (char, ink). The ink only has two values, so the cache stays tiny.
    private readonly Dictionary<(char, bool), FormattedText> _labels = new();
    private double _labelSize;

    private FormattedText LabelFor(char ch, Color tile, double fontSize)
    {
        if (fontSize != _labelSize) { _labels.Clear(); _labelSize = fontSize; }
        bool onLight = IsLight(tile);
        if (_labels.TryGetValue((ch, onLight), out FormattedText? cached)) return cached;
        var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono, Consolas, monospace", FontStyle.Normal, FontWeight.Bold),
            fontSize, onLight ? LabelDark : LabelLight);
        _labels[(ch, onLight)] = ft;
        return ft;
    }

    /// <summary>Perceived lightness (ITU-R BT.601), which tracks how bright a colour looks far
    /// better than a raw RGB average — the neon cyans and limes here are light despite their
    /// modest red channel.</summary>
    private static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140;

    private IImmutableBrush BrushFor(char ch, Dictionary<char, Color>? palette)
    {
        if (_brushes.TryGetValue(ch, out IImmutableBrush? cached)) return cached;
        IImmutableBrush brush = palette is not null && palette.TryGetValue(ch, out Color color)
            ? new ImmutableSolidColorBrush(color)
            : Unknown;
        _brushes[ch] = brush;
        return brush;
    }

    // Pens for weave-mode diagonal connectors, cached like the brushes (a map redraws on
    // every arrival). Thickness varies with cell size, so it joins the key.
    private readonly Dictionary<(char, double), ImmutablePen> _pens = new();

    private ImmutablePen PenFor(char ch, Dictionary<char, Color>? palette, double thickness)
    {
        if (_pens.TryGetValue((ch, thickness), out ImmutablePen? cached)) return cached;
        var pen = new ImmutablePen(BrushFor(ch, palette), thickness);
        _pens[(ch, thickness)] = pen;
        return pen;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty) { _brushes.Clear(); _pens.Clear(); _labels.Clear(); _iconInk.Clear(); }   // palette swap: rebuild caches
        if (change.Property == CellCommandProperty || change.Property == HoverCommandProperty)
            InvalidateVisual();   // toggle hit-test background
    }

    /// <summary>The cell under a pointer position, or null when off the grid / past a row's end.
    /// Shared by click and hover so the two can never disagree about what was hit.</summary>
    private GridCell? CellAt(Point p)
    {
        string[] rows = Rows(GridText);
        int col, row;
        if (Weave)
        {
            (double node, double edge) = WeaveCellsFor(rows, Bounds.Width);
            if (node <= 0) return null;
            // a (node, edge) pair repeats; which half of the pair the point falls in
            // decides even (tile) vs odd (connector) index
            double pair = node + edge;
            int cp = (int)(p.X / pair);
            int rp = (int)(p.Y / pair);
            col = cp * 2 + (p.X - cp * pair < node ? 0 : 1);
            row = rp * 2 + (p.Y - rp * pair < node ? 0 : 1);
        }
        else
        {
            double cell = CellFor(rows, Bounds.Width);
            if (cell <= 0) return null;
            col = (int)(p.X / cell);
            row = (int)(p.Y / cell);
        }
        if (row < 0 || row >= rows.Length) return null;
        if (col < 0 || col >= rows[row].Length) return null;
        return new GridCell(col, row, rows[row][col]);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ICommand? cmd = HoverCommand;
        if (cmd is null) return;

        GridCell? hit = CellAt(e.GetPosition(this));
        int col = hit?.Col ?? -1, row = hit?.Row ?? -1;
        if (col == _hoverCol && row == _hoverRow) return;   // same cell — per-pixel silence
        _hoverCol = col; _hoverRow = row;

        GridCell report = hit ?? new GridCell(-1, -1, '\0');
        if (cmd.CanExecute(report)) cmd.Execute(report);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverCol == -1 && _hoverRow == -1) return;
        _hoverCol = -1; _hoverRow = -1;

        ICommand? cmd = HoverCommand;
        if (cmd is null) return;
        var report = new GridCell(-1, -1, '\0');
        if (cmd.CanExecute(report)) cmd.Execute(report);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ICommand? cmd = CellCommand;
        if (cmd is null) return;

        if (CellAt(e.GetPosition(this)) is not { } hit) return;
        if (cmd.CanExecute(hit)) cmd.Execute(hit);
        e.Handled = true;
    }
}

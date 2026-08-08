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

    public ICommand? CellCommand { get => GetValue(CellCommandProperty); set => SetValue(CellCommandProperty, value); }
    public ICommand? HoverCommand { get => GetValue(HoverCommandProperty); set => SetValue(HoverCommandProperty, value); }
    public string LabelChars { get => GetValue(LabelCharsProperty); set => SetValue(LabelCharsProperty, value); }
    public bool Weave { get => GetValue(WeaveProperty); set => SetValue(WeaveProperty, value); }

    /// <summary>Last cell reported to <see cref="HoverCommand"/>; (-1,-1) = pointer not over a cell.</summary>
    private int _hoverCol = -1, _hoverRow = -1;

    /// <summary>Below this cell size a letter is illegible, so we draw tiles only.</summary>
    private const double MinLabelCell = 7;

    private static readonly ImmutableSolidColorBrush LabelDark = new(Color.FromRgb(0x08, 0x06, 0x14));
    private static readonly ImmutableSolidColorBrush LabelLight = new(Color.FromRgb(0xF4, 0xEF, 0xFF));

    private static readonly ImmutableSolidColorBrush Unknown = new(Color.FromRgb(0x28, 0x28, 0x28));
    private readonly Dictionary<char, IImmutableBrush> _brushes = new();

    static ColorGridView()
    {
        AffectsMeasure<ColorGridView>(GridTextProperty, WeaveProperty);
        AffectsRender<ColorGridView>(GridTextProperty, PaletteProperty, LabelCharsProperty, WeaveProperty);
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
        return Math.Clamp(Math.Floor(width / cols), 3, 12);
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
        string[] rows = Rows(GridText);
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        if (Weave)
        {
            (double node, double edge) = WeaveCellsFor(rows, width);
            return new Size(width, WeaveOffset(rows.Length, node, edge));
        }
        double cell = CellFor(rows, width);
        return new Size(width, rows.Length * cell);
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

        for (int r = 0; r < rows.Length; r++)
        {
            string row = rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                char ch = row[c];
                if (ch == ' ') continue;
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
                // draw as tiles, exactly like the unwoven grid
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
        if (change.Property == PaletteProperty) { _brushes.Clear(); _pens.Clear(); _labels.Clear(); }   // palette swap: rebuild caches
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

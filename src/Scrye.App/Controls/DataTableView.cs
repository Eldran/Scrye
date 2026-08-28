using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Scrye.App.Controls;

/// <summary>A clicked body row: its 1-based index and its first cell's text — the same
/// (label, index) pair a bound buttonrow reports, because a clickable row IS a choice from
/// a dynamic set. The header row is structure, not content, and never reports.</summary>
public readonly record struct TableRow(int Index, string Label);

/// <summary>
/// Renders the <c>list</c> and <c>table</c> plugin widgets: newline-separated
/// <see cref="Rows"/>, each split into cells by <see cref="Separator"/>, drawn as a
/// monospaced grid with columns measured to their widest cell.
///
/// <para><b>Why a drawn control rather than a Grid of TextBlocks.</b> The row set changes every
/// time the bound state value changes — which for a market or inventory panel is constantly.
/// Rebuilding a logical tree of controls per update allocates and re-layouts the whole panel;
/// drawing text at measured offsets does neither, and matches how <see cref="BarListView"/> and
/// <see cref="ColorGridView"/> already work.</para>
///
/// <para>Rows whose cell count differs from the rest are not an error: missing cells render
/// blank and extra ones are dropped when <see cref="Columns"/> fixes the width. A plugin
/// emitting a "nothing here" row should just emit one cell, and it will read as a plain line.</para>
/// </summary>
public class DataTableView : Control
{
    /// <summary>Newline-separated rows; each row is <see cref="Separator"/>-delimited cells.</summary>
    public static readonly StyledProperty<string> RowsProperty =
        AvaloniaProperty.Register<DataTableView, string>(nameof(Rows), "");

    /// <summary>Cell separator within a row. Defaults to a tab.</summary>
    public static readonly StyledProperty<string> SeparatorProperty =
        AvaloniaProperty.Register<DataTableView, string>(nameof(Separator), "\t");

    /// <summary>Optional header labels. When non-empty a header row is drawn and the column
    /// count is fixed to its length.</summary>
    public static readonly StyledProperty<string[]?> ColumnsProperty =
        AvaloniaProperty.Register<DataTableView, string[]?>(nameof(Columns));

    /// <summary>Per-column alignment, one char each: 'l', 'r' or 'c'. Missing entries are left.</summary>
    public static readonly StyledProperty<string?> AlignProperty =
        AvaloniaProperty.Register<DataTableView, string?>(nameof(Align));

    /// <summary>Body text brush. Null follows the theme's body text.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DataTableView, IBrush?>(nameof(Foreground));

    /// <summary>When true, every column after the first is drawn dimmed — the <c>list</c>
    /// widget's "label, then value" reading. Tables draw all body cells alike.</summary>
    public static readonly StyledProperty<bool> DimTrailingProperty =
        AvaloniaProperty.Register<DataTableView, bool>(nameof(DimTrailing));

    /// <summary>Optional command run when a body row is clicked, with a <see cref="TableRow"/>
    /// parameter (the plugin <c>onRowClick</c> callback, API 1.15). Null = the table is inert,
    /// exactly as before the property existed. The header row never fires it.</summary>
    public static readonly StyledProperty<ICommand?> RowCommandProperty =
        AvaloniaProperty.Register<DataTableView, ICommand?>(nameof(RowCommand));

    public string Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public string Separator { get => GetValue(SeparatorProperty); set => SetValue(SeparatorProperty, value); }
    public string[]? Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public string? Align { get => GetValue(AlignProperty); set => SetValue(AlignProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool DimTrailing { get => GetValue(DimTrailingProperty); set => SetValue(DimTrailingProperty, value); }
    public ICommand? RowCommand { get => GetValue(RowCommandProperty); set => SetValue(RowCommandProperty, value); }

    private const string Mono = "Cascadia Mono, Consolas, monospace";
    private const double FontSize = 10.5;
    private const double RowGap = 2;      // vertical breathing room per row
    private const double ColGap = 10;     // horizontal gutter between columns
    private const double HeaderGap = 3;   // extra space under the header row

    static DataTableView()
    {
        // A table wider than the panel clips rather than painting over its neighbours. The panel
        // width is the plugin's choice (PanelSpec.Width); a wide table must not silently override it.
        ClipToBoundsProperty.OverrideDefaultValue<DataTableView>(true);
        AffectsMeasure<DataTableView>(RowsProperty, SeparatorProperty, ColumnsProperty,
                                      AlignProperty, DimTrailingProperty);
        AffectsRender<DataTableView>(RowsProperty, SeparatorProperty, ColumnsProperty,
                                     AlignProperty, ForegroundProperty, DimTrailingProperty);
    }

    // Resolved once per render/measure so a scheme change is picked up without rebuilding the VM.
    private IImmutableBrush BodyBrush =>
        Foreground as IImmutableBrush
        ?? new ImmutableSolidColorBrush(Services.ThemeService.Current.Text);

    private IImmutableBrush DimBrush => new ImmutableSolidColorBrush(Services.ThemeService.Current.TextDim);

    // A widget with a CUSTOM colour repaints when ReTheme swaps its Foreground binding; a
    // theme-following one (Foreground null → BodyBrush/DimBrush above) has no property change
    // to ride, so a scheme switch must invalidate it explicitly — same pattern as OutputView.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Services.ThemeService.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Services.ThemeService.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    private string Sep => string.IsNullOrEmpty(Separator) ? "\t" : Separator;

    /// <summary>The parsed grid: one string[] per row. Empty trailing lines are dropped so a
    /// plugin ending its composed value with "\n" doesn't leave a blank row.</summary>
    private string[][] Grid()
    {
        string rows = Rows ?? "";
        if (rows.Length == 0) return Array.Empty<string[]>();
        string[] lines = rows.Split('\n');
        int count = lines.Length;
        while (count > 0 && lines[count - 1].Trim().Length == 0) count--;
        if (count == 0) return Array.Empty<string[]>();

        var grid = new string[count][];
        string sep = Sep;
        for (int i = 0; i < count; i++)
            grid[i] = lines[i].Split(sep);
        return grid;
    }

    private FormattedText Text(string s, IImmutableBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Mono), FontSize, brush);

    private double _lineH;

    private double LineHeight()
    {
        if (_lineH > 0) return _lineH;
        _lineH = Math.Ceiling(Text("Xg", BodyBrush).Height) + RowGap;
        return _lineH;
    }

    /// <summary>Column count: the header length when headers are set, otherwise the widest row.</summary>
    private int ColumnCount(string[][] grid)
    {
        string[]? headers = Columns;
        if (headers is { Length: > 0 }) return headers.Length;
        int n = 0;
        foreach (string[] row in grid) n = Math.Max(n, row.Length);
        return n;
    }

    /// <summary>Measured width of each column, including the header cell.</summary>
    private double[] ColumnWidths(string[][] grid, int cols)
    {
        var widths = new double[cols];
        string[]? headers = Columns;
        IImmutableBrush body = BodyBrush;

        if (headers is { Length: > 0 })
            for (int c = 0; c < cols && c < headers.Length; c++)
                widths[c] = Math.Max(widths[c], Text(headers[c] ?? "", body).Width);

        foreach (string[] row in grid)
            for (int c = 0; c < cols && c < row.Length; c++)
                widths[c] = Math.Max(widths[c], Text(row[c], body).Width);

        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        string[][] grid = Grid();
        int cols = ColumnCount(grid);
        bool hasHeader = Columns is { Length: > 0 };

        double h = (grid.Length + (hasHeader ? 1 : 0)) * LineHeight() + (hasHeader ? HeaderGap : 0);
        if (grid.Length == 0 && !hasHeader) return new Size(0, 0);

        double w = 0;
        if (cols > 0)
        {
            double[] widths = ColumnWidths(grid, cols);
            foreach (double cw in widths) w += cw;
            w += ColGap * Math.Max(0, cols - 1);
        }
        // Never demand more than offered — the panel width is the plugin's choice, and a wide
        // table should clip rather than push the panel out of the HUD.
        if (!double.IsInfinity(availableSize.Width)) w = Math.Min(w, availableSize.Width);
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        string[][] grid = Grid();
        string[]? headers = Columns;
        bool hasHeader = headers is { Length: > 0 };
        int cols = ColumnCount(grid);
        if (cols == 0 || (grid.Length == 0 && !hasHeader)) return;

        double[] widths = ColumnWidths(grid, cols);
        var x0 = new double[cols];
        double running = 0;
        for (int c = 0; c < cols; c++) { x0[c] = running; running += widths[c] + ColGap; }

        // When clickable, fill a transparent background so every row is hit-testable across
        // its full width (a bare Control only receives pointer events where it has drawn
        // something) — same trick as ColorGridView.
        if (RowCommand is not null)
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        IImmutableBrush body = BodyBrush;
        IImmutableBrush dim = DimBrush;
        string align = Align ?? "";
        double rowH = LineHeight();
        double y = 0;

        if (hasHeader)
        {
            for (int c = 0; c < cols && c < headers!.Length; c++)
                DrawCell(context, headers[c] ?? "", dim, c, x0, widths, align, y);
            y += rowH + HeaderGap;

            // A hairline under the header, at the dim colour so it reads as structure not content.
            context.FillRectangle(dim, new Rect(0, y - HeaderGap + 1, Math.Max(0, running - ColGap), 1));
        }

        foreach (string[] row in grid)
        {
            for (int c = 0; c < cols && c < row.Length; c++)
            {
                IImmutableBrush brush = DimTrailing && c > 0 ? dim : body;
                DrawCell(context, row[c], brush, c, x0, widths, align, y);
            }
            y += rowH;
        }
    }

    /// <summary>The body row under a point, or null: header space (and its gap) is skipped,
    /// gaps between rows count as the row above them (a click between two lines of text is
    /// aimed at a row, not at typography).</summary>
    private TableRow? RowAt(Point p)
    {
        string[][] grid = Grid();
        if (grid.Length == 0) return null;
        double y = p.Y;
        if (Columns is { Length: > 0 })
        {
            y -= LineHeight() + HeaderGap;
            if (y < 0) return null;                       // the header is not a row
        }
        int row = (int)(y / LineHeight());
        if (row < 0 || row >= grid.Length) return null;
        string label = grid[row].Length > 0 ? grid[row][0].Trim() : "";
        return new TableRow(row + 1, label);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ICommand? cmd = RowCommand;
        if (cmd is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (RowAt(e.GetPosition(this)) is not { } hit) return;
        if (cmd.CanExecute(hit)) cmd.Execute(hit);
        e.Handled = true;
    }

    private void DrawCell(DrawingContext context, string text, IImmutableBrush brush,
                          int col, double[] x0, double[] widths, string align, double y)
    {
        if (text.Length == 0) return;
        FormattedText ft = Text(text, brush);
        char a = col < align.Length ? char.ToLowerInvariant(align[col]) : 'l';
        double x = a switch
        {
            'r' => x0[col] + Math.Max(0, widths[col] - ft.Width),
            'c' => x0[col] + Math.Max(0, (widths[col] - ft.Width) / 2),
            _ => x0[col],
        };
        context.DrawText(ft, new Point(x, y));
    }
}

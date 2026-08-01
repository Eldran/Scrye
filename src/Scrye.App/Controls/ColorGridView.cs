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

    public ICommand? CellCommand { get => GetValue(CellCommandProperty); set => SetValue(CellCommandProperty, value); }

    private static readonly ImmutableSolidColorBrush Unknown = new(Color.FromRgb(0x28, 0x28, 0x28));
    private readonly Dictionary<char, IImmutableBrush> _brushes = new();

    static ColorGridView()
    {
        AffectsMeasure<ColorGridView>(GridTextProperty);
        AffectsRender<ColorGridView>(GridTextProperty, PaletteProperty);
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

    protected override Size MeasureOverride(Size availableSize)
    {
        string[] rows = Rows(GridText);
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double cell = CellFor(rows, width);
        return new Size(width, rows.Length * cell);
    }

    public override void Render(DrawingContext context)
    {
        string[] rows = Rows(GridText);
        if (rows.Length == 0) return;
        double cell = CellFor(rows, Bounds.Width);
        if (cell <= 0) return;
        Dictionary<char, Color>? palette = Palette;

        // When interactive, fill a transparent background so the whole grid is hit-testable
        // (a bare Control only receives pointer events where it has drawn something).
        if (CellCommand is not null)
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        for (int r = 0; r < rows.Length; r++)
        {
            string row = rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                char ch = row[c];
                if (ch == ' ') continue;
                context.FillRectangle(BrushFor(ch, palette),
                    new Rect(c * cell, r * cell, cell - 1, cell - 1));
            }
        }
    }

    private IImmutableBrush BrushFor(char ch, Dictionary<char, Color>? palette)
    {
        if (_brushes.TryGetValue(ch, out IImmutableBrush? cached)) return cached;
        IImmutableBrush brush = palette is not null && palette.TryGetValue(ch, out Color color)
            ? new ImmutableSolidColorBrush(color)
            : Unknown;
        _brushes[ch] = brush;
        return brush;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty) _brushes.Clear();   // palette swap: rebuild brush cache
        if (change.Property == CellCommandProperty) InvalidateVisual();   // toggle hit-test background
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ICommand? cmd = CellCommand;
        if (cmd is null) return;

        string[] rows = Rows(GridText);
        double cell = CellFor(rows, Bounds.Width);
        if (cell <= 0) return;

        Point p = e.GetPosition(this);
        int col = (int)(p.X / cell);
        int row = (int)(p.Y / cell);
        if (row < 0 || row >= rows.Length) return;
        if (col < 0 || col >= rows[row].Length) return;

        var hit = new GridCell(col, row, rows[row][col]);
        if (cmd.CanExecute(hit)) cmd.Execute(hit);
        e.Handled = true;
    }
}

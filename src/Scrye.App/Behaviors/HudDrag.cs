using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;   // ToggleButton
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Scrye.App.ViewModels;

namespace Scrye.App.Behaviors;

/// <summary>
/// Makes a HUD panel draggable on the HUD canvas. Attach with
/// <c>behaviors:HudDrag.Enabled="True"</c> on the panel's root Border (inside an
/// ItemsControl whose ItemsPanel is a Canvas). Dragging the panel's title strip
/// (top ~22 px) moves it; the position is written back to the
/// <see cref="HudPanelViewModel"/> and reported for persistence on release.
///
/// The canvas child is found through the VISUAL tree (item containers are logical
/// children of the ItemsControl, so the logical Parent chain never reaches the
/// Canvas). Panels with no saved position are auto-placed on first layout: stacked
/// down the right edge — the old fixed-stack look, but every panel is movable.
/// </summary>
public static class HudDrag
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(HudDrag));

    public static bool GetEnabled(Control c) => c.GetValue(EnabledProperty);
    public static void SetEnabled(Control c, bool value) => c.SetValue(EnabledProperty, value);

    private const double HandleHeight = 22;   // the title strip acts as the drag handle
    private const double Margin = 8;          // clamp margin + stacking gap
    private static int _topZ = 1;             // bring-to-front counter

    static HudDrag()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true) Attach(control);
        });
    }

    private static void Attach(Control control)
    {
        bool placed = false;
        Point grabOffset = default;
        bool dragging = false;

        // ---- initial placement (first layout pass, when bounds are known) ----
        void OnLayoutUpdated(object? s, EventArgs e)
        {
            if (placed) return;
            if (Find(control) is not (Control item, Canvas canvas)) return;
            if (canvas.Bounds.Width <= 0 || control.Bounds.Width <= 0) return;
            placed = true;
            control.LayoutUpdated -= OnLayoutUpdated;

            if (control.DataContext is not HudPanelViewModel vm) return;
            double x, y;
            if (double.IsNaN(vm.X) || double.IsNaN(vm.Y))
            {
                // default: right edge, below the lowest sibling already placed
                x = canvas.Bounds.Width - control.Bounds.Width - Margin - 12;
                y = Margin;
                foreach (Control child in canvas.Children)
                {
                    if (ReferenceEquals(child, item) || child.Bounds.Height <= 0) continue;
                    double top = Canvas.GetTop(child);
                    if (double.IsNaN(top)) continue;               // sibling not placed yet
                    double bottom = top + child.Bounds.Height;
                    if (bottom + Margin > y) y = bottom + Margin;
                }
                vm.X = x; vm.Y = y;   // remembered in-session; persisted only after a drag
            }
            else { x = vm.X; y = vm.Y; }

            (x, y) = Clamp(x, y, control, canvas);
            Canvas.SetLeft(item, x);
            Canvas.SetTop(item, y);
        }
        control.LayoutUpdated += OnLayoutUpdated;

        // ---- dragging (title strip only) ----
        // Tunnel so a press on the title strip starts the drag even if a child would
        // otherwise take the event; presses below the strip pass through untouched.
        control.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (Find(control) is not (Control item, Canvas canvas)) return;
            if (e.GetPosition(control).Y > HandleHeight) return;   // below the title strip
            if (e.Source is Control src && IsInteractive(src, control)) return;

            // if the initial placement somehow hasn't run, anchor at the current spot
            if (double.IsNaN(Canvas.GetLeft(item)) || double.IsNaN(Canvas.GetTop(item)))
            {
                Point cur = item.TranslatePoint(default, canvas) ?? default;
                Canvas.SetLeft(item, cur.X);
                Canvas.SetTop(item, cur.Y);
            }

            dragging = true;
            grabOffset = e.GetPosition(canvas) - new Point(Canvas.GetLeft(item), Canvas.GetTop(item));
            item.ZIndex = ++_topZ;                                 // bring to front
            e.Pointer.Capture(control);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        control.PointerMoved += (s, e) =>
        {
            if (Find(control) is not (Control item, Canvas canvas)) return;
            if (!dragging)
            {
                // hand cursor over the drag handle as an affordance
                control.Cursor = e.GetPosition(control).Y <= HandleHeight
                    ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                return;
            }
            Point pos = e.GetPosition(canvas) - grabOffset;
            (double x, double y) = Clamp(pos.X, pos.Y, control, canvas);
            Canvas.SetLeft(item, x);
            Canvas.SetTop(item, y);
            e.Handled = true;
        };

        control.PointerReleased += (s, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            if (Find(control) is not (Control item, _)) return;
            if (control.DataContext is HudPanelViewModel vm)
            {
                vm.X = Canvas.GetLeft(item);
                vm.Y = Canvas.GetTop(item);
                vm.ReportMoved();                                  // persist the layout
            }
            e.Handled = true;
        };
    }

    /// <summary>Walk the VISUAL tree upward and return the ancestor that is a direct
    /// child of the hosting Canvas (the element Canvas.Left/Top position), plus the
    /// Canvas itself. Null while detached or when no Canvas hosts the panel.</summary>
    private static (Control, Canvas)? Find(Control control)
    {
        Visual? node = control;
        while (node is not null)
        {
            Visual? parent = node.GetVisualParent();
            if (parent is Canvas canvas && node is Control item) return (item, canvas);
            node = parent;
        }
        return null;
    }

    /// <summary>Keep at least a 60 px sliver and the title strip reachable inside the canvas,
    /// so a panel can never be dragged (or restored) fully off-screen.</summary>
    private static (double, double) Clamp(double x, double y, Control control, Canvas canvas)
    {
        double w = control.Bounds.Width;
        double minX = Math.Min(Margin, 60 - w);                       // may hang off the left…
        double maxX = Math.Max(minX, canvas.Bounds.Width - 60);       // …or right, 60 px visible
        double maxY = Math.Max(Margin, canvas.Bounds.Height - HandleHeight - Margin);
        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, Margin, maxY));
    }

    /// <summary>True when the press landed on a control that should keep the event
    /// (buttons, inputs, tab headers) rather than start a drag.</summary>
    private static bool IsInteractive(Control source, Control root)
    {
        for (Visual? c = source; c is not null && !ReferenceEquals(c, root); c = c.GetVisualParent())
            if (c is Button or TextBox or CheckBox or ComboBox or TabItem or ToggleButton) return true;
        return false;
    }
}

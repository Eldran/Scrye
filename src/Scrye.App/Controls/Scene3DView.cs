using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Scrye.Core.Plugins;

namespace Scrye.App.Controls;

/// <summary>
/// Draws a <c>scene3d</c> widget's scene (plugin API 1.24): rooms as tiles and links as lines,
/// seen at an angle through a <see cref="SceneCamera"/>. All the maths — parsing, projection,
/// fitting, picking, shading — lives in Scrye.Core next to the camera, where it is tested;
/// this control only draws what the camera says and turns the pointer into camera moves.
///
/// <para>The pointer: drag to turn the map (across) and tilt it (up/down); Shift+drag, or a
/// middle-button drag, pans; the wheel zooms; a double-click on empty space resets the view.
/// A click on a room (a press and release that did not move) runs <see cref="ClickCommand"/>
/// with the room's id; a right-click runs <see cref="ContextCommand"/>, whose plugin may
/// answer with a menu (<see cref="PendingMenu"/>); moving onto a different room runs
/// <see cref="HoverCommand"/> with its id, and with "" when the pointer leaves the rooms.</para>
/// </summary>
public class Scene3DView : Control
{
    public static readonly StyledProperty<Scene3D?> SceneProperty =
        AvaloniaProperty.Register<Scene3DView, Scene3D?>(nameof(Scene));

    public static readonly StyledProperty<Dictionary<char, Color>?> PaletteProperty =
        AvaloniaProperty.Register<Scene3DView, Dictionary<char, Color>?>(nameof(Palette));

    /// <summary>The view's height in pixels; it takes the full width it is offered.</summary>
    public static readonly StyledProperty<double> ViewHeightProperty =
        AvaloniaProperty.Register<Scene3DView, double>(nameof(ViewHeight), 320);

    /// <summary>Run with the clicked node's id (a string).</summary>
    public static readonly StyledProperty<ICommand?> ClickCommandProperty =
        AvaloniaProperty.Register<Scene3DView, ICommand?>(nameof(ClickCommand));

    /// <summary>Run with the id of the node the pointer moved onto, "" when it left them.</summary>
    public static readonly StyledProperty<ICommand?> HoverCommandProperty =
        AvaloniaProperty.Register<Scene3DView, ICommand?>(nameof(HoverCommand));

    /// <summary>Run with the right-clicked node's id.</summary>
    public static readonly StyledProperty<ICommand?> ContextCommandProperty =
        AvaloniaProperty.Register<Scene3DView, ICommand?>(nameof(ContextCommand));

    /// <summary>A menu the right-click callback handed back (API 1.18) — shown at the pointer
    /// on every non-null change, exactly as the colour grid does.</summary>
    public static readonly StyledProperty<IReadOnlyList<MenuEntry>?> PendingMenuProperty =
        AvaloniaProperty.Register<Scene3DView, IReadOnlyList<MenuEntry>?>(nameof(PendingMenu));

    public static readonly StyledProperty<ICommand?> MenuChoiceCommandProperty =
        AvaloniaProperty.Register<Scene3DView, ICommand?>(nameof(MenuChoiceCommand));

    public Scene3D? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public Dictionary<char, Color>? Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public double ViewHeight { get => GetValue(ViewHeightProperty); set => SetValue(ViewHeightProperty, value); }
    public ICommand? ClickCommand { get => GetValue(ClickCommandProperty); set => SetValue(ClickCommandProperty, value); }
    public ICommand? HoverCommand { get => GetValue(HoverCommandProperty); set => SetValue(HoverCommandProperty, value); }
    public ICommand? ContextCommand { get => GetValue(ContextCommandProperty); set => SetValue(ContextCommandProperty, value); }
    public IReadOnlyList<MenuEntry>? PendingMenu { get => GetValue(PendingMenuProperty); set => SetValue(PendingMenuProperty, value); }
    public ICommand? MenuChoiceCommand { get => GetValue(MenuChoiceCommandProperty); set => SetValue(MenuChoiceCommandProperty, value); }

    private readonly SceneCamera _camera = new();
    /// <summary>The last frame's projection, for picking. Null until something is drawn.</summary>
    private ProjectedNode[]? _projected;
    private Scene3D? _projectedScene;

    // a press in progress: where it started and where the pointer was last
    private Point? _pressAt;
    private Point _lastAt;
    private bool _dragging, _panning;
    /// <summary>A press must move this far before it is a drag rather than a click.</summary>
    private const double DragSlop = 4;

    /// <summary>The node ringed as hovered (an index into the CURRENT scene, -1 none).</summary>
    private int _hover = -1;
    /// <summary>The id last reported to <see cref="HoverCommand"/>, "" for none. Kept apart from
    /// <see cref="_hover"/> because a new scene re-indexes everything but the plugin still
    /// believes the pointer is over the room it last heard: the leave must still be sent.</summary>
    private string _hoverId = "";
    /// <summary>The press in progress was the second of a double-click.</summary>
    private bool _secondClick;

    private static readonly ImmutableSolidColorBrush Backdrop = new(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
    private static readonly Color Unknown = Color.FromRgb(0x60, 0x60, 0x60);
    private static readonly ImmutablePen FocusRing = new(new ImmutableSolidColorBrush(Color.FromRgb(0xF4, 0xEF, 0xFF)), 1.5);
    private static readonly ImmutablePen HoverRing = new(new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xF4, 0xEF, 0xFF)), 1);

    // Brushes and pens per (palette char, brightness step). Brightness is quantised to
    // ShadeSteps so a map of a thousand rooms needs a few dozen brushes, not a thousand.
    private const int ShadeSteps = 16;
    private readonly Dictionary<(char, int), IImmutableBrush> _brushes = new();
    private readonly Dictionary<(char, int, double), ImmutablePen> _pens = new();

    static Scene3DView()
    {
        AffectsMeasure<Scene3DView>(ViewHeightProperty);
        AffectsRender<Scene3DView>(SceneProperty, PaletteProperty);
        // NOT focusable, and the release is never marked handled: the window's "click anywhere
        // and just start typing" runs on the release bubbling up, and a map that kept the
        // focus would swallow what you type next. The view takes no keys, so it needs none.
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        double h = Math.Clamp(ViewHeight > 0 ? ViewHeight : 320, 80, 2000);
        return new Size(w, h);
    }

    private Color ColourOf(char ch)
    {
        Dictionary<char, Color>? palette = Palette;
        return palette is not null && palette.TryGetValue(ch, out Color c) ? c : Unknown;
    }

    private static int Step(double shade) => (int)Math.Round(Math.Clamp(shade, 0, 1) * ShadeSteps);

    private static Color Shaded(Color c, int step) =>
        Color.FromArgb((byte)(c.A * step / ShadeSteps), c.R, c.G, c.B);

    private IImmutableBrush BrushFor(char ch, int step)
    {
        if (_brushes.TryGetValue((ch, step), out IImmutableBrush? b)) return b;
        b = new ImmutableSolidColorBrush(Shaded(ColourOf(ch), step));
        _brushes[(ch, step)] = b;
        return b;
    }

    private ImmutablePen PenFor(char ch, int step, double thickness)
    {
        if (_pens.TryGetValue((ch, step, thickness), out ImmutablePen? p)) return p;
        p = new ImmutablePen(BrushFor(ch, step), thickness);
        _pens[(ch, step, thickness)] = p;
        return p;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // a backdrop the size of the view: it marks the area and makes all of it hit-testable
        context.FillRectangle(Backdrop, new Rect(size));
        Scene3D? scene = Scene;
        if (scene is null || scene.IsEmpty || size.Width <= 0 || size.Height <= 0)
        {
            _projected = null; _projectedScene = null;
            return;
        }

        using (context.PushClip(new Rect(size)))
        {
            _camera.Sync(scene, size.Width, size.Height);
            ProjectedNode[] p = _camera.Project(scene, size.Width, size.Height);
            _projected = p; _projectedScene = scene;

            double min = double.MaxValue, max = double.MinValue;
            foreach (ProjectedNode n in p) { min = Math.Min(min, n.Depth); max = Math.Max(max, n.Depth); }

            // links first, each as bright as the dimmer of its two ends
            double thick = Math.Round(Math.Clamp(_camera.Scale * 0.12, 1, 3), 1);
            foreach (SceneLink l in scene.Links)
            {
                double shade = Math.Min(SceneCamera.Shade(scene, p, l.A, min, max),
                                        SceneCamera.Shade(scene, p, l.B, min, max));
                context.DrawLine(PenFor(l.Ch, Step(shade), thick),
                    new Point(p[l.A].X, p[l.A].Y), new Point(p[l.B].X, p[l.B].Y));
            }

            // then the rooms, back to front so nearer ones cover farther ones
            foreach (int i in SceneCamera.DrawOrder(p))
            {
                ProjectedNode n = p[i];
                double r = n.Radius;
                var box = new Rect(n.X - r, n.Y - r, 2 * r, 2 * r);
                context.DrawRectangle(BrushFor(scene.Nodes[i].Ch, Step(SceneCamera.Shade(scene, p, i, min, max))),
                    null, box, r * 0.3, r * 0.3);
                if (i == scene.Focus) context.DrawRectangle(null, FocusRing, box.Inflate(2), r * 0.4, r * 0.4);
                else if (i == _hover) context.DrawRectangle(null, HoverRing, box.Inflate(1.5), r * 0.4, r * 0.4);
            }
        }
    }

    /// <summary>The node under a point on the last frame drawn, or -1.</summary>
    private int NodeAt(Point at)
    {
        if (_projected is null || _projectedScene is null || !ReferenceEquals(_projectedScene, Scene)) return -1;
        return SceneCamera.Pick(_projectedScene, _projected, at.X, at.Y);
    }

    private string IdOf(int i) =>
        i >= 0 && _projectedScene is not null && i < _projectedScene.Nodes.Count ? _projectedScene.Nodes[i].Id : "";

    private static void Run(ICommand? cmd, string arg)
    {
        if (cmd is not null && cmd.CanExecute(arg)) cmd.Execute(arg);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point at = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            int hit = NodeAt(at);
            if (hit >= 0) Run(ContextCommand, IdOf(hit));
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed) return;

        if (props.IsLeftButtonPressed && e.ClickCount >= 2 && NodeAt(at) < 0)
        {
            // double-click on empty space: back to the starting angle, the whole map in view
            _camera.Reset();
            if (Scene is { IsEmpty: false } s) _camera.Fit(s, Bounds.Width, Bounds.Height);
            _pressAt = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _pressAt = at;
        _lastAt = at;
        _dragging = false;
        _panning = props.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _secondClick = e.ClickCount >= 2;     // a double-click on a room is ONE click on it
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point at = e.GetPosition(this);

        if (_pressAt is Point start)
        {
            if (!_dragging)
            {
                Vector moved = at - start;
                if (Math.Abs(moved.X) < DragSlop && Math.Abs(moved.Y) < DragSlop) return;
                _dragging = true;
            }
            Vector d = at - _lastAt;
            _lastAt = at;
            if (_panning) _camera.Pan(d.X, d.Y);
            else _camera.Rotate(d.X, d.Y);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        int hit = NodeAt(at);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
        string id = IdOf(hit);
        if (id == _hoverId) return;                  // the same room: per-pixel silence
        _hoverId = id;
        Run(HoverCommand, id);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressAt is null) return;
        bool click = !_dragging && !_panning && !_secondClick && e.InitialPressMouseButton == MouseButton.Left;
        _pressAt = null;
        _dragging = false;
        e.Pointer.Capture(null);
        if (click)
        {
            int hit = NodeAt(e.GetPosition(this));
            if (hit >= 0) Run(ClickCommand, IdOf(hit));
        }
        // not handled: the window gives the keyboard back to the command line on this release
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressAt = null;
        _dragging = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover >= 0) { _hover = -1; Cursor = Cursor.Default; InvalidateVisual(); }
        if (_hoverId.Length == 0) return;
        _hoverId = "";
        Run(HoverCommand, "");
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        _camera.Zoom(Math.Pow(1.15, e.Delta.Y));
        InvalidateVisual();
        e.Handled = true;                            // the wheel zooms the map, not the panel
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PaletteProperty) { _brushes.Clear(); _pens.Clear(); }
        // the ring's index belongs to the old scene; the reported id stays, so a later move
        // off the rooms (or out of the view) still tells the plugin
        if (change.Property == SceneProperty) _hover = -1;
        if (change.Property == PendingMenuProperty
            && change.GetNewValue<IReadOnlyList<MenuEntry>?>() is { Count: > 0 } menu)
            PluginMenu.Show(this, menu, MenuChoiceCommand);
    }
}

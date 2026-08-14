using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Scrye.App.Behaviors;

/// <summary>
/// Runs a command on a right-click (plugin API 1.9's <c>onRightClick</c>). Attach with
/// <c>behaviors:RightClick.Command="{Binding ContextCommand}"</c>.
///
/// <para>This exists because Avalonia's <see cref="Button"/> only raises Click — and so only
/// runs its <see cref="Button.Command"/> — for the left button, and a HUD button widget needs a
/// second, distinct action. Binding a null command (the normal case: a widget that declared no
/// <c>onRightClick</c>) leaves the control completely untouched.</para>
///
/// <para>Fires on <b>release</b> rather than press, and only when the pointer is still inside
/// the control, so a right-press dragged off the button cancels the way a left click does. The
/// event is marked handled to stop a context menu further up the tree opening on top of
/// whatever the plugin just did.</para>
/// </summary>
public static class RightClick
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(RightClick));

    public static ICommand? GetCommand(Control c) => c.GetValue(CommandProperty);
    public static void SetCommand(Control c, ICommand? value) => c.SetValue(CommandProperty, value);

    static RightClick()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            // Templates recycle controls, so a rebind must not stack a second handler.
            control.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            if (e.NewValue is ICommand)
                control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        });
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not Control control) return;
        if (GetCommand(control) is not { } cmd) return;

        // released outside the control: treat it as a cancelled click, not an activation
        Point p = e.GetPosition(control);
        if (p.X < 0 || p.Y < 0 || p.X > control.Bounds.Width || p.Y > control.Bounds.Height) return;

        if (cmd.CanExecute(null)) cmd.Execute(null);
        e.Handled = true;
    }
}

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace Scrye.App.Behaviors;

/// <summary>
/// Attached behavior: when <c>Enabled</c>, a <see cref="ListBox"/> scrolls to the
/// newest item as items are appended, so a live (or replaying) timeline follows the
/// tail instead of staying put. Pausing the source (Debugger.Paused) stops new items
/// arriving, which naturally stops the scrolling so you can read history.
///
/// Not a <c>static</c> class: it is used as the owner type argument to
/// <see cref="AvaloniaProperty.RegisterAttached"/>, and a static type can't be a
/// generic type argument (CS0718). Members are static; construction is blocked.
/// </summary>
public sealed class AutoScroll
{
    private AutoScroll() { }

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<AutoScroll, ListBox, bool>("Enabled");

    public static void SetEnabled(ListBox o, bool value) => o.SetValue(EnabledProperty, value);
    public static bool GetEnabled(ListBox o) => o.GetValue(EnabledProperty);

    static AutoScroll()
    {
        EnabledProperty.Changed.AddClassHandler<ListBox>(OnEnabledChanged);
    }

    private static void OnEnabledChanged(ListBox lb, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            lb.ItemsView.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && lb.ItemCount > 0)
                    lb.ScrollIntoView(lb.ItemCount - 1);
            };
    }
}

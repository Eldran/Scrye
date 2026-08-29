using System.Windows.Input;
using Avalonia.Controls;

namespace Scrye.App.Controls;

/// <summary>
/// Shows a plugin-provided context menu (API 1.18) as a native Avalonia flyout. One shared
/// implementation so a menu on a map grid and a menu on a table row can never drift apart.
///
/// <para>The flow is asynchronous by construction: the right-click posts the plugin callback
/// onto the session loop, the callback's returned entries come back to the UI thread, and only
/// then is this called — so the menu opens a beat after the press, anchored at the pointer's
/// current position (<c>showAtPointer</c>), which in practice is where the user still is. The
/// chosen entry's command goes through <paramref name="choice"/>, which hosts wire to the same
/// run-as-if-typed path as <c>click=</c> markup — the plugin's own aliases get first refusal.</para>
/// </summary>
internal static class PluginMenu
{
    public static void Show(Control anchor, IReadOnlyList<Scrye.Core.Plugins.MenuEntry> entries, ICommand? choice)
    {
        var flyout = new MenuFlyout();
        foreach (Scrye.Core.Plugins.MenuEntry entry in entries)
        {
            if (entry.IsSeparator) { flyout.Items.Add(new Separator()); continue; }
            // an entry with a label but no command renders disabled — a caption line
            var item = new MenuItem { Header = entry.Label, IsEnabled = entry.Command is not null };
            string? cmd = entry.Command;
            item.Click += (_, _) =>
            {
                if (cmd is not null && choice is not null && choice.CanExecute(cmd)) choice.Execute(cmd);
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor, showAtPointer: true);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Scrye.App.ViewModels;

namespace Scrye.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ToastRaised += () => { if (!IsActive) FlashTaskbar(); };
        };
        // pane-tab right-click menu (see OnPanePointerPressed for why this is code, not XAML)
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnPanePointerPressed,
                   Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // keyboard macros: window-level bubble handler — runs only for keys a focused
        // control didn't already consume (so Enter/Ctrl+F/typing are untouched).
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown,
                   Avalonia.Interactivity.RoutingStrategies.Bubble);
        // PgUp/PgDn page the scrollback. TUNNEL, and at the window: the command line is a
        // TextBox, a TextBox is entitled to those keys, and it takes them before a KeyDown
        // handler on the box itself is ever reached — which is why paging from the command
        // line did nothing at all. Coming down the tree instead of up puts the scrollback
        // first, which is what a MUD client's page keys are for.
        AddHandler(InputElement.KeyDownEvent, OnPagingKeyDown,
                   Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // "click anywhere and just start typing" (MUSHclient / Mudlet). Bubble, and on
        // RELEASE rather than press: a press handler would move focus out from under a
        // drag-selection in the output while it was still being made.
        AddHandler(InputElement.PointerReleasedEvent, OnWindowPointerReleased,
                   Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Give the command line focus after a click that had no better claim on it, so you can
    /// click the output (or the map, or a HUD panel) and start typing — what MUSHclient and
    /// Mudlet both do.
    ///
    /// <para>"No better claim" is the whole design. A control that <em>uses</em> the keyboard
    /// keeps what it was given: text boxes, buttons and toggles, lists and tabs you may want to
    /// arrow through, scrollbars and sliders you may still be dragging, menus. So does an open
    /// Settings or Edit-world overlay, which is nothing but such controls. And so does the
    /// output pane when the click finished a selection — <see cref="Controls.OutputView"/>
    /// handles Ctrl+C itself, so stealing focus there would send the copy to the input box.</para>
    /// </summary>
    private void OnWindowPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (DataContext is not MainWindowViewModel vm || vm.Active is not WorldViewModel world) return;
        if (vm.Settings is not null || vm.Editor is not null) return;   // an overlay owns the keyboard
        if (e.Source is not Avalonia.Visual source) return;
        if (!MayTakeFocus(source)) return;

        // Once the click has finished settling. Background rather than Input because clicking
        // a world TAB is one of the cases: the new world's content has to be realised before
        // its command line can be found at all. Same reason FocusFindBox posts at Background.
        Dispatcher.UIThread.Post(() =>
        {
            TextBox? input = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Name == "CommandInput"
                                     && ReferenceEquals(t.DataContext, world)
                                     && t.IsEffectivelyVisible);
            if (input is null || input.IsFocused) return;
            input.Focus();
            input.CaretIndex = (input.Text ?? "").Length;
        }, DispatcherPriority.Background);
    }

    /// <summary>Whether a click that landed on <paramref name="source"/> may be treated as
    /// "nothing in particular" and hand the keyboard to the command line. Walks up from what
    /// was hit, so a click on a button's label or a scrollbar's thumb answers for the whole
    /// control rather than for the scrap of visual it landed on.</summary>
    private static bool MayTakeFocus(Avalonia.Visual? source)
    {
        for (Avalonia.Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            switch (v)
            {
                // A button that ASKS for the focus to go back to the command line: the
                // "back to bottom" chip, whose whole purpose is "take me back to live".
                case Button b when b.Classes.Contains("refocus"):
                    return true;

                // Controls that keep using the keyboard after you have clicked them.
                case TextBox:
                // Button, not a ButtonBase: Avalonia 12 has no ButtonBase, because Button IS
                // the root here -- ToggleButton derives from it (and CheckBox and RadioButton
                // from ToggleButton), unlike WPF where they are siblings under ButtonBase.
                // So this one case catches every button-ish control in the window.
                case Button:
                case ComboBox:
                case ListBox:
                case TreeView:                                  // the world list: arrow keys belong to it
                case MenuItem:
                case Avalonia.Controls.Primitives.ScrollBar:
                case Avalonia.Controls.Primitives.Thumb:
                case Slider:
                    return false;

                // The output pane: a plain click hands over, a click that ended a
                // drag-selection does not (Ctrl+C has to reach it).
                case Controls.OutputView output:
                    return !output.HasSelection;
            }
        }
        // Everything else, deliberately including tab headers: clicking a world tab is how you
        // switch worlds, and having to click a second time before you can type is the exact
        // annoyance this whole thing is about.
        return true;
    }

    /// <summary>Window-level keys: F11 toggles fullscreen; otherwise fire a keyboard macro
    /// for the active world if the pressed key is bound.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;

        // Enter in a companion "add" box (a watched name, a notifying channel) submits it, the
        // same as pressing Add. Handled here rather than with KeyDown= in the template because
        // that box lives inside NESTED DataTemplates, which the Avalonia 11 XAML compiler
        // cannot compile handlers for (see OnPanePointerPressed).
        if (e.Key == Key.Enter && e.Source is TextBox { DataContext: PluginNotifyRow row } box
            && row.Add is not null)
        {
            row.Add.Execute(box.Text ?? "");
            box.Text = "";                   // the value moved into the list; don't leave it staged
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
            return;
        }

        if (DataContext is MainWindowViewModel vm && vm.Active is WorldViewModel world
            && world.TryFireMacro(e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    /// <summary>Right-click on a capture-pane tab header: open the move/float/close menu.
    /// Attached as a window-level tunnel handler (NOT a template event — the Avalonia
    /// 11.0 XAML compiler crashes on event handlers in nested DataTemplates). Header
    /// clicks resolve to a TabItem whose DataContext is a CapturePaneViewModel; pane
    /// CONTENT lives in the TabControl's content presenter, not inside the TabItem,
    /// so this fires for headers only.</summary>
    private void OnPanePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (e.Source is not Avalonia.Visual v) return;
        TabItem? tab = v.FindAncestorOfType<TabItem>(includeSelf: true);
        if (tab?.DataContext is not CapturePaneViewModel pane) return;

        MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            return mi;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Move to bottom", () => pane.MoveBottomCommand.Execute(null)));
        menu.Items.Add(Item("Move to right side", () => pane.MoveRightCommand.Execute(null)));
        menu.Items.Add(Item("Float as window", () => pane.FloatCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close pane", () => pane.CloseCommand.Execute(null)));
        menu.Open(tab);
        e.Handled = true;
    }

    /// <summary>Dismiss a toast on click.</summary>
    private void OnToastPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ToastViewModel toast } &&
            DataContext is MainWindowViewModel vm)
            vm.DismissToast(toast);
    }

    // ---- Windows taskbar flash (no-op elsewhere) ------------------------------

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private void FlashTaskbar()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr? handle = TryGetPlatformHandle()?.Handle;
            if (handle is not IntPtr h || h == IntPtr.Zero) return;
            var info = new FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
                hwnd = h,
                dwFlags = 3,      // FLASHW_ALL (caption + taskbar)
                uCount = 3,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { /* cosmetic only */ }
    }

    // tab-completion cycling state (single active input at a time)
    private TextBox? _tabBox;
    private int _tabAnchor = -1;
    private IReadOnlyList<string> _tabMatches = Array.Empty<string>();
    private int _tabIndex = -1;
    private string _tabLast = "";

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not WorldViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.SubmitCommand.Execute(null);
                // With "keep the last command" on, the text is still in the box: select it, so
                // the next keystroke replaces it and Enter on its own repeats it. Asks the
                // view-model rather than box.Text, so it does not depend on when the binding
                // got round to updating.
                if (!string.IsNullOrEmpty(vm.Input)) box.SelectAll();
                e.Handled = true;
                break;
            case Key.Up:                                  // recall previous (Ctrl/Alt: filtered)
                Recall(box, vm.HistoryPrevious(box.Text ?? "", RecallPrefix(box, e.KeyModifiers)));
                e.Handled = true;
                break;
            case Key.Down:                                // recall next / restore draft
                Recall(box, vm.HistoryNext(box.Text ?? ""));
                e.Handled = true;
                break;
            case Key.Tab:                                 // complete the word under the caret
                HandleTab(box, vm);
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):   // open find bar
                vm.OpenFind();
                FocusFindBox(box);
                e.Handled = true;
                break;
            case Key.Escape:                              // clear the input
                box.Text = "";
                box.CaretIndex = 0;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// PgUp/PgDn belong to the scrollback unless something on the way down genuinely needs
    /// them. Runs on the tunnel so it sees the key before any focused control does, and walks
    /// DOWN from the window to whatever has focus — a deny-list, like <see cref="MayTakeFocus"/>
    /// and for the same reason: the scrollback is this window's default subject, and the
    /// exceptions are the few controls with pages of their own.
    ///
    /// <para>An overlay (Settings, Edit world) owns the keyboard outright while it is open, so
    /// nothing here fires for it.</para>
    /// </summary>
    private void OnPagingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown)) return;
        if (DataContext is not MainWindowViewModel vm || vm.Active is not WorldViewModel world) return;
        if (vm.Settings is not null || vm.Editor is not null) return;

        int direction = e.Key == Key.PageUp ? -1 : 1;
        for (Avalonia.Visual? v = e.Source as Avalonia.Visual; v is not null; v = v.GetVisualParent())
        {
            switch (v)
            {
                // Inside a pane: page THAT pane. A capture pane shows different text and keeps
                // its own place in it, so paging this world's scrollback would scroll the wrong
                // window out from under you.
                case Controls.TerminalPane pane:
                    pane.Page(direction);
                    e.Handled = true;
                    return;

                // Controls with pages of their own. A single-line TextBox is NOT one of them:
                // the command line is one line tall, so there is no page in it to move through,
                // and it is where you are standing when you want to look at what just went past.
                case TextBox box when box.AcceptsReturn:
                case ListBox:
                case TreeView:
                case ComboBox:
                case MenuItem:
                    return;
            }
        }

        PageScrollback(world, direction);
        e.Handled = true;
    }

    /// <summary>Page this world's scrollback from somewhere that is not the output pane —
    /// the command line, where you spend most of your time.
    ///
    /// <para>Finds the pane by its SOURCE rather than by walking up from the box: capture
    /// panes and float windows are terminal panes too, and the one that should move is the
    /// one showing this world's scrollback.</para></summary>
    private void PageScrollback(WorldViewModel vm, int direction)
    {
        foreach (Controls.TerminalPane pane in this.GetVisualDescendants().OfType<Controls.TerminalPane>())
        {
            if (!ReferenceEquals(pane.Source, vm.Scrollback) || !pane.IsEffectivelyVisible) continue;
            pane.Page(direction);
            return;
        }
    }

    /// <summary>Enter in a plugin HUD input widget submits its value (mirrors the command line;
    /// a KeyBinding proved unreliable inside the widget DataTemplate).</summary>
    private void OnInputWidgetKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not InputWidgetViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.Text = box.Text ?? "";        // commit the latest keystrokes before submitting
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A command link was clicked — an MXP link from the MUD, or a <c>click=</c> run a plugin
    /// wrote with the colour markup. Either way it runs the way typing it would, so a plugin's
    /// own aliases get first refusal.
    /// </summary>
    private void OnCommandLinkClicked(object? sender, Controls.CommandLinkClickedEventArgs e)
    {
        if (sender is not Control c) return;
        // The main output sits on the world, but a capture pane's DataContext is the pane —
        // so walk up until a world turns up rather than silently doing nothing there.
        WorldViewModel? vm = c.DataContext as WorldViewModel;
        if (vm is null)
            foreach (Control anc in c.GetVisualAncestors().OfType<Control>())
                if (anc.DataContext is WorldViewModel found) { vm = found; break; }
        vm?.HandleCommandLink(e.Command, e.Prompt);
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not WorldViewModel vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.Find.PrevCommand.Execute(null);
                else vm.Find.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Find.CloseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void HandleTab(TextBox box, WorldViewModel vm)
    {
        string text = box.Text ?? "";
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        int start = caret;
        while (start > 0 && IsWordChar(text[start - 1])) start--;

        bool continuing = ReferenceEquals(_tabBox, box) && _tabAnchor == start
                          && _tabMatches.Count > 0 && text == _tabLast;
        if (!continuing)
        {
            string stub = text.Substring(start, caret - start);
            if (stub.Length == 0) return;
            IReadOnlyList<string> matches = vm.Complete(stub);
            if (matches.Count == 0) return;
            _tabBox = box; _tabAnchor = start; _tabMatches = matches; _tabIndex = 0;
        }
        else
        {
            _tabIndex = (_tabIndex + 1) % _tabMatches.Count;
        }

        string completion = _tabMatches[_tabIndex];
        int end = _tabAnchor;
        while (end < text.Length && IsWordChar(text[end])) end++;
        string before = text.Substring(0, _tabAnchor);
        string after = text.Substring(end);
        box.Text = before + completion + after;
        box.CaretIndex = before.Length + completion.Length;
        _tabLast = box.Text;
    }

    private void FocusFindBox(Control from)
    {
        // The find TextBox lives in the same tab template as the input box; focus it
        // once its bar has become visible.
        Dispatcher.UIThread.Post(() =>
        {
            TopLevel? top = TopLevel.GetTopLevel(from);
            TextBox? findBox = top?.GetVisualDescendants().OfType<TextBox>()
                                  .FirstOrDefault(t => t.Name == "FindBox" && t.IsVisible);
            findBox?.Focus();
        }, DispatcherPriority.Background);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '-';

    /// <summary>What an Up should filter the history by — empty for the whole history.
    ///
    /// <para><b>Plain Up/Down is the whole history</b>, always. It is the oldest habit in every
    /// shell and every MUD client, and answering a narrower question with it is worse than
    /// asking for a second key: you reach for Up to get back to something, and a filter you did
    /// not ask for looks like the history has holes in it.</para>
    ///
    /// <para>Hold <b>Ctrl or Alt</b> and the walk is filtered by the text BEFORE the caret
    /// instead, so you can park mid-line and filter on a stem: type <c>vtrade </c>, hold Alt,
    /// press Up, and you cycle only the vtrade commands. Alt+Up is MUSHclient's own key for
    /// this; Ctrl+Up does the same thing because it is a gesture this already shipped with.</para>
    ///
    /// <para>A fully-selected box counts as empty even with the modifier down, because the next
    /// keystroke would replace it anyway: with "keep the last command" on, the box holds the
    /// command selected after Enter, and anchoring on the whole thing would match only exact
    /// repeats of it.</para></summary>
    private static string RecallPrefix(TextBox box, KeyModifiers mods)
    {
        if (!mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Alt)) return "";
        string text = box.Text ?? "";
        if (text.Length == 0) return "";
        if (Math.Abs(box.SelectionEnd - box.SelectionStart) >= text.Length) return "";
        return text.Substring(0, Math.Clamp(box.CaretIndex, 0, text.Length));
    }

    /// <summary>Put a recalled command in the box.
    ///
    /// <para>Nothing has to be flagged around this write. The walk ends when the box no longer
    /// holds what the last step put there, and <c>CommandHistory</c> works that out from
    /// the text it is handed — so our own write cannot be mistaken for the user editing, which
    /// is exactly what a flag held across this assignment failed to guarantee.</para></summary>
    private static void Recall(TextBox box, string? text)
    {
        if (text is null) return;
        box.Text = text;
        box.CaretIndex = text.Length;   // caret to end
    }
}

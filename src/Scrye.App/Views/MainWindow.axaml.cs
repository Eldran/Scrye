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
    }

    /// <summary>Fire a keyboard macro for the active world if the pressed key is bound.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
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
                e.Handled = true;
                break;
            case Key.Up:                                  // recall previous command
                Recall(box, vm.HistoryPrevious(box.Text ?? ""));
                e.Handled = true;
                break;
            case Key.Down:                                // recall next / restore draft
                Recall(box, vm.HistoryNext());
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

    /// <summary>An MXP command link was clicked in a world's output.</summary>
    private void OnCommandLinkClicked(object? sender, Controls.CommandLinkClickedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not WorldViewModel vm) return;
        vm.HandleCommandLink(e.Command, e.Prompt);
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

    private static void Recall(TextBox box, string? text)
    {
        if (text is null) return;
        box.Text = text;
        box.CaretIndex = text.Length;   // caret to end
    }
}

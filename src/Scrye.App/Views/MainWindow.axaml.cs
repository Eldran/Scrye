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
    public MainWindow() => InitializeComponent();

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

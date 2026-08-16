using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace Scrye.App.ViewModels;

/// <summary>A collapsible group heading in a rule list. Not a rule — see <see cref="RuleListViewModel"/>.</summary>
public sealed class RuleGroupHeader : ViewModelBase
{
    public string Title { get; }
    public int Count { get; }
    public bool IsExpanded { get; }

    /// <summary>▾ open, ▸ closed — drawn to the left of the title.</summary>
    public string Glyph => IsExpanded ? "▾" : "▸";
    public string Label => $"{Title}  ({Count})";

    public RelayCommand ToggleCommand { get; }

    public RuleGroupHeader(string title, int count, bool expanded, Action toggle)
    {
        Title = title;
        Count = count;
        IsExpanded = expanded;
        ToggleCommand = new RelayCommand(toggle);
    }
}

/// <summary>
/// The presentation layer for one rule list — triggers, aliases, timers, sequences, macros or
/// variables. It sorts, filters and groups <b>for display only</b>; the underlying collection it
/// wraps stays exactly as it was, and that collection is still what gets written to the profile.
///
/// <para><b>Why display-only matters.</b> <see cref="Scrye.Core.Automation.AutomationEngine"/>
/// re-sorts rules by their <c>Sequence</c> when it loads them, so what you see here has never
/// decided match order. But its sort is not stable, so among rules sharing a Sequence (100 by
/// default) the order is already unspecified — physically reordering the saved list could
/// therefore change which of two same-priority triggers wins when one has KeepEvaluating off.
/// Sorting the view costs nothing and risks nothing; sorting the file risks that.</para>
///
/// <para><b>The flattening.</b> Groups are rendered as ordinary rows — <see cref="RuleGroupHeader"/>
/// items interleaved with the rules — rather than as nested lists. One ListBox means one
/// selection, which is what the detail pane on the right binds to. Nested lists would each own
/// their own selection and the pane would have to guess which one won.</para>
///
/// <para>Non-generic on purpose: the item type only matters to the caller's accessors, and
/// keeping generics out of the XAML avoids the compiled-binding corner they live in.</para>
/// </summary>
public sealed class RuleListViewModel : ViewModelBase
{
    /// <summary>Shown for rules with no group set. Sorts last, so the named groups lead.</summary>
    public const string Ungrouped = "(no group)";

    private readonly IEnumerable _source;
    private readonly Func<object, string> _nameOf;
    private readonly Func<object, string?>? _groupOf;      // null: this list does not group
    private readonly Func<object, string> _subtitleOf;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private bool _rebuilding;

    /// <summary>Headers plus rules, in display order. Bind a ListBox to this.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    /// <summary>True when this list has groups to show — drives the header row's visibility.</summary>
    public bool Groupable => _groupOf is not null;

    public RuleListViewModel(IEnumerable source,
                             Func<object, string> nameOf,
                             Func<object, string> subtitleOf,
                             Func<object, string?>? groupOf = null)
    {
        _source = source;
        _nameOf = nameOf;
        _subtitleOf = subtitleOf;
        _groupOf = groupOf;
        if (source is INotifyCollectionChanged incc) incc.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    // ---- filter ---------------------------------------------------------------------------

    private string _filter = "";
    /// <summary>Substring match over name, subtitle and group. Empty shows everything.</summary>
    public string Filter
    {
        get => _filter;
        set { if (SetField(ref _filter, value ?? "")) Rebuild(); }
    }

    public bool HasFilter => _filter.Length > 0;

    // ---- selection ------------------------------------------------------------------------

    private object? _selected;
    /// <summary>What the ListBox has selected — a rule OR a header.</summary>
    public object? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            // A header is a control, not a thing to edit: clicking one must leave the detail
            // pane showing whatever rule you were last looking at.
            if (value is RuleGroupHeader || value is null) return;
            SelectedRow = value;
        }
    }

    private object? _selectedRow;
    /// <summary>The last selected RULE, which is what the detail pane follows.</summary>
    public object? SelectedRow
    {
        get => _selectedRow;
        private set
        {
            object? previous = _selectedRow;
            if (!SetField(ref _selectedRow, value)) return;
            // Moving off a rule is the moment its edits are finished, so this is where a rename
            // or a group change is allowed to re-sort the list. Doing it on every keystroke
            // would make the list jump under the cursor while you type a name.
            if (previous is not null && NeedsRebuild(previous)) Rebuild();
        }
    }

    /// <summary>Select a rule from code (Add, or the caller's typed property).</summary>
    public void Select(object? row)
    {
        _selectedRow = row;
        OnPropertyChanged(nameof(SelectedRow));
        // A freshly added rule can be hidden by an active filter; clearing it is friendlier than
        // silently selecting something the user cannot see.
        if (row is not null && _filter.Length > 0 && !Matches(row)) Filter = "";
        if (row is not null && !Rows.Contains(row)) Rebuild();
        SetField(ref _selected, row, nameof(Selected));
    }

    // ---- building the display list ---------------------------------------------------------

    private readonly Dictionary<object, (string Name, string Group)> _shape = new();

    private bool NeedsRebuild(object row)
    {
        if (!_shape.TryGetValue(row, out (string Name, string Group) was)) return true;
        return was.Name != _nameOf(row) || was.Group != GroupOf(row);
    }

    private string GroupOf(object row)
    {
        if (_groupOf is null) return "";
        string g = (_groupOf(row) ?? "").Trim();
        return g.Length == 0 ? Ungrouped : g;
    }

    private bool Matches(object row)
    {
        if (_filter.Length == 0) return true;
        const StringComparison C = StringComparison.OrdinalIgnoreCase;
        return _nameOf(row).Contains(_filter, C)
            || _subtitleOf(row).Contains(_filter, C)
            || (_groupOf is not null && GroupOf(row).Contains(_filter, C));
    }

    public void ToggleGroup(string title)
    {
        if (!_collapsed.Remove(title)) _collapsed.Add(title);
        Rebuild();
    }

    private void Rebuild()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            object? keep = _selectedRow;
            Rows.Clear();
            _shape.Clear();

            var rows = _source.Cast<object>().ToList();
            foreach (object r in rows) _shape[r] = (_nameOf(r), GroupOf(r));

            List<object> visible = rows.Where(Matches).ToList();

            if (_groupOf is null)
            {
                foreach (object r in visible.OrderBy(_nameOf, StringComparer.OrdinalIgnoreCase))
                    Rows.Add(r);
            }
            else
            {
                // Named groups first in name order, "(no group)" last however it sorts.
                IEnumerable<IGrouping<string, object>> groups = visible
                    .GroupBy(GroupOf, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key == Ungrouped ? 1 : 0)
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (IGrouping<string, object> g in groups)
                {
                    // A filter that hides most of a group would otherwise leave its rules behind a
                    // closed header with no clue they are there, so filtering opens everything.
                    bool expanded = _filter.Length > 0 || !_collapsed.Contains(g.Key);
                    string key = g.Key;
                    Rows.Add(new RuleGroupHeader(key, g.Count(), expanded, () => ToggleGroup(key)));
                    if (!expanded) continue;
                    foreach (object r in g.OrderBy(_nameOf, StringComparer.OrdinalIgnoreCase))
                        Rows.Add(r);
                }
            }

            OnPropertyChanged(nameof(HasFilter));
            // Re-assert the selection: Rows.Clear() makes the ListBox drop it, and losing your
            // place every time you type a filter character would be worse than no filter at all.
            _selected = Rows.Contains(keep!) ? keep : null;
            OnPropertyChanged(nameof(Selected));
        }
        finally { _rebuilding = false; }
    }
}

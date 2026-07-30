using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Scrye.Core.State;

namespace Scrye.App.ViewModels;

/// <summary>
/// The game-state inspector: a live, filterable view of the session's
/// <see cref="StateStore"/> (GMCP/MIP → dotted paths). Subscribes to
/// <see cref="StateStore.Changed"/> at construction (pre-loop, per the threading
/// invariant); changes flow loop → <see cref="_queue"/> → <see cref="Drain"/> on the
/// UI thread (called from the world's existing flush timer). Selecting a row shows a
/// selectable "path = value" detail line for easy copying into triggers/HUD binds.
/// </summary>
public sealed class StateViewModel : ViewModelBase
{
    private readonly ConcurrentQueue<StateChange> _queue = new();
    private readonly Dictionary<string, StateRowViewModel> _byPath = new(StringComparer.Ordinal);

    /// <summary>Rows currently shown (filtered, path-sorted).</summary>
    public ObservableCollection<StateRowViewModel> Rows { get; } = new();

    public RelayCommand CloseCommand { get; }

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => SetField(ref _isOpen, value); }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (SetField(ref _filter, value)) Rebuild(); }
    }

    private StateRowViewModel? _selectedRow;
    public StateRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (SetField(ref _selectedRow, value)) OnPropertyChanged(nameof(Detail)); }
    }

    /// <summary>Selectable "path = value" line for the selected row.</summary>
    public string Detail => SelectedRow is null ? "" : $"{SelectedRow.Path} = {SelectedRow.Value}";

    private string _status = "no state yet — connect and the tree fills as GMCP/MIP data arrives";
    public string Status { get => _status; set => SetField(ref _status, value); }

    public StateViewModel(StateStore store)
    {
        CloseCommand = new RelayCommand(() => IsOpen = false);
        store.Changed += c => _queue.Enqueue(c);   // subscribed pre-loop; fired on the loop
    }

    /// <summary>Apply queued changes (UI thread — called from the world's flush timer).</summary>
    public void Drain()
    {
        bool structural = false;
        while (_queue.TryDequeue(out StateChange c))
        {
            if (c.Removed)
            {
                if (_byPath.Remove(c.Path)) structural = true;
                continue;
            }
            string text = c.Value.Text;
            if (_byPath.TryGetValue(c.Path, out StateRowViewModel? row))
            {
                row.Value = text;   // in-place update — no list churn
            }
            else
            {
                _byPath[c.Path] = new StateRowViewModel(c.Path) { Value = text };
                structural = true;
            }
        }
        if (structural) Rebuild();
        if (SelectedRow is not null) OnPropertyChanged(nameof(Detail));
    }

    private void Rebuild()
    {
        string f = Filter.Trim();
        Rows.Clear();
        foreach (StateRowViewModel row in _byPath.Values
                     .Where(r => f.Length == 0 ||
                                 r.Path.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                 r.Value.Contains(f, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            Rows.Add(row);
        Status = _byPath.Count == 0
            ? "no state yet — connect and the tree fills as GMCP/MIP data arrives"
            : f.Length == 0 ? $"{_byPath.Count} paths" : $"{Rows.Count} of {_byPath.Count} paths";
    }
}

/// <summary>One dotted state path + its current value.</summary>
public sealed class StateRowViewModel : ViewModelBase
{
    public string Path { get; }

    private string _value = "";
    public string Value { get => _value; set => SetField(ref _value, value); }

    public StateRowViewModel(string path) => Path = path;
}

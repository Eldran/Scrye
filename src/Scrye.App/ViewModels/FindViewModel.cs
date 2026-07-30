using System;
using System.Collections.Generic;
using Scrye.Core.Text;

namespace Scrye.App.ViewModels;

/// <summary>
/// Drives the find-in-scrollback bar. Searches the world's <see cref="ScrollbackBuffer"/>
/// for the query (case-insensitive), exposes the current match position, and
/// tells the <c>OutputView</c> what to highlight (<see cref="Term"/>) and which
/// line to scroll to (<see cref="ActiveLine"/>). Recomputes on navigation so
/// results stay fresh as new output streams in.
/// </summary>
public sealed class FindViewModel : ViewModelBase
{
    private readonly ScrollbackBuffer _buffer;
    private readonly List<int> _matches = new();
    private int _cursor = -1;

    public RelayCommand NextCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand CloseCommand { get; }

    public FindViewModel(ScrollbackBuffer buffer)
    {
        _buffer = buffer;
        NextCommand = new RelayCommand(() => Move(+1));
        PrevCommand = new RelayCommand(() => Move(-1));
        CloseCommand = new RelayCommand(Close);
    }

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; private set => SetField(ref _isOpen, value); }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value)) Search(); }
    }

    /// <summary>The term the OutputView should highlight.</summary>
    public string Term => _query;

    private int _activeLine = -1;
    /// <summary>Line index the OutputView should scroll into view (-1 = none).</summary>
    public int ActiveLine { get => _activeLine; private set => SetField(ref _activeLine, value); }

    private string _status = "";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public void Open() => IsOpen = true;

    public void Close()
    {
        IsOpen = false;
        Query = "";   // clears highlight + status via Search()
    }

    private void Search()
    {
        Refresh();
        _cursor = _matches.Count > 0 ? _matches.Count - 1 : -1;   // newest match first
        UpdateActive();
    }

    private void Move(int dir)
    {
        int prevLine = _cursor >= 0 && _cursor < _matches.Count ? _matches[_cursor] : -1;
        Refresh();
        if (_matches.Count == 0) { _cursor = -1; UpdateActive(); return; }
        int at = prevLine >= 0 ? _matches.IndexOf(prevLine) : -1;
        _cursor = at >= 0 ? at : (dir > 0 ? -1 : 0);
        _cursor = (_cursor + dir + _matches.Count) % _matches.Count;
        UpdateActive();
    }

    private void Refresh()
    {
        _matches.Clear();
        if (_query.Length == 0) return;
        int count = _buffer.Count;
        for (int i = 0; i < count; i++)
            if (_buffer[i].PlainText.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0)
                _matches.Add(i);
    }

    private void UpdateActive()
    {
        ActiveLine = _cursor >= 0 && _cursor < _matches.Count ? _matches[_cursor] : -1;
        Status = _query.Length == 0 ? ""
               : _matches.Count == 0 ? "no matches"
               : $"{_cursor + 1} / {_matches.Count}";
        OnPropertyChanged(nameof(Term));
    }
}

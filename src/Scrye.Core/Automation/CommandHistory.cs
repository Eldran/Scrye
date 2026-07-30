namespace Scrye.Core.Automation;

/// <summary>
/// Per-world command recall (up/down arrow). Stores submitted commands (deduping
/// consecutive repeats, capped), and walks them with a cursor. When you start
/// navigating up, the in-progress text is saved as a "draft" and restored when you
/// come back down past the newest entry — so arrowing up then down never loses what
/// you were typing. Pure logic; the input box just calls it.
/// </summary>
public sealed class CommandHistory
{
    private readonly List<string> _items = new();
    private readonly int _capacity;
    private int _index;        // cursor; == Count when not navigating (at the live draft)
    private string _draft = "";

    public CommandHistory(int capacity = 200)
    {
        _capacity = Math.Max(1, capacity);
        _index = 0;
    }

    public IReadOnlyList<string> Items => _items;
    public int Count => _items.Count;

    /// <summary>Record a submitted command. Empty is ignored; a consecutive duplicate is not
    /// re-added. Resets navigation to the newest end.</summary>
    public void Add(string command)
    {
        if (!string.IsNullOrEmpty(command) && (_items.Count == 0 || _items[^1] != command))
        {
            _items.Add(command);
            if (_items.Count > _capacity) _items.RemoveAt(0);
        }
        _index = _items.Count;
        _draft = "";
    }

    /// <summary>Up arrow: step to the previous command. <paramref name="currentText"/> is the
    /// box contents, saved as the draft when navigation begins. Null if there's no history.</summary>
    public string? Previous(string currentText)
    {
        if (_items.Count == 0) return null;
        if (_index == _items.Count) _draft = currentText;   // beginning navigation
        if (_index > 0) _index--;
        return _items[_index];
    }

    /// <summary>Down arrow: step to the next command, or back to the saved draft at the end.
    /// Null when not currently navigating.</summary>
    public string? Next()
    {
        if (_index >= _items.Count) return null;
        _index++;
        return _index == _items.Count ? _draft : _items[_index];
    }

    /// <summary>Drop back to the live end (call when the user edits the input directly).</summary>
    public void Resync() => _index = _items.Count;

    public void Clear()
    {
        _items.Clear();
        _index = 0;
        _draft = "";
    }
}

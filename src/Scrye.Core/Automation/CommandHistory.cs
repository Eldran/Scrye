namespace Scrye.Core.Automation;

/// <summary>
/// Per-world command recall (up/down arrow). Stores submitted commands, and walks them with
/// a cursor. When you start navigating up, the in-progress text is saved as a "draft" and
/// restored when you come back down past the newest entry — so arrowing up then down never
/// loses what you were typing. Pure logic; the input box just calls it.
///
/// <para><b>The walk is filtered by what you had typed.</b> Type <c>vtrade </c> and press Up
/// and you cycle only the vtrade commands; an empty box still means "everything". MUSHclient
/// puts this on Alt+Up because plain Up is already spoken for, but if there is a prefix in the
/// box there is no other sensible reading of Up — which is why zsh and fish both do it without
/// a second key. The unfiltered walk is still reachable (the input box binds it to Ctrl+Up).</para>
///
/// <para>Two details that matter more than they look:</para>
/// <list type="bullet">
/// <item>The filter is ANCHORED when the walk begins. After the first Up the box holds the
/// matched command, and re-deriving the prefix from that would collapse the cycle to a single
/// entry. The anchor lives until you edit (<see cref="Resync"/>) or submit.</item>
/// <item>A walk never shows the same command twice. <see cref="Add"/> only collapses
/// CONSECUTIVE repeats, which is fine for the full list and useless once filtered — running
/// <c>vtrade goods iron</c> five times between other commands is exactly the case where
/// cycling gets tedious.</item>
/// </list>
/// </summary>
public sealed class CommandHistory
{
    private readonly List<string> _items = new();
    private readonly List<string> _view = new();   // the deduped, filtered list this walk steps through
    private readonly int _capacity;
    private int _index;          // cursor into _view; == _view.Count means "at the live draft"
    private bool _walking;       // a walk is in progress, so the anchor and view stand
    private string _draft = "";

    public CommandHistory(int capacity = 200)
    {
        _capacity = Math.Max(1, capacity);
        _index = 0;
    }

    public IReadOnlyList<string> Items => _items;
    public int Count => _items.Count;

    /// <summary>How many entries the current walk can reach. 0 when not walking.</summary>
    public int MatchCount => _walking ? _view.Count : 0;

    /// <summary>Record a submitted command. Empty is ignored; a consecutive duplicate is not
    /// re-added. Ends any walk.</summary>
    public void Add(string command)
    {
        if (!string.IsNullOrEmpty(command) && (_items.Count == 0 || _items[^1] != command))
        {
            _items.Add(command);
            if (_items.Count > _capacity) _items.RemoveAt(0);
        }
        Resync();
        _draft = "";
    }

    /// <summary>Newest first, deduped, keeping only entries that start with
    /// <paramref name="prefix"/> (case-insensitively; an empty prefix keeps everything).
    /// Stored oldest-to-newest so the cursor walks the way it always has.</summary>
    private void BuildView(string prefix)
    {
        _view.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            string item = _items[i];
            if (prefix.Length > 0 && !item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(item)) _view.Add(item);   // newest occurrence of a repeat wins
        }
        _view.Reverse();
    }

    /// <summary>Up arrow: step to the previous command. <paramref name="currentText"/> is the
    /// box contents, saved as the draft when navigation begins; <paramref name="prefix"/> is
    /// what to filter by (the text before the caret — empty for the whole history). Null when
    /// nothing matches, which leaves the box alone.</summary>
    public string? Previous(string currentText, string? prefix = null)
    {
        if (!_walking)
        {
            _draft = currentText;
            BuildView(prefix ?? "");
            if (_view.Count == 0) return null;     // nothing to walk; do not enter a dead walk
            _index = _view.Count;
            _walking = true;
        }
        if (_index > 0) _index--;
        return _view[_index];
    }

    /// <summary>Down arrow: step to the next command, or back to the saved draft at the end.
    /// Null when not currently navigating.</summary>
    public string? Next()
    {
        if (!_walking || _index >= _view.Count) return null;
        _index++;
        return _index == _view.Count ? _draft : _view[_index];
    }

    /// <summary>The newest command that starts with <paramref name="prefix"/> and is longer
    /// than it, or null. The inline suggestion reads this; it deliberately ignores the walk,
    /// so a suggestion never depends on whether you happen to be mid-recall.</summary>
    public string? Suggest(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            string item = _items[i];
            if (item.Length > prefix.Length &&
                item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return item;
        }
        return null;
    }

    /// <summary>Drop back to the live end and forget the walk's filter. Call when the user
    /// edits the input directly — the next Up should anchor on what is there NOW.</summary>
    public void Resync()
    {
        _walking = false;
        _view.Clear();
        _index = 0;
    }

    public void Clear()
    {
        _items.Clear();
        Resync();
        _draft = "";
    }
}

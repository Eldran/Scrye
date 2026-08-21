namespace Scrye.Core.Automation;

/// <summary>
/// Per-world command recall (up/down arrow). Stores submitted commands, and walks them with
/// a cursor. When you start navigating up, the in-progress text is saved as a "draft" and
/// restored when you come back down past the newest entry — so arrowing up then down never
/// loses what you were typing. Pure logic; the input box just calls it.
///
/// <para><b>Two walks, on two gestures.</b> Plain Up/Down steps the whole history, which is
/// the oldest habit in every shell and every MUD client and has to keep working without
/// thinking about it. A <em>filtered</em> walk — only the commands starting with what you have
/// typed — is a separate gesture, which the input box binds to Ctrl+Up and Alt+Up (MUSHclient's
/// own key for it). This class does not know about keys; it takes the prefix to filter by, and
/// an empty one means the whole history.</para>
///
/// <para>An earlier version put the filter on plain Up, on the reasoning that a prefix in the
/// box leaves no other sensible reading. In use it does: you reach for Up to get back to
/// something you typed, and having it silently answer a different question is worse than
/// needing a second key for the narrower one.</para>
///
/// <para>Three details that matter more than they look:</para>
/// <list type="bullet">
/// <item>The filter is ANCHORED when the walk begins. After the first Up the box holds the
/// matched command, and re-deriving the prefix from that would collapse the cycle to a single
/// entry. The anchor lives until you edit (<see cref="Resync"/>) or submit.</item>
/// <item>A walk never shows the same command twice. <see cref="Add"/> only collapses
/// CONSECUTIVE repeats, which is fine for the full list and useless once filtered — running
/// <c>vtrade goods iron</c> five times between other commands is exactly the case where
/// cycling gets tedious.</item>
/// <item>An edit ends the walk, and that is detected from the TEXT the caller passes in, not
/// from an event. See <see cref="Previous"/>.</item>
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
    private string? _handed;     // what the last step gave the caller; see Previous

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
    /// nothing matches, which leaves the box alone.
    ///
    /// <para><b>The caller must pass what is actually in the box.</b> That is how an edit ends
    /// a walk: if the text is no longer what the last step handed back, the user has changed
    /// it, and the next step re-anchors on what is there now.</para>
    ///
    /// <para>It is asked here, of the text, rather than inferred from the box's TextChanged
    /// event — and that is a fix, not a preference. Putting a recalled command in the box
    /// raises TextChanged too, so telling the two apart needed a flag held across the write,
    /// and a two-way bound box does not necessarily raise it inside that window. When it
    /// landed late, every Up after the first re-anchored on the command it had just recalled:
    /// the view collapsed to that one entry and the walk appeared to stop dead after a single
    /// step. Comparing the text cannot go out of step with itself.</para></summary>
    public string? Previous(string currentText, string? prefix = null)
    {
        if (_walking && !string.Equals(currentText, _handed, StringComparison.Ordinal)) Resync();
        if (!_walking)
        {
            _draft = currentText;
            BuildView(prefix ?? "");
            if (_view.Count == 0) return null;     // nothing to walk; do not enter a dead walk
            _index = _view.Count;
            _walking = true;
        }
        if (_index > 0) _index--;
        _handed = _view[_index];
        return _handed;
    }

    /// <summary>Down arrow: step to the next command, or back to the saved draft at the end.
    /// Null when not currently navigating. <paramref name="currentText"/> is the box contents
    /// and ends the walk when it is no longer what the last step handed back, exactly as in
    /// <see cref="Previous"/>; pass null to skip that check.</summary>
    public string? Next(string? currentText = null)
    {
        if (_walking && currentText is not null
            && !string.Equals(currentText, _handed, StringComparison.Ordinal)) Resync();
        if (!_walking || _index >= _view.Count) return null;
        _index++;
        _handed = _index == _view.Count ? _draft : _view[_index];
        return _handed;
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

    /// <summary>Drop back to the live end and forget the walk's filter, so the next Up anchors
    /// on whatever is in the box then. <see cref="Previous"/> does this for itself when the
    /// text has been edited; this is for the caller that knows the walk is over for another
    /// reason — a command was submitted, or the history was cleared.</summary>
    public void Resync()
    {
        _walking = false;
        _view.Clear();
        _index = 0;
        _handed = null;
    }

    public void Clear()
    {
        _items.Clear();
        Resync();
        _draft = "";
    }
}

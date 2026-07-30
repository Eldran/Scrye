using System.Text;

namespace Scrye.Core.Automation;

/// <summary>
/// Tab-completion word source. Harvests words from output lines and submitted
/// commands, keeping them most-recently-seen first, and answers prefix queries
/// for the input box. Case-insensitive matching; the stored casing (as first
/// seen) is what gets completed. Bounded so a long session can't grow unbounded.
/// </summary>
public sealed class CompletionEngine
{
    private readonly int _minLength;
    private readonly int _capacity;
    // most-recent-first list of distinct words; _index maps lower-case -> stored word.
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _index = new(StringComparer.OrdinalIgnoreCase);

    public CompletionEngine(int minLength = 3, int capacity = 4000)
    {
        _minLength = minLength < 1 ? 1 : minLength;
        _capacity = capacity < 1 ? 1 : capacity;
    }

    public int Count => _order.Count;

    /// <summary>Harvest every word from a line of text (output or input).</summary>
    public void Observe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (string w in ExtractWords(text)) Add(w);
    }

    /// <summary>Record a single word as most-recently-seen (promotes an existing one).</summary>
    public void Add(string word)
    {
        if (word.Length < _minLength) return;
        if (_index.TryGetValue(word, out var existing))
        {
            _order.Remove(existing);
            _order.AddFirst(existing);
            return;
        }
        var node = _order.AddFirst(word);
        _index[word] = node;
        if (_order.Count > _capacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _index.Remove(last.Value);
        }
    }

    /// <summary>Words beginning with <paramref name="prefix"/> (case-insensitive),
    /// most-recently-seen first, excluding an exact case-insensitive match of the
    /// prefix itself. Empty when the prefix is blank.</summary>
    public IReadOnlyList<string> Complete(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return Array.Empty<string>();
        var hits = new List<string>();
        foreach (string w in _order)
            if (w.Length > prefix.Length && w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                hits.Add(w);
        return hits;
    }

    private IEnumerable<string> ExtractWords(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (IsWordChar(c)) { sb.Append(c); continue; }
            if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Characters that count as part of a completable word (letters, digits, and a few MUD-y symbols).</summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '-';
}

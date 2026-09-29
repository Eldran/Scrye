namespace Scrye.Core.Automation;

/// <summary>
/// The last few lines of output, the newest last, for triggers that match over several
/// lines. One of these sits in the automation engine and one in every plugin runtime; each
/// keeps only as many lines as its widest trigger looks back over, and none at all while
/// every trigger is one line.
/// </summary>
public sealed class RecentLines
{
    private readonly List<string> _lines = new();

    /// <summary>How many lines are kept (1 = none: one-line triggers need no history).</summary>
    public int Size { get; private set; } = 1;

    public int Count => _lines.Count;

    /// <summary>Keep this many lines. At 1 what is left is dropped: with nothing buffered in
    /// between, a line from before the gap must never be joined to one after it.</summary>
    public void Resize(int size)
    {
        Size = Math.Clamp(size, 1, TriggerDef.MaxLines);
        if (Size <= 1) _lines.Clear();
        else if (_lines.Count > Size) _lines.RemoveRange(0, _lines.Count - Size);
    }

    /// <summary>A line arrived. Kept only while some trigger looks back.</summary>
    public void Push(string line)
    {
        if (Size <= 1) return;
        _lines.Add(line);
        if (_lines.Count > Size) _lines.RemoveAt(0);
    }

    public void Clear() => _lines.Clear();

    /// <summary>The last <paramref name="lines"/> lines (the newest last) joined with \n, and
    /// where the newest starts in it. <paramref name="extra"/> is a line not yet pushed (the
    /// simulator's, which must not be remembered): it counts as the newest.</summary>
    public (string Joined, int NewestStart) Window(int lines, string? extra = null)
    {
        int have = _lines.Count + (extra is null ? 0 : 1);
        int take = Math.Min(lines, have);
        var sb = new System.Text.StringBuilder();
        int newestStart = 0;
        for (int k = have - take; k < have; k++)
        {
            string l = k < _lines.Count ? _lines[k] : extra!;
            if (k > have - take) sb.Append('\n');
            if (k == have - 1) newestStart = sb.Length;
            sb.Append(l);
        }
        return (sb.ToString(), newestStart);
    }

    /// <summary>
    /// Match a pattern that spans <paramref name="lines"/> lines against the newest line and
    /// those before it. The newest line must already be pushed (or be passed as
    /// <paramref name="extra"/>). A one-line pattern matches <paramref name="newest"/> alone.
    /// </summary>
    public MatchResult? Match(CompiledPattern pattern, int lines, string newest, out int newestStart,
                              bool pushed = true)
    {
        newestStart = 0;
        if (lines <= 1) return pattern.Match(newest);
        (string joined, int start) = pushed ? Window(lines) : Window(lines, newest);
        newestStart = start;
        return pattern.MatchWindow(joined, start);
    }
}

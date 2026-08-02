using Scrye.Core.Text;

namespace Scrye.Companion.Protocol;

/// <summary>
/// Turns a drained batch of <see cref="Line"/>s into an <see cref="OutputBatchMessage"/>,
/// interning styles as it goes so each distinct (fore, back, flags) combination appears
/// once per frame and spans carry an index (§3.1).
///
/// <para>Not thread-safe, and not meant to be: one instance per outbound frame, built on
/// the UI thread inside the flush that produced the lines. The companion server hands the
/// finished immutable message to its own outbound queue rather than letting socket threads
/// reach into <c>ScrollbackBuffer</c> (§4.1).</para>
/// </summary>
public sealed class OutputBatchBuilder
{
    private readonly Dictionary<(string Fg, string Bg, RunFlags Flags), int> _styleIndex = new();
    private readonly List<StyleDto> _styles = new();
    private readonly List<OutputLineDto> _lines = new();

    public int LineCount => _lines.Count;
    public int StyleCount => _styles.Count;

    /// <summary>Append one line, assigning it <paramref name="sequence"/>.</summary>
    public void Add(Line line, long sequence)
    {
        ArgumentNullException.ThrowIfNull(line);

        var spans = new List<OutputSpanDto>(line.Runs.Count);
        foreach (StyledRun run in line.Runs)
            spans.Add(new OutputSpanDto(
                run.Text,
                Intern(run),
                run.Link is null ? null : LinkDto.From(run.Link)));

        _lines.Add(new OutputLineDto(sequence, line.ReceivedUtc, line.IsPrompt, spans));
    }

    /// <summary>Append a run of lines whose sequences are consecutive, starting at
    /// <paramref name="firstSequence"/> — the shape a scrollback drain or a resume replay
    /// produces.</summary>
    public void AddRange(IReadOnlyList<Line> lines, long firstSequence)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (int i = 0; i < lines.Count; i++)
            Add(lines[i], firstSequence + i);
    }

    /// <summary>The finished frame. The builder should be discarded afterwards; the returned
    /// message holds the same list instances rather than copying them.</summary>
    public OutputBatchMessage Build(string sessionId) =>
        new(sessionId, _styles, _lines);

    private int Intern(in StyledRun run)
    {
        var key = (run.Fore.ToHex(), run.Back.ToHex(), run.Flags);
        if (_styleIndex.TryGetValue(key, out int existing)) return existing;

        int index = _styles.Count;
        _styles.Add(new StyleDto(key.Item1, key.Item2, key.Flags));
        _styleIndex[key] = index;
        return index;
    }
}

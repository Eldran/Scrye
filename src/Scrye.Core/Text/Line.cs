using System.Text.RegularExpressions;

namespace Scrye.Core.Text;

/// <summary>A clickable region of a line, in plain-text character coordinates.</summary>
public readonly record struct LinkSpan(int Start, int Length, LinkInfo Link)
{
    public bool Contains(int col) => col >= Start && col < Start + Length;
}

/// <summary>One display line: an immutable sequence of styled runs. Immutability
/// lets the UI thread read scrollback snapshots without locking.</summary>
public sealed class Line
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>()\[\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<StyledRun> Runs { get; }

    /// <summary>True when the line was flushed by a prompt marker (telnet GA/EOR)
    /// rather than a newline — i.e. the server is waiting for input on this line.</summary>
    public bool IsPrompt { get; }

    public DateTimeOffset ReceivedUtc { get; }

    private LinkSpan[]? _links;   // computed lazily; idempotent, so a benign race is fine

    public Line(IReadOnlyList<StyledRun> runs, bool isPrompt, DateTimeOffset receivedUtc)
    {
        Runs = runs;
        IsPrompt = isPrompt;
        ReceivedUtc = receivedUtc;
    }

    /// <summary>Convenience factory for a single-run, single-colour line
    /// (echoed input, system notices, etc.).</summary>
    public static Line FromText(string text, Rgb? fore = null, bool isPrompt = false) =>
        new(new[] { new StyledRun(text, fore ?? Rgb.DefaultFore, Rgb.DefaultBack, RunFlags.None) },
            isPrompt, DateTimeOffset.UtcNow);

    /// <summary>The line's text with styling stripped (for logging, triggers, search).</summary>
    public string PlainText => string.Concat(Runs.Select(r => r.Text));

    /// <summary>Return a copy of this line with the plain-text range
    /// [<paramref name="start"/>, start+<paramref name="length"/>) recoloured — used by
    /// highlight triggers. Runs straddling the range boundary are split; <see cref="RunFlags"/>
    /// and any <see cref="LinkInfo"/> are preserved. A null colour leaves that channel as-is.
    /// Out-of-range or empty selections, or two null colours, return this line unchanged.</summary>
    public Line RecolorRange(int start, int length, Rgb? fore, Rgb? back)
    {
        if (length <= 0 || (fore is null && back is null)) return this;
        int total = PlainText.Length;
        if (start < 0) { length += start; start = 0; }
        if (start >= total || length <= 0) return this;
        int end = Math.Min(total, start + length);

        var outRuns = new List<StyledRun>(Runs.Count + 2);
        int pos = 0;
        foreach (StyledRun run in Runs)
        {
            int rStart = pos, rEnd = pos + run.Text.Length;
            pos = rEnd;
            if (rEnd <= start || rStart >= end) { outRuns.Add(run); continue; }  // no overlap

            // [rStart,rEnd) overlaps [start,end): emit up to three pieces
            int a = Math.Max(rStart, start), b = Math.Min(rEnd, end);
            if (a > rStart) outRuns.Add(run with { Text = run.Text[..(a - rStart)] });
            outRuns.Add(run with
            {
                Text = run.Text[(a - rStart)..(b - rStart)],
                Fore = fore ?? run.Fore,
                Back = back ?? run.Back,
            });
            if (b < rEnd) outRuns.Add(run with { Text = run.Text[(b - rStart)..] });
        }
        return new Line(outRuns, IsPrompt, ReceivedUtc);
    }

    /// <summary>Clickable regions: MXP link runs plus auto-detected http(s) URLs in
    /// ordinary text. Character positions index into <see cref="PlainText"/>.</summary>
    public IReadOnlyList<LinkSpan> Links => _links ??= ComputeLinks();

    private LinkSpan[] ComputeLinks()
    {
        List<LinkSpan>? spans = null;

        // 1) MXP link runs — merge contiguous runs sharing the same LinkInfo instance
        int pos = 0;
        int spanStart = -1;
        LinkInfo? current = null;
        foreach (StyledRun run in Runs)
        {
            if (!ReferenceEquals(run.Link, current))
            {
                if (current is not null)
                    (spans ??= new List<LinkSpan>()).Add(new LinkSpan(spanStart, pos - spanStart, current));
                current = run.Link;
                spanStart = pos;
            }
            pos += run.Text.Length;
        }
        if (current is not null)
            (spans ??= new List<LinkSpan>()).Add(new LinkSpan(spanStart, pos - spanStart, current));

        // 2) plain-text URLs (skipping ranges already covered by an MXP link)
        string plain = PlainText;
        if (plain.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in UrlPattern.Matches(plain))
            {
                string url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                if (url.Length == 0) continue;
                bool overlaps = spans is not null &&
                    spans.Any(s => m.Index < s.Start + s.Length && m.Index + url.Length > s.Start);
                if (!overlaps)
                    (spans ??= new List<LinkSpan>()).Add(
                        new LinkSpan(m.Index, url.Length, new LinkInfo(url, IsUrl: true)));
            }
        }

        return spans?.ToArray() ?? Array.Empty<LinkSpan>();
    }

    public override string ToString() => PlainText;
}

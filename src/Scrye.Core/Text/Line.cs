namespace Scrye.Core.Text;

/// <summary>One display line: an immutable sequence of styled runs. Immutability
/// lets the UI thread read scrollback snapshots without locking.</summary>
public sealed class Line
{
    public IReadOnlyList<StyledRun> Runs { get; }

    /// <summary>True when the line was flushed by a prompt marker (telnet GA/EOR)
    /// rather than a newline — i.e. the server is waiting for input on this line.</summary>
    public bool IsPrompt { get; }

    public DateTimeOffset ReceivedUtc { get; }

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

    public override string ToString() => PlainText;
}

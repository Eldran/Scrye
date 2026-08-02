using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Sequence numbering on <see cref="ScrollbackBuffer"/> — the identity the companion's
/// resume path depends on (companion design §6). The point of these tests is the gap
/// between <i>index</i> and <i>sequence</i>: indices shift when the buffer trims from the
/// front, sequences never do. Getting that wrong produces a resume that serves the
/// <i>wrong lines</i> rather than failing loudly, which is the dangerous outcome.
/// </summary>
public class ScrollbackSequenceTests
{
    private static Line L(string text) => Line.FromText(text);

    // Trimming fires when Count > max + trimChunk, so a small buffer makes it observable.
    private static ScrollbackBuffer Small() => new(max: 10, trimChunk: 5);

    [Fact]
    public void EmptyBuffer_HasNoResolvableSequences()
    {
        var b = new ScrollbackBuffer();

        Assert.Equal(0, b.BaseSequence);
        Assert.Equal(0, b.NextSequence);
        Assert.False(b.TryGetIndex(0, out _));
    }

    [Fact]
    public void Sequences_AreAssignedInOrderFromZero()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("a"));
        b.Add(L("b"));
        b.Add(L("c"));

        Assert.Equal(3, b.NextSequence);
        Assert.Equal(0, b.SequenceAt(0));
        Assert.Equal(2, b.SequenceAt(2));
    }

    [Fact]
    public void AddRange_KeepsSequencesConsecutive()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("x"));
        b.AddRange(new[] { L("y"), L("z") });

        Assert.Equal(3, b.NextSequence);
        Assert.Equal(2, b.SequenceAt(2));
        Assert.Equal("z", b[2].PlainText);
    }

    [Fact]
    public void EmptyAddRange_IsANoOp()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("x"));
        b.AddRange(System.Array.Empty<Line>());

        Assert.Equal(1, b.NextSequence);
    }

    [Fact]
    public void TryGetIndex_RejectsSequencesNotYetProduced()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("a"));

        Assert.True(b.TryGetIndex(0, out int i) && i == 0);
        Assert.False(b.TryGetIndex(1, out _));
    }

    [Fact]
    public void SequenceAt_ThrowsOutsideTheBuffer()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("a"));

        Assert.Throws<System.ArgumentOutOfRangeException>(() => b.SequenceAt(1));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => b.SequenceAt(-1));
    }

    // ---- the trap: after a trim, index != sequence ---------------------------

    [Fact]
    public void AfterTrim_IndexZeroIsNoLongerSequenceZero()
    {
        var b = Small();
        for (int i = 0; i < 16; i++) b.Add(L($"line{i}"));

        Assert.Equal(10, b.Count);          // trimmed back to max
        Assert.Equal(6, b.BaseSequence);    // six oldest dropped
        Assert.Equal(16, b.NextSequence);   // but the counter kept going

        Assert.Equal(6, b.SequenceAt(0));
        Assert.Equal("line6", b[0].PlainText);
        Assert.Equal(15, b.SequenceAt(b.Count - 1));
    }

    [Fact]
    public void AfterTrim_TrimmedSequencesDoNotResolve()
    {
        var b = Small();
        for (int i = 0; i < 16; i++) b.Add(L($"line{i}"));

        Assert.False(b.TryGetIndex(5, out _));               // gone
        Assert.True(b.TryGetIndex(6, out int i6) && i6 == 0); // oldest survivor
    }

    [Fact]
    public void CanReplayFrom_DrawsTheLineWhereDataWouldBeLost()
    {
        var b = Small();
        for (int i = 0; i < 16; i++) b.Add(L($"line{i}"));

        // A client that saw through sequence 5 needs 6.. — all still held.
        Assert.True(b.CanReplayFrom(5));

        // A client that saw only through 4 needs line5, which was trimmed:
        // replaying would silently skip it, so it must be snapshotted instead.
        Assert.False(b.CanReplayFrom(4));

        Assert.True(b.CanReplayFrom(15));    // fully caught up: replay of zero lines
        Assert.False(b.CanReplayFrom(99));   // ahead of us; nonsense input
    }

    [Fact]
    public void LinesAfter_ReturnsOnlyNewerLinesOldestFirst()
    {
        var b = Small();
        for (int i = 0; i < 16; i++) b.Add(L($"line{i}"));

        Assert.Equal(
            new[] { "line13", "line14", "line15" },
            b.LinesAfter(12).Select(l => l.PlainText));
    }

    [Fact]
    public void LinesAfter_IsEmptyWhenCallerIsCurrent()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("a"));
        b.Add(L("b"));

        Assert.Empty(b.LinesAfter(1));
    }

    // ---- Clear ---------------------------------------------------------------

    [Fact]
    public void Clear_DoesNotReuseSequences()
    {
        var b = new ScrollbackBuffer();
        b.Add(L("a"));
        b.Add(L("b"));
        b.Clear();

        Assert.Equal(0, b.Count);
        Assert.Equal(2, b.BaseSequence);
        Assert.Equal(2, b.NextSequence);

        // A client holding sequence 0 must be told to resnapshot, NOT handed
        // whatever now sits at index 0.
        Assert.False(b.TryGetIndex(0, out _));
        Assert.False(b.CanReplayFrom(0));

        b.Add(L("fresh"));
        Assert.Equal(2, b.SequenceAt(0));
    }

    [Fact]
    public void Changed_FiresOnAddAndClear()
    {
        var b = new ScrollbackBuffer();
        int fired = 0;
        b.Changed += () => fired++;

        b.Add(L("a"));
        b.AddRange(new[] { L("b"), L("c") });
        b.Clear();

        Assert.Equal(3, fired);
    }
}

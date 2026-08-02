namespace Scrye.Core.Text;

/// <summary>
/// A bounded, append-only store of rendered <see cref="Line"/>s — the model the
/// output renderer virtualizes over. Trims oldest lines in chunks (amortized O(1)
/// per add) once it grows past its cap. Raises <see cref="Changed"/> after each
/// mutation; the UI batches adds and this fires once per batch.
///
/// <para>Every line also gets a monotonic <b>sequence number</b>, assigned on add and
/// never reused. Indices shift when the buffer trims from the front; sequences do not.
/// The mobile companion's resume path (design doc §6) asks "send me everything after
/// sequence N", so it needs an identity that survives trimming — see
/// <see cref="BaseSequence"/> and <see cref="TryGetIndex"/>.</para>
/// </summary>
public sealed class ScrollbackBuffer
{
    private readonly List<Line> _lines;
    private readonly int _max;
    private readonly int _trimChunk;

    private long _nextSequence;   // sequence the next added line will receive

    public ScrollbackBuffer(int max = 50_000, int trimChunk = 2_000)
    {
        _max = max;
        _trimChunk = trimChunk;
        _lines = new List<Line>(1024);
    }

    public int Count => _lines.Count;
    public Line this[int index] => _lines[index];

    /// <summary>Raised (on the UI thread, by the batching flush) after lines are added or cleared.</summary>
    public event Action? Changed;

    // ---- sequence numbering --------------------------------------------------

    /// <summary>The sequence of the oldest line still held (index 0). Rises as the buffer
    /// trims. When the buffer is empty this equals <see cref="NextSequence"/>.</summary>
    public long BaseSequence { get; private set; }

    /// <summary>The sequence that will be assigned to the next line added — i.e. one past
    /// the newest line's sequence. Never decreases, including across <see cref="Clear"/>.</summary>
    public long NextSequence => _nextSequence;

    /// <summary>Sequence of the line at <paramref name="index"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/> if the index is not in range.</summary>
    public long SequenceAt(int index)
    {
        if (index < 0 || index >= _lines.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return BaseSequence + index;
    }

    /// <summary>Map a sequence back to a current index. Returns false when the sequence has
    /// already been trimmed away (too old) or has not been produced yet (too new) — in the
    /// first case the caller must fall back to a snapshot rather than a replay.</summary>
    public bool TryGetIndex(long sequence, out int index)
    {
        long offset = sequence - BaseSequence;
        if (offset < 0 || offset >= _lines.Count) { index = -1; return false; }
        index = (int)offset;
        return true;
    }

    /// <summary>True when everything after <paramref name="lastReceivedSequence"/> is still
    /// held, so a resuming client can be replayed rather than re-snapshotted. A client that
    /// is fully caught up (has seen <see cref="NextSequence"/> - 1) is trivially replayable
    /// with zero lines.</summary>
    public bool CanReplayFrom(long lastReceivedSequence) =>
        lastReceivedSequence >= BaseSequence - 1 && lastReceivedSequence <= _nextSequence - 1;

    /// <summary>The lines with sequence strictly greater than <paramref name="afterSequence"/>,
    /// oldest first, for replaying to a reconnecting companion client. Returns an empty list
    /// when the caller is already current. Check <see cref="CanReplayFrom"/> first: if the gap
    /// is too old this returns only what survives, which would silently lose lines.</summary>
    public IReadOnlyList<Line> LinesAfter(long afterSequence)
    {
        long firstWanted = afterSequence + 1;
        int start = firstWanted <= BaseSequence ? 0 : (int)(firstWanted - BaseSequence);
        if (start >= _lines.Count) return Array.Empty<Line>();
        return _lines.GetRange(start, _lines.Count - start);
    }

    // ---- mutation ------------------------------------------------------------

    public void Add(Line line)
    {
        _lines.Add(line);
        _nextSequence++;
        TrimIfNeeded();
        Changed?.Invoke();
    }

    public void AddRange(IReadOnlyList<Line> lines)
    {
        if (lines.Count == 0) return;
        _lines.AddRange(lines);
        _nextSequence += lines.Count;
        TrimIfNeeded();
        Changed?.Invoke();
    }

    /// <summary>Drop every line. Sequences are NOT reset: a companion client holding an old
    /// sequence must see it as un-replayable rather than as a valid index into new content.</summary>
    public void Clear()
    {
        _lines.Clear();
        BaseSequence = _nextSequence;
        Changed?.Invoke();
    }

    private void TrimIfNeeded()
    {
        // trim in chunks so the O(n) front-removal is amortized across many adds
        if (_lines.Count > _max + _trimChunk)
        {
            int removed = _lines.Count - _max;
            _lines.RemoveRange(0, removed);
            BaseSequence += removed;
        }
    }
}

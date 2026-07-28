namespace Scrye.Core.Text;

/// <summary>
/// A bounded, append-only store of rendered <see cref="Line"/>s — the model the
/// output renderer virtualizes over. Trims oldest lines in chunks (amortized O(1)
/// per add) once it grows past its cap. Raises <see cref="Changed"/> after each
/// mutation; the UI batches adds and this fires once per batch.
/// </summary>
public sealed class ScrollbackBuffer
{
    private readonly List<Line> _lines;
    private readonly int _max;
    private readonly int _trimChunk;

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

    public void Add(Line line)
    {
        _lines.Add(line);
        TrimIfNeeded();
        Changed?.Invoke();
    }

    public void AddRange(IReadOnlyList<Line> lines)
    {
        if (lines.Count == 0) return;
        _lines.AddRange(lines);
        TrimIfNeeded();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _lines.Clear();
        Changed?.Invoke();
    }

    private void TrimIfNeeded()
    {
        // trim in chunks so the O(n) front-removal is amortized across many adds
        if (_lines.Count > _max + _trimChunk)
            _lines.RemoveRange(0, _lines.Count - _max);
    }
}

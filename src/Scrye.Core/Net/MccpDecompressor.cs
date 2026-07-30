using System.IO.Compression;

namespace Scrye.Core.Net;

/// <summary>
/// Streaming zlib inflater for MCCP2 (telnet option 86). Feed it the raw socket bytes
/// that follow the server's <c>IAC SB COMPRESS2 IAC SE</c> marker (in whatever chunk
/// sizes they arrive); it emits inflated bytes via the constructor callback as they
/// become decodable. Built on <see cref="ZLibStream"/> over a small blocking feed
/// stream, pumped by a dedicated task — no NuGet dependencies. Emissions happen on the
/// pump task, in order; route them back to the session loop (e.g. via the mailbox).
/// When the server finishes the zlib stream (rare mid-session; usually at disconnect),
/// <c>onEnded</c> fires once — reset compression and return to the plain path.
/// </summary>
public sealed class MccpDecompressor : IDisposable
{
    private readonly BlockingFeedStream _feed = new();
    private readonly ZLibStream _zlib;
    private readonly Task _pump;
    private volatile bool _disposed;

    /// <param name="onInflated">Receives each inflated chunk, in order (pump-task thread).</param>
    /// <param name="onEnded">Fires once when the zlib stream ends naturally (not on Dispose).</param>
    public MccpDecompressor(Action<byte[]> onInflated, Action? onEnded = null)
    {
        _zlib = new ZLibStream(_feed, CompressionMode.Decompress);
        _pump = Task.Run(() =>
        {
            byte[] buf = new byte[16384];
            try
            {
                int n;
                while ((n = _zlib.Read(buf, 0, buf.Length)) > 0)
                    onInflated(buf[..n]);
                if (!_disposed) onEnded?.Invoke();
            }
            catch
            {
                // corrupt stream / disposed mid-read: treat as ended so the session
                // can fall back to the plain path rather than hanging
                if (!_disposed) onEnded?.Invoke();
            }
        });
    }

    /// <summary>Queue a chunk of compressed bytes for inflation (any thread; non-blocking).</summary>
    public void Feed(byte[] compressed) => _feed.Append(compressed);

    public void Dispose()
    {
        _disposed = true;
        _feed.CompleteWriting();      // pump's Read drains + returns 0 → task exits
        try { _pump.Wait(1000); } catch { /* pump ends on its own */ }
        try { _zlib.Dispose(); } catch { }
        _feed.Dispose();
    }

    /// <summary>A read-side Stream over an append-only byte queue: Read blocks until data
    /// is available (or writing completes → returns 0 = EOF). Lets the synchronous
    /// <see cref="ZLibStream"/> consume a chunk-at-a-time network feed on the pump task.</summary>
    private sealed class BlockingFeedStream : Stream
    {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;              // read position within the head chunk
        private bool _completed;

        public void Append(byte[] chunk)
        {
            if (chunk.Length == 0) return;
            lock (_lock)
            {
                if (_completed) return;
                _chunks.Enqueue(chunk);
                Monitor.PulseAll(_lock);
            }
        }

        public void CompleteWriting()
        {
            lock (_lock)
            {
                _completed = true;
                Monitor.PulseAll(_lock);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                while (_chunks.Count == 0)
                {
                    if (_completed) return 0;
                    Monitor.Wait(_lock);
                }
                byte[] head = _chunks.Peek();
                int available = head.Length - _offset;
                int n = Math.Min(available, count);
                Array.Copy(head, _offset, buffer, offset, n);
                _offset += n;
                if (_offset >= head.Length) { _chunks.Dequeue(); _offset = 0; }
                return n;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

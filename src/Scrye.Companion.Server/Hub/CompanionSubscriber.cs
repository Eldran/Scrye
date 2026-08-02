using System.Threading.Channels;
using Scrye.Companion.Protocol;

namespace Scrye.Companion.Server.Hub;

/// <summary>
/// One connected companion device: its subscription, its own outbound queue, and its own
/// sequence cursor.
///
/// <para>Per-connection state is the point. Even a single user is realistically a phone, a
/// tablet and a desktop browser at once, so nothing here may be shared or global — a single
/// global cursor would be a rewrite the first time a second device connected (§11.3).</para>
///
/// <para>The queue is <b>bounded and drops oldest</b>. A phone on a bad connection must not
/// be able to make the desktop allocate without limit during a combat burst; losing frames
/// is recoverable, because the client's cursor then falls behind and it resumes or
/// snapshots. Backpressure onto the UI thread would not be recoverable.</para>
/// </summary>
public sealed class CompanionSubscriber
{
    /// <summary>Frames buffered per device before the oldest start being dropped. Roughly
    /// eight seconds of 33 ms flushes — long enough to ride out a stall, short enough that
    /// a dead client cannot cost much memory.</summary>
    public const int QueueCapacity = 256;

    private readonly Channel<object> _outbound;

    public CompanionSubscriber(string id, bool mayRunScripts)
    {
        Id = id;
        MayRunScripts = mayRunScripts;
        _outbound = Channel.CreateBounded<object>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // exactly one socket writer loop drains this
            SingleWriter = false,  // UI thread and session loop both publish
        });
    }

    /// <summary>Stable id for logging and the paired-devices list.</summary>
    public string Id { get; }

    /// <summary>Whether this device may use the Lua console (§7.3). Off unless granted;
    /// carried here so the boundary check needs no lookup per frame.</summary>
    public bool MayRunScripts { get; }

    /// <summary>The world this device is watching, or null before it subscribes. Switching
    /// sessions on the phone only changes this — the desktop's connections are untouched.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Highest sequence this device has been *sent*. Its resume point. -1 means it
    /// has received nothing yet, so the first delivery must be a snapshot.</summary>
    public long LastSentSequence { get; private set; } = -1;

    /// <summary>Frames dropped by the bounded queue. Non-zero means this device fell behind
    /// and will need a resume; worth surfacing rather than hiding (§10, "no silent caps").</summary>
    public long DroppedFrames { get; private set; }

    public ChannelReader<object> Outbound => _outbound.Reader;

    public void Subscribe(string sessionId)
    {
        SessionId = sessionId;
        LastSentSequence = -1;   // new subscription starts cold
    }

    /// <summary>Note the resume point a client claimed, so subsequent replay starts there.</summary>
    public void SetResumePoint(long lastReceivedSequence) => LastSentSequence = lastReceivedSequence;

    /// <summary>Queue a frame for this device. Never blocks and never throws: a full queue
    /// drops the oldest frame and counts it. Returns false only once the connection is
    /// closing.</summary>
    public bool TryPublish(object message)
    {
        if (message is OutputBatchMessage batch && batch.Lines.Count > 0)
            LastSentSequence = batch.Lines[^1].Sequence;

        if (_outbound.Writer.TryWrite(message)) return true;

        // Bounded + DropOldest only fails once the writer is completed.
        return false;
    }

    /// <summary>Called by the hub when DropOldest actually discarded something. The channel
    /// does not report this, so the hub infers it from queue depth.</summary>
    public void NoteDropped(long count) => DroppedFrames += count;

    /// <summary>Whether this subscriber wants frames for <paramref name="sessionId"/>.</summary>
    public bool Watches(string sessionId) =>
        SessionId is not null && string.Equals(SessionId, sessionId, StringComparison.Ordinal);

    public void Complete() => _outbound.Writer.TryComplete();
}

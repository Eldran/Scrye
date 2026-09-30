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

    // Guards the live-hold state below AND orders every live publish against the release,
    // so "reply first, then what was held, then live" holds across the UI thread, the
    // session loop and the socket reader. Held only for list/TryWrite work — never across I/O.
    private readonly object _gate = new();

    // While a subscribe/resume reply is being built, live frames for the new session are
    // parked here instead of queued. Otherwise a batch published after the snapshot was
    // captured but before the reply was queued would land AHEAD of the snapshot, and the
    // snapshot (which resets the client's view) would wipe it — lost lines.
    private List<object>? _held;

    // 1 when a frame was dropped and TryPublish has not yet looked at it.
    private int _dropPending;

    // 1 once a resync notice has been queued. Reset by Subscribe, whose snapshot IS the
    // resync, so a device gets at most one notice per subscribe round trip.
    private int _resyncNoticeSent;

    private long _droppedFrames;

    public CompanionSubscriber(string id, bool mayRunScripts)
    {
        Id = id;
        MayRunScripts = mayRunScripts;
        _outbound = Channel.CreateBounded<object>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // exactly one socket writer loop drains this
            SingleWriter = false,  // UI thread and session loop both publish
        },
        // The channel reports each discarded item here (synchronously, from inside TryWrite).
        // Only count it: writing the resync notice from inside the callback would re-enter
        // the channel, so TryPublish does that once TryWrite has returned.
        _ => NoteDropped(1));
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
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public ChannelReader<object> Outbound => _outbound.Reader;

    public void Subscribe(string sessionId) => Subscribe(sessionId, holdLive: false);

    /// <summary>Watch <paramref name="sessionId"/>. With <paramref name="holdLive"/>, live
    /// frames for it are parked until <see cref="PublishReply"/> queues the subscribe/resume
    /// answer, so nothing published while that answer was being built can overtake it.</summary>
    public void Subscribe(string sessionId, bool holdLive)
    {
        lock (_gate)
        {
            SessionId = sessionId;
            LastSentSequence = -1;   // new subscription starts cold
            _held = holdLive ? new List<object>() : null;
            // A fresh snapshot is on its way — that IS the resync, so a later overflow may
            // notify again.
            Volatile.Write(ref _dropPending, 0);
            Volatile.Write(ref _resyncNoticeSent, 0);
        }
    }

    /// <summary>Queue the answer to a client frame (may be null), then release any live
    /// frames held since <see cref="Subscribe(string, bool)"/>, in order. Called by the socket
    /// reader after every client frame so a hold can never outlive its request.</summary>
    public void PublishReply(object? reply)
    {
        lock (_gate)
        {
            if (reply is not null) TryPublish(reply);
            if (_held is null) return;
            List<object> held = _held;
            _held = null;
            // Some of these may predate the snapshot capture; the client drops output lines
            // whose sequence it has already seen, and state/panel frames are idempotent.
            foreach (object m in held) TryPublish(m);
        }
    }

    /// <summary>A live broadcast (output, state, panels) for the watched session. Parked
    /// while a subscribe/resume reply is pending; queued normally otherwise.</summary>
    public bool TryPublishLive(object message)
    {
        lock (_gate)
        {
            if (_held is null) return TryPublish(message);
            if (_held.Count >= QueueCapacity)
            {
                // Same policy as the queue itself: drop oldest, count it, ask for a resync.
                _held.RemoveAt(0);
                NoteDropped(1);
            }
            _held.Add(message);
            return true;
        }
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

        if (!_outbound.Writer.TryWrite(message))
            return false;   // Bounded + DropOldest only fails once the writer is completed.

        // Something was discarded (possibly a snapshot, a state update or a panel — losses a
        // sequence gap cannot reveal). Tell the client once per episode to re-subscribe; the
        // notice is the newest item, so it survives unless 256 more frames follow it.
        if (Interlocked.Exchange(ref _dropPending, 0) == 1 && SessionId is { } session
            && Interlocked.CompareExchange(ref _resyncNoticeSent, 1, 0) == 0)
            _outbound.Writer.TryWrite(new SessionResyncMessage(session));
        return true;
    }

    /// <summary>Count discarded frames and arm the resync notice. Called by the channel's
    /// item-dropped callback and by the hold buffer's own overflow.</summary>
    public void NoteDropped(long count)
    {
        Interlocked.Add(ref _droppedFrames, count);
        Volatile.Write(ref _dropPending, 1);
    }

    /// <summary>Whether this subscriber wants frames for <paramref name="sessionId"/>.</summary>
    public bool Watches(string sessionId) =>
        SessionId is not null && string.Equals(SessionId, sessionId, StringComparison.Ordinal);

    public void Complete() => _outbound.Writer.TryComplete();
}

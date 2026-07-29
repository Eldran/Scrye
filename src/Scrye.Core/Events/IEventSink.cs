namespace Scrye.Core.Events;

/// <summary>
/// A consumer of the session event stream. Sinks are called synchronously on the
/// session's single mailbox loop, in registration order, so they must not block.
/// Implementations: the ring-buffer <see cref="EventLog"/>, the
/// <see cref="SessionRecorder"/>, and (later) plugin-facing observers.
/// </summary>
public interface IEventSink
{
    void OnEvent(SessionEvent ev);
}

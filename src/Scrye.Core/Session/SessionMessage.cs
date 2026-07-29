namespace Scrye.Core.Session;

/// <summary>Messages processed serially by the <see cref="MudSession"/> loop.
/// Everything that mutates session state (incoming data, user input, outgoing
/// sends, raw protocol replies, timer ticks, script runs) arrives as one of
/// these, so ordering is deterministic and there is no re-entrancy.</summary>
public abstract record SessionMessage
{
    /// <summary>Raw bytes arrived from the server.</summary>
    public sealed record DataArrived(byte[] Bytes) : SessionMessage;

    /// <summary>The user submitted a line of input (runs through aliases).</summary>
    public sealed record UserInput(string Text) : SessionMessage;

    /// <summary>Text to send to the MUD as a line (appends newline). From triggers/aliases/timers.</summary>
    public sealed record SendText(string Text) : SessionMessage;

    /// <summary>Raw bytes to send to the server verbatim (telnet negotiation replies, GMCP, ...).</summary>
    public sealed record SendBytes(byte[] Bytes) : SessionMessage;

    /// <summary>A one-second scheduler tick, driving timers.</summary>
    public sealed record Tick : SessionMessage;

    /// <summary>A chunk of script to execute (runs on the loop, single-threaded
    /// with respect to trigger/alias script callbacks).</summary>
    public sealed record RunScript(string Code) : SessionMessage;

    /// <summary>Connection state changed (raised on a socket thread, routed through
    /// the mailbox so state notifications and event emission stay on the loop thread).</summary>
    public sealed record ConnectionStateChanged(Scrye.Core.Model.ConnectionState State) : SessionMessage;
}

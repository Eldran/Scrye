namespace Scrye.Core.Session;

/// <summary>Messages processed serially by the <see cref="MudSession"/> loop.
/// Everything that mutates session state (incoming data, user input, outgoing
/// sends from automation, timer ticks) arrives as one of these, so ordering is
/// deterministic and there is no re-entrancy.</summary>
public abstract record SessionMessage
{
    /// <summary>Raw bytes arrived from the server.</summary>
    public sealed record DataArrived(byte[] Bytes) : SessionMessage;

    /// <summary>The user submitted a line of input (runs through aliases).</summary>
    public sealed record UserInput(string Text) : SessionMessage;

    /// <summary>Text to send straight to the MUD (e.g. from a trigger/alias/timer action).</summary>
    public sealed record SendText(string Text) : SessionMessage;

    /// <summary>A one-second scheduler tick, driving timers.</summary>
    public sealed record Tick : SessionMessage;

    /// <summary>A chunk of script to execute (runs on the loop, so single-threaded
    /// with respect to trigger/alias script callbacks).</summary>
    public sealed record RunScript(string Code) : SessionMessage;
}

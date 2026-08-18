namespace Scrye.Core.Session;

/// <summary>Messages processed serially by the <see cref="MudSession"/> loop.
/// Everything that mutates session state (incoming data, user input, outgoing
/// sends, raw protocol replies, timer ticks, script runs) arrives as one of
/// these, so ordering is deterministic and there is no re-entrancy.</summary>
public abstract record SessionMessage
{
    /// <summary>Raw bytes arrived from the server.</summary>
    public sealed record DataArrived(byte[] Bytes) : SessionMessage;

    /// <summary>Bytes inflated by the MCCP2 decompressor pump — the decompressed
    /// continuation of the telnet stream, processed exactly like plain arrivals.</summary>
    public sealed record DataInflated(byte[] Bytes) : SessionMessage;

    /// <summary>The user submitted a line of input (runs through aliases).
    /// <paramref name="Split"/> is false for text the MUD authored rather than the user --
    /// an MXP command link -- which must stay one command whatever separators it contains.</summary>
    public sealed record UserInput(string Text, bool Split = true) : SessionMessage;

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

    /// <summary>Control the sequence engine from off-loop (UI). Kind: run/walk/stop/pause/resume.</summary>
    public sealed record SequenceControl(string Kind, string Arg) : SessionMessage;

    /// <summary>Install a transcript logger (or null to stop). Routed through the
    /// mailbox so the <c>_logger</c> field is only mutated on the loop thread.</summary>
    public sealed record LoggingControl(Scrye.Core.Logging.SessionLogger? Logger) : SessionMessage;

    /// <summary>A client-side notice to display + log (e.g. reconnect countdown),
    /// raised on the loop so ordering and logging stay consistent.</summary>
    public sealed record SystemNotice(string Text) : SessionMessage;

    /// <summary>Replace the live automation rule set (triggers/aliases/timers) with a
    /// freshly-resolved profile's — applied on the loop so it can't race the engine.
    /// Runtime variables are left untouched.</summary>
    public sealed record ReloadAutomation(Scrye.Core.Profiles.EffectiveProfile Profile) : SessionMessage;

    /// <summary>Run an arbitrary action on the loop thread (e.g. a plugin panel-button
    /// callback fired from the UI), so it stays single-threaded with the rest of the session.</summary>
    public sealed record Invoke(Action Action) : SessionMessage;
}

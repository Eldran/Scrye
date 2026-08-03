namespace Scrye.Companion.Protocol;

/// <summary>
/// The <c>type</c> discriminator carried by every companion frame. String constants
/// rather than an enum: the wire form is JSON read by a browser client, and a client
/// from a newer or older build must be able to see an unrecognised type without a
/// deserializer throwing.
/// </summary>
public static class MessageTypes
{
    // desktop → client
    public const string OutputBatch = "output.batch";
    public const string PaneOutput = "output.pane";
    public const string StateUpdate = "state.update";
    public const string SessionState = "session.state";
    public const string SessionList = "session.list";
    public const string HudPanel = "hud.panel";
    public const string HudPanelRemoved = "hud.panel.removed";
    public const string Snapshot = "session.snapshot";
    public const string Error = "error";

    // client → desktop
    public const string CommandSend = "command.send";
    public const string HudAction = "hud.action";
    public const string HudSubmit = "hud.submit";
    public const string HudCell = "hud.cell";
    public const string SessionResume = "session.resume";
    public const string SessionSubscribe = "session.subscribe";
    public const string PushSubscribe = "push.subscribe";
    public const string PushUnsubscribe = "push.unsubscribe";
}

/// <summary>
/// Why the desktop rejected something. Sent as an <see cref="ErrorMessage"/> rather than
/// silently dropped, so a client can tell "you may not do that" from "the socket died".
/// </summary>
public enum CompanionErrorCode
{
    /// <summary>Catch-all for a malformed or unrecognised frame.</summary>
    BadRequest,

    /// <summary>The device is authenticated but lacks the permission this action needs —
    /// e.g. sending a <c>/</c> script command without the Run-scripts permission (§7.3).</summary>
    PermissionDenied,

    /// <summary>The referenced <c>sessionId</c> is not a currently connected world.</summary>
    UnknownSession,

    /// <summary>The requested resume point has already been trimmed out of scrollback;
    /// the client should ask for a snapshot instead (§6).</summary>
    ResumeTooOld,
}

// NOTE: CommandSource / CommandOrigin deliberately live in Scrye.Core.Automation, not here.
// They never cross the wire — a client does not get to declare its own privilege; the server
// derives it from which paired device sent the frame. Keeping them out of the wire contract
// makes that non-negotiable.

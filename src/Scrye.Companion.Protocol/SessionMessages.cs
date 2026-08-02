using System.Text.Json.Serialization;

namespace Scrye.Companion.Protocol;

/// <summary>One connected world, as the client sees it in a session picker.</summary>
public sealed record SessionStateMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("connected")] bool IsConnected,
    [property: JsonPropertyName("character")] string? CharacterName,
    [property: JsonPropertyName("world")] string? WorldName)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.SessionState;
}

/// <summary>Every world the desktop currently has open. Sent on connect and whenever a
/// world is added or closed — the desktop keeps them all connected regardless of which one
/// the phone is looking at (§4).</summary>
public sealed record SessionListMessage(
    [property: JsonPropertyName("sessions")] IReadOnlyList<SessionStateMessage> Sessions)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.SessionList;
}

/// <summary>A command from a companion device. The server routes this through the same
/// pipeline as typed input — so aliases, triggers, highlights and logging all apply — but
/// tagged <see cref="CommandSource.Companion"/> so the scripting gate in §7.3 can act on it.
/// The client never says what the source is; the server sets it.</summary>
public sealed record SendCommandMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("command")] string Command)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.CommandSend;
}

/// <summary>Client → desktop on reconnect: "I last saw this sequence." The desktop replays
/// from scrollback when <c>CanReplayFrom</c> allows, and otherwise answers with a
/// <see cref="SnapshotMessage"/> (§6).</summary>
public sealed record SessionResumeMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("lastReceivedSequence")] long LastReceivedSequence)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.SessionResume;
}

/// <summary>Client → desktop: watch this world. Switching sessions on the phone is only a
/// change of subscription; the desktop's connections are unaffected.</summary>
public sealed record SessionSubscribeMessage(
    [property: JsonPropertyName("sessionId")] string SessionId)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.SessionSubscribe;
}

/// <summary>
/// A full rebuild for a client whose resume gap was too large: the tail of scrollback, the
/// whole state tree, and the current HUD panels. Because the desktop never dropped the MUD
/// connection, "the phone slept for an hour" is only a big gap, not a lost session (§6).
/// </summary>
public sealed record SnapshotMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("session")] SessionStateMessage Session,
    [property: JsonPropertyName("output")] OutputBatchMessage Output,
    [property: JsonPropertyName("state")] IReadOnlyList<StateUpdateMessage> State,
    [property: JsonPropertyName("panels")] IReadOnlyList<HudPanelMessage> Panels)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.Snapshot;
}

/// <summary>A rejection, always sent rather than dropped silently, so the client can tell
/// a refusal from a dead socket. <see cref="Detail"/> is for humans and logs.</summary>
public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] CompanionErrorCode Code,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("sessionId")] string? SessionId = null)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.Error;
}

using System.Text.Json.Serialization;
using Scrye.Core.Plugins;
using Scrye.Core.State;

namespace Scrye.Companion.Protocol;

/// <summary>
/// One leaf change in the shared state tree, mirroring <see cref="StateValue"/>: a
/// <see cref="StateKind"/> plus the canonical text form.
///
/// <para>Two things this deliberately does NOT do (§3.3). It does not type the value as a
/// number — <c>char.name</c> and <c>room.exits.0</c> are strings, and a numeric field would
/// silently destroy them. And it carries no <c>maximum</c>: max is a sibling path
/// (<c>char.vitals.hp</c> and <c>char.vitals.maxhp</c> are independent leaves), exactly as
/// <see cref="WidgetSpec"/> already models with separate Value/Max paths.</para>
///
/// <para><see cref="Removed"/> matters because <c>StateStore</c> genuinely deletes leaves —
/// via <c>ClearPrefix</c>, and via the diffing resend inside <c>SetJson</c>. A client that
/// ignored removals would keep showing stale values after a GMCP package shrank.</para>
/// </summary>
public sealed record StateUpdateMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] StateKind Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("removed")] bool Removed = false)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StateUpdate;

    public static StateUpdateMessage From(string sessionId, in StateChange change) =>
        new(sessionId, change.Path, change.Value.Kind, change.Value.Text, change.Removed);
}

/// <summary>A HUD panel's declarative spec, streamed on build and replaced on rebuild.
/// <see cref="PanelSpec"/> is a plain record in Scrye.Core with no Avalonia dependency
/// (tabs and buttonrow children included), so it crosses the wire as-is — no adapter,
/// and every plugin panel gains a mobile rendering for free (§2, §4).</summary>
public sealed record HudPanelMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("panelId")] string PanelId,
    [property: JsonPropertyName("spec")] PanelSpec Spec)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.HudPanel;
}

/// <summary>A panel went away (plugin disabled, unloaded, or reloaded).</summary>
public sealed record HudPanelRemovedMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("panelId")] string PanelId)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.HudPanelRemoved;
}

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

/// <summary>
/// Client → desktop: a panel button was tapped. <see cref="Action"/> is the opaque id the
/// plugin runtime put in <c>WidgetSpec.Action</c>; <see cref="PanelId"/> is the panel key
/// (<c>pluginId|title</c>), from which the desktop recovers which plugin to call.
///
/// <para>Without this a streamed panel is a picture: gauges and bars you can read and
/// buttons you cannot press. It carries no script or command text — only an id the desktop
/// already published — so it grants a device nothing beyond firing a callback its own
/// plugins defined. That is why it is not gated like the <c>/</c> console (§7.3).</para>
///
/// <para>Buttons only. <c>colorgrid</c> cell taps and <c>input</c> submits have their own
/// runtime entry points and so their own messages — <see cref="HudCellMessage"/> and
/// <see cref="HudSubmitMessage"/>.</para>
/// </summary>
public sealed record HudActionMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("panelId")] string PanelId,
    [property: JsonPropertyName("action")] string Action)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.HudAction;

    /// <summary>The plugin that owns the panel — the part of the key before the '|'
    /// separator that <c>HudPanelViewModel.Key</c> builds. Empty when malformed.</summary>
    public string PluginId => PluginIdOf(PanelId);

    internal static string PluginIdOf(string panelId)
    {
        int bar = panelId.IndexOf('|');
        return bar > 0 ? panelId[..bar] : "";
    }
}

/// <summary>
/// Client → desktop: an <c>input</c> widget was submitted. Separate from
/// <see cref="HudActionMessage"/> because the plugin runtime has a distinct entry point
/// (<c>InvokeSubmit</c>) that takes the entered text.
///
/// <para>Unlike a button this carries <b>user-supplied text</b> — but it reaches a callback
/// the plugin defined for exactly this widget, never the command pipeline, so it grants no
/// more than typing into the same field on the desktop would.</para>
/// </summary>
public sealed record HudSubmitMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("panelId")] string PanelId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("text")] string Text)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.HudSubmit;

    public string PluginId => HudActionMessage.PluginIdOf(PanelId);
}

/// <summary>Client → desktop: a <c>colorgrid</c> cell was tapped. The plugin's callback
/// receives the cell's column, row and character and maps them back to its own data — a map
/// square, a chart column — so the coordinates mean nothing outside that plugin.</summary>
public sealed record HudCellMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("panelId")] string PanelId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("col")] int Col,
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("ch")] string Ch)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.HudCell;

    public string PluginId => HudActionMessage.PluginIdOf(PanelId);
}

using System.Text;

namespace Scrye.Core.Model;

/// <summary>
/// Serializable per-world configuration. Deliberately separate from the live
/// <c>MudSession</c>: this is data, the session is behaviour. Persisted as
/// JSON/TOML. (Will become a layer in the Global->MUD->Account->Character
/// cascade — see the profile-model doc.)
/// </summary>
public sealed class WorldProfile
{
    public string Name { get; set; } = "New World";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 23;

    public bool UseTls { get; set; } = false;
    /// <summary>Accept self-signed / otherwise-invalid TLS certificates (common on MUDs).</summary>
    public bool AcceptInvalidCertificates { get; set; } = false;

    /// <summary>Character encoding of the stream. Common MUD choices: UTF-8, Latin-1, CP437.</summary>
    public string EncodingName { get; set; } = "utf-8";

    /// <summary>Terminal size reported via NAWS (until the UI supplies the live size).</summary>
    public int TerminalColumns { get; set; } = 120;
    public int TerminalRows { get; set; } = 40;

    /// <summary>Auto-login: sent in reply to a name/login prompt after connect.
    /// Resolved from the profile cascade (typically the Account layer). Empty = off.</summary>
    public string Username { get; set; } = "";

    /// <summary>Auto-login password, injected at runtime from the OS credential store
    /// via the layer's PasswordRef. NEVER serialized — this type is runtime data.</summary>
    public string Password { get; set; } = "";

    /// <summary>Accept MXP (telnet option 91: clickable links + inline markup) when the
    /// server offers it. Harmless when the server doesn't — negotiation-gated.</summary>
    public bool EnableMxp { get; set; } = true;

    /// <summary>Consume in-band MSP lines (<c>!!SOUND(…)</c>/<c>!!MUSIC(…)</c>) and play them.
    /// Harmless when the server never sends them.</summary>
    public bool EnableMsp { get; set; } = true;

    /// <summary>Enable the 3Kingdoms/3Scapes in-band MIP protocol (handshake + frame parsing).</summary>
    public bool EnableMip { get; set; } = false;
    /// <summary>5-digit MIP client id (generated on first connect if empty; persisted with the profile).</summary>
    public string MipClientId { get; set; } = "";

    /// <summary>
    /// Which of this world's chat channels are relayed into whatever world tab is in FRONT, so a
    /// tell to a character on another MUD is not missed while you are playing elsewhere. A
    /// comma-separated list of channel names as <c>ChannelMessage</c> reports them —
    /// <c>"Tell"</c> for direct messages, otherwise the MUD's own channel name.
    ///
    /// <para><c>"*"</c> relays every channel; empty relays nothing. The default relays tells
    /// only: channel chatter from a MUD you are not looking at is noise, but a tell is the one
    /// thing worth interrupting you.</para>
    ///
    /// <para>This is a property of the SOURCE world — "what may this world interrupt me with" —
    /// which is why it resolves through the cascade like any other setting rather than being one
    /// app-wide switch. Set it once on the Global layer for a default across every world.</para>
    /// </summary>
    public string RelayChannels { get; set; } = "Tell";

    /// <summary>True when <paramref name="channel"/> is one this world may relay. Case-insensitive,
    /// tolerant of spaces around the commas, and never true for a blank channel name.</summary>
    public bool ShouldRelay(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return false;
        if (string.IsNullOrWhiteSpace(RelayChannels)) return false;

        string wanted = channel.Trim();
        foreach (string raw in RelayChannels.Split(','))
        {
            string entry = raw.Trim();
            if (entry.Length == 0) continue;
            if (entry == "*") return true;
            if (string.Equals(entry, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public Encoding ResolveEncoding()
    {
        try { return Encoding.GetEncoding(EncodingName); }
        catch { return Encoding.UTF8; }
    }
}

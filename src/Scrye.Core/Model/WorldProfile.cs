using System.Text;

namespace Scrye.Core.Model;

/// <summary>
/// Serializable per-world configuration (host, encoding, and — later — triggers,
/// aliases, timers, palette, logging…). Deliberately separate from the live
/// <c>MudSession</c>: this is data, the session is behaviour. Persisted as JSON/TOML.
/// </summary>
public sealed class WorldProfile
{
    public string Name { get; set; } = "New World";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 23;
    public bool UseTls { get; set; } = false;

    /// <summary>Character encoding of the stream. Common MUD choices: UTF-8, Latin-1, CP437.</summary>
    public string EncodingName { get; set; } = "utf-8";

    // Placeholders for the automation surface (filled in a later milestone).
    // public List<TriggerDef> Triggers { get; set; } = new();
    // public List<AliasDef>   Aliases  { get; set; } = new();
    // public List<TimerDef>   Timers   { get; set; } = new();
    // public Dictionary<string,string> Variables { get; set; } = new();

    public Encoding ResolveEncoding()
    {
        try { return Encoding.GetEncoding(EncodingName); }
        catch { return Encoding.UTF8; }
    }
}

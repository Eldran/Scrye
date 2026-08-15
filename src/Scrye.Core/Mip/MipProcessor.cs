using System.Text.RegularExpressions;
using Scrye.Core.Automation;

namespace Scrye.Core.Mip;

/// <summary>
/// Decodes MIP frames into game state and display events. FFF composite vitals
/// (hp/sp/gold/enemy/glines) and BBE map data are written into the world's
/// <see cref="VariableStore"/> (so aliases/triggers/scripts and a future HUD can
/// read <c>${hp}</c> etc.); tells/channels/notices are surfaced for display.
/// Port of the 3Scapes MIP plugin's tag handling.
/// </summary>
public sealed class MipProcessor
{
    private readonly VariableStore _vars;
    public MipProcessor(VariableStore vars) => _vars = vars;

    /// <summary>Raised after vitals/map variables change.</summary>
    public event Action? VitalsUpdated;
    /// <summary>A BBE viking-feed key/value pair arrived (raised per key, before
    /// <see cref="VitalsUpdated"/>). The session maps these into the state tree.</summary>
    public event Action<string, string>? VikingData;
    /// <summary>A MIP special notice (BAA) to display.</summary>
    public event Action<string>? Notice;
    /// <summary>A tell (BAB), pre-formatted for display.</summary>
    public event Action<string>? Tell;
    /// <summary>A broadcast-channel message (CAA): (channel, message).</summary>
    public event Action<string, string>? Channel;

    public void Handle(MipMessage m)
    {
        switch (m.Tag)
        {
            case "FFF": HandleFFF(m.Data); break;
            case "BBE": HandleBBE(m.Data); break;
            case "AAC": _vars.Set("reboot", m.Data); break;
            case "AAF": _vars.Set("uptime", m.Data); break;
            case "BAE": _vars.Set("lag", m.Data); break;
            case "BAA": Notice?.Invoke(m.Data); break;
            case "BAB": HandleBAB(m.Data); break;
            case "CAA": HandleCAA(m.Data); break;
        }
    }

    // FFF: FLAG~VALUE~FLAG~VALUE~... (single-tilde). Composite vitals.
    private void HandleFFF(string data)
    {
        string[] toks = data.Split('~');
        for (int i = 0; i + 1 < toks.Length; i += 2)
        {
            string flag = toks[i], val = toks[i + 1];
            if (flag.Length != 1) continue;
            switch (flag[0])
            {
                case 'A': _vars.Set("hp", val); break;
                case 'B': _vars.Set("hpmax", val); break;
                case 'C': _vars.Set("sp", val); break;
                case 'D': _vars.Set("spmax", val); break;
                case 'E': _vars.Set("gp1", val); break;
                case 'F': _vars.Set("gp1max", val); break;
                case 'G': _vars.Set("gp2", val); break;
                case 'H': _vars.Set("gp2max", val); break;
                case 'I': _vars.Set("gline1", ColorConv(val)); break;
                case 'J': _vars.Set("gline2", ColorConv(val)); break;
                case 'K':
                    if (val.Length > 0) _vars.Set("enemy_name", val);
                    else { _vars.Set("enemy_name", ""); _vars.Set("enemy_hp", ""); }
                    break;
                case 'L': _vars.Set("enemy_hp", val); break;
                case 'N': _vars.Set("round", val); break;
            }
        }
        VitalsUpdated?.Invoke();
    }

    // BBE: KEY^^VALUE^^KEY^^VALUE... (Viking map / vmip feed).
    //
    // Over-long values arrive split as KEY_<n>of<m> chunks (e.g. SHIPS_1of4). We buffer
    // the pieces and, once all m are in, stitch them back into the base KEY — the chunk
    // keys themselves are never stored, so the base key always holds the CURRENT whole
    // value and no stale chunks accumulate. (An older KEY_<n> format also exists in the
    // wild; those are ignored.) Faithful port of the reference ThreeS_MIP plugin.
    private static readonly Regex ChunkKey = new(@"^(.+)_(\d+)of(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex OldChunkKey = new(@"_\d+$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, (int Total, string?[] Parts)> _chunks = new(StringComparer.Ordinal);

    private void HandleBBE(string data)
    {
        string[] t = data.Split("^^");
        for (int i = 0; i + 1 < t.Length; i += 2)
        {
            string key = t[i], val = t[i + 1];

            // A stray caret in the stream leaks into the next key: an extra '^' before the
            // separator makes "^^^THRALLS^^" split into a key literally named "^THRALLS". Seen
            // live — the shape audit reported THRALLS and ^THRALLS side by side with identical
            // shapes. Left alone it is worse than cosmetic: the two are different state keys, so
            // every caret-prefixed arrival updates a key nothing reads and leaves vmip_THRALLS
            // holding the previous value. No MIP key legitimately starts with one.
            key = key.TrimStart('^');

            if (key.Length == 0) continue;

            Match cm = ChunkKey.Match(key);
            if (cm.Success && int.TryParse(cm.Groups[2].Value, out int n)
                           && int.TryParse(cm.Groups[3].Value, out int m) && m > 0 && n >= 1 && n <= m)
            {
                string baseKey = cm.Groups[1].Value;
                // a new transmission (different chunk count, or part 1) restarts the buffer
                if (!_chunks.TryGetValue(baseKey, out (int Total, string?[] Parts) buf) || buf.Total != m || n == 1)
                    _chunks[baseKey] = buf = (m, new string?[m]);
                buf.Parts[n - 1] = val;

                bool complete = true;
                for (int j = 0; j < m; j++) if (buf.Parts[j] is null) { complete = false; break; }
                if (complete)
                {
                    _chunks.Remove(baseKey);
                    SetVikingKey(baseKey, string.Concat(buf.Parts));
                }
            }
            else if (OldChunkKey.IsMatch(key))
            {
                // stale old-format (KEY_n) chunk: ignore
            }
            else
            {
                SetVikingKey(key, val);
            }
        }
        VitalsUpdated?.Invoke();
    }

    private void SetVikingKey(string key, string val)
    {
        // freshness stamp for the live map: consumers compare vmaph_time to now
        if (key == "VMAPH" && _vars.Get("vmip_VMAPH") != val)
            _vars.Set("vmaph_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        _vars.Set("vmip_" + key, val);
        VikingData?.Invoke(key, val);
    }

    /// <summary>Prefix the tell text carries when the message is one YOU sent rather than one you
    /// received. Public because the cross-world relay filters on it: echoing your own outgoing
    /// tells into another world's pane is noise. Kept here, beside the one place that writes it,
    /// so the two cannot drift apart.</summary>
    public const string OutgoingTellPrefix = "To ";

    // BAB: marker~source~message
    private void HandleBAB(string data)
    {
        string[] p = data.Split('~', 3);
        if (p.Length < 3) return;
        string marker = p[0], source = p[1], msg = p[2];
        if (source is "0" or "") return;
        Tell?.Invoke(marker == "x" ? $"{OutgoingTellPrefix}{source}: {msg}" : $"{source}: {msg}");
    }

    // CAA: command~channel~source~message
    private void HandleCAA(string data)
    {
        if (data.Contains("[PARTY] GOLD divvy called by") || data.Contains("[PARTY] All gold divvied")
            || data.Contains("[PARTY] coins called by") || data.Contains("[PARTY] Divvy of")) return;
        string[] p = data.Split('~', 4);
        if (p.Length < 4) return;
        string channel = p[1], msg = p[3];
        if (msg.Length == 0) return;
        Channel?.Invoke(channel, msg);
    }

    // strip 3k gline colour tags like "<r", "<g", ">" and "red:" colour-words
    private static string ColorConv(string s)
    {
        s = Regex.Replace(s, "[<]([bcgrsvwy])", "");
        s = s.Replace(">", "");
        s = Regex.Replace(s, "gre[ya]y?:", "");
        s = s.Replace("red:", "").Replace("green:", "").Replace("blue:", "").Replace("yellow:", "");
        return s;
    }
}

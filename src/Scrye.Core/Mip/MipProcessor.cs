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
    private void HandleBBE(string data)
    {
        string[] t = data.Split("^^");
        for (int i = 0; i + 1 < t.Length; i += 2)
            if (t[i].Length > 0)
            {
                _vars.Set("vmip_" + t[i], t[i + 1]);
                VikingData?.Invoke(t[i], t[i + 1]);
            }
        VitalsUpdated?.Invoke();
    }

    // BAB: marker~source~message
    private void HandleBAB(string data)
    {
        string[] p = data.Split('~', 3);
        if (p.Length < 3) return;
        string marker = p[0], source = p[1], msg = p[2];
        if (source is "0" or "") return;
        Tell?.Invoke(marker == "x" ? $"To {source}: {msg}" : $"{source}: {msg}");
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

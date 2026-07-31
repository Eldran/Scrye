namespace Scrye.Core.Net;

/// <summary>A parsed MSP directive: the sound/music file plus the common parameters.
/// Volume 0-100; Loops -1 = repeat forever; Url = download hint (unused for now).</summary>
public sealed record MspDirective(
    string FileName,
    bool IsMusic,
    int Volume = 100,
    int Loops = 1,
    string? Type = null,
    string? Url = null);

/// <summary>
/// MUD Sound Protocol (in-band form): lines of the shape
/// <c>!!SOUND(file.wav V=80 L=2 T=combat U=http://…)</c> or <c>!!MUSIC(…)</c>.
/// The session consumes matching lines (they never display or hit triggers) and
/// surfaces them as sound requests. <c>!!SOUND(Off)</c> parses with FileName "Off".
/// </summary>
public static class MspParser
{
    /// <summary>Try to parse a display line as an MSP directive. Returns false for
    /// ordinary text (including lines that merely mention !!SOUND mid-line).</summary>
    public static bool TryParse(string line, out MspDirective? directive)
    {
        directive = null;
        string s = line.Trim();

        bool music;
        if (s.StartsWith("!!SOUND(", StringComparison.OrdinalIgnoreCase)) music = false;
        else if (s.StartsWith("!!MUSIC(", StringComparison.OrdinalIgnoreCase)) music = true;
        else return false;

        int open = s.IndexOf('(');
        int close = s.LastIndexOf(')');
        if (close <= open + 1) return false;   // "!!SOUND()" or unterminated

        string body = s.Substring(open + 1, close - open - 1).Trim();
        if (body.Length == 0) return false;

        // first token is the file; the rest are K=V parameters
        string[] tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string file = tokens[0];

        int volume = 100, loops = 1;
        string? type = null, url = null;
        for (int i = 1; i < tokens.Length; i++)
        {
            string tok = tokens[i];
            int eq = tok.IndexOf('=');
            if (eq <= 0 || eq == tok.Length - 1) continue;
            string key = tok[..eq].ToUpperInvariant();
            string val = tok[(eq + 1)..];
            switch (key)
            {
                case "V": if (int.TryParse(val, out int v)) volume = Math.Clamp(v, 0, 100); break;
                case "L": if (int.TryParse(val, out int l)) loops = Math.Max(-1, l); break;
                case "T": type = val; break;
                case "U": url = val; break;
            }
        }

        directive = new MspDirective(file, music, volume, loops, type, url);
        return true;
    }
}

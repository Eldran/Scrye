namespace Scrye.Core.Plugins;

/// <summary>
/// Orders plugin version strings the way their authors mean them: dotted numbers compared as
/// numbers (<c>1.10.0</c> is newer than <c>1.9.3</c>), missing parts as zero (<c>1.2</c> is
/// <c>1.2.0</c>), and a pre-release older than its release (<c>2.0.0-beta</c> before
/// <c>2.0.0</c>). Build metadata after <c>+</c> is ignored. Anything that is not a number is
/// compared as text, so a malformed version still sorts somewhere stable instead of throwing.
/// </summary>
public static class PluginVersion
{
    /// <summary>Negative when <paramref name="a"/> is older, zero when equal, positive when newer.</summary>
    public static int Compare(string? a, string? b)
    {
        (string[] ca, string? pa) = Split(a);
        (string[] cb, string? pb) = Split(b);
        int n = Math.Max(ca.Length, cb.Length);
        for (int i = 0; i < n; i++)
        {
            int c = ComparePart(i < ca.Length ? ca[i] : "0", i < cb.Length ? cb[i] : "0");
            if (c != 0) return c;
        }
        // same numbers: a release beats any pre-release of it
        if (pa is null) return pb is null ? 0 : 1;
        if (pb is null) return -1;
        return string.CompareOrdinal(pa, pb) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    public static bool IsNewer(string? candidate, string? than) => Compare(candidate, than) > 0;

    private static (string[] Core, string? Pre) Split(string? v)
    {
        string s = (v ?? "").Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; }
        string[] core = s.Length == 0 ? Array.Empty<string>() : s.Split('.');
        return (core, string.IsNullOrEmpty(pre) ? null : pre);
    }

    private static int ComparePart(string x, string y)
    {
        bool nx = long.TryParse(x, out long ix), ny = long.TryParse(y, out long iy);
        if (nx && ny) return ix.CompareTo(iy) switch { < 0 => -1, > 0 => 1, _ => 0 };
        if (nx != ny) return nx ? 1 : -1;               // a number beats a word ("1.2.x")
        return string.CompareOrdinal(x, y) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }
}

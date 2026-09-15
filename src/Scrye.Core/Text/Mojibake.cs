using System.Text;

namespace Scrye.Core.Text;

/// <summary>
/// Undoes one specific damage: UTF-8 bytes that were read as single-byte characters, so
/// "låda" arrives as "lÃ¥da". LDMud's JSON serializer treats strings as bytes and escapes
/// each byte above 0x7F on its own, so every non-ASCII character a player typed reaches
/// Scrye through GMCP as two or three Latin-1 characters (Comm.Channel.Text, 15 Sep 2026:
/// Swedish in tells). The repair is safe to apply blind: a string is re-decoded only when
/// every character fits in one byte AND those bytes are strictly valid UTF-8 with at least
/// one multi-byte sequence - real Latin-1 text like "Ã" on its own fails the second test.
/// </summary>
public static class Mojibake
{
    private static readonly Encoding Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Repair(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        bool high = false;
        foreach (char c in s)
        {
            if (c > 0xFF) return s;          // already real Unicode: nothing to undo
            if (c >= 0x80) high = true;
        }
        if (!high) return s;                  // plain ASCII
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
        try { return Strict.GetString(bytes); }
        catch (DecoderFallbackException) { return s; }
    }
}

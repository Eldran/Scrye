using System.Text;
using System.Text.RegularExpressions;

namespace Scrye.Core.Mip;

/// <summary>
/// In-band MIP frame extractor for 3Kingdoms/3Scapes. MIP rides inside the normal
/// text stream: each frame is a line containing <c>#K%</c> + 5-digit id + 3-char
/// length + 3-char tag + data. This runs at the packet level (before the ANSI
/// parser), pulls frames out (raising <see cref="MessageReceived"/>), keeps any
/// text that preceded a marker, and buffers a trailing partial that might be a
/// frame split across packets. Faithful port of the reference mip.tin / plugin logic.
/// </summary>
public sealed class MipParser
{
    // <pre>#K%<id:5 digits><len:3, ignored — data runs to EOL><tag:3><data>
    private static readonly Regex Frame =
        new(@"^(.*?)#K%(\d{5})(.{3})(.{3})(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string _tail = "";

    public event Action<MipMessage>? MessageReceived;

    /// <summary>Process a chunk of decoded text; returns the text with MIP frames removed.
    /// Frames are scanned per line, where a line ends at <c>\n</c>, <c>\r\n</c> — or a bare
    /// <c>\r</c>, since the server terminates some MIP bursts with carriage returns only;
    /// non-frame text is passed through byte-for-byte either way.</summary>
    public string Process(string input)
    {
        string s = _tail + input;
        _tail = "";
        var outSb = new StringBuilder(s.Length);
        int pos = 0, len = s.Length;

        while (pos < len)
        {
            // find the next terminator: \n, \r\n, or bare \r
            int termAt = -1, termLen = 0;
            for (int i = pos; i < len; i++)
            {
                char c = s[i];
                if (c == '\n') { termAt = i; termLen = 1; break; }
                if (c == '\r')
                {
                    if (i + 1 < len) { termAt = i; termLen = s[i + 1] == '\n' ? 2 : 1; }
                    // trailing '\r' at the buffer end: could be half of a \r\n split
                    // across packets — fall through to tail handling
                    break;
                }
            }

            if (termAt >= 0)
            {
                string line = s.Substring(pos, termAt - pos + termLen);   // includes terminator
                if (line.Contains("#K%"))
                {
                    string? pre = ConsumeMipLine(line.TrimEnd('\r', '\n'));
                    if (pre is null) outSb.Append(line);                    // had #K% but wasn't a MIP frame
                    else if (pre.Length > 0) outSb.Append(pre).Append("\r\n");
                }
                else
                {
                    outSb.Append(line);
                }
                pos = termAt + termLen;
            }
            else
            {
                string tail = s.Substring(pos);
                // Hold a fragment that might be a MIP line split across packets. EndsWith("#")
                // covers a split landing exactly between '#' and 'K'; a trailing '\r' that may
                // be half of \r\n is held too when the fragment is frame-like. The cap only
                // guards against pathological runaway growth.
                bool mipLike = tail.Contains("#K") || tail.EndsWith('#');
                if (mipLike && tail.Length < 65536)
                    _tail = tail;
                else
                    outSb.Append(tail);
                break;
            }
        }
        return outSb.ToString();
    }

    private string? ConsumeMipLine(string line)
    {
        Match m = Frame.Match(line);
        if (!m.Success) return null;
        MessageReceived?.Invoke(new MipMessage(m.Groups[2].Value, m.Groups[4].Value, m.Groups[5].Value));
        return m.Groups[1].Value;   // text before the marker (kept for display)
    }

    /// <summary>Display-time fallback net: if a COMPLETED line is a MIP frame that slipped
    /// past packet-level stripping (odd packet splits, terminators the scanner missed),
    /// consume it here. Returns true when the line was a frame (raising
    /// <see cref="MessageReceived"/>); <paramref name="pre"/> carries any text that
    /// preceded the marker and should still be displayed.</summary>
    public bool TryConsumeDisplayedLine(string plainText, out string pre)
    {
        pre = "";
        if (!plainText.Contains("#K%")) return false;
        string? p = ConsumeMipLine(plainText.TrimEnd('\r', '\n'));
        if (p is null) return false;
        pre = p;
        return true;
    }
}

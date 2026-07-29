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

    /// <summary>Process a chunk of decoded text; returns the text with MIP frames removed.</summary>
    public string Process(string input)
    {
        string s = _tail + input;
        _tail = "";
        var outSb = new StringBuilder(s.Length);
        int pos = 0, len = s.Length;

        while (pos < len)
        {
            int nl = s.IndexOf('\n', pos);
            if (nl >= 0)
            {
                string line = s.Substring(pos, nl - pos + 1);   // includes '\n'
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
                pos = nl + 1;
            }
            else
            {
                string tail = s.Substring(pos);
                if (tail.Contains("#K") && tail.Length < 4096)
                    _tail = tail;                               // possibly a split MIP line: hold it
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
}

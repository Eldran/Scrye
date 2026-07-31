using System.Text;

namespace Scrye.Core.Text;

/// <summary>
/// Turns styled scrollback content back into portable formats: plain text,
/// ANSI (truecolour SGR sequences), and HTML (a self-contained &lt;pre&gt; block).
/// Consumed by the copy commands in the output view; lives in Core so the CLI
/// can exercise it offline (<c>--export</c>).
/// </summary>
public static class TextExporter
{
    /// <summary>The column range [from, to) of a line as styled runs — the unit the
    /// exporters consume. Clamps to the line's actual length.</summary>
    public static IReadOnlyList<StyledRun> Slice(Line line, int from, int to)
    {
        string plain = line.PlainText;
        from = Math.Clamp(from, 0, plain.Length);
        to = Math.Clamp(to, from, plain.Length);
        if (from == 0 && to == plain.Length) return line.Runs;

        var result = new List<StyledRun>();
        int pos = 0;
        foreach (StyledRun run in line.Runs)
        {
            int runStart = pos, runEnd = pos + run.Text.Length;
            pos = runEnd;
            int s = Math.Max(runStart, from), e = Math.Min(runEnd, to);
            if (e <= s) continue;
            result.Add(run with { Text = run.Text.Substring(s - runStart, e - s) });
        }
        return result;
    }

    public static string ToPlain(IEnumerable<IReadOnlyList<StyledRun>> lines)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (IReadOnlyList<StyledRun> runs in lines)
        {
            if (!first) sb.Append('\n');
            first = false;
            foreach (StyledRun run in runs) sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>Each line rendered with truecolour SGR codes and a trailing reset,
    /// so pasting into a terminal (or another MUD client) keeps the colours.</summary>
    public static string ToAnsi(IEnumerable<IReadOnlyList<StyledRun>> lines)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (IReadOnlyList<StyledRun> runs in lines)
        {
            if (!first) sb.Append('\n');
            first = false;
            foreach (StyledRun run in runs)
            {
                sb.Append("\x1b[0");
                if ((run.Flags & RunFlags.Bold) != 0) sb.Append(";1");
                if ((run.Flags & RunFlags.Italic) != 0) sb.Append(";3");
                if ((run.Flags & RunFlags.Underline) != 0) sb.Append(";4");
                if ((run.Flags & RunFlags.Inverse) != 0) sb.Append(";7");
                if (!run.Fore.Equals(Rgb.DefaultFore))
                    sb.Append(";38;2;").Append(run.Fore.R).Append(';').Append(run.Fore.G).Append(';').Append(run.Fore.B);
                if (!run.Back.Equals(Rgb.DefaultBack))
                    sb.Append(";48;2;").Append(run.Back.R).Append(';').Append(run.Back.G).Append(';').Append(run.Back.B);
                sb.Append('m').Append(run.Text);
            }
            sb.Append("\x1b[0m");
        }
        return sb.ToString();
    }

    /// <summary>A self-contained monospace &lt;pre&gt; block with inline styles —
    /// pasteable into mails, docs, and forums that accept HTML.</summary>
    public static string ToHtml(IEnumerable<IReadOnlyList<StyledRun>> lines)
    {
        var sb = new StringBuilder();
        sb.Append("<pre style=\"font-family:'Cascadia Mono',Consolas,Menlo,monospace;")
          .Append("background:").Append(Hex(Rgb.DefaultBack)).Append(';')
          .Append("color:").Append(Hex(Rgb.DefaultFore)).Append(";padding:8px\">");
        bool first = true;
        foreach (IReadOnlyList<StyledRun> runs in lines)
        {
            if (!first) sb.Append('\n');
            first = false;
            foreach (StyledRun run in runs)
            {
                Rgb fore = run.Fore, back = run.Back;
                if ((run.Flags & RunFlags.Inverse) != 0) (fore, back) = (back, fore);

                var style = new StringBuilder();
                if (!fore.Equals(Rgb.DefaultFore)) style.Append("color:").Append(Hex(fore)).Append(';');
                if (!back.Equals(Rgb.DefaultBack)) style.Append("background:").Append(Hex(back)).Append(';');
                if ((run.Flags & RunFlags.Bold) != 0) style.Append("font-weight:bold;");
                if ((run.Flags & RunFlags.Italic) != 0) style.Append("font-style:italic;");
                if ((run.Flags & RunFlags.Underline) != 0) style.Append("text-decoration:underline;");

                if (style.Length > 0)
                    sb.Append("<span style=\"").Append(style).Append("\">").Append(Escape(run.Text)).Append("</span>");
                else
                    sb.Append(Escape(run.Text));
            }
        }
        sb.Append("</pre>");
        return sb.ToString();
    }

    private static string Hex(Rgb c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

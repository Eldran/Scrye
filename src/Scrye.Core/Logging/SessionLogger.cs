using System.Text;
using Scrye.Core.Text;

namespace Scrye.Core.Logging;

/// <summary>Transcript output format.</summary>
public enum LogFormat
{
    /// <summary>Plain UTF-8 text, one line per output line, styling stripped.</summary>
    Text,
    /// <summary>Self-contained HTML with per-run colour spans, preserving the terminal look.</summary>
    Html,
}

/// <summary>
/// Writes a world's output transcript to a <see cref="TextWriter"/> (a file, in
/// normal use). Text logs are plain; HTML logs preserve each run's colour and
/// weight so the saved transcript looks like the terminal. One instance per
/// active log; call <see cref="Log(Line)"/> per displayed line and
/// <see cref="Close"/> when done. Thread-safe: all writes are serialized on an
/// internal lock, so the session loop and an off-loop toggle can't tear a line.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly TextWriter _writer;
    private readonly LogFormat _format;
    private readonly bool _timestamps;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private bool _closed;

    public string World { get; }
    public LogFormat Format => _format;
    /// <summary>The file path when created via <see cref="CreateFile"/>; null for an in-memory writer.</summary>
    public string? Path { get; }
    /// <summary>Number of lines written so far (excludes header/footer).</summary>
    public int LineCount { get; private set; }
    public bool IsOpen => !_closed;

    public SessionLogger(string world, TextWriter writer, LogFormat format = LogFormat.Text,
                         bool timestamps = true, Func<DateTimeOffset>? clock = null, string? path = null)
    {
        World = world;
        _writer = writer;
        _format = format;
        _timestamps = timestamps;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Path = path;
        WriteHeader();
    }

    /// <summary>Open a fresh log file <c>&lt;world&gt;-&lt;yyyy-MM-dd_HHmmss&gt;.log|.html</c>
    /// under <paramref name="directory"/> (created if missing) and return the logger.</summary>
    public static SessionLogger CreateFile(string directory, string world, LogFormat format = LogFormat.Text,
                                           bool timestamps = true, Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(directory);
        Func<DateTimeOffset> clk = clock ?? (() => DateTimeOffset.Now);
        string stamp = clk().ToString("yyyy-MM-dd_HHmmss");
        string ext = format == LogFormat.Html ? ".html" : ".log";
        string file = Sanitize(world) + "-" + stamp + ext;
        string full = System.IO.Path.Combine(directory, file);
        var writer = new StreamWriter(new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read),
                                      new UTF8Encoding(false));
        return new SessionLogger(world, writer, format, timestamps, clk, full);
    }

    /// <summary>Append one displayed line to the transcript.</summary>
    public void Log(Line line)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_format == LogFormat.Html) WriteHtmlLine(line);
            else WriteTextLine(line);
            LineCount++;
        }
    }

    /// <summary>Append a plain-text line (system notice, echoed input, …) in the given colour.</summary>
    public void Log(string text, Rgb? colour = null) => Log(Line.FromText(text, colour));

    /// <summary>Flush and finalize the transcript. Idempotent.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            WriteFooter();
            _writer.Flush();
            _writer.Dispose();
        }
    }

    public void Dispose() => Close();

    // ---- formatting ----------------------------------------------------------

    private void WriteHeader()
    {
        if (_format == LogFormat.Html)
        {
            _writer.WriteLine("<!DOCTYPE html>");
            _writer.WriteLine("<html><head><meta charset=\"utf-8\">");
            _writer.WriteLine($"<title>Scrye log — {Escape(World)}</title>");
            _writer.WriteLine("<style>body{background:#10141A;color:#C0C0C0;margin:0;padding:12px;}"
                              + "pre{font-family:'Cascadia Mono',Consolas,Menlo,monospace;font-size:13px;"
                              + "line-height:1.35;white-space:pre-wrap;word-break:break-word;margin:0;}"
                              + ".ts{color:#5A6675;}</style></head><body>");
            _writer.WriteLine($"<!-- Scrye transcript for {Escape(World)}, started {_clock():yyyy-MM-dd HH:mm:ss} -->");
            _writer.WriteLine("<pre>");
        }
        else
        {
            _writer.WriteLine($"-- Scrye transcript for {World}, started {_clock():yyyy-MM-dd HH:mm:ss} --");
        }
        _writer.Flush();
    }

    private void WriteFooter()
    {
        if (_format == LogFormat.Html)
        {
            _writer.WriteLine("</pre>");
            _writer.WriteLine($"<!-- ended {_clock():yyyy-MM-dd HH:mm:ss}, {LineCount} lines -->");
            _writer.WriteLine("</body></html>");
        }
        else
        {
            _writer.WriteLine($"-- transcript ended {_clock():yyyy-MM-dd HH:mm:ss}, {LineCount} lines --");
        }
    }

    private void WriteTextLine(Line line)
    {
        if (_timestamps) _writer.Write($"[{_clock():HH:mm:ss}] ");
        _writer.WriteLine(line.PlainText);
        _writer.Flush();
    }

    private void WriteHtmlLine(Line line)
    {
        if (_timestamps) _writer.Write($"<span class=\"ts\">[{_clock():HH:mm:ss}] </span>");
        foreach (StyledRun run in line.Runs)
        {
            if (run.Text.Length == 0) continue;
            Rgb fore = run.Fore, back = run.Back;
            if ((run.Flags & RunFlags.Inverse) != 0) (fore, back) = (back, fore);

            var style = new StringBuilder();
            style.Append("color:").Append(Hex(fore)).Append(';');
            if (!back.Equals(Rgb.DefaultBack)) style.Append("background:").Append(Hex(back)).Append(';');
            if ((run.Flags & RunFlags.Bold) != 0) style.Append("font-weight:bold;");
            if ((run.Flags & RunFlags.Italic) != 0) style.Append("font-style:italic;");
            if ((run.Flags & RunFlags.Underline) != 0) style.Append("text-decoration:underline;");

            _writer.Write("<span style=\"");
            _writer.Write(style.ToString());
            _writer.Write("\">");
            _writer.Write(Escape(run.Text));
            _writer.Write("</span>");
        }
        _writer.Write('\n');
        _writer.Flush();
    }

    private static string Hex(Rgb c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "world" : s;
    }
}

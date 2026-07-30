using System;
using System.IO;
using Scrye.Core.Logging;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

public class SessionLoggerTests
{
    private static Func<DateTimeOffset> FixedClock()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 20, 15, 0, TimeSpan.Zero);
        int n = 0;
        return () => t0.AddSeconds(n++);
    }

    private static Line Coloured() => new(new[]
    {
        new StyledRun("HP:", new Rgb(0xF0, 0xC0, 0x40), Rgb.DefaultBack, RunFlags.Bold),
        new StyledRun(" 42/100", new Rgb(0x55, 0xFF, 0x55), Rgb.DefaultBack, RunFlags.None),
    }, false, DateTimeOffset.UtcNow);

    [Fact]
    public void TextLogWritesPlainLinesWithTimestamps()
    {
        var sw = new StringWriter();
        var log = new SessionLogger("W", sw, LogFormat.Text, timestamps: true, clock: FixedClock());
        log.Log(Line.FromText("You enter the Plaza."));
        log.Log(Coloured());
        log.Close();

        string s = sw.ToString();
        Assert.Contains("You enter the Plaza.", s);
        Assert.Contains("HP: 42/100", s);        // styling stripped, runs concatenated
        Assert.Contains("[20:15", s);            // a timestamp was emitted
        Assert.Equal(2, log.LineCount);
    }

    [Fact]
    public void HtmlLogEmitsColourSpansAndEscapes()
    {
        var sw = new StringWriter();
        var log = new SessionLogger("W", sw, LogFormat.Html, timestamps: false, clock: FixedClock());
        log.Log(Coloured());
        log.Log(Line.FromText("<script> & danger"));
        log.Close();

        string s = sw.ToString();
        Assert.Contains("color:#55FF55", s);         // truecolour run
        Assert.Contains("font-weight:bold", s);      // bold flag honoured
        Assert.Contains("&lt;script&gt; &amp; danger", s);  // HTML-escaped
        Assert.Contains("<pre>", s);
        Assert.Contains("</pre>", s);
        Assert.Contains("</html>", s);
    }

    [Fact]
    public void CloseIsIdempotentAndStopsLogging()
    {
        var sw = new StringWriter();
        var log = new SessionLogger("W", sw, LogFormat.Text, timestamps: false, clock: FixedClock());
        log.Log(Line.FromText("one"));
        log.Close();
        int len = sw.ToString().Length;
        log.Close();                                  // no throw
        log.Log(Line.FromText("ignored"));            // dropped once closed
        Assert.False(log.IsOpen);
        Assert.Equal(1, log.LineCount);
        Assert.Equal(len, sw.ToString().Length);
    }

    [Fact]
    public void CreateFileWritesUnderDirectoryAndSanitizesName()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrye_logtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = SessionLogger.CreateFile(dir, "3Scapes: Main", LogFormat.Text, timestamps: false, clock: FixedClock());
            log.Log(Line.FromText("hello world"));
            string path = log.Path!;
            log.Close();

            Assert.True(File.Exists(path));
            Assert.DoesNotContain(":", Path.GetFileName(path));   // sanitized
            Assert.Contains("hello world", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

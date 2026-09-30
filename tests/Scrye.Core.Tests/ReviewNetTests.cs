using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Scrye.Core.Logging;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Session;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>Telnet layer: bounded subnegotiation, the no-IAC fast path, and per-connection reset.</summary>
public class ReviewTelnetTests
{
    private static (TelnetLayer t, List<byte> sent) NewLayer()
    {
        var t = new TelnetLayer();
        var sent = new List<byte>();
        t.SendData += b => sent.AddRange(b);
        return (t, sent);
    }

    [Fact]
    public void UnterminatedSubnegotiationIsCappedAndOutputResumes()
    {
        var (t, _) = NewLayer();
        var chunk = new List<byte> { 255, 250, 201 };                     // IAC SB GMCP, never closed
        chunk.AddRange(Enumerable.Repeat((byte)'x', TelnetLayer.MaxSubnegotiationBytes + 5));
        t.Process(chunk.ToArray());
        byte[] data = t.Process(Encoding.ASCII.GetBytes("hello"));
        Assert.Equal("hello", Encoding.ASCII.GetString(data));
    }

    [Fact]
    public void PlainChunkPassesThroughAndIacSplitAcrossChunksStillWorks()
    {
        var (t, sent) = NewLayer();
        Assert.Equal("abc", Encoding.ASCII.GetString(t.Process(Encoding.ASCII.GetBytes("abc"))));
        Assert.Empty(t.Process(new byte[] { 255 }));                     // IAC ...
        byte[] rest = t.Process(new byte[] { 251, 201, (byte)'z' });      // ... WILL GMCP, then text
        Assert.Equal("z", Encoding.ASCII.GetString(rest));
        Assert.Equal(new byte[] { 255, 253, 201 }, sent);
    }

    private static string TtypeReply(List<byte> sent)
    {
        // IAC SB 24 IS <name> IAC SE
        byte[] b = sent.ToArray();
        sent.Clear();
        return Encoding.ASCII.GetString(b, 4, b.Length - 6);
    }

    [Fact]
    public void ResetForNewConnectionRestartsTtypeCycle()
    {
        var (t, sent) = NewLayer();
        byte[] send = { 255, 250, 24, 1, 255, 240 };                      // IAC SB TTYPE SEND IAC SE
        t.Process(send); Assert.Equal("Scrye", TtypeReply(sent));
        t.Process(send); Assert.Equal("XTERM-256COLOR", TtypeReply(sent));
        t.Process(send); Assert.StartsWith("MTTS", TtypeReply(sent));

        t.ResetForNewConnection();
        t.Process(send); Assert.Equal("Scrye", TtypeReply(sent));
    }

    [Fact]
    public void ResetForNewConnectionDropsHalfReadSubnegotiation()
    {
        var (t, _) = NewLayer();
        t.Process(new byte[] { 255, 250, 201, (byte)'a', (byte)'b' });   // SB left open by a dropped socket
        t.ResetForNewConnection();
        Assert.Equal("hi", Encoding.ASCII.GetString(t.Process(Encoding.ASCII.GetBytes("hi"))));
    }
}

/// <summary>ANSI parser: ECMA-48 CSI shape, colon colours, order-independent bold, entity range.</summary>
public class ReviewAnsiTests
{
    private static List<Line> Parse(string s, bool mxp = false)
    {
        var parser = new AnsiParser(() => DateTimeOffset.UnixEpoch) { MxpEnabled = mxp };
        var lines = new List<Line>();
        parser.LineCompleted += lines.Add;
        parser.Feed(s);
        parser.FlushAsPrompt();
        return lines;
    }

    [Theory]
    [InlineData("a\x1b[?25lb\n")]
    [InlineData("a\x1b[?1049hb\n")]
    [InlineData("a\x1b[>cb\n")]
    [InlineData("a\x1b[2 qb\n")]
    [InlineData("a\x1b[=5ub\n")]
    public void PrivateAndIntermediateSequencesAreConsumedWhole(string input)
    {
        Assert.Equal("ab", Parse(input)[0].PlainText);
    }

    [Fact]
    public void PrivateSgrIsNotApplied()
    {
        StyledRun run = Parse("\x1b[?1mX\n")[0].Runs[0];
        Assert.Equal(RunFlags.None, run.Flags);
    }

    [Fact]
    public void ColonTruecolourWithEmptyColourspace()
    {
        Assert.Equal(new Rgb(255, 0, 0), Parse("\x1b[38:2::255:0:0mX\n")[0].Runs[0].Fore);
        Assert.Equal(new Rgb(10, 20, 30), Parse("\x1b[38:2:10:20:30mX\n")[0].Runs[0].Fore);
        Assert.Equal(new Rgb(1, 2, 3), Parse("\x1b[48:2:0:1:2:3mX\n")[0].Runs[0].Back);
        Assert.Equal(Rgb.Xterm256(196), Parse("\x1b[38:5:196mX\n")[0].Runs[0].Fore);
    }

    [Fact]
    public void OverlongCsiIsAbandonedWithoutLeaking()
    {
        List<Line> lines = Parse("a\x1b[" + new string('1', 5000) + "mb\n");
        Assert.Equal("ab", lines[0].PlainText);
        Assert.Equal(RunFlags.None, lines[0].Runs[^1].Flags);
    }

    [Fact]
    public void NewlineInsideCsiStillEndsTheLine()
    {
        List<Line> lines = Parse("a\x1b[3\nb\n");
        Assert.Equal("a", lines[0].PlainText);
        Assert.Equal("b", lines[1].PlainText);
    }

    [Fact]
    public void BoldBrightensRegardlessOfOrder()
    {
        Assert.Equal(Rgb.Ansi16(1, bright: true), Parse("\x1b[31;1mX\n")[0].Runs[0].Fore);
        Assert.Equal(Rgb.Ansi16(1, bright: true), Parse("\x1b[1;31mX\n")[0].Runs[0].Fore);
        Assert.Equal(Rgb.Ansi16(2, bright: true), Parse("\x1b[32m\x1b[1mX\n")[0].Runs[0].Fore);
    }

    [Fact]
    public void NormalIntensityDimsBasicColourAgain()
    {
        StyledRun run = Parse("\x1b[1;31m\x1b[22mX\n")[0].Runs[0];
        Assert.Equal(Rgb.Ansi16(1, bright: false), run.Fore);
        Assert.Equal(RunFlags.None, run.Flags & RunFlags.Bold);
    }

    [Fact]
    public void BoldDoesNotAlterExtendedColour()
    {
        Assert.Equal(Rgb.Xterm256(1), Parse("\x1b[38;5;1m\x1b[1mX\n")[0].Runs[0].Fore);
        Assert.Equal(Rgb.Ansi16(1, bright: true), Parse("\x1b[91m\x1b[22mX\n")[0].Runs[0].Fore);
    }

    [Fact]
    public void SurrogateNumericEntityIsReplayedNotThrown()
    {
        Assert.Equal("a&#55296;b", Parse("a&#55296;b\n", mxp: true)[0].PlainText);
        Assert.Equal("a\U0001F600b", Parse("a&#128512;b\n", mxp: true)[0].PlainText);
    }

    [Fact]
    public void PlainTextIsCachedAndCorrect()
    {
        var line = new Line(new[]
        {
            new StyledRun("ab", Rgb.DefaultFore, Rgb.DefaultBack, RunFlags.None),
            new StyledRun("cd", Rgb.DefaultFore, Rgb.DefaultBack, RunFlags.Bold),
        }, false, DateTimeOffset.UnixEpoch);
        Assert.Equal("abcd", line.PlainText);
        Assert.True(ReferenceEquals(line.PlainText, line.PlainText));
        Assert.Equal("", new Line(Array.Empty<StyledRun>(), false, DateTimeOffset.UnixEpoch).PlainText);
    }
}

/// <summary>Session loop resilience, reconnect give-up, and stale MCCP output.</summary>
public class ReviewSessionTests
{
    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    }

    private static async Task<(MudSession s, TcpClient server, TcpListener listener)> Connect(Action<MudSession>? setup = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var s = new MudSession(new WorldProfile { Host = "127.0.0.1", Port = port });
        setup?.Invoke(s);
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await s.ConnectAsync();
        TcpClient server = await accept;
        return (s, server, listener);
    }

    private static async Task<bool> LoopAlive(MudSession s)
    {
        bool ran = false;
        s.Post(() => ran = true);
        await WaitFor(() => ran, 3000);
        return ran;
    }

    [Fact]
    public async Task SendAfterDropDoesNotKillTheLoop()
    {
        var (s, server, listener) = await Connect(x => x.ReconnectEnabled = false);
        try
        {
            server.Close();
            await WaitFor(() => s.State == ConnectionState.Disconnected);
            s.Submit("look");                                  // used to throw "Not connected." on the loop
            Assert.True(await LoopAlive(s));
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task ThrowingLineSubscriberIsReportedAndLoopContinues()
    {
        var shown = new List<string>();
        var (s, server, listener) = await Connect(x =>
        {
            x.ReconnectEnabled = false;
            x.LineReady += l =>
            {
                lock (shown) shown.Add(l.PlainText);
                if (l.PlainText == "boom") throw new InvalidOperationException("subscriber bug");
            };
        });
        try
        {
            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("boom\n"));
            await WaitFor(() => { lock (shown) return shown.Any(t => t.Contains("subscriber bug")); });
            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("after\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("after"); });
            lock (shown)
            {
                Assert.Contains(shown, t => t.Contains("subscriber bug"));
                Assert.Contains("after", shown);
            }
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task ReconnectStopsAfterMaxAttempts()
    {
        var shown = new List<string>();
        var (s, server, listener) = await Connect(x =>
        {
            x.ReconnectPolicy.MaxAttempts = 2;
            x.ReconnectPolicy.BaseDelay = TimeSpan.FromMilliseconds(10);
            x.ReconnectPolicy.MaxDelay = TimeSpan.FromMilliseconds(10);
            x.LineReady += l => { lock (shown) shown.Add(l.PlainText); };
        });
        try
        {
            listener.Stop();                                   // every retry is refused
            server.Close();
            await WaitFor(() => { lock (shown) return shown.Any(t => t.Contains("gave up")); });
            await Task.Delay(800);                             // room for a wrongly restarted series
            lock (shown)
            {
                Assert.Equal(2, shown.Count(t => t.StartsWith("[reconnect] attempt")));
                Assert.Equal(1, shown.Count(t => t.Contains("gave up")));
            }
        }
        finally { await s.DisposeAsync(); }
    }

    [Fact]
    public async Task StaleInflatedDataFromAnOldPumpIsDropped()
    {
        var shown = new List<string>();
        var (s, server, listener) = await Connect(x =>
        {
            x.ReconnectEnabled = false;
            x.LineReady += l => { lock (shown) shown.Add(l.PlainText); };
        });
        try
        {
            Assert.True(await LoopAlive(s));                   // Connected has been handled
            var mailbox = (Channel<SessionMessage>)typeof(MudSession)
                .GetField("_mailbox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
            int gen = (int)typeof(MudSession)
                .GetField("_mccpGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
            mailbox.Writer.TryWrite(new SessionMessage.DataInflated(Encoding.ASCII.GetBytes("stale\n"), gen - 1));
            mailbox.Writer.TryWrite(new SessionMessage.DataInflated(Encoding.ASCII.GetBytes("fresh\n"), gen));
            await WaitFor(() => { lock (shown) return shown.Contains("fresh"); });
            lock (shown)
            {
                Assert.Contains("fresh", shown);
                Assert.DoesNotContain("stale", shown);
            }
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }
}

public class ReviewLoggerTests
{
    [Fact]
    public void FlushWritesBufferedLinesBeforeClose()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrye_review_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = SessionLogger.CreateFile(dir, "w", LogFormat.Text, timestamps: false);
            log.Log(Line.FromText("buffered line"));
            log.Flush();
            string path = log.Path!;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs))
                Assert.Contains("buffered line", r.ReadToEnd());
            log.Close();
            log.Flush();                                       // after Close: a no-op, not a throw
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

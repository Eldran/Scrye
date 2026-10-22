using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Scrye.Core.Automation;
using Scrye.Core.Logging;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Plugins;
using Scrye.Core.Session;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The engine fixes from the 1.10.7 release review (docs/Plan-Release-1.10.7.md, R4/R5/R7):
/// a failing handler inside the telnet parse must not swallow game output, a transcript that
/// cannot be written must not stop the line from being shown, and a user pattern that
/// backtracks forever must not freeze the session.
/// </summary>
public class ReleaseReviewTests
{
    private const byte IAC = 255, SB = 250, SE = 240, GA = 249, GMCP = 201;

    private static byte[] Chunk(params object[] parts)
    {
        var b = new List<byte>();
        foreach (object p in parts)
            b.AddRange(p is string s ? Encoding.ASCII.GetBytes(s) : (byte[])p);
        return b.ToArray();
    }

    private static byte[] Sub(string payload)
    {
        var b = new List<byte> { IAC, SB, GMCP };
        b.AddRange(Encoding.UTF8.GetBytes(payload));
        b.Add(IAC); b.Add(SE);
        return b.ToArray();
    }

    // ---- R4: a throwing handler inside the telnet parse ---------------------------------

    [Fact]
    public void A_throwing_gmcp_handler_does_not_swallow_the_text_around_it()
    {
        var t = new TelnetLayer();
        var failures = new List<Exception>();
        t.HandlerFailed += failures.Add;
        t.GmcpReceived += (_, _) => throw new InvalidOperationException("handler bug");

        byte[] data = t.Process(Chunk("before\n", Sub("Comm.Channel.Text {}"), "after\n"));

        Assert.Equal("before\nafter\n", Encoding.ASCII.GetString(data));
        Assert.Single(failures);
        Assert.Equal("handler bug", failures[0].Message);
    }

    [Fact]
    public void After_a_throwing_handler_the_next_chunk_is_plain_text_again()
    {
        // Before the fix the state machine was left inside the subnegotiation it had just
        // finished, so the NEXT chunk's text was read as GMCP until an IAC SE turned up.
        var t = new TelnetLayer();
        t.HandlerFailed += _ => { };
        t.GmcpReceived += (_, _) => throw new InvalidOperationException("handler bug");

        t.Process(Sub("Comm.Channel.Text {}"));
        byte[] next = t.Process(Encoding.ASCII.GetBytes("You see a room.\n"));

        Assert.Equal("You see a room.\n", Encoding.ASCII.GetString(next));
    }

    [Fact]
    public void A_throwing_prompt_handler_does_not_cut_the_chunk_short()
    {
        var t = new TelnetLayer();
        int failed = 0;
        t.HandlerFailed += _ => failed++;
        t.GoAhead += () => throw new InvalidOperationException("prompt bug");

        byte[] data = t.Process(Chunk("> ", new byte[] { IAC, GA }, "more\n"));

        Assert.Equal("> more\n", Encoding.ASCII.GetString(data));
        Assert.Equal(1, failed);
    }

    [Fact]
    public void A_gmcp_chat_message_with_non_string_fields_is_ignored_not_thrown()
    {
        // `"channel": 3` made GetString() throw InvalidOperationException, which the chat
        // parser's JsonException catch did not cover.
        var s = new MudSession(new WorldProfile { Host = "localhost", Port = 1, EnableGmcp = true });
        var telnet = (TelnetLayer)typeof(MudSession).GetField("_telnet", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
        var failures = new List<Exception>();
        telnet.HandlerFailed += failures.Add;
        var chat = new List<string>();
        s.ChannelMessage += (ch, msg) => chat.Add(ch + "|" + msg);

        byte[] data = telnet.Process(Chunk(
            "a\n",
            Sub("Comm.Channel.Text { \"channel\": 3, \"text\": \"hi\", \"talker\": \"Bob\" }"),
            Sub("Comm.Channel.Text { \"channel\": \"vik\", \"text\": { \"x\": 1 }, \"talker\": \"Bob\" }"),
            Sub("Comm.Channel.Text { \"channel\": \"vik\", \"text\": \"yo\", \"talker\": [ \"Bob\" ] }"),
            "b\n"));

        Assert.Equal("a\nb\n", Encoding.ASCII.GetString(data));
        Assert.Empty(failures);                               // nothing threw at all
        Assert.Equal(new[] { "vik|yo" }, chat);               // the one usable line still filed
    }

    // ---- R5: a transcript that cannot be written ----------------------------------------

    /// <summary>A writer that accepts the header, then fails like a full disk.</summary>
    private sealed class FailingWriter : TextWriter
    {
        public bool Broken;
        public int Writes;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { if (Broken) throw new IOException("There is not enough space on the disk."); Writes++; }
        public override void Write(string? value) { if (Broken) throw new IOException("There is not enough space on the disk."); Writes++; }
        public override void Flush() { if (Broken) throw new IOException("There is not enough space on the disk."); }
    }

    [Fact]
    public void A_logger_that_cannot_write_stops_itself_and_says_so_once()
    {
        var w = new FailingWriter();
        var log = new SessionLogger("W", w, LogFormat.Text, timestamps: false);
        var said = new List<Exception>();
        log.WriteFailed += said.Add;
        w.Broken = true;

        log.Log(Line.FromText("one"));       // must not throw
        log.Log(Line.FromText("two"));
        log.Flush();
        log.Close();

        Assert.Single(said);
        Assert.IsType<IOException>(log.Failure);
        Assert.False(log.IsOpen);
    }

    [Fact]
    public void A_failing_transcript_does_not_stop_the_line_being_shown()
    {
        var s = new MudSession(new WorldProfile { Host = "localhost", Port = 1 });
        var w = new FailingWriter();
        var log = new SessionLogger("W", w, LogFormat.Text, timestamps: false);
        w.Broken = true;
        typeof(MudSession).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, log);
        var shown = new List<string>();
        s.LineReady += l => shown.Add(l.PlainText);

        typeof(MudSession).GetMethod("RaiseLine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(s, new object[] { Line.FromText("You see a room.") });

        Assert.Contains("You see a room.", shown);
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    }

    [Fact]
    public async Task A_transcript_that_fails_live_is_reported_and_the_game_keeps_flowing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var s = new MudSession(new WorldProfile { Host = "127.0.0.1", Port = port }) { ReconnectEnabled = false };
        var shown = new List<string>();
        s.LineReady += l => { lock (shown) shown.Add(l.PlainText); };
        int logFailed = 0;
        s.LogFailed += () => Interlocked.Increment(ref logFailed);
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await s.ConnectAsync();
        using TcpClient server = await accept;
        try
        {
            var w = new FailingWriter();
            var log = new SessionLogger("W", w, LogFormat.Text, timestamps: false);
            w.Broken = true;
            var mailbox = (Channel<SessionMessage>)typeof(MudSession)
                .GetField("_mailbox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
            mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(log));

            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("first\nsecond\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("second"); });
            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("third\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("third"); });

            lock (shown)
            {
                Assert.Contains("first", shown);
                Assert.Contains("second", shown);
                Assert.Contains("third", shown);
                Assert.Single(shown, l => l.StartsWith("[log] transcript stopped"));
            }
            Assert.Equal(1, Volatile.Read(ref logFailed));   // the app is told logging is off, once
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task An_old_transcript_failing_as_it_is_replaced_does_not_report_the_new_one_off()
    {
        // `.log html` while a text log runs: the old one is closed as the new one takes over.
        // If that last flush fails, the user is told - but logging is NOT off.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var s = new MudSession(new WorldProfile { Host = "127.0.0.1", Port = port }) { ReconnectEnabled = false };
        var shown = new List<string>();
        s.LineReady += l => { lock (shown) shown.Add(l.PlainText); };
        int logFailed = 0;
        s.LogFailed += () => Interlocked.Increment(ref logFailed);
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await s.ConnectAsync();
        using TcpClient server = await accept;
        try
        {
            var mailbox = (Channel<SessionMessage>)typeof(MudSession)
                .GetField("_mailbox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
            var oldWriter = new FailingWriter();
            mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(
                new SessionLogger("W", oldWriter, LogFormat.Text, timestamps: false)));
            var fresh = new StringWriter();
            await WaitFor(() => oldWriter.Writes > 0);            // the old one is live
            oldWriter.Broken = true;                              // ...and will fail on close
            mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(
                new SessionLogger("W", fresh, LogFormat.Text, timestamps: false)));

            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("kept\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("kept"); });
            mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(null));   // close + flush the fresh one
            await WaitFor(() => fresh.ToString().Contains("kept"));

            lock (shown) Assert.Single(shown, l => l.StartsWith("[log] transcript stopped"));   // said
            Assert.Equal(0, Volatile.Read(ref logFailed));                                     // not "off"
            Assert.Contains("kept", fresh.ToString());                                         // the new log has it
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    // ---- R7: a user pattern that backtracks forever --------------------------------------

    private sealed class Recorder : IWorldActions
    {
        public List<string> Sends { get; } = new();
        public List<string> Echoes { get; } = new();
        public void Send(string text) => Sends.Add(text);
        public void Echo(string text) => Echoes.Add(text);
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    // (a+)+$ against a run of 'a' with a non-matching tail is the textbook catastrophic-
    // backtracking case: each extra 'a' doubles the work. 24 of them is several seconds
    // unprotected - far past the 250 ms limit on any machine, yet short enough that a
    // regression fails the assertions instead of hanging the run (40 would never return).
    private const string Evil = "(a+)+$";
    private static readonly string Victim = new string('a', 24) + "!";

    [Fact]
    public void A_backtracking_trigger_is_skipped_and_the_rest_still_fire()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        engine.AddTrigger(new TriggerDef { Name = "evil", Pattern = Evil, IsRegex = true, Send = "never", KeepEvaluating = true, Sequence = 1 });
        engine.AddTrigger(new TriggerDef { Name = "good", Pattern = "aaa", IsRegex = true, Send = "fine", Sequence = 2 });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        engine.ProcessLine(Victim, rec);
        engine.ProcessLine(Victim, rec);
        sw.Stop();

        Assert.Equal(new[] { "fine", "fine" }, rec.Sends);
        Assert.Single(rec.Echoes, e => e.Contains("'evil' took over 250 ms"));   // said once, not per line
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_backtracking_alias_is_skipped_and_the_command_still_goes_through()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        engine.AddAlias(new AliasDef { Name = "evil", Pattern = Evil, IsRegex = true, Send = "never" });

        bool consumed = engine.ProcessInput(Victim, rec);

        Assert.False(consumed);                               // not eaten: it goes to the MUD as typed
        Assert.Single(rec.Echoes, e => e.Contains("'evil' took over 250 ms"));
    }

    // ---- R7: a plugin that keeps failing -------------------------------------------------

    [Fact]
    public void A_failing_plugin_prints_a_few_errors_a_minute_and_then_says_how_many_it_hid()
    {
        double now = 0;
        var reports = new List<string>();
        var diag = new PluginDiagnostics(reports.Add, () => now);

        var shown = new List<bool>();
        for (int i = 0; i < 10; i++) { shown.Add(diag.ShouldPrintError("p")); now += 1000; }

        Assert.Equal(PluginDiagnostics.ErrorLinesPerWindow, shown.Count(b => b));
        Assert.True(shown.Take(PluginDiagnostics.ErrorLinesPerWindow).All(b => b));   // the FIRST ones
        Assert.Single(reports, r => r.Contains("keeps failing"));                      // said once

        now = PluginDiagnostics.FailureWindow.TotalMilliseconds + 1;                   // next minute
        Assert.True(diag.ShouldPrintError("p"));
        Assert.Contains(reports, r => r.Contains("7 more error(s) were not shown"));
        Assert.True(diag.ShouldPrintError("other"));                                   // per plugin
    }

    [Fact]
    public void A_plugin_failing_every_other_line_is_quarantined_even_though_it_succeeds_between()
    {
        double now = 0;
        var reports = new List<string>();
        var diag = new PluginDiagnostics(reports.Add, () => now);

        bool tripped = false;
        for (int i = 0; i < PluginDiagnostics.QuarantineAfterFailuresInWindow; i++)
        {
            Assert.False(diag.IsQuarantined("p"));
            tripped |= diag.RecordFailure("p", "onLine", "boom");
            diag.RecordSuccess("p");                 // the streak never builds
            now += 500;
        }

        Assert.True(tripped);
        Assert.True(diag.IsQuarantined("p"));
        Assert.Equal(new[] { "p" }, diag.TakeQuarantined());
        Assert.Single(reports, r => r.Contains("in the last 60 seconds") && r.Contains("unloading"));
    }

    [Fact]
    public void Occasional_failures_spread_over_time_never_quarantine()
    {
        // 20 a minute, for five minutes: an annoying plugin, not a broken one - only failures
        // INSIDE one window count, so the old ones have to fall out of it.
        double now = 0;
        var diag = new PluginDiagnostics(_ => { }, () => now);

        for (int i = 0; i < 100; i++)
        {
            diag.RecordFailure("p", "onLine", "boom");
            diag.RecordSuccess("p");
            now += 3000;
        }

        Assert.False(diag.IsQuarantined("p"));
        Assert.Empty(diag.TakeQuarantined());
    }
}

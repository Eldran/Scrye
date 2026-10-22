using System.Net;
using System.Net.Sockets;
using System.Text;
using Scrye.Core.Model;
using Scrye.Core.Session;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// A prompt sent with neither a newline nor GA/EOR reached triggers, onPrompt and auto-login
/// only when more text arrived. The idle flush (1.10.7) shows it after a quiet spell - only
/// when it looks like a prompt, and without changing the lines that come after it.
/// </summary>
public class IdleFlushTests
{
    private static (AnsiParser p, List<Line> lines) Parser()
    {
        var p = new AnsiParser();
        var lines = new List<Line>();
        p.LineCompleted += lines.Add;
        return (p, lines);
    }

    [Fact]
    public void A_waiting_prompt_is_emitted_and_the_newline_after_it_adds_no_blank_line()
    {
        (AnsiParser p, List<Line> lines) = Parser();
        p.Feed("> ");
        Assert.True(p.TryFlushIdlePrompt());
        p.Feed("\r\nYou see a room.\r\n");

        Assert.Equal(new[] { "> ", "You see a room." }, lines.Select(l => l.PlainText));
        Assert.True(lines[0].IsPrompt);
        Assert.False(lines[1].IsPrompt);
    }

    [Fact]
    public void Colour_codes_before_the_newline_do_not_bring_the_blank_line_back()
    {
        (AnsiParser p, List<Line> lines) = Parser();
        p.Feed("\x1b[32mWhat is your name:\x1b[0m ");
        Assert.True(p.TryFlushIdlePrompt());
        p.Feed("\x1b[0m\nWelcome.\n");

        Assert.Equal(new[] { "What is your name: ", "Welcome." }, lines.Select(l => l.PlainText));
    }

    [Fact]
    public void Only_one_newline_is_swallowed()
    {
        (AnsiParser p, List<Line> lines) = Parser();
        p.Feed("> ");
        p.TryFlushIdlePrompt();
        p.Feed("\n\nafter a blank\n");

        Assert.Equal(new[] { "> ", "", "after a blank" }, lines.Select(l => l.PlainText));
    }

    [Fact]
    public void Text_that_does_not_look_like_a_prompt_is_never_cut()
    {
        (AnsiParser p, List<Line> lines) = Parser();
        p.Feed("You think about it");
        Assert.False(p.TryFlushIdlePrompt());
        p.Feed(" for a while.\n");

        Assert.Equal(new[] { "You think about it for a while." }, lines.Select(l => l.PlainText));
    }

    [Fact]
    public void Nothing_is_flushed_in_the_middle_of_an_escape_sequence()
    {
        (AnsiParser p, List<Line> lines) = Parser();
        p.Feed("> \x1b[3");
        Assert.False(p.TryFlushIdlePrompt());
        p.Feed("1mred\n");
        Assert.Single(lines);
    }

    [Fact]
    public void A_line_that_carries_on_after_the_flush_starts_a_new_line()
    {
        (AnsiParser p, List<Line> lines) = Parser();
        p.Feed("HP: 100 >");
        p.TryFlushIdlePrompt();
        p.Feed("You hit the rat.\n");
        Assert.Equal(new[] { "HP: 100 >", "You hit the rat." }, lines.Select(l => l.PlainText));
    }

    [Theory]
    [InlineData("> ", true)]
    [InlineData("Password:", true)]
    [InlineData("Continue? ", true)]
    [InlineData("[HP 100/100]", true)]
    [InlineData("(more)", true)]
    [InlineData("You think", false)]
    [InlineData("   ", false)]
    public void What_looks_like_a_prompt(string text, bool expected) =>
        Assert.Equal(expected, AnsiParser.LooksLikePrompt(text));

    // ---- in a running session --------------------------------------------------------------

    private static async Task WaitFor(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    }

    private static async Task<(MudSession s, TcpClient server, TcpListener l, List<Line> lines)> Live()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var s = new MudSession(new WorldProfile { Host = "127.0.0.1", Port = port }) { ReconnectEnabled = false };
        var lines = new List<Line>();
        s.LineReady += l => { lock (lines) lines.Add(l); };
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await s.ConnectAsync();
        return (s, await accept, listener, lines);
    }

    [Fact]
    public async Task A_login_prompt_without_newline_or_GA_shows_up_on_its_own()
    {
        (MudSession s, TcpClient server, TcpListener l, List<Line> lines) = await Live();
        try
        {
            await server.GetStream().WriteAsync(Encoding.ASCII.GetBytes("Welcome!\r\nWhat is your name: "));
            await WaitFor(() => { lock (lines) return lines.Any(x => x.PlainText.StartsWith("What is your name")); });

            lock (lines)
            {
                Line prompt = Assert.Single(lines, x => x.PlainText.StartsWith("What is your name"));
                Assert.True(prompt.IsPrompt);
            }
        }
        finally { await s.DisposeAsync(); l.Stop(); }
    }

    [Fact]
    public async Task A_line_still_arriving_is_not_flushed_before_the_quiet_window()
    {
        // pieces 200 ms apart - quicker than IdleFlushMs, slower than the 250 ms tick, so a
        // flush that ignored the quiet window would cut it (and mark it a prompt)
        (MudSession s, TcpClient server, TcpListener l, List<Line> lines) = await Live();
        try
        {
            NetworkStream ns = server.GetStream();
            await ns.WriteAsync(Encoding.ASCII.GetBytes("Score: "));
            await Task.Delay(200);
            await ns.WriteAsync(Encoding.ASCII.GetBytes("[42]"));
            await Task.Delay(200);
            await ns.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
            await WaitFor(() => { lock (lines) return lines.Any(x => x.PlainText.Contains("Score")); });

            lock (lines)
            {
                Line only = Assert.Single(lines, x => x.PlainText.Contains("Score"));
                Assert.Equal("Score: [42]", only.PlainText);
                Assert.False(only.IsPrompt);
            }
        }
        finally { await s.DisposeAsync(); l.Stop(); }
    }

    [Fact]
    public async Task A_server_that_sends_GA_is_left_to_mark_its_own_prompts()
    {
        (MudSession s, TcpClient server, TcpListener l, List<Line> lines) = await Live();
        try
        {
            NetworkStream ns = server.GetStream();
            await ns.WriteAsync(new byte[] { (byte)'>', (byte)' ', 255, 249 });          // "> " IAC GA
            await WaitFor(() => { lock (lines) return lines.Any(x => x.PlainText == "> "); });
            await ns.WriteAsync(Encoding.ASCII.GetBytes("\r\nHalf a line: "));            // no GA this time
            await Task.Delay(1200);                                                       // > 2 idle windows

            lock (lines) Assert.DoesNotContain(lines, x => x.PlainText.StartsWith("Half a line"));
        }
        finally { await s.DisposeAsync(); l.Stop(); }
    }
}

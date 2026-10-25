using System.Net;
using System.Net.Sockets;
using System.Text;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Session;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The Reconnect button (1.10.7): connecting again from any state - after auto-reconnect gave
/// up, during a backoff, or over a live connection. Real loopback sockets, because what goes
/// wrong is which socket a late-finishing read loop tears down.
/// </summary>
public class ManualReconnectTests
{
    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    }

    private static (TcpListener listener, int port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static async Task<string> ReadSome(TcpClient c, int ms = 3000)
    {
        using var cts = new CancellationTokenSource(ms);
        var buf = new byte[256];
        int n = await c.GetStream().ReadAsync(buf, cts.Token);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    [Fact]
    public async Task Connecting_again_over_a_live_connection_keeps_the_new_one()
    {
        // Before the generation tag, the OLD read loop - cancelled by the second connect,
        // finishing a moment later - nulled the shared stream (by then the new one's) and
        // reported Disconnected after the new Connected.
        (TcpListener listener, int port) = Listen();
        var conn = new Connection();
        try
        {
            Task<TcpClient> a1 = listener.AcceptTcpClientAsync();
            await conn.ConnectAsync("127.0.0.1", port, false, false);
            using TcpClient first = await a1;

            var states = new List<ConnectionState>();
            conn.StateChanged += s => { lock (states) states.Add(s); };
            var got = new StringBuilder();
            conn.BytesReceived += b => { lock (got) got.Append(Encoding.ASCII.GetString(b)); };

            Task<TcpClient> a2 = listener.AcceptTcpClientAsync();
            await conn.ConnectAsync("127.0.0.1", port, false, false);
            using TcpClient second = await a2;
            await Task.Delay(300);                       // the old loop has long finished

            Assert.Equal(ConnectionState.Connected, conn.State);
            lock (states) Assert.DoesNotContain(ConnectionState.Disconnected, states);

            await conn.SendAsync(Encoding.ASCII.GetBytes("hello\n"));     // goes out the NEW socket
            Assert.Equal("hello\n", await ReadSome(second));
            await second.GetStream().WriteAsync(Encoding.ASCII.GetBytes("back\n"));
            await WaitFor(() => { lock (got) return got.ToString().Contains("back"); });
            lock (got) Assert.Contains("back", got.ToString());
        }
        finally { await conn.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task A_cancelled_attempt_does_not_close_what_an_earlier_one_connected()
    {
        // attemptCt aborts the dial only: cancelling it after the connect must leave the
        // connection up (the retry loop cancels its token on every success).
        (TcpListener listener, int port) = Listen();
        var conn = new Connection();
        try
        {
            using var attempt = new CancellationTokenSource();
            Task<TcpClient> a = listener.AcceptTcpClientAsync();
            await conn.ConnectAsync("127.0.0.1", port, false, false, CancellationToken.None, attempt.Token);
            using TcpClient server = await a;
            attempt.Cancel();
            await Task.Delay(200);

            Assert.Equal(ConnectionState.Connected, conn.State);
            await conn.SendAsync(Encoding.ASCII.GetBytes("x"));
            Assert.Equal("x", await ReadSome(server));
        }
        finally { await conn.DisposeAsync(); listener.Stop(); }
    }

    private static async Task<(MudSession s, TcpClient server, List<string> shown, List<ConnectionState> states)>
        Session(TcpListener listener, int port, Action<MudSession>? setup = null)
    {
        var s = new MudSession(new WorldProfile { Host = "127.0.0.1", Port = port });
        setup?.Invoke(s);
        var shown = new List<string>();
        s.LineReady += l => { lock (shown) shown.Add(l.PlainText); };
        var states = new List<ConnectionState>();
        s.StateChanged += st => { lock (states) states.Add(st); };
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await s.ConnectAsync();
        return (s, await accept, shown, states);
    }

    [Fact]
    public async Task Reconnect_after_the_connection_dropped_and_nothing_retried()
    {
        (TcpListener listener, int port) = Listen();
        (MudSession s, TcpClient first, List<string> shown, List<ConnectionState> states) =
            await Session(listener, port, x => x.ReconnectEnabled = false);
        try
        {
            first.Close();
            await WaitFor(() => { lock (states) return states.Contains(ConnectionState.Disconnected); });

            Task<TcpClient> a2 = listener.AcceptTcpClientAsync();
            await s.ReconnectAsync();
            using TcpClient second = await a2.WaitAsync(TimeSpan.FromSeconds(5));
            await second.GetStream().WriteAsync(Encoding.ASCII.GetBytes("welcome back\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("welcome back"); });

            lock (shown) Assert.Contains("welcome back", shown);
            lock (states) Assert.Equal(ConnectionState.Connected, states[^1]);
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task Reconnect_during_a_backoff_connects_now_and_the_retry_stands_down()
    {
        (TcpListener listener, int port) = Listen();
        (MudSession s, TcpClient first, List<string> shown, List<ConnectionState> states) =
            await Session(listener, port, x =>
            {
                x.ReconnectPolicy.BaseDelay = TimeSpan.FromMilliseconds(1500);
                x.ReconnectPolicy.MaxDelay = TimeSpan.FromMilliseconds(1500);
                x.ReconnectPolicy.StableAfter = TimeSpan.Zero;
            });
        try
        {
            first.Close();
            await WaitFor(() => { lock (shown) return shown.Exists(l => l.StartsWith("[reconnect] attempt 1")); });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task<TcpClient> a2 = listener.AcceptTcpClientAsync();
            await s.ReconnectAsync();
            using TcpClient second = await a2.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sw.ElapsedMilliseconds < 1200, $"took {sw.ElapsedMilliseconds} ms - waited out the backoff");

            await Task.Delay(2500);                      // well past when the retry was due
            Assert.False(listener.Pending(), "the old retry opened a second connection");
            Assert.True(second.Connected);
            await second.GetStream().WriteAsync(Encoding.ASCII.GetBytes("still here\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("still here"); });
            lock (shown) Assert.Contains("still here", shown);
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task Reconnect_over_a_live_connection_replaces_it_cleanly()
    {
        (TcpListener listener, int port) = Listen();
        (MudSession s, TcpClient first, List<string> shown, List<ConnectionState> states) =
            await Session(listener, port);
        try
        {
            lock (states) states.Clear();
            Task<TcpClient> a2 = listener.AcceptTcpClientAsync();
            await s.ReconnectAsync();
            using TcpClient second = await a2.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);

            Assert.Equal(0, await first.GetStream().ReadAsync(new byte[16]));   // the old one was closed
            lock (states)
            {
                Assert.Equal(ConnectionState.Connected, states[^1]);
                Assert.DoesNotContain(ConnectionState.Disconnected, states);   // no stale drop reported
            }
            Assert.False(listener.Pending());            // and nothing reconnected on its own
            await second.GetStream().WriteAsync(Encoding.ASCII.GetBytes("new link\n"));
            await WaitFor(() => { lock (shown) return shown.Contains("new link"); });
            lock (shown) Assert.Contains("new link", shown);
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }

    [Fact]
    public async Task Reconnect_with_edited_settings_goes_to_the_new_address()
    {
        // a host typo fixed in the profile after the tab opened: the double-click must dial
        // the corrected one, not the one the tab was made with
        (TcpListener oldL, int oldPort) = Listen();
        (TcpListener newL, int newPort) = Listen();
        (MudSession s, TcpClient first, List<string> shown, List<ConnectionState> states) =
            await Session(oldL, oldPort, x => x.ReconnectEnabled = false);
        try
        {
            first.Close();
            await WaitFor(() => { lock (states) return states.Contains(ConnectionState.Disconnected); });

            Task<TcpClient> a = newL.AcceptTcpClientAsync();
            await s.ReconnectAsync(new WorldProfile { Host = "127.0.0.1", Port = newPort, Username = "bjorn" });
            using TcpClient onNew = await a.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(oldL.Pending());
            Assert.Equal(newPort, s.Profile.Port);
            Assert.Equal("bjorn", s.Profile.Username);
        }
        finally { await s.DisposeAsync(); oldL.Stop(); newL.Stop(); }
    }

    [Fact]
    public async Task A_reconnect_that_fails_says_why()
    {
        (TcpListener listener, int port) = Listen();
        (MudSession s, TcpClient first, List<string> shown, List<ConnectionState> states) =
            await Session(listener, port, x => x.ReconnectEnabled = false);
        try
        {
            first.Close();
            listener.Stop();                                        // nothing listens any more
            await WaitFor(() => { lock (states) return states.Contains(ConnectionState.Disconnected); });

            await s.ReconnectAsync();
            // a refused loopback connect takes ~2 s on Windows (SYN retried after the RST)
            await WaitFor(() => { lock (shown) return shown.Exists(l => l.StartsWith("[reconnect] could not connect:")); }, 15000);

            lock (shown) Assert.Contains(shown, l => l.StartsWith("[reconnect] could not connect:"));
        }
        finally { await s.DisposeAsync(); listener.Stop(); }
    }
}

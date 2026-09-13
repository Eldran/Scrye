using System.Net;
using System.Net.Sockets;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Against a real loopback TcpListener, because the bug these guard is about OS socket
/// state - a mocked stream cannot sit in CLOSE_WAIT. When the peer closes first, the read
/// loop must dispose the local half too (7 Sep 2026: one leaked descriptor per server-side
/// drop, held until the next reconnect or process exit), and a Connection must still be
/// able to reconnect afterwards.
/// </summary>
public class ConnectionTests
{
    private static async Task<(Connection conn, TcpClient serverSide, TcpListener listener)> ConnectedPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var conn = new Connection();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await conn.ConnectAsync("127.0.0.1", port, useTls: false, acceptInvalidCerts: false);
        TcpClient serverSide = await accept;
        return (conn, serverSide, listener);
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    }

    [Fact]
    public async Task PeerCloseDisposesOurSocket()
    {
        (Connection conn, TcpClient serverSide, TcpListener listener) = await ConnectedPair();
        try
        {
            var states = new List<ConnectionState>();
            conn.StateChanged += states.Add;
            Assert.Equal(ConnectionState.Connected, conn.State);

            serverSide.Close();                          // the peer's FIN
            await WaitFor(() => conn.State == ConnectionState.Disconnected);

            Assert.Equal(ConnectionState.Disconnected, conn.State);
            Assert.Contains(ConnectionState.Disconnected, states);
            // the local half is really gone: a send has nothing to write to, rather than
            // silently succeeding into a CLOSE_WAIT socket
            await Assert.ThrowsAsync<InvalidOperationException>(() => conn.SendAsync(new byte[] { 65 }));
        }
        finally
        {
            await conn.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task ReconnectsAfterPeerClose()
    {
        (Connection conn, TcpClient serverSide, TcpListener listener) = await ConnectedPair();
        try
        {
            serverSide.Close();
            await WaitFor(() => conn.State == ConnectionState.Disconnected);

            // the same Connection connects again and carries data on the new socket
            var got = new List<byte>();
            conn.BytesReceived += b => { lock (got) got.AddRange(b); };
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            await conn.ConnectAsync("127.0.0.1", port, useTls: false, acceptInvalidCerts: false);
            using TcpClient second = await accept;
            Assert.Equal(ConnectionState.Connected, conn.State);

            await conn.SendAsync(new byte[] { 104, 105 });
            var buf = new byte[2];
            int n = await second.GetStream().ReadAsync(buf, 0, 2);
            Assert.Equal(2, n);
            Assert.Equal("hi", System.Text.Encoding.ASCII.GetString(buf));

            await second.GetStream().WriteAsync(new byte[] { 111, 107 });
            await WaitFor(() => { lock (got) return got.Count >= 2; });
            lock (got) Assert.Equal(new byte[] { 111, 107 }, got.ToArray());
        }
        finally
        {
            await conn.DisposeAsync();
            listener.Stop();
        }
    }
}

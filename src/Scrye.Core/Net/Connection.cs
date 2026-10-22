using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Scrye.Core.Model;

namespace Scrye.Core.Net;

/// <summary>
/// Async TCP transport with optional TLS. Connects, pumps a background read loop
/// that raises <see cref="BytesReceived"/>, and exposes <see cref="SendAsync"/>.
/// When TLS is requested the network stream is wrapped in an <see cref="SslStream"/>
/// after connect — the same stream-wrapping seam MCCP decompression will reuse.
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    // Which connect is current. A read loop belongs to the connect that started it: when that
    // connect has been superseded (ConnectAsync again while it was still running - a manual
    // reconnect), the old loop must clean up ITS socket and say nothing. Before 1.10.7 its
    // finally nulled the shared fields - by then the NEW socket's - and reported Disconnected
    // after the new Connected, so a reconnect over a live connection killed itself.
    private int _generation;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event Action<byte[]>? BytesReceived;
    public event Action<ConnectionState>? StateChanged;

    /// <param name="ct">The connection's lifetime: cancelling it aborts the attempt AND later
    /// closes the established connection.</param>
    /// <param name="attemptCt">Aborts only the attempt (DNS, TCP, TLS). Once connected it no
    /// longer matters, so a retry loop can be cancelled without killing what it connected.</param>
    public async Task ConnectAsync(string host, int port, bool useTls, bool acceptInvalidCerts,
                                   CancellationToken ct = default, CancellationToken attemptCt = default)
    {
        // The generation moves BEFORE the old socket is closed: closing it wakes the old read
        // loop, and if that loop finished in between it would still see itself as current.
        int gen = Interlocked.Increment(ref _generation);
        CloseSocket();   // tear down any prior socket so this instance can reconnect cleanly
        SetState(ConnectionState.Connecting);
        using CancellationTokenSource? attempt = attemptCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, attemptCt) : null;
        CancellationToken connectCt = attempt?.Token ?? ct;
        try
        {
            var tcp = new TcpClient { NoDelay = true };
            _tcp = tcp;
            await tcp.ConnectAsync(host, port, connectCt).ConfigureAwait(false);

            Stream stream = tcp.GetStream();
            if (useTls)
            {
                RemoteCertificateValidationCallback? validate =
                    acceptInvalidCerts ? (_, _, _, _) => true : null;
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, validate);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None,   // OS-chosen (TLS 1.2/1.3)
                }, connectCt).ConfigureAwait(false);
                stream = ssl;
            }

            _stream = stream;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            SetState(ConnectionState.Connected);
            _readLoop = Task.Run(() => ReadLoopAsync(gen, tcp, stream, cts.Token));
        }
        catch
        {
            SetState(ConnectionState.Failed);
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(int gen, TcpClient tcp, Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;
                BytesReceived?.Invoke(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }   // CloseSocket disposed this stream under us
        finally
        {
            // Close OUR half of the socket, not just the loop.
            //
            // When the peer closes first, ReadAsync returns 0 and this loop exits - but a TCP
            // close is two independent halves, and until the local handle is disposed the
            // socket sits in CLOSE_WAIT holding an OS file descriptor. Nothing else disposed
            // it: ConnectAsync's CloseSocket() only runs on the NEXT connect, so a session
            // that stays disconnected (a tab you don't reconnect, an idle world) leaked one
            // socket per drop for the lifetime of the process (seen live against 3k.org:3200
            // during an upstream fault, 7 Sep 2026: two CLOSE_WAIT entries beside the live one).
            //
            // The CancellationTokenSource is deliberately left alone: it belongs to
            // ConnectAsync/DisposeAsync, and this loop is still running under its token.
            // The fields are nulled so CloseSocket()/DisposeAsync's `?.` become no-ops and
            // SendAsync says "Not connected." rather than throwing ObjectDisposedException -
            // but only while this loop's connect is still the current one (see _generation).
            stream.Dispose();
            tcp.Dispose();
            if (gen == Volatile.Read(ref _generation))
            {
                _stream = null;
                _tcp = null;
                SetState(ConnectionState.Disconnected);
            }
        }
    }

    private void SetState(ConnectionState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>Cancel and dispose the current socket/read-loop without changing
    /// state — used to reset before a reconnect.</summary>
    private void CloseSocket()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _readLoop = null;   // the old loop observes cancellation and exits on its own
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    public async ValueTask DisposeAsync()
    {
        SetState(ConnectionState.Disconnecting);
        _cts?.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _stream?.Dispose();
        _tcp?.Dispose();
        _cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }
}

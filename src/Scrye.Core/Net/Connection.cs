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

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event Action<byte[]>? BytesReceived;
    public event Action<ConnectionState>? StateChanged;

    public async Task ConnectAsync(string host, int port, bool useTls, bool acceptInvalidCerts, CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

            Stream stream = _tcp.GetStream();
            if (useTls)
            {
                RemoteCertificateValidationCallback? validate =
                    acceptInvalidCerts ? (_, _, _, _) => true : null;
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, validate);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None,   // OS-chosen (TLS 1.2/1.3)
                }, ct).ConfigureAwait(false);
                stream = ssl;
            }

            _stream = stream;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SetState(ConnectionState.Connected);
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
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

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;
                BytesReceived?.Invoke(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            SetState(ConnectionState.Disconnected);
        }
    }

    private void SetState(ConnectionState s)
    {
        State = s;
        StateChanged?.Invoke(s);
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

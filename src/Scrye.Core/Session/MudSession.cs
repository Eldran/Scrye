using System.Text;
using System.Threading.Channels;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Text;

namespace Scrye.Core.Session;

/// <summary>
/// The live world. Owns the connection and the receive pipeline
/// (telnet → decode → ANSI → line) and drives everything through a single
/// serialized mailbox loop. Raises events the UI subscribes to; it never
/// reaches into the UI. One instance per connected world.
/// </summary>
public sealed class MudSession : IAsyncDisposable
{
    private readonly Channel<SessionMessage> _mailbox =
        Channel.CreateUnbounded<SessionMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Connection _connection = new();
    private readonly TelnetLayer _telnet = new();
    private readonly AnsiParser _ansi;
    private readonly Decoder _decoder;
    private readonly Encoding _encoding;

    private Task? _loop;
    private CancellationTokenSource? _cts;

    public WorldProfile Profile { get; }
    public ConnectionState State => _connection.State;

    /// <summary>Raised on the session loop for every completed line.</summary>
    public event Action<Line>? LineReady;
    public event Action<ConnectionState>? StateChanged;

    public MudSession(WorldProfile profile)
    {
        Profile = profile;
        _encoding = profile.ResolveEncoding();
        _decoder = _encoding.GetDecoder();
        _ansi = new AnsiParser();

        _ansi.LineCompleted += line => LineReady?.Invoke(line);
        _connection.BytesReceived += bytes => _mailbox.Writer.TryWrite(new SessionMessage.DataArrived(bytes));
        _connection.StateChanged += s => StateChanged?.Invoke(s);
    }

    /// <summary>Connects and starts the session loop.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        await _connection.ConnectAsync(Profile.Host, Profile.Port, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Queue a line of user input for sending.</summary>
    public void Submit(string text) => _mailbox.Writer.TryWrite(new SessionMessage.UserInput(text));

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (SessionMessage msg in _mailbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (msg)
                {
                    case SessionMessage.DataArrived d:
                        await HandleDataAsync(d.Bytes, ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.UserInput u:
                        await HandleInputAsync(u.Text, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleDataAsync(byte[] bytes, CancellationToken ct)
    {
        byte[] data = _telnet.Process(bytes, out byte[] response);
        if (response.Length > 0)
            await _connection.SendAsync(response, ct).ConfigureAwait(false);
        if (data.Length > 0)
            _ansi.Feed(Decode(data));
    }

    private async Task HandleInputAsync(string text, CancellationToken ct)
    {
        byte[] outBytes = _encoding.GetBytes(text + "\r\n");
        await _connection.SendAsync(outBytes, ct).ConfigureAwait(false);
    }

    private string Decode(byte[] data)
    {
        int max = _encoding.GetMaxCharCount(data.Length);
        char[] chars = new char[max];
        int n = _decoder.GetChars(data, 0, data.Length, chars, 0);   // stateful: handles split multibyte
        return new string(chars, 0, n);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _mailbox.Writer.TryComplete();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}

using System.Text;
using System.Threading.Channels;
using Scrye.Core.Automation;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Text;

namespace Scrye.Core.Session;

/// <summary>
/// The live world. Owns the connection and the receive pipeline
/// (telnet -> decode -> ANSI -> line), the automation engine, and drives
/// everything through a single serialized mailbox loop. Implements
/// <see cref="IWorldActions"/> so the engine can act without referencing the
/// session type. Surfaces protocol events (GMCP/MSSP/echo) the UI can use.
/// One instance per connected world.
/// </summary>
public sealed class MudSession : IAsyncDisposable, IWorldActions
{
    private readonly Channel<SessionMessage> _mailbox =
        Channel.CreateUnbounded<SessionMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Connection _connection = new();
    private readonly TelnetLayer _telnet = new();
    private readonly AnsiParser _ansi;
    private readonly Decoder _decoder;
    private readonly Encoding _encoding;

    private readonly VariableStore _variables = new();
    private readonly AutomationEngine _automation;

    private Task? _loop;
    private Task? _ticker;
    private CancellationTokenSource? _cts;

    public WorldProfile Profile { get; }
    public ConnectionState State => _connection.State;
    public AutomationEngine Automation => _automation;
    public VariableStore Variables => _variables;

    public Action<string, IReadOnlyList<string>>? ScriptDispatcher { get; set; }
    public Action<string>? ScriptExecutor { get; set; }

    // ---- events the UI/host can subscribe to ----
    public event Action<Line>? LineReady;
    public event Action<ConnectionState>? StateChanged;
    /// <summary>Out-of-band GMCP: (package, json-or-empty).</summary>
    public event Action<string, string>? GmcpReceived;
    /// <summary>Parsed MSSP server-status variables.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? MsspReceived;
    /// <summary>Server ECHO changed — true means the client should mask local input (passwords).</summary>
    public event Action<bool>? EchoModeChanged;

    public MudSession(WorldProfile profile)
    {
        Profile = profile;
        _encoding = profile.ResolveEncoding();
        _decoder = _encoding.GetDecoder();
        _ansi = new AnsiParser();
        _automation = new AutomationEngine(_variables);

        _ansi.LineCompleted += OnLineCompleted;

        _connection.BytesReceived += bytes => _mailbox.Writer.TryWrite(new SessionMessage.DataArrived(bytes));
        _connection.StateChanged += s => StateChanged?.Invoke(s);

        _telnet.SendData += bytes => _mailbox.Writer.TryWrite(new SessionMessage.SendBytes(bytes));
        _telnet.GmcpReceived += (pkg, json) => GmcpReceived?.Invoke(pkg, json);
        _telnet.MsspReceived += vars => MsspReceived?.Invoke(vars);
        _telnet.ServerEchoChanged += on => EchoModeChanged?.Invoke(on);
        _telnet.WindowSize = () => (Profile.TerminalColumns, Profile.TerminalRows);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        _ticker = Task.Run(() => RunTickerAsync(_cts.Token));
        await _connection.ConnectAsync(Profile.Host, Profile.Port, Profile.UseTls,
            Profile.AcceptInvalidCertificates, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Queue a line of user input (runs through aliases before sending).</summary>
    public void Submit(string text) => _mailbox.Writer.TryWrite(new SessionMessage.UserInput(text));

    /// <summary>Queue a script chunk to run on the session loop (the `/` console).</summary>
    public void RunScript(string code) => _mailbox.Writer.TryWrite(new SessionMessage.RunScript(code));

    /// <summary>Send an out-of-band GMCP message.</summary>
    public void SendGmcp(string package, string json) => _telnet.SendGmcp(package, json);

    // ---- IWorldActions (called by the automation engine, on the loop) --------

    void IWorldActions.Send(string text) => _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    void IWorldActions.Echo(string text) => LineReady?.Invoke(Line.FromText(text));
    string? IWorldActions.GetVariable(string name) => _variables.Get(name);
    void IWorldActions.SetVariable(string name, string value) => _variables.Set(name, value);
    void IWorldActions.CallScript(string function, IReadOnlyList<string> wildcards) => ScriptDispatcher?.Invoke(function, wildcards);

    // ---- loop ----------------------------------------------------------------

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
                        HandleInput(u.Text);
                        break;
                    case SessionMessage.SendText s:
                        await SendRawAsync(_encoding.GetBytes(s.Text + "\r\n"), ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.SendBytes sb:
                        await SendRawAsync(sb.Bytes, ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.Tick:
                        _automation.Tick(1.0, this);
                        break;
                    case SessionMessage.RunScript r:
                        try { ScriptExecutor?.Invoke(r.Code); }
                        catch (Exception ex) { LineReady?.Invoke(Line.FromText("lua: " + ex.Message)); }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunTickerAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                _mailbox.Writer.TryWrite(new SessionMessage.Tick());
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleDataAsync(byte[] bytes, CancellationToken ct)
    {
        byte[] data = _telnet.Process(bytes);   // telnet replies come back via SendData -> SendBytes
        if (data.Length > 0)
            _ansi.Feed(Decode(data));           // emits lines via OnLineCompleted
        await Task.CompletedTask;
    }

    private void OnLineCompleted(Line line)
    {
        LineReady?.Invoke(line);                         // display first
        _automation.ProcessLine(line.PlainText, this);   // then react
    }

    private void HandleInput(string text)
    {
        if (!_automation.ProcessInput(text, this))
            _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    }

    private async Task SendRawAsync(byte[] bytes, CancellationToken ct)
    {
        await _connection.SendAsync(bytes, ct).ConfigureAwait(false);
    }

    private string Decode(byte[] data)
    {
        int max = _encoding.GetMaxCharCount(data.Length);
        char[] chars = new char[max];
        int n = _decoder.GetChars(data, 0, data.Length, chars, 0);
        return new string(chars, 0, n);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _mailbox.Writer.TryComplete();
        foreach (Task? t in new[] { _loop, _ticker })
            if (t is not null) { try { await t.ConfigureAwait(false); } catch { /* ignore */ } }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}

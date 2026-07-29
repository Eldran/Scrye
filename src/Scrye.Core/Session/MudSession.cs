using System.Text;
using System.Threading.Channels;
using Scrye.Core.Automation;
using Scrye.Core.Mip;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Text;

namespace Scrye.Core.Session;

/// <summary>
/// The live world. Owns the connection and the receive pipeline
/// (telnet -> decode -> MIP -> ANSI -> line), the automation engine, and drives
/// everything through a single serialized mailbox loop. Implements
/// <see cref="IWorldActions"/> so the engine can act without referencing the
/// session type. One instance per connected world.
/// </summary>
public sealed class MudSession : IAsyncDisposable, IWorldActions
{
    private static readonly Rgb MipColour = new(0x80, 0xC0, 0xF0);
    private static readonly Rgb SysColour = new(0xF0, 0xC0, 0x40);

    private readonly Channel<SessionMessage> _mailbox =
        Channel.CreateUnbounded<SessionMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Connection _connection = new();
    private readonly TelnetLayer _telnet = new();
    private readonly AnsiParser _ansi;
    private readonly Decoder _decoder;
    private readonly Encoding _encoding;

    private readonly VariableStore _variables = new();
    private readonly AutomationEngine _automation;

    private readonly MipParser _mip = new();
    private readonly MipProcessor _mipProc;
    private string _mipId = "";
    private bool _mipPending, _mipGotData, _mipSent;
    private int _mipRetries, _mipSecondsSinceHandshake;

    private Task? _loop;
    private Task? _ticker;
    private CancellationTokenSource? _cts;

    public WorldProfile Profile { get; }
    public ConnectionState State => _connection.State;
    public AutomationEngine Automation => _automation;
    public VariableStore Variables => _variables;

    public Action<string, IReadOnlyList<string>>? ScriptDispatcher { get; set; }
    public Action<string>? ScriptExecutor { get; set; }

    public event Action<Line>? LineReady;
    public event Action<ConnectionState>? StateChanged;
    public event Action<string, string>? GmcpReceived;
    public event Action<IReadOnlyDictionary<string, string>>? MsspReceived;
    public event Action<bool>? EchoModeChanged;
    /// <summary>Raised after MIP vitals/map variables change (drive a HUD from this).</summary>
    public event Action? MipVitalsUpdated;

    public MudSession(WorldProfile profile)
    {
        Profile = profile;
        _encoding = profile.ResolveEncoding();
        _decoder = _encoding.GetDecoder();
        _ansi = new AnsiParser();
        _automation = new AutomationEngine(_variables);
        _mipProc = new MipProcessor(_variables);

        _ansi.LineCompleted += OnLineCompleted;

        _connection.BytesReceived += bytes => _mailbox.Writer.TryWrite(new SessionMessage.DataArrived(bytes));
        _connection.StateChanged += s => StateChanged?.Invoke(s);

        _telnet.SendData += bytes => _mailbox.Writer.TryWrite(new SessionMessage.SendBytes(bytes));
        _telnet.GmcpReceived += (pkg, json) => GmcpReceived?.Invoke(pkg, json);
        _telnet.MsspReceived += vars => MsspReceived?.Invoke(vars);
        _telnet.ServerEchoChanged += on => EchoModeChanged?.Invoke(on);
        _telnet.WindowSize = () => (Profile.TerminalColumns, Profile.TerminalRows);
        _telnet.GoAhead += () => _ansi.FlushAsPrompt();

        _mip.MessageReceived += m => { if (m.Id == _mipId) { _mipGotData = true; _mipProc.Handle(m); } };
        _mipProc.VitalsUpdated += () => MipVitalsUpdated?.Invoke();
        _mipProc.Notice += text => LineReady?.Invoke(Line.FromText(text));
        _mipProc.Tell += text => LineReady?.Invoke(Line.FromText(text, MipColour));
        _mipProc.Channel += (ch, msg) => LineReady?.Invoke(Line.FromText($"[{ch}] {msg}", MipColour));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Profile.EnableMip)
        {
            _mipId = EnsureMipId();
            _mipPending = true; _mipGotData = false; _mipSent = false; _mipRetries = 0; _mipSecondsSinceHandshake = 0;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        _ticker = Task.Run(() => RunTickerAsync(_cts.Token));
        await _connection.ConnectAsync(Profile.Host, Profile.Port, Profile.UseTls,
            Profile.AcceptInvalidCertificates, _cts.Token).ConfigureAwait(false);
    }

    public void Submit(string text) => _mailbox.Writer.TryWrite(new SessionMessage.UserInput(text));
    public void RunScript(string code) => _mailbox.Writer.TryWrite(new SessionMessage.RunScript(code));
    public void SendGmcp(string package, string json) => _telnet.SendGmcp(package, json);

    /// <summary>Force the MIP handshake now (manual `mipstart`).</summary>
    public void StartMip()
    {
        if (!Profile.EnableMip) { LineReady?.Invoke(Line.FromText("[MIP] not enabled for this world", SysColour)); return; }
        _mipId = EnsureMipId();
        _mipPending = false; _mipGotData = false; _mipRetries = 0;
        SendMipHandshake();
    }

    void IWorldActions.Send(string text) => _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    void IWorldActions.Echo(string text) => LineReady?.Invoke(Line.FromText(text));
    string? IWorldActions.GetVariable(string name) => _variables.Get(name);
    void IWorldActions.SetVariable(string name, string value) => _variables.Set(name, value);
    void IWorldActions.CallScript(string function, IReadOnlyList<string> wildcards) => ScriptDispatcher?.Invoke(function, wildcards);

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
                        MipTick();
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
        byte[] data = _telnet.Process(bytes);
        if (data.Length == 0) return;
        string text = Decode(data);
        if (Profile.EnableMip)
            text = _mip.Process(text);   // strip MIP frames; raises MessageReceived
        if (text.Length > 0)
            _ansi.Feed(text);
        await Task.CompletedTask;
    }

    private void OnLineCompleted(Line line)
    {
        if (_mipPending && line.PlainText.Trim() == ">")
        {
            _mipPending = false;
            SendMipHandshake();
        }
        LineReady?.Invoke(line);
        _automation.ProcessLine(line.PlainText, this);
    }

    private void HandleInput(string text)
    {
        if (!_automation.ProcessInput(text, this))
            _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    }

    // ---- MIP handshake -------------------------------------------------------

    private string EnsureMipId()
    {
        if (string.IsNullOrEmpty(Profile.MipClientId))
            Profile.MipClientId = Random.Shared.Next(0, 100000).ToString("D5");
        _variables.Set("mipid", Profile.MipClientId);
        return Profile.MipClientId;
    }

    private void SendMipHandshake()
    {
        _mailbox.Writer.TryWrite(new SessionMessage.SendText($"3klient {_mipId}~~Scrye"));
        _mailbox.Writer.TryWrite(new SessionMessage.SendText("3klient LINEFEED on"));
        _mailbox.Writer.TryWrite(new SessionMessage.SendText("3klient HAA off"));
        _mailbox.Writer.TryWrite(new SessionMessage.SendText("forcehp"));
        _mipSent = true;
        _mipRetries++;
        _mipSecondsSinceHandshake = 0;
        LineReady?.Invoke(Line.FromText($"[MIP] handshake sent (id {_mipId})", SysColour));
    }

    private void MipTick()
    {
        if (!Profile.EnableMip || !_mipSent || _mipGotData) return;
        _mipSecondsSinceHandshake++;
        if (_mipSecondsSinceHandshake >= 10 && _mipRetries < 3)
        {
            LineReady?.Invoke(Line.FromText("[MIP] no data yet - retrying handshake", SysColour));
            SendMipHandshake();
        }
    }

    private async Task SendRawAsync(byte[] bytes, CancellationToken ct) =>
        await _connection.SendAsync(bytes, ct).ConfigureAwait(false);

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

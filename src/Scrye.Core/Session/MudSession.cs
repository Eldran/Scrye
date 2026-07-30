using System.IO;
using System.Text;
using System.Threading.Channels;
using Scrye.Core.Automation;
using Scrye.Core.Events;
using Scrye.Core.Logging;
using Scrye.Core.Mip;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Profiles;
using Scrye.Core.State;
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
    private static readonly Rgb InputColour = new(0x60, 0xC0, 0xF0);

    private readonly Channel<SessionMessage> _mailbox =
        Channel.CreateUnbounded<SessionMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Connection _connection = new();
    private readonly TelnetLayer _telnet = new();
    private readonly AnsiParser _ansi;
    private readonly Decoder _decoder;
    private readonly Encoding _encoding;

    private readonly VariableStore _variables = new();
    private readonly AutomationEngine _automation;
    private readonly SequenceEngine _sequences = new();

    private readonly EventBus _events = new();
    private readonly EventLog _log = new();
    private SessionRecorder? _recorder;

    private readonly StateStore _state = new();

    private SessionLogger? _logger;   // active transcript logger, mutated only on the loop

    private readonly ReconnectPolicy _reconnect = new();
    private bool _userClosing;        // set on Dispose so a deliberate close doesn't trigger reconnect
    private bool _everConnected;      // reconnect only after a live connection has dropped
    private Task? _reconnectTask;
    private CancellationTokenSource? _reconnectCts;

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
    public SequenceEngine Sequences => _sequences;
    public VariableStore Variables => _variables;

    /// <summary>The instrumented event spine: every line, send, rule fire, protocol
    /// message, and state change flows through here. Subscribe sinks or the
    /// <see cref="EventBus.Emitted"/> event to build timelines, dashboards, etc.</summary>
    public EventBus Events => _events;
    /// <summary>Always-on bounded ring buffer of recent events (for the timeline/debugger).</summary>
    public EventLog Log => _log;
    /// <summary>Structured game state (GMCP/MIP → watchable tree). The HUD and inspector read this.</summary>
    public StateStore GameState => _state;
    /// <summary>The active recorder, or null when not recording.</summary>
    public SessionRecorder? Recorder => _recorder;
    public bool IsRecording => _recorder is not null;

    /// <summary>Backoff schedule used when a live connection drops. Tune before connecting.</summary>
    public ReconnectPolicy ReconnectPolicy => _reconnect;
    /// <summary>When true (default), a dropped connection triggers automatic reconnect with backoff.</summary>
    public bool ReconnectEnabled { get; set; } = true;

    public Action<string, IReadOnlyList<string>>? ScriptDispatcher { get; set; }
    public Action<string>? ScriptExecutor { get; set; }

    public event Action<Line>? LineReady;
    public event Action<ConnectionState>? StateChanged;
    public event Action<string, string>? GmcpReceived;
    public event Action<IReadOnlyDictionary<string, string>>? MsspReceived;
    public event Action<bool>? EchoModeChanged;
    /// <summary>Raised after MIP vitals/map variables change (drive a HUD from this).</summary>
    public event Action? MipVitalsUpdated;
    /// <summary>Raised as a running command sequence progresses (drive the status strip).</summary>
    public event Action<SequenceStatus>? SequenceStatusChanged;
    /// <summary>Fires once per scheduler tick (1s), on the loop thread, with the delta seconds.
    /// Drives plugin timers.</summary>
    public event Action<double>? Ticked;

    public MudSession(WorldProfile profile)
    {
        Profile = profile;
        _encoding = profile.ResolveEncoding();
        _decoder = _encoding.GetDecoder();
        _ansi = new AnsiParser();
        _automation = new AutomationEngine(_variables);
        _mipProc = new MipProcessor(_variables);

        _ansi.LineCompleted += OnLineCompleted;

        _events.Subscribe(_log);   // the ring buffer is always listening

        _connection.BytesReceived += bytes => _mailbox.Writer.TryWrite(new SessionMessage.DataArrived(bytes));
        // Route state changes through the mailbox so notification + emission happen on the loop thread.
        _connection.StateChanged += s => _mailbox.Writer.TryWrite(new SessionMessage.ConnectionStateChanged(s));

        _telnet.SendData += bytes => _mailbox.Writer.TryWrite(new SessionMessage.SendBytes(bytes));
        _telnet.GmcpReceived += (pkg, json) =>
        {
            _events.Emit(SessionEventKind.Gmcp, json, pkg);
            if (!string.IsNullOrWhiteSpace(json)) _state.SetJson(pkg, json);   // GMCP → structured state
            GmcpReceived?.Invoke(pkg, json);
        };
        _telnet.MsspReceived += vars => MsspReceived?.Invoke(vars);
        _telnet.ServerEchoChanged += on => EchoModeChanged?.Invoke(on);
        _telnet.WindowSize = () => (Profile.TerminalColumns, Profile.TerminalRows);
        _telnet.GoAhead += () => _ansi.FlushAsPrompt();

        _automation.Hit += OnAutomationHit;

        // sequences: emitted commands go to the MUD via the mailbox; progress surfaces to the UI.
        _sequences.Send += text => _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
        _sequences.StatusChanged += s => SequenceStatusChanged?.Invoke(s);

        _mip.MessageReceived += m =>
        {
            if (m.Id == _mipId)
            {
                _mipGotData = true;
                _events.Emit(SessionEventKind.Mip, m.Data, $"{m.Id}/{m.Tag}");
                _mipProc.Handle(m);
            }
        };
        _mipProc.VitalsUpdated += () => { MapMipVitals(); MipVitalsUpdated?.Invoke(); };
        _mipProc.Notice += text => Echo(text);
        _mipProc.Tell += text => RaiseLine(Line.FromText(text, MipColour));
        _mipProc.Channel += (ch, msg) => RaiseLine(Line.FromText($"[{ch}] {msg}", MipColour));
    }

    private void OnAutomationHit(AutomationHit hit)
    {
        SessionEventKind kind = hit.Kind switch
        {
            AutomationHitKind.Trigger => SessionEventKind.TriggerMatched,
            AutomationHitKind.Alias => SessionEventKind.AliasMatched,
            _ => SessionEventKind.TimerFired,
        };
        _events.Emit(kind, hit.Input, hit.Name, hit.Action);
    }

    /// <summary>Single chokepoint for every displayed line: feeds the transcript
    /// logger (when active) then notifies the UI. Keeps logging and display in lockstep.</summary>
    private void RaiseLine(Line line)
    {
        _logger?.Log(line);
        LineReady?.Invoke(line);
    }

    private void Echo(string text)
    {
        _events.Emit(SessionEventKind.Notice, text);
        RaiseLine(Line.FromText(text));
    }

    /// <summary>Mirror the MIP flat vitals variables into structured state paths, so a HUD
    /// can bind to <c>character.health.current</c> regardless of whether the source is MIP or GMCP.</summary>
    private void MapMipVitals()
    {
        void Num(string var, string path)
        {
            string? v = _variables.Get(var);
            if (v is not null)
                _state.Set(path, double.TryParse(v, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d) ? StateValue.Num(d) : StateValue.Str(v));
        }
        void Str(string var, string path)
        {
            string? v = _variables.Get(var);
            if (v is not null) _state.Set(path, StateValue.Str(v));
        }

        Num("hp", "character.health.current");   Num("hpmax", "character.health.max");
        Num("sp", "character.spell.current");    Num("spmax", "character.spell.max");
        Num("gp1", "character.gold.a");           Num("gp1max", "character.gold.amax");
        Num("gp2", "character.gold.b");           Num("gp2max", "character.gold.bmax");
        Str("enemy_name", "enemy.name");          Num("enemy_hp", "enemy.health");
        Num("round", "combat.round");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Profile.EnableMip) ResetMipForConnect();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        _ticker = Task.Run(() => RunTickerAsync(_cts.Token));
        await _connection.ConnectAsync(Profile.Host, Profile.Port, Profile.UseTls,
            Profile.AcceptInvalidCertificates, _cts.Token).ConfigureAwait(false);
    }

    private void ResetMipForConnect()
    {
        _mipId = EnsureMipId();
        _mipPending = true; _mipGotData = false; _mipSent = false; _mipRetries = 0; _mipSecondsSinceHandshake = 0;
    }

    // ---- transcript logging --------------------------------------------------

    /// <summary>Default per-user log directory: <c>%APPDATA%/Scrye/logs</c> (or the
    /// XDG equivalent on non-Windows).</summary>
    public static string DefaultLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "logs");

    /// <summary>Start writing this world's transcript to a timestamped file. Any
    /// existing log is closed first. Returns the file path.</summary>
    public string StartLogging(LogFormat format = LogFormat.Text, string? directory = null)
    {
        var logger = SessionLogger.CreateFile(directory ?? DefaultLogDirectory(), Profile.Name, format);
        _mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(logger));
        return logger.Path ?? "";
    }

    /// <summary>Stop and finalize the current transcript (no-op if not logging).</summary>
    public void StopLogging() => _mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(null));

    // ---- auto-reconnect ------------------------------------------------------

    private void MaybeReconnect()
    {
        if (!ReconnectEnabled || _userClosing || !_everConnected) return;
        if (_reconnectTask is { IsCompleted: false }) return;   // a retry loop is already running
        _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
        _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
    }

    private void CancelReconnect() => _reconnectCts?.Cancel();

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!_reconnect.ShouldRetry(attempt))
            {
                _mailbox.Writer.TryWrite(new SessionMessage.SystemNotice(
                    $"[reconnect] gave up after {attempt} attempt(s)"));
                return;
            }
            attempt++;
            TimeSpan delay = _reconnect.Delay(attempt);
            string cap = _reconnect.MaxAttempts > 0 ? $"/{_reconnect.MaxAttempts}" : "";
            _mailbox.Writer.TryWrite(new SessionMessage.SystemNotice(
                $"[reconnect] attempt {attempt}{cap} in {delay.TotalSeconds:0}s…"));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            try
            {
                // Bind the socket to the SESSION lifetime (_cts), not the reconnect
                // token — CancelReconnect() fires on success and must not kill the
                // freshly-established connection.
                await _connection.ConnectAsync(Profile.Host, Profile.Port, Profile.UseTls,
                    Profile.AcceptInvalidCertificates, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                return;   // Connected state will fire and CancelReconnect() this loop
            }
            catch (OperationCanceledException) { return; }
            catch { /* Failed state fired; loop for the next attempt */ }
        }
    }

    public void Submit(string text) => _mailbox.Writer.TryWrite(new SessionMessage.UserInput(text));
    public void RunScript(string code) => _mailbox.Writer.TryWrite(new SessionMessage.RunScript(code));
    public void SendGmcp(string package, string json) => _telnet.SendGmcp(package, json);

    // Sequence control (posted to the loop so the engine stays single-threaded).
    public void RunSequence(string name) => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("run", name));
    public void RunWalk(string steps) => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("walk", steps));
    public void StopSequence() => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("stop", ""));
    public void PauseSequence() => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("pause", ""));
    public void ResumeSequence() => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("resume", ""));

    /// <summary>Load a resolved profile's triggers/aliases/timers/variables into the
    /// engine. Call before ConnectAsync.</summary>
    public void LoadProfileData(EffectiveProfile eff)
    {
        foreach (var t in eff.Triggers) _automation.AddTrigger(t);
        foreach (var a in eff.Aliases) _automation.AddAlias(a);
        foreach (var tm in eff.Timers) _automation.AddTimer(tm);
        foreach (var s in eff.Sequences) _sequences.Register(s.ToDef());
        foreach (var kv in eff.Variables) _variables.Set(kv.Key, kv.Value);
    }

    /// <summary>Replace the live rule set (triggers/aliases/timers) with a re-resolved
    /// profile's, without reconnecting. Posted to the loop so it can't race processing.
    /// Runtime variables are preserved.</summary>
    public void ReloadAutomation(EffectiveProfile eff) =>
        _mailbox.Writer.TryWrite(new SessionMessage.ReloadAutomation(eff));

    /// <summary>Begin capturing the full event stream. Idempotent — returns the
    /// active recorder. Records everything from this point until <see cref="StopRecording"/>.</summary>
    public SessionRecorder StartRecording()
    {
        if (_recorder is null)
        {
            _recorder = new SessionRecorder(Profile.Name, _events.Clock());
            _events.Subscribe(_recorder);
        }
        return _recorder;
    }

    /// <summary>Stop capturing and return the recording (null if not recording).</summary>
    public SessionRecording? StopRecording()
    {
        if (_recorder is null) return null;
        _events.Unsubscribe(_recorder);
        var rec = new SessionRecording(_recorder.Header, _recorder.Events.ToArray());
        _recorder = null;
        return rec;
    }

    /// <summary>Save the current recording to a <c>.scryerec</c> file (no-op if not recording).</summary>
    public void SaveRecording(string path) => _recorder?.Save(path);

    /// <summary>Force the MIP handshake now (manual `mipstart`).</summary>
    public void StartMip()
    {
        if (!Profile.EnableMip) { RaiseLine(Line.FromText("[MIP] not enabled for this world", SysColour)); return; }
        _mipId = EnsureMipId();
        _mipPending = false; _mipGotData = false; _mipRetries = 0;
        SendMipHandshake();
    }

    void IWorldActions.Send(string text) => _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    void IWorldActions.Echo(string text) => Echo(text);
    string? IWorldActions.GetVariable(string name) => _variables.Get(name);
    void IWorldActions.SetVariable(string name, string value)
    {
        string? old = _variables.Get(name);
        _variables.Set(name, value);
        _events.Emit(SessionEventKind.VariableChanged, value, name, old);
    }
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
                        _events.Emit(SessionEventKind.Sent, s.Text);
                        await SendRawAsync(_encoding.GetBytes(s.Text + "\r\n"), ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.ConnectionStateChanged cs:
                        OnConnectionState(cs.State);
                        break;
                    case SessionMessage.SendBytes sb:
                        await SendRawAsync(sb.Bytes, ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.Tick:
                        _automation.Tick(1.0, this);
                        _sequences.Tick(1.0);
                        MipTick();
                        Ticked?.Invoke(1.0);
                        break;
                    case SessionMessage.SequenceControl sc:
                        HandleSequenceControl(sc.Kind, sc.Arg);
                        break;
                    case SessionMessage.LoggingControl lc:
                        _logger?.Close();
                        _logger = lc.Logger;
                        break;
                    case SessionMessage.SystemNotice n:
                        _events.Emit(SessionEventKind.Notice, n.Text);
                        RaiseLine(Line.FromText(n.Text, SysColour));
                        break;
                    case SessionMessage.ReloadAutomation ra:
                        _automation.ClearTriggers(); _automation.ClearAliases(); _automation.ClearTimers();
                        foreach (var t in ra.Profile.Triggers) _automation.AddTrigger(t);
                        foreach (var a in ra.Profile.Aliases) _automation.AddAlias(a);
                        foreach (var tm in ra.Profile.Timers) _automation.AddTimer(tm);
                        _sequences.ClearRegistry();
                        foreach (var s in ra.Profile.Sequences) _sequences.Register(s.ToDef());
                        _events.Emit(SessionEventKind.Notice, "automation reloaded");
                        RaiseLine(Line.FromText(
                            $"* automation reloaded: {ra.Profile.Triggers.Count} triggers, {ra.Profile.Aliases.Count} aliases, {ra.Profile.Timers.Count} timers, {ra.Profile.Sequences.Count} sequences",
                            SysColour));
                        break;
                    case SessionMessage.RunScript r:
                        _events.Emit(SessionEventKind.ScriptRun, r.Code);
                        try { ScriptExecutor?.Invoke(r.Code); }
                        catch (Exception ex)
                        {
                            _events.Emit(SessionEventKind.ScriptError, ex.Message);
                            RaiseLine(Line.FromText("lua: " + ex.Message));
                        }
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
        _events.Emit(line.IsPrompt ? SessionEventKind.Prompt : SessionEventKind.LineReceived, line.PlainText);
        RaiseLine(line);
        _automation.ProcessLine(line.PlainText, this);

        // a prompt (GA/EOR flush, or a lone ">") lets a prompt-gated sequence advance
        if (line.IsPrompt || line.PlainText.Trim() == ">") _sequences.OnPrompt();
    }

    private void HandleSequenceControl(string kind, string arg)
    {
        switch (kind)
        {
            case "run":
                if (!_sequences.Run(arg)) Echo($"[seq] no sequence named '{arg}'");
                break;
            case "walk":
                _sequences.RunAdHoc(SequenceParser.Parse("walk", arg));
                break;
            case "stop": _sequences.Stop(); break;
            case "pause": _sequences.Pause(); break;
            case "resume": _sequences.Resume(); break;
        }
    }

    private void HandleInput(string text)
    {
        _events.Emit(SessionEventKind.InputSubmitted, text);
        _logger?.Log("> " + text, InputColour);   // transcript records what the user typed
        if (!_automation.ProcessInput(text, this))
            _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    }

    private void OnConnectionState(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connecting:
                _events.Emit(SessionEventKind.Connecting, $"{Profile.Host}:{Profile.Port}");
                break;
            case ConnectionState.Connected:
                _everConnected = true;
                CancelReconnect();               // a successful connect ends any retry loop
                if (Profile.EnableMip) ResetMipForConnect();   // re-arm the handshake on (re)connect
                _events.Emit(SessionEventKind.Connected, $"{Profile.Host}:{Profile.Port}");
                break;
            case ConnectionState.Failed:
                _events.Emit(SessionEventKind.Disconnected, "connection failed");
                MaybeReconnect();
                break;
            case ConnectionState.Disconnected:
                _events.Emit(SessionEventKind.Disconnected);
                MaybeReconnect();
                break;
            // Disconnecting is transient — no event.
        }
        StateChanged?.Invoke(state);
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
        RaiseLine(Line.FromText($"[MIP] handshake sent (id {_mipId})", SysColour));
    }

    private void MipTick()
    {
        if (!Profile.EnableMip || !_mipSent || _mipGotData) return;
        _mipSecondsSinceHandshake++;
        if (_mipSecondsSinceHandshake >= 10 && _mipRetries < 3)
        {
            RaiseLine(Line.FromText("[MIP] no data yet - retrying handshake", SysColour));
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
        _userClosing = true;      // deliberate close — suppress auto-reconnect
        _reconnectCts?.Cancel();
        _cts?.Cancel();
        _mailbox.Writer.TryComplete();
        foreach (Task? t in new[] { _loop, _ticker, _reconnectTask })
            if (t is not null) { try { await t.ConfigureAwait(false); } catch { /* ignore */ } }
        _logger?.Close();         // finalize the transcript (loop has stopped, no more Log calls)
        await _connection.DisposeAsync().ConfigureAwait(false);
        _reconnectCts?.Dispose();
        _cts?.Dispose();
    }
}

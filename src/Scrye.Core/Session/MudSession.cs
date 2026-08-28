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
    private bool _idlePausedSequence;      // did the idle guard pause the sequence, or the user?

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

    private AutoLogin? _autoLogin;    // armed on connect when the profile has a username; loop-only
    private MccpDecompressor? _mccp;  // active MCCP2 inflater (loop-managed; pump emits via mailbox)

    // per-line routing state, valid only while triggers process the current line (loop-only)
    private readonly List<string> _lineCaptures = new();
    private readonly List<(Rgb? Fore, Rgb? Back, int Start, int Length)> _lineHighlights = new();
    private bool _lineGagged;
    private bool _lineNotify;
    private bool _processingLine;

    private Task? _loop;
    private Task? _ticker;
    private CancellationTokenSource? _cts;

    public WorldProfile Profile { get; }

    /// <summary>Watches the viking feed for structural drift — a field inserted into a record
    /// silently re-indexes everything after it, so nothing throws and the numbers just go wrong.
    /// Fed from the BBE handler below; read by the ".mip" command.</summary>
    public Scrye.Core.Mip.MipShapeAudit MipAudit { get; } = new();
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

    /// <summary>Optional display filter for server output lines (plugins): return the line
    /// to show — possibly rewritten — or null to gag it. Automation, events, and the
    /// sequence engine still see the ORIGINAL line; only what's shown/logged is affected.</summary>
    public Func<Line, Line?>? LineDisplayFilter { get; set; }

    /// <summary>Optional filter for user input (plugin aliases): return the command to
    /// process — possibly rewritten — or null to consume it (nothing sent). Runs before
    /// the automation engine's own aliases.</summary>
    public Func<string, string?>? InputFilter { get; set; }

    public event Action<Line>? LineReady;
    /// <summary>A trigger routed a line to a named capture pane (pane, line).
    /// Raised on the loop thread, before the line is (possibly) displayed.</summary>
    public event Action<string, Line>? LineRouted;
    /// <summary>A trigger flagged Notify matched this line (loop thread; UI shows toast/flash).</summary>
    public event Action<Line>? NotifyRequested;
    /// <summary>A sound should play: trigger Sound field or an MSP directive
    /// ("beep", absolute path, or sounds-folder file name). Loop thread.</summary>
    public event Action<string>? SoundRequested;
    public event Action<ConnectionState>? StateChanged;
    public event Action<string, string>? GmcpReceived;
    public event Action<IReadOnlyDictionary<string, string>>? MsspReceived;
    public event Action<bool>? EchoModeChanged;
    /// <summary>Raised after MIP vitals/map variables change (drive a HUD from this).</summary>
    public event Action? MipVitalsUpdated;
    /// <summary>A structured chat message from the MIP feed: (channel, message).
    /// Tells arrive with channel "Tell". Raised on the loop thread, in addition to
    /// the formatted output line — drives plugin <c>scrye.onChannel</c> hooks.</summary>
    public event Action<string, string>? ChannelMessage;
    /// <summary>Raised as a running command sequence progresses (drive the status strip).</summary>
    public event Action<SequenceStatus>? SequenceStatusChanged;
    /// <summary>Fires once per scheduler tick, on the loop thread, with the delta seconds
    /// (currently 0.25 — see <see cref="TickIntervalSeconds"/>). Drives plugin timers, which is
    /// why it ticks faster than the 1s automation clock: plugin API 1.6 honours fractional
    /// <c>scrye.after</c>/<c>scrye.every</c> intervals down to this resolution.</summary>
    public event Action<double>? Ticked;

    /// <summary>
    /// A command line was sent to the MUD — observe-only, raised on the loop thread just before
    /// the bytes go out. This fires for EVERY <c>SendText</c> regardless of origin: typed input
    /// (after alias processing), macros, sequences, triggers, and other plugins' <c>scrye.send</c>.
    /// That completeness is the point (plugin API 1.6's <c>scrye.onCommand</c> exists so an
    /// automapper can see moves it didn't originate); the flip side is that a handler which
    /// itself sends can ping-pong forever — handlers observe, they don't react by sending.
    /// Auto-login credential replies go out as raw bytes, not <c>SendText</c>, so they never
    /// pass through here.
    /// </summary>
    public event Action<string>? CommandSent;

    /// <summary>The idle guard crossed a threshold. <see cref="IdleGuardSignal.Warning"/> is a
    /// nudge; <see cref="IdleGuardSignal.Fired"/> means automation has just been suspended and
    /// plugins should be told.</summary>
    public event Action<IdleGuardSignal>? IdleSignal;

    /// <summary>The dead-man's switch. Settings come from the profile; the session drives it from
    /// the same one-second tick that runs timers.</summary>
    public IdleGuard IdleGuard { get; } = new();

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
        _telnet.GmcpSupported = profile.EnableGmcp;
        _telnet.GmcpEnabled += () =>
            _mailbox.Writer.TryWrite(new SessionMessage.Invoke(OnGmcpNegotiated));
        _telnet.GmcpReceived += (pkg, json) =>
        {
            _events.Emit(SessionEventKind.Gmcp, json, pkg);
            GmcpAudit.Observe(pkg, json);
            if (!string.IsNullOrWhiteSpace(json)) _state.SetJson(pkg, json);   // GMCP → structured state
            MapGmcpState(pkg);
            if (GmcpAudit.Raw) RaiseLine(Line.FromText($"[GMCP] {pkg} {json}", SysColour));
            if (pkg.Equals("Comm.Channel.Text", StringComparison.OrdinalIgnoreCase))
                RaiseGmcpChannel(json);
            GmcpReceived?.Invoke(pkg, json);
        };
        _telnet.MsspReceived += vars => MsspReceived?.Invoke(vars);
        _telnet.ServerEchoChanged += on => EchoModeChanged?.Invoke(on);
        _telnet.WindowSize = () => (Profile.TerminalColumns, Profile.TerminalRows);
        _telnet.GoAhead += () => _ansi.FlushAsPrompt();

        // MXP: negotiated via telnet option 91 → the ANSI parser starts interpreting tags.
        _telnet.MxpSupported = profile.EnableMxp;
        MxpAudit.Enabled = profile.EnableMxp;
        _telnet.MxpEnabled += () =>
        {
            MxpAudit.Negotiated = true;
            if (_ansi.MxpEnabled) return;
            _ansi.MxpEnabled = true;
            _events.Emit(SessionEventKind.Notice, "MXP enabled");
            RaiseLine(Line.FromText("[MXP] enabled - '.mxp' to see what it sends", SysColour));
        };
        _ansi.MxpTagSeen += (name, secure, closing) =>
        {
            MxpAudit.Observe(name, secure, closing);
            if (MxpAudit.Raw)
                RaiseLine(Line.FromText(
                    $"[MXP] <{(closing ? "/" : "")}{name}>{(secure ? " (secure)" : "")}", SysColour));
        };
        _ansi.MxpTagIgnored += name => MxpAudit.Ignored(name);
        _ansi.MxpResponse += reply =>
            _mailbox.Writer.TryWrite(new SessionMessage.SendBytes(_encoding.GetBytes(reply)));

        // <VAR name>value</VAR>. Namespaced under "mxp." on purpose: MXP variables come from
        // the server, and dropping them into the same namespace as the user's own would let a
        // MUD redefine the variable an alias depends on. ${mxp.roomname} is explicit about
        // where the value came from, and no server can reach ${targ}.
        _ansi.MxpVariable += (name, value) =>
        {
            string clean = SanitiseMxpName(name);
            if (clean.Length == 0) return;
            _variables.Set(MxpVarPrefix + clean, value);
            _state.Set("mxp.var." + clean, StateValue.Str(value));
        };

        // <GAUGE> lands in the state store rather than inventing a second HUD mechanism: the
        // existing gauge widget binds a state path, so a plugin (or a future built-in panel)
        // renders a server gauge with nothing new written.
        _ansi.MxpGauge += (name, value, max, caption) =>
        {
            string clean = SanitiseMxpName(name);
            if (clean.Length == 0) return;
            _state.Set($"mxp.gauge.{clean}.value", StateValue.Num(value));
            if (max > 0) _state.Set($"mxp.gauge.{clean}.max", StateValue.Num(max));
            if (caption.Length > 0) _state.Set($"mxp.gauge.{clean}.caption", StateValue.Str(caption));
        };

        _automation.Hit += OnAutomationHit;

        // sequences: emitted commands go to the MUD via the mailbox; progress surfaces to the UI.
        _sequences.Send += text => _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
        _sequences.SendClient += RunClientCommand;   // ">cs pause" steps go through the pipeline
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
        // the raw viking (BBE) feed becomes watchable state: vik.<key>
        _mipProc.VikingData += (k, v) =>
        {
            _state.Set("vik." + k.ToLowerInvariant(), StateValue.Str(v));
            MipAudit.Observe(k, v);        // structural drift watch; see MipShapeAudit
        };
        // Every frame, decoded or not. Two jobs: the audit tallies it so ".mip fields" can name
        // a tag this build has never heard of, and an undecoded payload is parked in state as
        // mip.<tag> so a plugin can read a new guild's feed the day the MUD starts sending it,
        // without waiting for the client to learn its structure.
        _mipProc.TagSeen += (tag, data, handled) =>
        {
            MipAudit.ObserveTag(tag, data, handled);
            if (!handled) _state.Set("mip." + tag.ToLowerInvariant(), StateValue.Str(data));
        };
        _mipProc.Notice += text => Echo(text);
        // These used to also RaiseLine a "[Channel] text" copy into the output pane. They no
        // longer do: 3Scapes prints every tell and channel message to the screen itself, so
        // the echo was a second copy of a line the player had already read — with the MIP
        // banner still embedded in it, so it did not even read as tidier than the original.
        //
        // The events stay. They are what feeds scrye.onChannel, and so the Chats capture
        // pane, the companion's chat tab and notifications — none of which depend on the
        // line ever having been drawn in the main pane. If a MUD is ever found that reports
        // a channel over MIP without printing it, the fix is to route it to a pane from a
        // plugin, not to reinstate a blanket duplicate for everyone.
        _mipProc.Tell += text => ChannelMessage?.Invoke("Tell", text);
        _mipProc.Channel += (ch, msg) => ChannelMessage?.Invoke(ch, msg);
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
        Str("gline1", "character.gline1");        Str("gline2", "character.gline2");
        // The colour-tagged originals. A watcher on "character.gline1" fires for its ".raw"
        // child too, which is what you want: they always change together.
        Str("gline1_raw", "character.gline1.raw"); Str("gline2_raw", "character.gline2.raw");
        Str("uptime", "server.uptime");           Str("lag", "server.lag");
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

    /// <summary>The variables MIP owns. Cleared when a different character takes over the
    /// connection, so none of the previous one's numbers linger in a HUD. Deliberately a
    /// list rather than a wipe: variables are also user territory (aliases set them), and
    /// "mipid" is the client id, which is per-connection and must survive.</summary>
    private static readonly string[] MipOwnedVariables =
    {
        "hp", "hpmax", "sp", "spmax", "gp1", "gp1max", "gp2", "gp2max",
        "gline1", "gline1_raw", "gline2", "gline2_raw", "enemy_name", "enemy_hp", "round",
    };

    /// <summary>
    /// A password prompt on an already-connected session means a <i>different character</i>
    /// is taking over it. 3Scapes registers a MIP client per <b>login</b>, not per socket, so
    /// the handshake sent at connect does not carry over: without this the new character
    /// silently gets no feed at all — every vital reads empty — and the only cure is
    /// reconnecting. Re-arming hands the existing "first <c>&gt;</c> prompt" trigger its job
    /// back, so the handshake goes out again for whoever just logged in.
    ///
    /// <para>The <b>password</b> prompt is the signal, not the name prompt: a MUD asks for one
    /// exactly once per login and essentially never during play, whereas a line ending
    /// "...name:" turns up in ordinary game text and would re-handshake at random. A MUD that
    /// swaps characters without re-authenticating is not covered — nothing in the stream
    /// distinguishes that from normal play.</para>
    ///
    /// <para>The previous character's state goes too. Leaving it would be worse than leaving it
    /// blank: a HUD would show another character's health, and a plugin that keys off a
    /// guild-specific feed (3s-vitals picks its bars from <c>vik.*</c>) would keep drawing the
    /// wrong ones. Everything here is re-sent by the MUD within a prompt or two.</para>
    /// </summary>
    /// <summary>The MIP-owned variables paired with their current values, in report order, for
    /// <see cref="MipShapeAudit.FieldReport"/>. A null value means the server never sent that
    /// field, which is a finding rather than a blank: Vikings never report SP.</summary>
    public IReadOnlyList<(string Name, string? Value)> MipVitalsSnapshot()
    {
        var rows = new List<(string, string?)>(MipOwnedVariables.Length);
        foreach (string name in MipOwnedVariables) rows.Add((name, _variables.Get(name)));
        return rows;
    }

    private void ReArmMipForNewLogin()
    {
        if (!Profile.EnableMip || _mipPending) return;   // already waiting to hand shake
        foreach (string v in MipOwnedVariables) _variables.Delete(v);
        _state.ClearPrefix("character");
        _state.ClearPrefix("enemy");
        _state.ClearPrefix("combat");
        _state.ClearPrefix("vik");        // the guild feed is per-character too
        ResetMipForConnect();
        MipVitalsUpdated?.Invoke();       // tell the HUD its numbers just went away
        RaiseLine(Line.FromText("[MIP] new login - handshake will re-send", SysColour));
    }

    // ---- transcript logging --------------------------------------------------

    /// <summary>Default per-user log directory: <c>%APPDATA%/Scrye/logs</c> (or the
    /// XDG equivalent on non-Windows).</summary>
    public static string DefaultLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "logs");

    /// <summary>Start writing this world's transcript to a timestamped file. Any
    /// existing log is closed first. Returns the file path.</summary>
    public string StartLogging(LogFormat format = LogFormat.Text, string? directory = null,
                               string? fileStem = null)
    {
        var logger = SessionLogger.CreateFile(directory ?? DefaultLogDirectory(), Profile.Name, format,
                                              fileStem: fileStem);
        _mailbox.Writer.TryWrite(new SessionMessage.LoggingControl(logger));
        return logger.Path ?? "";
    }

    /// <summary>The file stem the automatic transcript uses: <c>yyyy-MM-dd</c> then this world's
    /// name, which for a character connection IS the character (ProfileResolver sets Name from
    /// the deepest named layer). Date first so a folder sorted by name is also sorted by day.</summary>
    public string AutoLogStem() => $"{DateTime.Now:yyyy-MM-dd}-{Profile.Name}";

    /// <summary>The log format the profile asked for; anything unrecognised reads as text.</summary>
    public LogFormat AutoLogFormat() =>
        Profile.AutoLogFormat.Trim().StartsWith("htm", StringComparison.OrdinalIgnoreCase)
            ? LogFormat.Html : LogFormat.Text;

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

    /// <summary>Queue an action to run on the session loop thread (keeps off-loop callers —
    /// e.g. UI button handlers invoking plugin callbacks — single-threaded with the session).</summary>
    public void Post(Action action) => _mailbox.Writer.TryWrite(new SessionMessage.Invoke(action));

    public void Submit(string text) => _mailbox.Writer.TryWrite(new SessionMessage.UserInput(text));

    /// <summary>
    /// Submit input that must be taken as ONE command however many separators it contains.
    /// For text the MUD authored rather than the user: an MXP <c>&lt;SEND&gt;</c> link.
    ///
    /// <para><c>look;quit</c> in a link is the MUD's text, not a person asking for two
    /// commands, and honouring ';' there would let a hostile link fan one click out into
    /// several client-side commands -- each of which can match an alias, and an alias can
    /// run script. Same reasoning as <c>WorldViewModel.HandleCommandLink</c> keeping links
    /// away from the '/' console.</para>
    /// </summary>
    public void SubmitLiteral(string text) =>
        _mailbox.Writer.TryWrite(new SessionMessage.UserInput(text, Split: false));
    public void RunScript(string code) => _mailbox.Writer.TryWrite(new SessionMessage.RunScript(code));
    public void SendGmcp(string package, string json) => _telnet.SendGmcp(package, json);

    // Sequence control (posted to the loop so the engine stays single-threaded).
    public void RunSequence(string name) => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("run", name));
    public void RunWalk(string steps) => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("walk", steps));
    public void StopSequence() => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("stop", ""));
    public void PauseSequence() => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("pause", ""));
    public void ResumeSequence() => _mailbox.Writer.TryWrite(new SessionMessage.SequenceControl("resume", ""));

    /// <summary>
    /// Advance the guard and act on what it decided. Firing suspends what the session owns --
    /// profile timers and any running sequence -- and raises <see cref="IdleSignal"/> so the host
    /// can warn the user and hand the news to plugins, which stop what they own.
    /// </summary>
    private void TickIdleGuard()
    {
        IdleGuardSignal signal = IdleGuard.Tick(1.0);
        if (signal == IdleGuardSignal.None) return;
        if (signal == IdleGuardSignal.Fired)
        {
            _automation.TimersSuspended = true;
            // Only pause a sequence that was actually running, and remember that we did, so
            // coming back does not silently un-pause one the user had paused on purpose.
            _idlePausedSequence = _sequences.State
                is SequenceState.Waiting or SequenceState.WaitingForPrompt;
            if (_idlePausedSequence) _sequences.Pause();
        }
        IdleSignal?.Invoke(signal);
    }

    /// <summary>
    /// Undo what firing suspended, because the hazard was being away and you are back. Only the
    /// session's own automation resumes: a plugin told to stop stays stopped until you restart it
    /// deliberately, which is what the MUSHclient original did and what you want from a thing that
    /// walks your character around.
    /// </summary>
    private void ResumeAfterIdle()
    {
        _automation.TimersSuspended = false;
        if (_idlePausedSequence)
        {
            _idlePausedSequence = false;
            _sequences.Resume();
        }
    }

    /// <summary>Load a resolved profile's triggers/aliases/timers/variables into the
    /// engine. Call before ConnectAsync.</summary>
    public void LoadProfileData(EffectiveProfile eff)
    {
        IdleGuard.Seconds = eff.IdleGuardSeconds;
        IdleGuard.Enabled = eff.IdleGuardEnabled;
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

    // ---- plugin-facing routing/alert requests (call on the loop thread) -------

    /// <summary>Prefix for server-set MXP variables, so <c>${mxp.hp}</c> can never collide with
    /// a variable the user (or one of their aliases) owns.</summary>
    public const string MxpVarPrefix = "mxp.";

    /// <summary>MXP names come off the network and end up as state paths and variable names.
    /// Keep them to something that cannot escape its namespace or break a path.</summary>
    private static string SanitiseMxpName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(name.Length, 48));
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(char.ToLowerInvariant(c));
            if (sb.Length >= 48) break;
        }
        return sb.ToString();
    }

    /// <summary>Route a line to a named capture pane (plugin parity with trigger CapturePane).</summary>
    public void RoutePane(string pane, Line line)
    {
        if (!string.IsNullOrWhiteSpace(pane)) LineRouted?.Invoke(pane.Trim(), line);
    }

    /// <summary>Play a sound (plugin parity with trigger Sound / MSP).</summary>
    public void RequestSound(string sound)
    {
        if (!string.IsNullOrWhiteSpace(sound)) SoundRequested?.Invoke(sound.Trim());
    }

    /// <summary>Raise a notification toast (plugin parity with trigger Notify).</summary>
    public void RequestNotify(Line line) => NotifyRequested?.Invoke(line);

    void IWorldActions.Send(string text) => _mailbox.Writer.TryWrite(new SessionMessage.SendText(text));
    void IWorldActions.SendToClient(string text) => RunClientCommand(text);
    void IWorldActions.Echo(string text) => Echo(text);
    string? IWorldActions.GetVariable(string name) => _variables.Get(name);
    void IWorldActions.SetVariable(string name, string value)
    {
        string? old = _variables.Get(name);
        _variables.Set(name, value);
        _events.Emit(SessionEventKind.VariableChanged, value, name, old);
    }
    void IWorldActions.CallScript(string function, IReadOnlyList<string> wildcards) => ScriptDispatcher?.Invoke(function, wildcards);
    void IWorldActions.Capture(string pane)
    {
        if (!_processingLine || string.IsNullOrWhiteSpace(pane)) return;   // only meaningful mid-line
        if (!_lineCaptures.Contains(pane)) _lineCaptures.Add(pane);
    }
    void IWorldActions.GagLine() { if (_processingLine) _lineGagged = true; }
    void IWorldActions.Notify() { if (_processingLine) _lineNotify = true; }
    void IWorldActions.Highlight(Rgb? fore, Rgb? back, int start, int length)
    {
        if (_processingLine && (fore is not null || back is not null) && length > 0)
            _lineHighlights.Add((fore, back, start, length));
    }
    void IWorldActions.PlaySound(string sound)
    {
        if (!string.IsNullOrWhiteSpace(sound)) SoundRequested?.Invoke(sound);
    }

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
                    case SessionMessage.DataInflated di:
                        ProcessTelnetChunk(di.Bytes);
                        break;
                    case SessionMessage.UserInput u:
                        HandleInput(u.Text, u.Split);
                        break;
                    case SessionMessage.SendText s:
                        _events.Emit(SessionEventKind.Sent, s.Text);
                        CommandSent?.Invoke(s.Text);
                        await SendRawAsync(_encoding.GetBytes(s.Text + "\r\n"), ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.ConnectionStateChanged cs:
                        OnConnectionState(cs.State);
                        break;
                    case SessionMessage.SendBytes sb:
                        await SendRawAsync(sb.Bytes, ct).ConfigureAwait(false);
                        break;
                    case SessionMessage.Tick:
                        // The scheduler ticks at 250 ms so PLUGIN timers get sub-second
                        // resolution (API 1.6), but everything that always thought in whole
                        // seconds — the idle guard, profile automation timers, sequences, the
                        // MIP keepalive — still runs on an accumulated 1s clock. Their
                        // semantics (and their tests) are unchanged.
                        _tickCarry += TickIntervalSeconds;
                        if (_tickCarry >= 0.999)
                        {
                            _tickCarry -= 1.0;
                            TickIdleGuard();
                            _automation.Tick(1.0, this);
                            _sequences.Tick(1.0);
                            GmcpTick();
                            MipTick();
                        }
                        Ticked?.Invoke(TickIntervalSeconds);
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
                    case SessionMessage.Invoke inv:
                        try { inv.Action(); }
                        catch (Exception ex) { RaiseLine(Line.FromText("plugin action error: " + ex.Message, SysColour)); }
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

    /// <summary>Scheduler tick length. 250 ms is the plugin-timer resolution (API 1.6);
    /// the 1s consumers accumulate four of these — see the Tick case in the loop.</summary>
    private const double TickIntervalSeconds = 0.25;
    private double _tickCarry;

    private async Task RunTickerAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                _mailbox.Writer.TryWrite(new SessionMessage.Tick());
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleDataAsync(byte[] bytes, CancellationToken ct)
    {
        // Once MCCP2 is live, raw socket bytes are zlib: queue them for the inflater;
        // the pump posts DataInflated messages that re-enter via ProcessTelnetChunk.
        if (_mccp is not null) { _mccp.Feed(bytes); return; }

        ProcessTelnetChunk(bytes);

        if (_telnet.CompressionActive && _mccp is null)
        {
            _mccp = new MccpDecompressor(
                inflated => _mailbox.Writer.TryWrite(new SessionMessage.DataInflated(inflated)),
                onEnded: () => _mailbox.Writer.TryWrite(new SessionMessage.Invoke(EndCompression)));
            _events.Emit(SessionEventKind.Notice, "MCCP2 compression enabled");
            RaiseLine(Line.FromText("[MCCP2] compression enabled", SysColour));
            byte[]? tail = _telnet.TakePendingCompressed();
            if (tail is { Length: > 0 }) _mccp.Feed(tail);
        }
        await Task.CompletedTask;
    }

    /// <summary>Run an in-band telnet chunk (raw or inflated) through telnet → decode →
    /// MIP → ANSI. The single downstream path for both plain and MCCP2 data.</summary>
    private void ProcessTelnetChunk(byte[] bytes)
    {
        byte[] data = _telnet.Process(bytes);
        if (data.Length == 0) return;
        string text = Decode(data);
        if (Profile.EnableMip)
            text = _mip.Process(text);   // strip MIP frames; raises MessageReceived
        if (text.Length > 0)
            _ansi.Feed(text);
    }

    /// <summary>The server ended the zlib stream (or it broke): drop back to plain bytes.
    /// Runs on the loop via an Invoke message from the pump.</summary>
    private void EndCompression()
    {
        if (_mccp is null) return;
        _mccp.Dispose();
        _mccp = null;
        _telnet.ResetCompression();
        _events.Emit(SessionEventKind.Notice, "MCCP2 compression ended");
        RaiseLine(Line.FromText("[MCCP2] compression ended", SysColour));
    }

    /// <summary>
    /// <c>Comm.Channel.Text</c> → the same <see cref="ChannelMessage"/> event MIP chat raises,
    /// so chat panes, plugin <c>onChannel</c> hooks, and cross-world relay all keep working on
    /// a world that has moved to GMCP (3Scapes cannot run MIP and GMCP together, so a GMCP
    /// character loses the MIP feed entirely). The payload's <c>text</c> arrives display-ready
    /// ("Rictor: Interesting"); <c>talker</c>/<c>prefix</c> are metadata and not re-composed
    /// into it. The two feeds never both fire on one world, so nothing is delivered twice —
    /// though unlike MIP frames, GMCP chat is out-of-band and the line ALSO prints in the main
    /// output; the panes are a second view of it, not its only home.
    /// </summary>
    private void RaiseGmcpChannel(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            string channel = root.TryGetProperty("channel", out System.Text.Json.JsonElement c)
                ? c.GetString() ?? "" : "";
            string text = root.TryGetProperty("text", out System.Text.Json.JsonElement t)
                ? t.GetString() ?? "" : "";
            if (channel.Length == 0 || text.Length == 0) return;   // not a chat line we can file
            // WHO said it: channels are inconsistent about embedding the speaker - vik sends
            // "Rictor: neigh" while gossip sends a bare "yep" with the name only in `talker`.
            // When the talker is nowhere in the text, prepend it; when it already appears -
            // a "Name: ..." chat line, or a notify narrative like "... Bjorndraugr fades
            // from the hall" - the text stands as sent. A contains-check is a heuristic
            // (a name that happens to occur mid-word would suppress the prefix), accepted:
            // the failure mode is a missing name, never a mangled line.
            string talker = root.TryGetProperty("talker", out System.Text.Json.JsonElement tk)
                ? tk.GetString() ?? "" : "";
            if (talker.Length > 0
                && text.IndexOf(talker, StringComparison.OrdinalIgnoreCase) < 0)
                text = talker + ": " + text;
            ChannelMessage?.Invoke(channel, text);
        }
        catch (System.Text.Json.JsonException) { }                 // hostile payload: not chat
    }

    private void OnLineCompleted(Line line)
    {
        if (_mipPending && line.PlainText.Trim() == ">")
        {
            _mipPending = false;
            SendMipHandshake();
        }
        else if (AutoLogin.IsPasswordPrompt(line.PlainText))
        {
            // Not at connect — _mipPending is already true then, so this no-ops and the
            // login we are in the middle of is left alone. This is the second login onward.
            ReArmMipForNewLogin();
        }
        // Fallback net: a MIP frame that survived packet-level stripping (unlucky packet
        // splits, terminators the stream scanner missed) is consumed here rather than
        // displayed — same belt-and-braces the reference MUSHclient plugin kept.
        if (Profile.EnableMip && line.PlainText.Contains("#K%")
            && _mip.TryConsumeDisplayedLine(line.PlainText, out string mipPre))
        {
            if (mipPre.Length > 0) RaiseLine(Line.FromText(mipPre));
            return;
        }
        if (_autoLogin is not null)
        {
            // Reply to name/password prompts. SendBytes (not SendText) keeps the
            // password out of the event log; the system note never contains it.
            string? reply = _autoLogin.Feed(line.PlainText, out bool isPassword);
            if (reply is not null)
            {
                _mailbox.Writer.TryWrite(new SessionMessage.SendBytes(_encoding.GetBytes(reply + "\r\n")));
                RaiseLine(Line.FromText(
                    isPassword ? "* auto-login: sent password" : $"* auto-login: sent '{reply}'", SysColour));
            }
            if (_autoLogin.Done) _autoLogin = null;
        }
        // In-band MSP: !!SOUND(…)/!!MUSIC(…) lines are directives, not text —
        // consume them entirely (no display, no triggers, no events beyond a notice).
        if (Profile.EnableMsp && line.PlainText.TrimStart().StartsWith("!!", StringComparison.Ordinal)
            && MspParser.TryParse(line.PlainText, out MspDirective? msp) && msp is not null)
        {
            _events.Emit(SessionEventKind.Notice, $"MSP {(msp.IsMusic ? "music" : "sound")}: {msp.FileName} V={msp.Volume}");
            if (!msp.FileName.Equals("Off", StringComparison.OrdinalIgnoreCase))
                SoundRequested?.Invoke(msp.FileName);
            return;
        }

        _events.Emit(line.IsPrompt ? SessionEventKind.Prompt : SessionEventKind.LineReceived, line.PlainText);

        // Triggers run BEFORE display so capture/gag can route the line. (Ordering
        // note: engine triggers now fire before plugin onLine filters.)
        _lineCaptures.Clear();
        // MXP <DEST> addressed this line at a pane. Seed it before triggers run so it goes
        // through exactly the same routing they use -- a server-directed line and a
        // trigger-captured one are indistinguishable downstream, which is the point.
        if (!string.IsNullOrWhiteSpace(line.Destination)) _lineCaptures.Add(line.Destination!.Trim());
        _lineHighlights.Clear();
        _lineGagged = false;
        _lineNotify = false;
        _processingLine = true;
        _automation.ProcessLine(line.PlainText, this);
        _processingLine = false;
        foreach (string pane in _lineCaptures) LineRouted?.Invoke(pane, line);
        if (_lineNotify) NotifyRequested?.Invoke(line);

        // Apply highlight-trigger recolours to the line before display (captures/logs keep
        // the original styling; only the shown line is recoloured).
        foreach ((Rgb? fore, Rgb? back, int start, int length) in _lineHighlights)
            line = line.RecolorRange(start, length, fore, back);

        // Plugins may gag (null) or rewrite the DISPLAYED line; automation still sees the original.
        Line? shown = LineDisplayFilter is null ? line : LineDisplayFilter(line);
        if (shown is not null && !_lineGagged) RaiseLine(shown);

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

    /// <summary>How deep the client pipeline is currently nested. Only ever touched on the
    /// session loop, which is single-threaded, so a plain field is the whole story.</summary>
    private int _clientDepth;

    /// <summary>How many hops a rule-driven command may take before we call it a loop. Five is
    /// past anything a person composes on purpose (an alias reaching a plugin that answers with
    /// another alias is two) and short enough that a runaway stops before it is noticed.</summary>
    private const int MaxClientDepth = 5;

    /// <summary>
    /// Run <paramref name="text"/> through the client's own command pipeline -- plugin aliases,
    /// then profile aliases, then the MUD if nothing claimed it -- on behalf of a rule or a
    /// sequence step whose destination is <see cref="SendTo.Client"/>. This is what lets a
    /// trigger say <c>cs pause</c> and have the chaos-sea plugin hear it.
    ///
    /// <para>Deliberately <see cref="RunInput"/> and not <see cref="HandleInput"/>: no idle
    /// poke, no "&gt;" transcript line, no ';' split. A rule firing is not a person at the
    /// keyboard, and the whole point of the idle guard is that a bot must never look like one.
    /// The separator was already applied to the rule's own template, where the author typed it.</para>
    ///
    /// <para>Runs inline rather than queueing, so a rule's commands leave in the order the rules
    /// fired: a queued hop would let a later plain send overtake an earlier client one. The
    /// depth cap is what makes inlining safe -- an alias that produces its own pattern stops
    /// with a message instead of recursing until the stack gives out.</para>
    /// </summary>
    private void RunClientCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_clientDepth >= MaxClientDepth)
        {
            Echo($"[scrye] command loop stopped after {MaxClientDepth} hops - '{text}' was not run");
            return;
        }
        _clientDepth++;
        try { RunInput(text); }
        finally { _clientDepth--; }
    }

    private void HandleInput(string text, bool split = true)
    {
        // Presence, and the only evidence of it. This is reached by typing, by a macro key and by
        // a click on a plugin's panel link, because all three arrive through Submit. What does NOT
        // reach it is anything a trigger, a timer or a plugin sends -- those go out through
        // IWorldActions.Send. That asymmetry is the whole point: a bot walking an area all night
        // must never look like someone at the keyboard.
        if (IdleGuard.HasFired) ResumeAfterIdle();
        IdleGuard.Poke();

        _events.Emit(SessionEventKind.InputSubmitted, text);
        _logger?.Log("> " + text, InputColour);   // transcript records what the user typed, as typed

        // One typed line can stand for several commands (";" separates, ";;" is a literal
        // ";"). Split AFTER the idle poke and the transcript line -- those are about the
        // person and the keystroke, not about how many commands came out of it -- and run
        // each part through the whole alias pipeline separately, so an alias can match a
        // part exactly as it would have matched a line typed on its own.
        IReadOnlyList<string>? parts = split ? CommandSeparator.Split(text) : null;
        if (parts is null) { RunInput(text); return; }
        foreach (string part in parts) RunInput(part);
    }

    /// <summary>One command's trip through plugin aliases, then profile aliases, then the
    /// wire. Separated from <see cref="HandleInput"/> only so a multi-command line can run
    /// it once per command.</summary>
    private void RunInput(string text)
    {
        if (InputFilter is not null)
        {
            string? filtered = InputFilter(text);
            if (filtered is null) return;   // a plugin alias consumed the input
            text = filtered;
        }
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
                _mccp?.Dispose(); _mccp = null;  // compression renegotiates per connection
                _telnet.ResetCompression();
                ResetGmcpForConnect();                        // GMCP re-negotiates per connection
                MxpAudit.Reset();                             // ...and so does MXP
                MxpAudit.Enabled = Profile.EnableMxp;
                if (Profile.EnableMip) ResetMipForConnect();   // re-arm the handshake on (re)connect
                // arm auto-login for this (re)connect when the profile carries a username
                _autoLogin = Profile.Username.Length > 0
                    ? new AutoLogin(Profile.Username, Profile.Password)
                    : null;
                _events.Emit(SessionEventKind.Connected, $"{Profile.Host}:{Profile.Port}");
                break;
            case ConnectionState.Failed:
                _events.Emit(SessionEventKind.Disconnected, "connection failed");
                MaybeReconnect();
                break;
            case ConnectionState.Disconnected:
                _mccp?.Dispose(); _mccp = null;
                _telnet.ResetCompression();
                _events.Emit(SessionEventKind.Disconnected);
                MaybeReconnect();
                break;
            // Disconnecting is transient — no event.
        }
        StateChanged?.Invoke(state);
    }

    /// <summary>What <c>.mxp</c> reads: which tags the server sends, and which are stripped.</summary>
    public MxpAudit MxpAudit { get; } = new();

    // ---- GMCP ----------------------------------------------------------------

    /// <summary>What the audit and <c>.gmcp</c> read.</summary>
    public Gmcp.GmcpAudit GmcpAudit { get; } = new();

    /// <summary>
    /// The roots we subscribe to. Roots rather than exact packages, so a server that adds
    /// <c>Char.Something</c> later starts sending it without a client release — GMCP's own
    /// reason for allowing "Char 1" to stand for the family.
    ///
    /// <para>These four are what 3Scapes documents: Char (vitals, combat), Room (info,
    /// contents, map), Comm (chat) and Guild (identity, state, guild-specific extras).
    /// Subscribing to everything is the right default for a client with an inspector: a
    /// package you did not ask for is invisible, and invisible is the hardest kind of missing
    /// data to diagnose.</para>
    /// </summary>
    public static readonly string[] GmcpPackages = { "Char 1", "Room 1", "Comm 1", "Guild 1" };

    private bool _gmcpHandshakeSent;
    private int _gmcpSecondsSinceSubscribe = -1;
    private bool _gmcpRetriedVerb;

    /// <summary>
    /// GMCP has been negotiated: say hello and subscribe.
    ///
    /// <para>The subscription is the part that matters. A server that only sends the packages
    /// you asked for will send NOTHING to a client that negotiates the option and then stays
    /// quiet — which looks exactly like a server that does not support GMCP, and is why this
    /// is done here rather than left to a plugin.</para>
    /// </summary>
    private void OnGmcpNegotiated()
    {
        GmcpAudit.Negotiated = true;
        if (_gmcpHandshakeSent) return;
        _gmcpHandshakeSent = true;

        _telnet.SendGmcp("Core.Hello",
            $"{{\"client\":\"Scrye\",\"version\":\"{ClientVersion}\"}}");
        SendGmcpSubscription("Core.Supports.Set");
        RaiseLine(Line.FromText("[GMCP] negotiated - subscribed to "
                                + string.Join(", ", GmcpPackages), SysColour));
    }

    private void SendGmcpSubscription(string verb)
    {
        string payload = "[" + string.Join(",", GmcpPackages.Select(p => $"\"{p}\"")) + "]";
        _telnet.SendGmcp(verb, payload);
        GmcpAudit.SubscriptionSent = payload;
        GmcpAudit.SubscriptionVerb = verb;
        _gmcpSecondsSinceSubscribe = 0;
    }

    /// <summary>
    /// One second of the GMCP watchdog, driven from the same tick as the MIP one.
    ///
    /// <para>It exists for a single doubt. <c>Core.Supports.Set</c> is the spelling the GMCP
    /// specification gives and every client uses; 3Scapes' own help text calls the mechanism
    /// "Core.Supports". If those are two names for the same thing, nothing here ever fires.
    /// If they are not, the alternative goes out after a few seconds of silence and says so,
    /// which turns "GMCP does not work" into one line naming exactly what happened.</para>
    /// </summary>
    private void GmcpTick()
    {
        if (!Profile.EnableGmcp || _gmcpSecondsSinceSubscribe < 0) return;
        if (GmcpAudit.AnyData) { _gmcpSecondsSinceSubscribe = -1; return; }   // it worked
        _gmcpSecondsSinceSubscribe++;
        if (_gmcpSecondsSinceSubscribe < 5 || _gmcpRetriedVerb) return;

        _gmcpRetriedVerb = true;
        RaiseLine(Line.FromText(
            "[GMCP] no reply to Core.Supports.Set - trying the bare 'Core.Supports' spelling",
            SysColour));
        SendGmcpSubscription("Core.Supports");
    }

    private void ResetGmcpForConnect()
    {
        GmcpAudit.Reset();
        _gmcpHandshakeSent = false;
        _gmcpRetriedVerb = false;
        _gmcpSecondsSinceSubscribe = -1;
    }

    /// <summary>
    /// Mirror the GMCP packages onto the state paths MIP already feeds, so a HUD panel or a
    /// plugin written against <c>character.health.current</c> lights up from either protocol
    /// without knowing which one it is talking to.
    ///
    /// <para><see cref="StateStore.SetJson"/> has already put the payload at its own dotted
    /// path (<c>char.vitals.hp</c>); this is the second, compatibility copy. Both are kept:
    /// the raw tree is the truth about what the server sent, and this one is the contract
    /// everything already written depends on.</para>
    ///
    /// <para>Only fields whose MEANING matches are mirrored. GMCP's <c>attacker</c> is who is
    /// attacking you and MIP's <c>enemy_name</c> is who you are fighting; in practice on
    /// 3Scapes those are the same creature, and every consumer uses it as "am I in combat and
    /// with what", which is true of both. <c>Char.Combat</c>'s <c>target</c> has no MIP
    /// equivalent at all and keeps its own path rather than being forced into one.</para>
    /// </summary>
    private void MapGmcpState(string package)
    {
        void Copy(string from, string to)
        {
            StateValue v = _state.Get(from);
            if (!v.IsNull) _state.Set(to, v);
        }

        switch (package.ToLowerInvariant())
        {
            case "char.vitals":
                Copy("char.vitals.hp", "character.health.current");
                Copy("char.vitals.maxhp", "character.health.max");
                Copy("char.vitals.sp", "character.spell.current");
                Copy("char.vitals.maxsp", "character.spell.max");
                Copy("char.vitals.enc", "character.encumbrance");
                Copy("char.vitals.coffin", "character.coffin.current");
                Copy("char.vitals.coffin_max", "character.coffin.max");
                break;

            case "char.combat":
                // Combat ending arrives as one EMPTY snapshot, so the mirror has to be able to
                // clear as well as set: SetJson has already removed the leaves this reads, and
                // Get returns null for them, which writes an empty name -- the "no enemy" that
                // every consumer already tests for.
                _state.Set("enemy.name", _state.Get("char.combat.attacker") is { IsNull: false } a
                    ? a : StateValue.Str(""));
                Copy("char.combat.attacker_hp", "enemy.health");
                Copy("char.combat.rounds", "combat.round");
                Copy("char.combat.target", "combat.target");
                break;

            case "room.info":
                Copy("room.info.num", "room.num");
                Copy("room.info.name", "room.name");
                Copy("room.info.area", "room.area");
                _state.Set("room.exits", StateValue.Str(RoomExitList()));
                break;
        }
    }

    /// <summary>
    /// The compact exit list for <c>room.exits</c>: <c>"e,w"</c>.
    ///
    /// <para><c>Room.Info</c>'s exits are an OBJECT — direction to destination room number,
    /// <c>{"w":3873,"e":0}</c> — which is far more than the help text's "exits" suggested and
    /// better than anything the room header ever gave us. It is not a leaf, though, so it has
    /// no single value to mirror; the destinations stay where <see cref="StateStore.SetJson"/>
    /// put them, at <c>room.info.exits.&lt;dir&gt;</c>, where they are kept correct on the way
    /// out as well as in. What is mirrored here is the one thing a room header used to give and
    /// this does not: the plain list of which ways lead somewhere.</para>
    ///
    /// <para>A destination of 0 still counts as an exit — it means the way is there and the
    /// server is not saying where it goes, which is exactly the frontier a mapper wants.
    /// Compass order rather than the server's, because the store keeps no order and a stable
    /// one can be compared between rooms.</para>
    ///
    /// <para>Compass points FIRST, and the rest after, is not only tidiness: a compass exit
    /// reverses (north out is south back, almost always) and a special one need not — <c>in</c>
    /// does not have to come back as <c>out</c>. The split in the list is the split between
    /// what a mapper may reason about in both directions and what it must walk to learn.</para>
    /// </summary>
    private string RoomExitList()
    {
        const string Prefix = "room.info.exits.";
        var found = new List<string>();
        foreach (KeyValuePair<string, StateValue> kv in _state.Snapshot())
            if (kv.Key.StartsWith(Prefix, StringComparison.Ordinal))
                found.Add(kv.Key[Prefix.Length..]);

        string[] order = { "n", "ne", "e", "se", "s", "sw", "w", "nw", "u", "d" };
        var ordered = new List<string>();
        foreach (string d in order) if (found.Remove(d)) ordered.Add(d);
        found.Sort(StringComparer.Ordinal);          // anything the compass does not cover
        ordered.AddRange(found);
        return string.Join(",", ordered);
    }

    /// <summary>The version reported in <c>Core.Hello</c>. Read off the assembly so it cannot
    /// drift from what shipped.</summary>
    public static string ClientVersion =>
        typeof(MudSession).Assembly.GetName().Version?.ToString(3) ?? "0";

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
        // 'forcehp' used to follow, kicking an immediate HP line out of the MUD so the vitals
        // populated before the first natural prompt. Removed (28 Aug, Joakim): the prompt after
        // the handshake carries the same fields moments later, and the forced line was noise -
        // on every connect AND on each of the up-to-3 handshake retries. If vitals ever seem
        // slow to fill right after login, this is the line that used to hurry them.
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

using System.Text;
using System.Text.Json;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;
using Scrye.Scripting.Plugins;
using Wasmtime;

namespace Scrye.Scripting.Wasm;

/// <summary>
/// Runs one <c>lang: "wasm"</c> plugin: a core WebAssembly module speaking
/// <c>scrye-wasm-abi</c> v1 (docs/scrye-wasm-abi.md — the spec is authoritative; this file
/// implements it). Hook registrations map to integer hook ids the host allocates; every
/// dispatch is one <c>scrye_hook(id, payloadJson)</c> call into the guest.
///
/// <para>Distinct from the script runtimes in two ways worth knowing. First, LIMITS ARE
/// REAL: every host→guest call runs under an epoch deadline (a spinning plugin traps
/// instead of freezing the session loop) and linear memory is capped. Second,
/// PERMISSIONS ARE ENFORCED: imports whose manifest permission isn't declared are linked
/// to stubs that trap with a message naming the permission — at first use, not at load,
/// so additive API growth never breaks instantiation.</para>
///
/// <para>All execution is on the session loop thread; the instance is never re-entered
/// concurrently. Trap/ABI failures inside a dispatch are reported and counted for
/// quarantine, exactly like a Lua callback error.</para>
/// </summary>
public sealed class WasmPluginRuntime : IPluginRuntime
{
    // ---- shared engine + epoch ticker -----------------------------------------
    // One Engine for the whole app: module compilation is per-engine, and the epoch
    // counter it carries is bumped by a single background ticker. IncrementEpoch is
    // explicitly thread-safe. Stores (per-plugin) set how many ticks a call may span.
    private const int EpochTickMs = 25;
    private const ulong DeadlineTicks = 4;            // ≈ 100 ms per host→guest call
    private const long MaxMemoryBytes = 64L * 1024 * 1024;

    private static readonly Lazy<Engine> SharedEngine = new(() =>
    {
        var engine = new Engine(new Config().WithEpochInterruption(true));

        // A DEDICATED THREAD, deliberately not a System.Threading.Timer.
        //
        // A timer callback is queued to the thread pool — the same pool a spinning guest is
        // sitting on, and the same pool everything else in the app competes for. That makes
        // the watchdog depend on the resource the runaway plugin is exhausting, which is
        // precisely backwards. CI proved it: on a two-core runner with tests running in
        // parallel, a trap that should land in ~100 ms took 3688 ms.
        //
        // This thread only sleeps and increments, so it stays schedulable no matter how
        // busy the pool gets. Background, so it never holds the process open, and it is
        // never joined or cancelled: the epoch counter is process-wide and wrapping is
        // harmless, so there is nothing to tear down.
        var ticker = new System.Threading.Thread(() =>
        {
            while (true)
            {
                System.Threading.Thread.Sleep(EpochTickMs);
                engine.IncrementEpoch();
            }
        })
        {
            IsBackground = true,
            Name = "wasm-epoch-ticker",
        };
        ticker.Start();

        return engine;
    });

    private enum HookKind { Line, Channel, Gmcp, Connect, Disconnect, Prompt, Idle, Command, Event, Timer, RuleRun, Action, Watch }

    private sealed class PluginRule
    {
        public CompiledPattern Pattern = null!;
        public string? Send;
        public int Run;                                // hook id, 0 = none
    }

    private readonly PluginDescriptor _descriptor;
    private readonly IPluginHost _host;
    private readonly PluginDiagnostics? _diagnostics;
    private readonly HashSet<string> _permissions;

    private Store _store = null!;
    private Instance _instance = null!;
    private Memory _memory = null!;
    private Func<int, int> _alloc = null!;
    private Action<int, int> _free = null!;
    private Func<int, int, int, long> _hook = null!;

    // Host-allocated hook ids and what each was registered as.
    private int _nextHookId = 1;
    private readonly Dictionary<int, HookKind> _hookKinds = new();

    private readonly List<int> _lineHooks = new();
    private readonly List<(string chan, int fn)> _channelHooks = new();
    private readonly List<(string pkg, int fn)> _gmcpHooks = new();
    private readonly List<int> _connectHooks = new();
    private readonly List<int> _disconnectHooks = new();
    private readonly List<int> _promptHooks = new();
    private readonly List<int> _idleHooks = new();
    private readonly List<int> _commandHooks = new();
    private readonly List<(string name, int fn)> _eventHooks = new();
    private readonly List<PluginRule> _triggers = new();
    private readonly List<PluginRule> _aliases = new();
    private readonly Dictionary<int, int> _timerIds = new();       // hook id → TimerWheel id
    private readonly HashSet<int> _actionHooks = new();            // ids from register_action
    private readonly Dictionary<string, int> _actions = new();     // "w<id>" → hook id (live panel callbacks)
    private readonly Dictionary<string, List<string>> _panelActions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = new();
    private readonly TimerWheel _timers = new();
    private readonly VariableStore _vars = new();
    private bool _warnedOutputModify;

    public string Id => _descriptor.Manifest.Id;
    public string EngineName => "Wasm";

    public WasmPluginRuntime(PluginDescriptor descriptor, IPluginHost host, PluginDiagnostics? diagnostics = null)
    {
        _descriptor = descriptor;
        _host = host;
        _diagnostics = diagnostics;
        _permissions = new HashSet<string>(descriptor.Permissions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Compile, link (permission-gated), instantiate, verify the ABI version and
    /// run <c>scrye_init</c>. Throws on any failure — the manager reports and skips.</summary>
    public void Load()
    {
        Engine engine = SharedEngine.Value;
        using var module = Module.FromBytes(engine, Id, File.ReadAllBytes(_descriptor.EntryPath));

        _store = new Store(engine);
        _store.SetLimits(memorySize: MaxMemoryBytes);
        _store.SetEpochDeadline(DeadlineTicks);

        using var linker = new Linker(engine);
        DefineImports(linker);
        _instance = linker.Instantiate(_store, module);

        _memory = _instance.GetMemory("memory")
            ?? throw new InvalidOperationException("module does not export 'memory'");
        _alloc = _instance.GetFunction<int, int>("scrye_alloc")
            ?? throw new InvalidOperationException("module does not export 'scrye_alloc'");
        _free = _instance.GetAction<int, int>("scrye_free")
            ?? throw new InvalidOperationException("module does not export 'scrye_free'");
        _hook = _instance.GetFunction<int, int, int, long>("scrye_hook")
            ?? throw new InvalidOperationException("module does not export 'scrye_hook'");
        Func<int> abi = _instance.GetFunction<int>("scrye_abi_version")
            ?? throw new InvalidOperationException("module does not export 'scrye_abi_version'");
        Action init = _instance.GetAction("scrye_init")
            ?? throw new InvalidOperationException("module does not export 'scrye_init'");

        _store.SetEpochDeadline(DeadlineTicks);
        int version = abi();
        if (version != 1)
            throw new InvalidOperationException($"module speaks scrye-wasm-abi v{version}; this build speaks v1");

        _store.SetEpochDeadline(DeadlineTicks * 4);    // init gets a little longer than a dispatch
        init();
        _store.SetEpochDeadline(DeadlineTicks);
    }

    // ---- dispatch (IPluginRuntime) --------------------------------------------

    public (bool Gag, string? Rewrite) ProcessLine(string text)
    {
        bool gag = false;
        string current = text;
        bool canModify = _permissions.Contains(PluginPermissions.OutputModify);
        for (int i = 0; i < _lineHooks.Count; i++)
        {
            using JsonDocument? r = CallHook("onLine", _lineHooks[i],
                JsonSerializer.Serialize(new Dictionary<string, string> { ["line"] = current }));
            if (r is null) continue;
            bool wantsGag = r.RootElement.ValueKind == JsonValueKind.Object
                && r.RootElement.TryGetProperty("gag", out JsonElement g) && g.ValueKind == JsonValueKind.True;
            string? rewrite = r.RootElement.ValueKind == JsonValueKind.Object
                && r.RootElement.TryGetProperty("rewrite", out JsonElement rw) && rw.ValueKind == JsonValueKind.String
                ? rw.GetString() : null;
            if ((wantsGag || rewrite is not null) && !canModify)
            {
                // Enforced permissions (see class remarks): reading lines is output.read,
                // changing what the user sees is output.modify. Warn once, loudly.
                if (!_warnedOutputModify)
                {
                    _warnedOutputModify = true;
                    _host.Print(Id, "tried to gag/rewrite a line without declaring 'output.modify' — ignored");
                }
                continue;
            }
            if (wantsGag) gag = true;
            if (rewrite is not null) current = rewrite;
        }
        for (int i = 0; i < _triggers.Count; i++)
        {
            MatchResult? m = _triggers[i].Pattern.Match(text);
            if (m is not null) Apply(_triggers[i], m);
        }
        return (gag, current != text ? current : null);
    }

    public (bool Consumed, string? Rewrite) ProcessInput(string text)
    {
        for (int i = 0; i < _aliases.Count; i++)
        {
            MatchResult? m = _aliases[i].Pattern.Match(text);
            if (m is null) continue;
            Apply(_aliases[i], m);
            return (true, null);
        }
        return (false, null);
    }

    private void Apply(PluginRule rule, MatchResult m)
    {
        if (rule.Send is not null)
            AutomationEngine.ForEachLine(Template.Expand(rule.Send, m, _vars), _host.Send);
        if (rule.Run != 0)
            CallHook("rule", rule.Run,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["wildcards"] = m.Wildcards }))?.Dispose();
    }

    public void DispatchChannel(string channel, string message)
    {
        for (int i = 0; i < _channelHooks.Count; i++)
        {
            (string chan, int fn) = _channelHooks[i];
            if (chan.Length == 0 || string.Equals(chan, channel, StringComparison.OrdinalIgnoreCase))
                CallHook("onChannel", fn, JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["channel"] = channel, ["message"] = message }))?.Dispose();
        }
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _gmcpHooks.Count; i++)
        {
            (string pkg, int fn) = _gmcpHooks[i];
            if (pkg.Length == 0 || string.Equals(pkg, package, StringComparison.OrdinalIgnoreCase))
                CallHook("onGmcp", fn, JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["package"] = package, ["json"] = json }))?.Dispose();
        }
    }

    public void Tick(double dtSeconds) => _timers.Tick(dtSeconds);

    public void DispatchConnect() => FireAll(_connectHooks, "onConnect");
    public void DispatchDisconnect() => FireAll(_disconnectHooks, "onDisconnect");
    public void DispatchPrompt() => FireAll(_promptHooks, "onPrompt");
    public void DispatchIdle() => FireAll(_idleHooks, "onIdle");

    public void DispatchCommand(string text)
    {
        for (int i = 0; i < _commandHooks.Count; i++)
            CallHook("onCommand", _commandHooks[i], JsonSerializer.Serialize(
                new Dictionary<string, string> { ["command"] = text }))?.Dispose();
    }

    public void DispatchPluginEvent(string name, string data, string sourceId)
    {
        for (int i = 0; i < _eventHooks.Count; i++)
        {
            (string hookName, int fn) = _eventHooks[i];
            if (string.Equals(hookName, name, StringComparison.OrdinalIgnoreCase))
                CallHook("on:" + name, fn, JsonSerializer.Serialize(new Dictionary<string, string>
                    { ["name"] = name, ["data"] = data, ["source"] = sourceId }))?.Dispose();
        }
    }

    public void InvokeAction(string actionId)
    {
        if (_actions.TryGetValue(actionId, out int fn)) CallHook("action", fn, "{}")?.Dispose();
    }

    public void InvokeCellAction(string actionId, int col, int row, string ch)
    {
        if (_actions.TryGetValue(actionId, out int fn))
            CallHook("cellAction", fn, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["col"] = col, ["row"] = row, ["ch"] = ch }))?.Dispose();
    }

    public void InvokeChoice(string actionId, string label, int index)
    {
        if (_actions.TryGetValue(actionId, out int fn))
            CallHook("choice", fn, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["label"] = label, ["index"] = index }))?.Dispose();
    }

    public void InvokeSubmit(string actionId, string text)
    {
        if (_actions.TryGetValue(actionId, out int fn))
            CallHook("submit", fn, JsonSerializer.Serialize(new Dictionary<string, string>
                { ["text"] = text }))?.Dispose();
    }

    private void FireAll(List<int> hooks, string what)
    {
        for (int i = 0; i < hooks.Count; i++) CallHook(what, hooks[i], "{}")?.Dispose();
    }

    public void Dispose()
    {
        foreach (IDisposable sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        _timers.Clear();
        _hookKinds.Clear();
        _lineHooks.Clear(); _channelHooks.Clear(); _gmcpHooks.Clear();
        _connectHooks.Clear(); _disconnectHooks.Clear(); _promptHooks.Clear();
        _idleHooks.Clear(); _commandHooks.Clear(); _eventHooks.Clear();
        _triggers.Clear(); _aliases.Clear(); _timerIds.Clear();
        _actionHooks.Clear(); _actions.Clear(); _panelActions.Clear();
        _store?.Dispose();     // frees the instance and every guest-side byte
    }

    // ---- calling into the guest -----------------------------------------------

    /// <summary>One <c>scrye_hook</c> dispatch: write the payload into guest memory (via
    /// the guest's allocator), call, read+free the packed JSON result. Traps and ABI
    /// violations are reported and counted for quarantine, never thrown — one bad plugin
    /// must never take down line processing.</summary>
    private JsonDocument? CallHook(string what, int hookId, string payloadJson)
    {
        try
        {
            _store.SetEpochDeadline(DeadlineTicks);
            byte[] bytes = Encoding.UTF8.GetBytes(payloadJson);
            int ptr = 0;
            if (bytes.Length > 0)
            {
                ptr = _alloc(bytes.Length);
                if (ptr == 0) throw new InvalidOperationException("guest scrye_alloc returned 0 (out of memory?)");
                bytes.CopyTo(_memory.GetSpan(ptr, bytes.Length));
            }
            long packed = _hook(hookId, ptr, bytes.Length);
            JsonDocument? result = null;
            if (packed != 0)
            {
                int rptr = (int)(packed >> 32), rlen = (int)(packed & 0xffffffff);
                string json = rlen > 0 ? _memory.ReadString(rptr, rlen) : "";
                _free(rptr, rlen);
                if (json.Length > 0)
                    result = JsonDocument.Parse(json, new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            _diagnostics?.RecordSuccess(Id);
            return result;
        }
        catch (Exception ex)
        {
            _host.Print(Id, $"{what} error: {FirstLine(ex.Message)}");
            _diagnostics?.RecordFailure(Id, what, ex.Message);
            return null;
        }
    }

    private static string FirstLine(string message)
    {
        int nl = message.IndexOf('\n');
        return nl < 0 ? message : message[..nl];
    }

    // ---- imports (the "scrye" module) -----------------------------------------

    private string ReadStr(Caller caller, int ptr, int len) =>
        len <= 0 ? "" : caller.GetMemory("memory")!.ReadString(ptr, len);

    /// <summary>Pack a string return: allocate in the guest, write, return
    /// <c>(ptr &lt;&lt; 32) | len</c>. Whole-zero means nil; ptr≠0 with len 0 is "".</summary>
    private long PackStr(Caller caller, string? value)
    {
        if (value is null) return 0;
        var mem = caller.GetMemory("memory")!;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int len = bytes.Length;
        int ptr = _alloc(Math.Max(len, 1));            // len 0 still needs a non-zero ptr for ""
        if (ptr == 0) return 0;
        if (len > 0) bytes.CopyTo(mem.GetSpan(ptr, len));
        return ((long)(uint)ptr << 32) | (uint)len;
    }

    private int NewHook(HookKind kind)
    {
        int id = _nextHookId++;
        _hookKinds[id] = kind;
        return id;
    }

    private void DefineImports(Linker linker)
    {
        Store s = _store;

        // Each import is gated on a manifest permission (docs/scrye-wasm-abi.md has the
        // table; keep the two in sync). Undeclared → a same-shaped stub that throws, which
        // Wasmtime turns into a trap naming the permission at first USE.
        void Gate(string name, string? permission, Function real, Function stub)
            => linker.Define("scrye", name,
                permission is null || _permissions.Contains(permission) ? real : stub);
        Function Stub2(string name, string perm) => Function.FromCallback(s,
            (Caller _, int _, int _) => throw Missing(name, perm));
        Function Stub4(string name, string perm) => Function.FromCallback(s,
            (Caller _, int _, int _, int _, int _) => throw Missing(name, perm));
        static Exception Missing(string name, string perm) =>
            new InvalidOperationException($"scrye.{name} requires the '{perm}' permission, which this plugin does not declare");

        // ---- always available ----
        linker.Define("scrye", "print", Function.FromCallback(s,
            (Caller c, int p, int l) => _host.Print(Id, ReadStr(c, p, l))));
        linker.Define("scrye", "emit", Function.FromCallback(s,
            (Caller c, int np, int nl, int dp, int dl) => _host.EmitEvent(Id, ReadStr(c, np, nl), ReadStr(c, dp, dl))));
        linker.Define("scrye", "get_data", Function.FromCallback(s,
            (Caller c) => PackStr(c, BuildDataJson())));
        linker.Define("scrye", "on_connect", Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Connect); _connectHooks.Add(id); return id; }));
        linker.Define("scrye", "on_disconnect", Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Disconnect); _disconnectHooks.Add(id); return id; }));
        linker.Define("scrye", "on_prompt", Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Prompt); _promptHooks.Add(id); return id; }));
        linker.Define("scrye", "on_idle", Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Idle); _idleHooks.Add(id); return id; }));
        linker.Define("scrye", "on_event", Function.FromCallback(s,
            (Caller c, int p, int l) =>
            {
                int id = NewHook(HookKind.Event);
                _eventHooks.Add((ReadStr(c, p, l), id));
                return id;
            }));

        // ---- gated ----
        Gate("log", PluginPermissions.LogWrite, Function.FromCallback(s,
            (Caller c, int p, int l) => _host.Log(Id, ReadStr(c, p, l))), Stub2("log", PluginPermissions.LogWrite));
        Gate("send", PluginPermissions.CommandsSend, Function.FromCallback(s,
            (Caller c, int p, int l) => _host.Send(ReadStr(c, p, l))), Stub2("send", PluginPermissions.CommandsSend));
        Gate("notify", PluginPermissions.NotificationsShow, Function.FromCallback(s,
            (Caller c, int p, int l) => _host.Notify(Id, ReadStr(c, p, l))), Stub2("notify", PluginPermissions.NotificationsShow));
        Gate("sound", PluginPermissions.SoundPlay, Function.FromCallback(s,
            (Caller c, int p, int l) => _host.PlaySound(ReadStr(c, p, l))), Stub2("sound", PluginPermissions.SoundPlay));
        Gate("capture", PluginPermissions.CaptureWrite, Function.FromCallback(s,
            (Caller c, int pp, int pl, int tp, int tl) => _host.Capture(Id, ReadStr(c, pp, pl), ReadStr(c, tp, tl))),
            Stub4("capture", PluginPermissions.CaptureWrite));

        Gate("get_state", PluginPermissions.StateRead, Function.FromCallback(s,
            (Caller c, int p, int l) => PackStr(c, _host.GetState(ReadStr(c, p, l)))),
            Function.FromCallback(s, (Caller _, int _, int _) => (long)0 == 0
                ? throw Missing("get_state", PluginPermissions.StateRead) : 0L));
        Gate("set_state", PluginPermissions.StateWrite, Function.FromCallback(s,
            (Caller c, int kp, int kl, int vp, int vl) => _host.SetState(ReadStr(c, kp, kl), ReadStr(c, vp, vl))),
            Stub4("set_state", PluginPermissions.StateWrite));
        Gate("watch_state", PluginPermissions.StateRead, Function.FromCallback(s,
            (Caller c, int p, int l) =>
            {
                string path = ReadStr(c, p, l);
                int id = NewHook(HookKind.Watch);
                _subscriptions.Add(_host.WatchState(path, (pp, v) =>
                    CallHook("watch", id, JsonSerializer.Serialize(new Dictionary<string, string>
                        { ["path"] = pp, ["value"] = v }))?.Dispose()));
                return id;
            }),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("watch_state", PluginPermissions.StateRead) : 0));

        Gate("get_variable", PluginPermissions.VariablesRead, Function.FromCallback(s,
            (Caller c, int p, int l) => PackStr(c, _host.GetVariable(ReadStr(c, p, l)) ?? "")),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("get_variable", PluginPermissions.VariablesRead) : 0L));
        Gate("set_variable", PluginPermissions.VariablesWrite, Function.FromCallback(s,
            (Caller c, int kp, int kl, int vp, int vl) => _host.SetVariable(ReadStr(c, kp, kl), ReadStr(c, vp, vl))),
            Stub4("set_variable", PluginPermissions.VariablesWrite));

        Gate("store_get", PluginPermissions.StoragePrivate, Function.FromCallback(s,
            (Caller c, int p, int l) => PackStr(c, _host.StoreGet(Id, ReadStr(c, p, l)))),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("store_get", PluginPermissions.StoragePrivate) : 0L));
        Gate("store_set", PluginPermissions.StoragePrivate, Function.FromCallback(s,
            (Caller c, int kp, int kl, int vp, int vl) => _host.StoreSet(Id, ReadStr(c, kp, kl), ReadStr(c, vp, vl))),
            Stub4("store_set", PluginPermissions.StoragePrivate));
        Gate("store_set_many", PluginPermissions.StoragePrivate, Function.FromCallback(s,
            (Caller c, int p, int l) =>
            {
                var batch = new Dictionary<string, string>(StringComparer.Ordinal);
                using var doc = JsonDocument.Parse(ReadStr(c, p, l));
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    batch[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
                if (batch.Count > 0) _host.StoreSetMany(Id, batch);
            }), Stub2("store_set_many", PluginPermissions.StoragePrivate));
        Gate("store_delete", PluginPermissions.StoragePrivate, Function.FromCallback(s,
            (Caller c, int p, int l) => _host.StoreDelete(Id, ReadStr(c, p, l))),
            Stub2("store_delete", PluginPermissions.StoragePrivate));
        Gate("store_keys", PluginPermissions.StoragePrivate, Function.FromCallback(s,
            (Caller c) => PackStr(c, JsonSerializer.Serialize(_host.StoreKeys(Id)))),
            Function.FromCallback(s, (Caller _) => true
                ? throw Missing("store_keys", PluginPermissions.StoragePrivate) : 0L));

        Gate("add_panel", PluginPermissions.UiPanels, Function.FromCallback(s,
            (Caller c, int p, int l) => AddPanel(ReadStr(c, p, l))),
            Stub2("add_panel", PluginPermissions.UiPanels));
        Gate("register_action", PluginPermissions.UiPanels, Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Action); _actionHooks.Add(id); return id; }),
            Function.FromCallback(s, (Caller _) => true
                ? throw Missing("register_action", PluginPermissions.UiPanels) : 0));

        Gate("on_line", PluginPermissions.OutputRead, Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Line); _lineHooks.Add(id); return id; }),
            Function.FromCallback(s, (Caller _) => true
                ? throw Missing("on_line", PluginPermissions.OutputRead) : 0));
        Gate("on_channel", PluginPermissions.OutputRead, Function.FromCallback(s,
            (Caller c, int p, int l) =>
            {
                int id = NewHook(HookKind.Channel);
                _channelHooks.Add((ReadStr(c, p, l), id));
                return id;
            }),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("on_channel", PluginPermissions.OutputRead) : 0));
        Gate("on_gmcp", PluginPermissions.OutputRead, Function.FromCallback(s,
            (Caller c, int p, int l) =>
            {
                int id = NewHook(HookKind.Gmcp);
                _gmcpHooks.Add((ReadStr(c, p, l), id));
                return id;
            }),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("on_gmcp", PluginPermissions.OutputRead) : 0));
        Gate("on_command", PluginPermissions.OutputRead, Function.FromCallback(s,
            (Caller _) => { int id = NewHook(HookKind.Command); _commandHooks.Add(id); return id; }),
            Function.FromCallback(s, (Caller _) => true
                ? throw Missing("on_command", PluginPermissions.OutputRead) : 0));

        Gate("after", PluginPermissions.TimersManage, Function.FromCallback(s,
            (Caller _, double secs) => AddTimer(secs, repeat: false)),
            Function.FromCallback(s, (Caller _, double _) => true
                ? throw Missing("after", PluginPermissions.TimersManage) : 0));
        Gate("every", PluginPermissions.TimersManage, Function.FromCallback(s,
            (Caller _, double secs) => AddTimer(secs, repeat: true)),
            Function.FromCallback(s, (Caller _, double _) => true
                ? throw Missing("every", PluginPermissions.TimersManage) : 0));
        Gate("cancel", PluginPermissions.TimersManage, Function.FromCallback(s,
            (Caller _, int hookId) => { if (_timerIds.Remove(hookId, out int t)) _timers.Cancel(t); }),
            Function.FromCallback(s, (Caller _, int _) =>
            { throw Missing("cancel", PluginPermissions.TimersManage); }));

        Gate("add_trigger", PluginPermissions.TriggersManage, Function.FromCallback(s,
            (Caller c, int p, int l) => AddRule(ReadStr(c, p, l), _triggers)),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("add_trigger", PluginPermissions.TriggersManage) : 0));
        Gate("add_alias", PluginPermissions.AliasesManage, Function.FromCallback(s,
            (Caller c, int p, int l) => AddRule(ReadStr(c, p, l), _aliases)),
            Function.FromCallback(s, (Caller _, int _, int _) => true
                ? throw Missing("add_alias", PluginPermissions.AliasesManage) : 0));
    }

    private int AddTimer(double seconds, bool repeat)
    {
        int id = NewHook(HookKind.Timer);
        _timerIds[id] = _timers.Add(seconds, repeat, () => CallHook(repeat ? "every" : "after", id, "{}")?.Dispose());
        return id;
    }

    private int AddRule(string json, List<PluginRule> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonElement root = doc.RootElement;
            string pattern = root.TryGetProperty("pattern", out JsonElement pe) ? pe.GetString() ?? "" : "";
            if (pattern.Length == 0) { _host.Print(Id, "addRule: missing 'pattern'"); return 0; }
            bool isRegex = root.TryGetProperty("regex", out JsonElement re) && re.ValueKind == JsonValueKind.True;
            bool ignoreCase = !root.TryGetProperty("ignoreCase", out JsonElement ic) || ic.ValueKind != JsonValueKind.False;
            string? send = root.TryGetProperty("send", out JsonElement se) && se.ValueKind == JsonValueKind.String
                ? se.GetString() : null;
            bool wantsRun = root.TryGetProperty("run", out JsonElement ru) && ru.ValueKind == JsonValueKind.True;
            int runId = wantsRun ? NewHook(HookKind.RuleRun) : 0;
            into.Add(new PluginRule
            {
                Pattern = new CompiledPattern(pattern, isRegex, ignoreCase),
                Send = send,
                Run = runId,
            });
            return runId;
        }
        catch (Exception ex)
        {
            _host.Print(Id, "addRule: " + ex.Message);
            return 0;
        }
    }

    // ---- panels ---------------------------------------------------------------

    private void AddPanel(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var created = new List<string>();
            PanelSpec spec = ToPanelSpec(doc.RootElement, created);

            string title = string.IsNullOrWhiteSpace(spec.Title) ? Id : spec.Title;
            if (_panelActions.TryGetValue(title, out List<string>? old))
                foreach (string id in old) _actions.Remove(id);     // retire the previous build
            _panelActions[title] = created;

            _host.AddPanel(Id, spec);
        }
        catch (Exception ex)
        {
            _host.Print(Id, "addPanel: " + ex.Message);
        }
    }

    private PanelSpec ToPanelSpec(JsonElement e, List<string> created)
    {
        var widgets = new List<WidgetSpec>();
        if (e.TryGetProperty("widgets", out JsonElement w) && w.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in w.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) widgets.Add(ToWidgetSpec(item, created));

        var tabs = new List<PanelTabSpec>();
        if (e.TryGetProperty("tabs", out JsonElement t) && t.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (JsonElement tab in t.EnumerateArray())
            {
                i++;
                if (tab.ValueKind != JsonValueKind.Object) continue;
                var tabWidgets = new List<WidgetSpec>();
                if (tab.TryGetProperty("widgets", out JsonElement tw) && tw.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement item in tw.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object) tabWidgets.Add(ToWidgetSpec(item, created));
                tabs.Add(new PanelTabSpec { Title = Str(tab, "title") ?? $"Tab {i}", Widgets = tabWidgets });
            }
        }

        return new PanelSpec
        {
            Title = Str(e, "title") ?? "",
            Widgets = widgets,
            Tabs = tabs,
            Width = e.TryGetProperty("width", out JsonElement wd) && wd.ValueKind == JsonValueKind.Number ? wd.GetDouble() : 0,
            Background = Str(e, "background"),
            Accent = Str(e, "accent"),
            Foreground = Str(e, "color"),
        };
    }

    private WidgetSpec ToWidgetSpec(JsonElement w, List<string> created)
    {
        // Where Lua embeds functions, wasm embeds hook ids from register_action (numbers).
        string? actionId = ActionRef(w, created, "action") ?? ActionRef(w, created, "onClick") ?? ActionRef(w, created, "onSubmit");
        string? hoverId = ActionRef(w, created, "onHover");

        Dictionary<string, string>? palette = null;
        if (w.TryGetProperty("palette", out JsonElement pal) && pal.ValueKind == JsonValueKind.Object)
        {
            palette = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty p in pal.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) palette[p.Name] = p.Value.GetString() ?? "";
        }

        // colorgrid micro-icons (API 1.8): { "char": "glyph-name", ... }
        Dictionary<string, string>? iconMap = null;
        if (w.TryGetProperty("icons", out JsonElement ico) && ico.ValueKind == JsonValueKind.Object)
        {
            iconMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty p in ico.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) iconMap[p.Name] = p.Value.GetString() ?? "";
        }

        List<WidgetSpec>? children = null;
        if (w.TryGetProperty("buttons", out JsonElement btns) && btns.ValueKind == JsonValueKind.Array)
        {
            children = new List<WidgetSpec>();
            foreach (JsonElement b in btns.EnumerateArray())
                if (b.ValueKind == JsonValueKind.Object) children.Add(ToWidgetSpec(b, created));
        }
        else if (w.TryGetProperty("widgets", out JsonElement kids) && kids.ValueKind == JsonValueKind.Array)
        {
            // row-container children (API 1.8)
            children = new List<WidgetSpec>();
            foreach (JsonElement k in kids.EnumerateArray())
                if (k.ValueKind == JsonValueKind.Object) children.Add(ToWidgetSpec(k, created));
        }

        List<string>? columns = null;
        if (w.TryGetProperty("columns", out JsonElement cols) && cols.ValueKind == JsonValueKind.Array)
        {
            columns = new List<string>();
            foreach (JsonElement c in cols.EnumerateArray()) columns.Add(c.GetString() ?? "");
        }

        return new WidgetSpec
        {
            Type = Str(w, "type") ?? "label",
            Text = Str(w, "text"),
            Bind = Str(w, "bind"),
            Value = StrLoose(w, "value"),
            Max = StrLoose(w, "max"),
            Color = Str(w, "color"),
            Dim = w.TryGetProperty("dim", out JsonElement d) && d.ValueKind == JsonValueKind.True,
            Weave = w.TryGetProperty("weave", out JsonElement wv) && wv.ValueKind == JsonValueKind.True,
            Palette = palette,
            Icons = iconMap,
            Cell = w.TryGetProperty("cell", out JsonElement ce) && ce.ValueKind == JsonValueKind.Number
                ? ce.GetDouble() : 0,
            Columns = columns,
            Separator = Str(w, "separator"),
            Labels = Str(w, "labels"),
            Align = Str(w, "align"),
            Action = actionId,
            HoverAction = hoverId,
            Children = children,
        };
    }

    /// <summary>A widget callback field: a hook id previously handed out by
    /// <c>register_action</c>. Anything else (unknown id, wrong type) is ignored — a
    /// plugin cannot conjure dispatches to ids it was never given.</summary>
    private string? ActionRef(JsonElement w, List<string> created, string field)
    {
        if (!w.TryGetProperty(field, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return null;
        int hookId = v.GetInt32();
        if (!_actionHooks.Contains(hookId)) return null;
        string actionId = "w" + hookId;
        _actions[actionId] = hookId;
        created.Add(actionId);
        return actionId;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>String OR number accepted (a gauge's max may be written as 100), matching
    /// the Lua runtime's loose Field conversion.</summary>
    private static string? StrLoose(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private string BuildDataJson()
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> kv in PluginAssets.Load(
                     _descriptor.FolderPath, _descriptor.Manifest.Data, msg => _host.Print(Id, msg)))
            map[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(map);
    }
}

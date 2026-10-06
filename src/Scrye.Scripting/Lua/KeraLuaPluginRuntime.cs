using KeraLua;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;
using Scrye.Scripting.Plugins;
using NativeLua = KeraLua.Lua;

namespace Scrye.Scripting.Lua;

/// <summary>
/// Runs one plugin on NATIVE Lua 5.4 (KeraLua): its own sandboxed <see cref="LuaHost"/>
/// state with a bound <c>scrye.*</c> API table backed by an <see cref="IPluginHost"/>.
/// THE Lua runtime since migration Phase 5 (docs/Plan-KeraLua-Migration.md): a structural
/// port of the retired MoonSharp <c>LuaPluginRuntime</c> — same hook model, same
/// panel/action bookkeeping — with <c>DynValue</c> handles replaced by registry references
/// (<c>luaL_ref</c> ints) into the plugin's own state. The <c>scrye.*</c> surface is
/// unchanged from the MoonSharp era; MoonSharp-parity notes in comments below record why
/// conversions behave the way they do.
///
/// <para>All execution is on the session loop thread, so the per-plugin state is never
/// re-entered concurrently. Bindings read arguments from the CALLING state
/// (<see cref="NativeLua.FromIntPtr"/>) — a coroutine's stack when called from one — and
/// never raise Lua errors (see <see cref="LuaHost"/> boundary rules).</para>
/// </summary>
public sealed class KeraLuaPluginRuntime : IPluginRuntime
{
    private const int NoRef = -1;

    private sealed class PluginRule
    {
        public CompiledPattern Pattern = null!;
        public string? Send;
        public int Run = NoRef;
        public int Lines = 1;                          // >1: matches over that many lines, newest last
    }

    private readonly PluginDescriptor _descriptor;
    private readonly IPluginHost _host;
    private readonly PluginDiagnostics? _diagnostics;
    private readonly LuaHost _lua;

    private readonly List<int> _lineHooks = new();
    private readonly List<(string chan, int fn)> _channelHooks = new();
    private readonly List<(string chan, int fn)> _relayHooks = new();
    private readonly List<(string pkg, int fn)> _gmcpHooks = new();
    private readonly List<int> _connectHooks = new();
    private readonly List<int> _disconnectHooks = new();
    private readonly List<int> _promptHooks = new();
    private readonly List<int> _idleHooks = new();
    private readonly List<int> _commandHooks = new();                 // scrye.onCommand (1.6)
    private readonly List<(string name, int fn)> _eventHooks = new(); // scrye.on (1.6)
    private readonly List<PluginRule> _triggers = new();   // match output lines
    private readonly RecentLines _recent = new();          // the lines multi-line triggers look back over
    private readonly List<PluginRule> _aliases = new();    // match user input
    private readonly Dictionary<string, int> _actions = new();   // panel-button callbacks by id
    // Action ids created while building each panel, by panel title — same retirement scheme
    // as the MoonSharp runtime, except retirement also UNREFS, which is what actually lets
    // native Lua collect the closures a rebuilt panel abandoned.
    private readonly Dictionary<string, List<string>> _panelActions = new(StringComparer.Ordinal);
    private List<string>? _buildingActions;   // non-null only while ToPanelSpec is running
    private readonly List<IDisposable> _subscriptions = new();
    private readonly TimerWheel _timers = new();
    // Timer id → registry ref of its function, so the ref is released when the timer is done
    // (a one-shot after it fires, any timer on cancel) instead of leaking one registry slot
    // (and the closure it pins) per scrye.after.
    private readonly Dictionary<int, int> _timerRefs = new();
    private readonly VariableStore _vars = new();          // for %-expansion in rule 'send'
    private int _nextActionId = 1;

    // Panel nesting cap: a table that contains itself (w.buttons = { w }) would otherwise
    // recurse, pushing stack slots, until it wrote past the Lua stack (native heap corruption).
    private const int MaxPanelDepth = 16;

    public string Id => _descriptor.Manifest.Id;

    public string EngineName => "Lua 5.4 (native)";

    public KeraLuaPluginRuntime(PluginDescriptor descriptor, IPluginHost host, PluginDiagnostics? diagnostics = null)
    {
        _descriptor = descriptor;
        _host = host;
        _diagnostics = diagnostics;
        _lua = new LuaHost();
        LuaSandbox.Apply(_lua);
        _lua.EnableDispatchBudget();   // a spinning hook aborts + reports instead of freezing the loop
        BuildApi();               // leaves globals scrye + print bound
    }

    /// <summary>Read and run the entry script (registers the plugin's hooks). Throws on script error.</summary>
    public void Load()
    {
        string code = File.ReadAllText(_descriptor.EntryPath);
        _lua.DoText(code, Id);
    }

    // ---- dispatch (IPluginRuntime) -------------------------------------------

    public (bool Gag, string? Rewrite) ProcessLine(string text)
    {
        bool gag = false;
        string current = text;
        NativeLua l = _lua.State;
        for (int i = 0; i < _lineHooks.Count; i++)
        {
            _lua.PushRef(_lineHooks[i]);
            l.PushString(current);
            if (!PCallReporting("onLine", 1, 1)) continue;
            if (l.Type(-1) == LuaType.Boolean && !l.ToBoolean(-1)) gag = true;    // return false -> gag
            else if (l.Type(-1) == LuaType.String) current = l.ToString(-1, false); // return "text" -> rewrite
            l.Pop(1);
        }
        // remembered BEFORE matching: a multi-line trigger's window ends with this line
        _recent.Push(text);
        for (int i = 0; i < _triggers.Count; i++)
        {
            PluginRule t = _triggers[i];
            MatchResult? m = _recent.Match(t.Pattern, t.Lines, text, out _);
            if (m is not null) Apply(t, m);
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
        if (rule.Run != NoRef)
        {
            IReadOnlyList<string> wildcards = m.Wildcards;
            // One slot per wildcard (a regex can have hundreds of groups) + the function +
            // pcall's message handler: grow the stack first, never push past its end.
            if (!_lua.State.CheckStack(wildcards.Count + 2))
            {
                BindError("rule", $"too many wildcards ({wildcards.Count}) to pass to the callback");
                return;
            }
            _lua.PushRef(rule.Run);
            foreach (string w in wildcards) _lua.State.PushString(w);
            PCallReporting("rule", wildcards.Count, 0);
        }
    }

    public void DispatchChannel(string channel, string message)
    {
        for (int i = 0; i < _channelHooks.Count; i++)
        {
            (string chan, int fn) = _channelHooks[i];
            if (chan.Length == 0 || string.Equals(chan, channel, StringComparison.OrdinalIgnoreCase))
                CallRef("onChannel", fn, channel, message);
        }
    }

    public void DispatchRelay(string sourceWorld, string channel, string message)
    {
        for (int i = 0; i < _relayHooks.Count; i++)
        {
            (string chan, int fn) = _relayHooks[i];
            if (chan.Length == 0 || string.Equals(chan, channel, StringComparison.OrdinalIgnoreCase))
                CallRef("onRelay", fn, sourceWorld, channel, message);
        }
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _gmcpHooks.Count; i++)
        {
            (string pkg, int fn) = _gmcpHooks[i];
            if (pkg.Length == 0 || string.Equals(pkg, package, StringComparison.OrdinalIgnoreCase))
                CallRef("onGmcp", fn, json, package);
        }
    }

    public void Tick(double dtSeconds) => _timers.Tick(dtSeconds);

    public void DispatchConnect() => FireAll(_connectHooks, "onConnect");
    public void DispatchDisconnect() => FireAll(_disconnectHooks, "onDisconnect");
    public void DispatchPrompt() => FireAll(_promptHooks, "onPrompt");
    public void DispatchIdle() => FireAll(_idleHooks, "onIdle");

    public void DispatchCommand(string text)
    {
        for (int i = 0; i < _commandHooks.Count; i++) CallRef("onCommand", _commandHooks[i], text);
    }

    public void DispatchPluginEvent(string name, string data, string sourceId)
    {
        for (int i = 0; i < _eventHooks.Count; i++)
        {
            (string hookName, int fn) = _eventHooks[i];
            if (string.Equals(hookName, name, StringComparison.OrdinalIgnoreCase))
                CallRef("on:" + name, fn, data, name, sourceId);
        }
    }

    public void InvokeAction(string actionId)
    {
        if (_actions.TryGetValue(actionId, out int fn)) CallRef("action", fn);
    }

    public IReadOnlyList<MenuEntry>? InvokeCellAction(string actionId, int col, int row, string ch)
    {
        if (!_actions.TryGetValue(actionId, out int fn)) return null;
        return CallRefMenu("cellAction", fn, (long)col, (long)row, ch);
    }

    public IReadOnlyList<MenuEntry>? InvokeChoice(string actionId, string label, int index)
    {
        if (!_actions.TryGetValue(actionId, out int fn)) return null;
        return CallRefMenu("choice", fn, label, (long)index);
    }

    public void InvokeSubmit(string actionId, string text)
    {
        if (_actions.TryGetValue(actionId, out int fn)) CallRef("submit", fn, text);
    }

    private void FireAll(List<int> hooks, string what)
    {
        for (int i = 0; i < hooks.Count; i++) CallRef(what, hooks[i]);
    }

    public void Dispose()
    {
        foreach (IDisposable sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        _timers.Clear();
        _timerRefs.Clear();
        _lineHooks.Clear();
        _channelHooks.Clear();
        _relayHooks.Clear();
        _gmcpHooks.Clear();
        _connectHooks.Clear();
        _disconnectHooks.Clear();
        _promptHooks.Clear();
        _idleHooks.Clear();
        _commandHooks.Clear();
        _eventHooks.Clear();
        _triggers.Clear();
        _recent.Clear();
        _aliases.Clear();
        _actions.Clear();
        _panelActions.Clear();
        _lua.Dispose();   // lua_close releases every ref above in one stroke
    }

    // ---- calling into Lua ----------------------------------------------------

    /// <summary>Call a referenced function with simple args (string/long/double/bool),
    /// discarding results. Errors are reported, never thrown — one bad plugin must never
    /// take down line processing — and counted for quarantine.</summary>
    private void CallRef(string what, int fnRef, params object?[] args)
    {
        _lua.PushRef(fnRef);
        NativeLua l = _lua.State;
        foreach (object? a in args) PushSimple(l, a);
        PCallReporting(what, args.Length, 0);
    }

    /// <summary>Like <see cref="CallRef"/>, but reads ONE result as a context menu (API 1.18):
    /// an array of <c>{ label, command? }</c> entry tables — <c>{ "Walk there", "mapg go 42" }</c>,
    /// <c>{ "-" }</c> for a separator. Anything else (nil, a non-table, the return every
    /// pre-1.18 callback produces) is null: no menu, exactly the old behaviour. Entries that
    /// are not tables or have no label are skipped rather than failing the whole menu.</summary>
    private IReadOnlyList<MenuEntry>? CallRefMenu(string what, int fnRef, params object?[] args)
    {
        _lua.PushRef(fnRef);
        NativeLua l = _lua.State;
        foreach (object? a in args) PushSimple(l, a);
        if (!PCallReporting(what, args.Length, 1)) return null;

        List<MenuEntry>? menu = null;
        if (l.IsTable(-1))
        {
            int tbl = l.GetTop();
            long n = l.RawLen(tbl);
            for (long i = 1; i <= n; i++)
            {
                l.RawGetInteger(tbl, (int)i);
                if (l.IsTable(-1))
                {
                    int entry = l.GetTop();
                    l.RawGetInteger(entry, 1);
                    string? label = LuaHost.ToStringLoose(l, l.GetTop());
                    l.Pop(1);
                    l.RawGetInteger(entry, 2);
                    string? cmd = LuaHost.ToStringLoose(l, l.GetTop());
                    l.Pop(1);
                    if (!string.IsNullOrEmpty(label))
                    {
                        menu ??= new List<MenuEntry>();
                        menu.Add(new MenuEntry(label, string.IsNullOrEmpty(cmd) ? null : cmd));
                    }
                }
                l.Pop(1);
            }
        }
        l.Pop(1);   // the result
        return menu is { Count: > 0 } ? menu : null;
    }

    /// <summary>pcall + the Safe/SafeCall accounting: success/failure recorded to
    /// diagnostics, failures printed to the world. True = results are on the stack.</summary>
    private bool PCallReporting(string what, int nargs, int nresults)
    {
        if (_lua.PCall(nargs, nresults, out string? error))
        {
            _diagnostics?.RecordSuccess(Id);
            return true;
        }
        _host.Print(Id, $"{what} error: {FirstLine(error)}");
        _diagnostics?.RecordFailure(Id, what, error ?? "unknown error");
        return false;
    }

    /// <summary>The message line of a traceback-decorated error — the MoonSharp runtime
    /// printed <c>ex.Message</c>, so parity means not dumping the whole traceback into the
    /// world output. The full text still reaches diagnostics.</summary>
    private static string FirstLine(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "unknown error";
        int nl = error.IndexOf('\n');
        return nl < 0 ? error : error[..nl];
    }

    private static void PushSimple(NativeLua l, object? value)
    {
        switch (value)
        {
            case null: l.PushNil(); break;
            case string s: l.PushString(s); break;
            case long i: l.PushInteger(i); break;
            case int i: l.PushInteger(i); break;
            case double d: l.PushNumber(d); break;
            case bool b: l.PushBoolean(b); break;
            default: l.PushString(value.ToString() ?? ""); break;
        }
    }

    // ---- the scrye.* table ---------------------------------------------------

    /// <summary>Report a binding-level failure (a .NET exception inside a host call). The
    /// MoonSharp equivalent surfaced through Safe's catch; here the binding reports
    /// directly — same visible outcome, same accounting.</summary>
    private void BindError(string what, string message)
    {
        _host.Print(Id, $"{what} error: {message}");
        _diagnostics?.RecordFailure(Id, what, message);
    }

    /// <summary>Set <c>t[name] = binding</c> for the table on top of the stack. The body
    /// runs under <see cref="LuaHost.Protect"/> against the calling state.</summary>
    private void Bind(string name, Func<NativeLua, int> body)
    {
        _lua.PushCallback(ptr =>
        {
            NativeLua l = NativeLua.FromIntPtr(ptr);
            return LuaHost.Protect(l, () => body(l), msg => BindError(name, msg));
        });
        _lua.State.SetField(-2, name);
    }

    private void BuildApi()
    {
        NativeLua l = _lua.State;
        l.NewTable();                                   // scrye

        l.PushString(Id);
        l.SetField(-2, "id");

        // scrye.data.<key> — manifest-declared files, parsed once at construction (the
        // plugin folder is read-only to the plugin; nothing to re-read).
        BuildData(l);
        l.SetField(-2, "data");

        Bind("print", cl => { _host.Print(Id, LuaHost.ArgString(cl, 1)); return 0; });
        Bind("send",  cl => { _host.Send(LuaHost.ArgString(cl, 1)); return 0; });
        Bind("log",   cl => { _host.Log(Id, LuaHost.ArgString(cl, 1)); return 0; });

        Bind("getVariable", cl => { cl.PushString(_host.GetVariable(LuaHost.ArgString(cl, 1)) ?? ""); return 1; });
        Bind("setVariable", cl => { _host.SetVariable(LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2)); return 0; });
        Bind("getState",    cl => { cl.PushString(_host.GetState(LuaHost.ArgString(cl, 1))); return 1; });
        Bind("setState",    cl => { _host.SetState(LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2)); return 0; });

        // scrye.watch(path, function(value, path) ... end)
        Bind("watch", cl =>
        {
            string path = LuaHost.ArgString(cl, 1);
            if (cl.GetTop() >= 2 && cl.IsFunction(2))
            {
                cl.PushCopy(2);
                int fn = cl.Ref(LuaRegistry.Index);   // registry is shared across threads
                _subscriptions.Add(_host.WatchState(path, (p, v) => CallRef("watch", fn, v, p)));
            }
            return 0;
        });

        // routing + alerts (parity with trigger CapturePane / Sound / Notify)
        Bind("capture", cl => { _host.Capture(Id, LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2)); return 0; });
        Bind("sound",   cl => { _host.PlaySound(LuaHost.ArgString(cl, 1)); return 0; });
        Bind("notify",  cl => { _host.Notify(Id, LuaHost.ArgString(cl, 1)); return 0; });

        // timers: scrye.after(seconds, fn) -> id (one-shot);  scrye.every(seconds, fn) -> id
        Bind("after",  cl => AddTimer(cl, repeat: false));
        Bind("every",  cl => AddTimer(cl, repeat: true));
        Bind("cancel", cl =>
        {
            int timerId = (int)LuaHost.ArgNumber(cl, 1);
            _timers.Cancel(timerId);
            ReleaseTimer(timerId);
            return 0;
        });

        // lifecycle hooks
        Bind("onConnect",    cl => AddHook(cl, _connectHooks));
        Bind("onDisconnect", cl => AddHook(cl, _disconnectHooks));
        Bind("onPrompt",     cl => AddHook(cl, _promptHooks));
        Bind("onIdle",       cl => AddHook(cl, _idleHooks));
        Bind("onCommand",    cl => AddHook(cl, _commandHooks));

        // inter-plugin events (1.6)
        Bind("emit", cl => { _host.EmitEvent(Id, LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2)); return 0; });
        Bind("on", cl =>
        {
            if (cl.GetTop() >= 2 && cl.Type(1) == LuaType.String && cl.IsFunction(2))
            {
                string name = cl.ToString(1, false);
                cl.PushCopy(2);
                _eventHooks.Add((name, cl.Ref(LuaRegistry.Index)));
            }
            return 0;
        });

        // scrye.json (1.6): encode(value) -> json | nil,err ; decode(json) -> value | nil,err
        l.NewTable();
        Bind("encode", cl =>
        {
            try
            {
                if (cl.GetTop() < 1) { cl.PushString("null"); return 1; }
                cl.PushString(LuaJsonNative.Encode(cl, 1));
                return 1;
            }
            catch (Exception ex)
            {
                cl.PushNil();
                cl.PushString(ex.Message);
                return 2;
            }
        });
        Bind("decode", cl =>
        {
            try
            {
                LuaJsonNative.Decode(cl, LuaHost.ArgString(cl, 1));
                return 1;
            }
            catch (Exception ex)
            {
                cl.PushNil();
                cl.PushString("json.decode: " + ex.Message);
                return 2;
            }
        });
        l.SetField(-2, "json");

        // scrye.onLine(function(line) ... end)  — return false to gag, a string to rewrite
        Bind("onLine", cl => AddHook(cl, _lineHooks));

        // scrye.onChannel(fn)  OR  scrye.onChannel("Party", fn)
        Bind("onChannel", cl => AddFilteredHook(cl, _channelHooks));
        // scrye.onRelay(fn)  OR  scrye.onRelay("Tell", fn) — chat from ANOTHER open world (1.10)
        Bind("onRelay", cl => AddFilteredHook(cl, _relayHooks));
        // scrye.onGmcp(fn)  OR  scrye.onGmcp("Char.Vitals", fn)
        Bind("onGmcp", cl => AddFilteredHook(cl, _gmcpHooks));

        // rules: scrye.addTrigger{ pattern=, regex=, ignoreCase=, send=, run=fn }
        Bind("addTrigger", cl => AddRule(cl, _triggers));
        Bind("addAlias",   cl => AddRule(cl, _aliases));

        // persistent per-plugin storage (scrye.store)
        l.NewTable();
        Bind("get", cl =>
        {
            string? v = _host.StoreGet(Id, LuaHost.ArgString(cl, 1));
            if (v is null) cl.PushNil(); else cl.PushString(v);
            return 1;
        });
        Bind("set", cl => { _host.StoreSet(Id, LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2)); return 0; });
        Bind("setMany", cl =>
        {
            if (cl.GetTop() >= 1 && cl.IsTable(1))
            {
                var batch = new Dictionary<string, string>(StringComparer.Ordinal);
                cl.PushNil();
                while (cl.Next(1))
                {
                    // key at -2, value at -1; ToStringLoose never converts slots in place
                    string? key = LuaHost.ToStringLoose(cl, cl.GetTop() - 1);
                    if (!string.IsNullOrEmpty(key)) batch[key] = LuaHost.ToStringLoose(cl, cl.GetTop()) ?? "";
                    cl.Pop(1);   // keep the key for the next lua_next
                }
                if (batch.Count > 0) _host.StoreSetMany(Id, batch);
            }
            return 0;
        });
        Bind("delete", cl => { _host.StoreDelete(Id, LuaHost.ArgString(cl, 1)); return 0; });
        Bind("keys", cl =>
        {
            cl.NewTable();
            string[] ks = _host.StoreKeys(Id);
            for (int i = 0; i < ks.Length; i++)
            {
                cl.PushString(ks[i]);
                cl.RawSetInteger(-2, i + 1);
            }
            return 1;
        });
        l.SetField(-2, "store");

        // MUD-shared storage (scrye.shared, 1.14) - the same surface as scrye.store, backed
        // by the host's shared root. On a host with no shared backing every get returns nil
        // and writes vanish, which is why plugins that care fall back:
        //     local ST = scrye.shared or scrye.store
        // (the table always exists on this runtime; the FALLBACK idiom is for scripts that
        // must also run on hosts predating 1.14, where scrye.shared is absent entirely).
        l.NewTable();
        Bind("get", cl =>
        {
            string? v = _host.SharedGet(Id, LuaHost.ArgString(cl, 1));
            if (v is null) cl.PushNil(); else cl.PushString(v);
            return 1;
        });
        Bind("set", cl => { _host.SharedSet(Id, LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2)); return 0; });
        Bind("setMany", cl =>
        {
            if (cl.GetTop() >= 1 && cl.IsTable(1))
            {
                var batch = new Dictionary<string, string>(StringComparer.Ordinal);
                cl.PushNil();
                while (cl.Next(1))
                {
                    string? key = LuaHost.ToStringLoose(cl, cl.GetTop() - 1);
                    if (!string.IsNullOrEmpty(key)) batch[key] = LuaHost.ToStringLoose(cl, cl.GetTop()) ?? "";
                    cl.Pop(1);
                }
                if (batch.Count > 0) _host.SharedSetMany(Id, batch);
            }
            return 0;
        });
        Bind("delete", cl => { _host.SharedDelete(Id, LuaHost.ArgString(cl, 1)); return 0; });
        Bind("keys", cl =>
        {
            cl.NewTable();
            string[] ks = _host.SharedKeys(Id);
            for (int i = 0; i < ks.Length; i++)
            {
                cl.PushString(ks[i]);
                cl.RawSetInteger(-2, i + 1);
            }
            return 1;
        });
        l.SetField(-2, "shared");

        // The exports folder (scrye.exports, 1.21): a file by NAME in %APPDATA%/Scrye/exports,
        // nothing else on disk. Lua convention for failure - nil plus the reason - because a
        // refused name or a missing file is an answer the script should show, not an error
        // that aborts the command that asked.
        //     local path, err = scrye.exports.write("3s-map-all.json", text)
        //     local text, err = scrye.exports.read("friend-map.json")
        //     for _, name in ipairs(scrye.exports.list()) do ... end
        l.NewTable();
        Bind("write", cl =>
        {
            try { cl.PushString(_host.ExportWrite(Id, LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2))); return 1; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { cl.PushNil(); cl.PushString(ex.Message); return 2; }
        });
        Bind("read", cl =>
        {
            try { cl.PushString(_host.ExportRead(Id, LuaHost.ArgString(cl, 1))); return 1; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { cl.PushNil(); cl.PushString(ex.Message); return 2; }
        });
        Bind("list", cl =>
        {
            cl.NewTable();
            string[] names = _host.ExportList(Id);
            for (int i = 0; i < names.Length; i++)
            {
                cl.PushString(names[i]);
                cl.RawSetInteger(-2, i + 1);
            }
            return 1;
        });
        l.SetField(-2, "exports");

        // scrye.addPanel{...} — same rebuild-retires-old-callbacks scheme as MoonSharp,
        // plus Unref so the abandoned closures are actually collectable.
        Bind("addPanel", cl =>
        {
            if (cl.GetTop() >= 1 && cl.IsTable(1))
            {
                var created = new List<string>();
                _buildingActions = created;
                PanelSpec spec;
                bool built = false;
                try { spec = ToPanelSpec(cl, 1); built = true; }
                finally
                {
                    _buildingActions = null;
                    // A failed build (too deep, stack exhausted) never reaches the retirement
                    // below, so release the callbacks it had already registered here.
                    if (!built)
                        foreach (string id in created)
                            if (_actions.Remove(id, out int fn)) _lua.Unref(fn);
                }

                string title = string.IsNullOrWhiteSpace(spec.Title) ? Id : spec.Title;
                if (_panelActions.TryGetValue(title, out List<string>? old))
                    foreach (string id in old)
                        if (_actions.Remove(id, out int fn)) _lua.Unref(fn);
                _panelActions[title] = created;

                _host.AddPanel(Id, spec);
            }
            return 0;
        });

        l.SetGlobal("scrye");

        // print → scrye.print: a stray debugging print lands in the world output (tagged
        // with the plugin id) instead of nowhere. Arguments joined by tabs, like Lua print.
        _lua.PushCallback(ptr =>
        {
            NativeLua cl = NativeLua.FromIntPtr(ptr);
            return LuaHost.Protect(cl, () =>
            {
                int n = cl.GetTop();
                var parts = new string[n];
                for (int i = 1; i <= n; i++)
                    parts[i - 1] = LuaHost.ToStringLoose(cl, i) ?? cl.Type(i).ToString().ToLowerInvariant();
                _host.Print(Id, string.Join("\t", parts));
                return 0;
            }, msg => BindError("print", msg));
        });
        l.SetGlobal("print");
    }

    private int AddHook(NativeLua cl, List<int> hooks)
    {
        if (cl.GetTop() >= 1 && cl.IsFunction(1))
        {
            cl.PushCopy(1);
            hooks.Add(cl.Ref(LuaRegistry.Index));
        }
        return 0;
    }

    private int AddFilteredHook(NativeLua cl, List<(string, int)> hooks)
    {
        if (cl.GetTop() == 1 && cl.IsFunction(1))
        {
            cl.PushCopy(1);
            hooks.Add(("", cl.Ref(LuaRegistry.Index)));
        }
        else if (cl.GetTop() >= 2 && cl.IsFunction(2))
        {
            string filter = LuaHost.ArgString(cl, 1);
            cl.PushCopy(2);
            hooks.Add((filter, cl.Ref(LuaRegistry.Index)));
        }
        return 0;
    }

    private int AddTimer(NativeLua cl, bool repeat)
    {
        if (cl.GetTop() >= 2 && cl.IsFunction(2))
        {
            cl.PushCopy(2);
            int fn = cl.Ref(LuaRegistry.Index);
            int id = 0;
            id = _timers.Add(LuaHost.ArgNumber(cl, 1), repeat, () =>
            {
                CallRef(repeat ? "every" : "after", fn);
                if (!repeat) ReleaseTimer(id);   // spent (no-op if it cancelled itself meanwhile)
            });
            _timerRefs[id] = fn;
            cl.PushInteger(id);
            return 1;
        }
        return 0;
    }

    /// <summary>Release a timer's function ref. Idempotent — the dictionary entry is the
    /// proof the ref is still ours, so a cancel inside the timer's own callback followed by
    /// the one-shot cleanup never unrefs twice (a double luaL_unref corrupts the free list).</summary>
    private void ReleaseTimer(int timerId)
    {
        if (_timerRefs.Remove(timerId, out int fn)) _lua.Unref(fn);
    }

    private int AddRule(NativeLua cl, List<PluginRule> into)
    {
        if (cl.GetTop() < 1 || !cl.IsTable(1)) return 0;
        string pattern = Field(cl, 1, "pattern") ?? "";
        if (pattern.Length == 0) { _host.Print(Id, "addRule: missing 'pattern'"); return 0; }

        bool isRegex = FieldBool(cl, 1, "regex", defaultValue: false);
        bool ignoreCase = FieldBool(cl, 1, "ignoreCase", defaultValue: true);

        // lines = N (triggers only): match over the last N lines. A pattern with a newline in
        // it spans that many on its own. An alias is one command line, always.
        int lines = 1;
        if (ReferenceEquals(into, _triggers))
        {
            RawField(cl, 1, "lines");
            int asked = cl.Type(-1) == LuaType.Number ? (int)Math.Clamp(cl.ToNumber(-1), 1, TriggerDef.MaxLines) : 1;
            cl.Pop(1);
            lines = TriggerDef.LinesFor(pattern, isRegex, asked);
        }

        int run = NoRef;
        RawField(cl, 1, "run");
        if (cl.IsFunction(-1)) run = cl.Ref(LuaRegistry.Index);
        else cl.Pop(1);

        try
        {
            into.Add(new PluginRule
            {
                Pattern = new CompiledPattern(pattern, isRegex, ignoreCase, multiLine: lines > 1,
                                              matchTimeout: CompiledPattern.PluginMatchTimeout),
                Send = Field(cl, 1, "send"),
                Run = run,
                Lines = lines,
            });
            if (lines > _recent.Size) _recent.Resize(lines);
        }
        catch (Exception ex)
        {
            if (run != NoRef) _lua.Unref(run);
            _host.Print(Id, "addRule: bad pattern — " + ex.Message);
        }
        return 0;
    }

    // ---- panel/widget parsing ------------------------------------------------

    private PanelSpec ToPanelSpec(NativeLua cl, int tblIndex)
    {
        if (!cl.CheckStack(8)) throw new InvalidOperationException("Lua stack exhausted");
        RawField(cl, tblIndex, "widgets");
        List<WidgetSpec> widgets = ToWidgetList(cl, cl.GetTop(), 0);
        cl.Pop(1);

        // tabbed panel: tabs = { { title=..., widgets={...} }, ... }
        var tabs = new List<PanelTabSpec>();
        RawField(cl, tblIndex, "tabs");
        if (cl.IsTable(-1))
        {
            int arr = cl.GetTop();
            long n = cl.RawLen(arr);
            for (long i = 1; i <= n; i++)
            {
                cl.RawGetInteger(arr, i);
                if (cl.IsTable(-1))
                {
                    int item = cl.GetTop();
                    RawField(cl, item, "widgets");
                    tabs.Add(new PanelTabSpec
                    {
                        Title = Field(cl, item, "title") ?? $"Tab {i}",
                        Widgets = ToWidgetList(cl, cl.GetTop(), 0),
                    });
                    cl.Pop(1);   // widgets value
                }
                cl.Pop(1);       // item
            }
        }
        cl.Pop(1);               // tabs value

        RawField(cl, tblIndex, "width");
        double width = cl.Type(-1) == LuaType.Number ? cl.ToNumber(-1) : 0;
        cl.Pop(1);

        return new PanelSpec
        {
            Title = Field(cl, tblIndex, "title") ?? "",
            Widgets = widgets,
            Tabs = tabs,
            Width = width,
            Background = Field(cl, tblIndex, "background"),
            Accent = Field(cl, tblIndex, "accent"),
            Foreground = Field(cl, tblIndex, "color"),
            Solo = FieldBool(cl, tblIndex, "solo", defaultValue: false),
        };
    }

    /// <summary>Widgets from the ARRAY at <paramref name="index"/> (absolute; any non-table
    /// value yields the empty list, matching the MoonSharp runtime).</summary>
    private List<WidgetSpec> ToWidgetList(NativeLua cl, int index, int depth)
    {
        if (depth > MaxPanelDepth)
            throw new InvalidOperationException($"widgets nested deeper than {MaxPanelDepth} levels (a widget containing itself?)");
        var widgets = new List<WidgetSpec>();
        if (cl.IsTable(index))
        {
            long n = cl.RawLen(index);
            for (long i = 1; i <= n; i++)
            {
                cl.RawGetInteger(index, i);
                if (cl.IsTable(-1)) widgets.Add(ToWidgetSpec(cl, cl.GetTop(), depth));
                cl.Pop(1);
            }
        }
        return widgets;
    }

    private WidgetSpec ToWidgetSpec(NativeLua cl, int w, int depth)
    {
        // Each nesting level holds a couple of slots (the list item, the children table) and
        // the parsing below needs up to ~4 more transiently (table, key, value, pushed key).
        // A binding is only promised LUA_MINSTACK (20) slots, so grow before every level.
        if (!cl.CheckStack(8)) throw new InvalidOperationException("Lua stack exhausted while reading the panel");
        // 'action' (button) / 'onClick' (colorgrid cell) / 'onSubmit' (input) /
        // 'onRowClick' (list/table row, 1.15) — first function found is registered under an
        // opaque id the host calls back with. One slot, because a widget has at most one of
        // these; a clickable row reports through the choice path like a bound buttonrow.
        string? actionId = null;
        foreach (string key in ActionKeys)
        {
            RawField(cl, w, key);
            if (cl.IsFunction(-1))
            {
                actionId = "a" + _nextActionId++;
                _actions[actionId] = cl.Ref(LuaRegistry.Index);   // pops
                _buildingActions?.Add(actionId);
                break;
            }
            cl.Pop(1);
        }
        // 'onHover' (colorgrid, 1.6) is a SECOND callback on the same widget.
        string? hoverId = null;
        RawField(cl, w, "onHover");
        if (cl.IsFunction(-1))
        {
            hoverId = "a" + _nextActionId++;
            _actions[hoverId] = cl.Ref(LuaRegistry.Index);        // pops
            _buildingActions?.Add(hoverId);
        }
        else cl.Pop(1);

        // 'onRightClick' (colorgrid / button / buttonrow, 1.9) is a THIRD callback: the
        // secondary activation — right-click on the desktop, long-press on the phone.
        // 'onRowMenu' (list/table, 1.18) is the same slot in row clothing: a right-clicked
        // row fires it through the choice path, and its RETURN may be a context menu.
        string? contextId = null;
        foreach (string key in new[] { "onRightClick", "onRowMenu" })
        {
            RawField(cl, w, key);
            if (cl.IsFunction(-1))
            {
                contextId = "a" + _nextActionId++;
                _actions[contextId] = cl.Ref(LuaRegistry.Index);      // pops
                _buildingActions?.Add(contextId);
                break;
            }
            cl.Pop(1);
        }

        // colorgrid palette: { ["char"] = "#RRGGBB", ... }
        Dictionary<string, string>? palette = null;
        RawField(cl, w, "palette");
        if (cl.IsTable(-1))
        {
            palette = new Dictionary<string, string>(StringComparer.Ordinal);
            int pal = cl.GetTop();
            cl.PushNil();
            while (cl.Next(pal))
            {
                string? key = LuaHost.ToStringLoose(cl, cl.GetTop() - 1);
                string? val = LuaHost.ToStringLoose(cl, cl.GetTop());
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val)) palette[key] = val;
                cl.Pop(1);
            }
        }
        cl.Pop(1);

        // colorgrid micro-icons (API 1.8): { ["char"] = "glyph-name", ... }
        Dictionary<string, string>? iconMap = null;
        RawField(cl, w, "icons");
        if (cl.IsTable(-1))
        {
            iconMap = new Dictionary<string, string>(StringComparer.Ordinal);
            int ic = cl.GetTop();
            cl.PushNil();
            while (cl.Next(ic))
            {
                string? key = LuaHost.ToStringLoose(cl, cl.GetTop() - 1);
                string? val = LuaHost.ToStringLoose(cl, cl.GetTop());
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val)) iconMap[key] = val;
                cl.Pop(1);
            }
        }
        cl.Pop(1);

        // colorgrid image tiles (API 1.17): { ["char"] = "tiles/tower.png", ... }. Paths are
        // taken RELATIVE TO THE PLUGIN'S OWN FOLDER and resolved here, so hosts only ever see
        // absolute paths into the declaring plugin's files; anything that escapes the folder
        // (an absolute path, a ".." climb) is dropped on the floor — the cell then falls back
        // to its palette tile, which is also what an honest typo gets.
        Dictionary<string, string>? imageMap = null;
        RawField(cl, w, "images");
        if (cl.IsTable(-1))
        {
            string root = System.IO.Path.GetFullPath(_descriptor.FolderPath);
            int im = cl.GetTop();
            cl.PushNil();
            while (cl.Next(im))
            {
                string? key = LuaHost.ToStringLoose(cl, cl.GetTop() - 1);
                string? val = LuaHost.ToStringLoose(cl, cl.GetTop());
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    string full;
                    try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, val)); }
                    catch (ArgumentException) { full = ""; }   // hostile characters: not a path
                    if (full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        imageMap ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        imageMap[key] = full;
                    }
                }
                cl.Pop(1);
            }
        }
        cl.Pop(1);

        // buttonrow children: buttons = { {text=, action=fn}, ... }
        List<WidgetSpec>? children = null;
        RawField(cl, w, "buttons");
        if (cl.IsTable(-1)) children = ToWidgetList(cl, cl.GetTop(), depth + 1);
        cl.Pop(1);

        // row-container children (API 1.8): widgets = { <any widget specs> }
        if (children is null)
        {
            RawField(cl, w, "widgets");
            if (cl.IsTable(-1)) children = ToWidgetList(cl, cl.GetTop(), depth + 1);
            cl.Pop(1);
        }

        // table columns: columns = { "Item", "Qty", "Price" }
        List<string>? columns = null;
        RawField(cl, w, "columns");
        if (cl.IsTable(-1))
        {
            columns = new List<string>();
            int colsIdx = cl.GetTop();
            long n = cl.RawLen(colsIdx);
            for (long i = 1; i <= n; i++)
            {
                cl.RawGetInteger(colsIdx, i);
                columns.Add(LuaHost.ToStringLoose(cl, cl.GetTop()) ?? "");
                cl.Pop(1);
            }
        }
        cl.Pop(1);

        // colorgrid cell-size ceiling (API 1.8): cell = 24 doubles the compact default
        RawField(cl, w, "cell");
        double cellMax = cl.Type(-1) == LuaType.Number ? cl.ToNumber(-1) : 0;
        cl.Pop(1);

        // input field width in characters (API 1.23): width = 10
        RawField(cl, w, "width");
        int width = cl.Type(-1) == LuaType.Number ? (int)cl.ToNumber(-1) : 0;
        cl.Pop(1);

        return new WidgetSpec
        {
            Type = Field(cl, w, "type") ?? "label",
            Text = Field(cl, w, "text"),
            Bind = Field(cl, w, "bind"),
            Value = Field(cl, w, "value"),
            Max = Field(cl, w, "max"),
            Color = Field(cl, w, "color"),
            Dim = FieldBool(cl, w, "dim", defaultValue: false),
            Weave = FieldBool(cl, w, "weave", defaultValue: false),
            Palette = palette,
            Icons = iconMap,
            Images = imageMap,
            Cell = cellMax,
            Width = width,
            Columns = columns,
            Separator = Field(cl, w, "separator"),
            Labels = Field(cl, w, "labels"),
            Align = Field(cl, w, "align"),
            Action = actionId,
            HoverAction = hoverId,
            ContextAction = contextId,
            Children = children,
        };
    }

    private static readonly string[] ActionKeys = { "action", "onClick", "onSubmit", "onRowClick" };

    /// <summary>t[key] as a loose string, or null when nil — the MoonSharp Field twin.
    /// <paramref name="tblIndex"/> must be absolute.</summary>
    private static string? Field(NativeLua cl, int tblIndex, string key)
    {
        RawField(cl, tblIndex, key);
        string? v = LuaHost.ToStringLoose(cl, cl.GetTop());
        cl.Pop(1);
        return v;
    }

    /// <summary>t[key] as a boolean when it IS one; otherwise the default — matches the
    /// MoonSharp runtime's "Type == Boolean &amp;&amp; Boolean" checks (a truthy string
    /// does not count).</summary>
    private static bool FieldBool(NativeLua cl, int tblIndex, string key, bool defaultValue)
    {
        RawField(cl, tblIndex, key);
        bool result = cl.Type(-1) == LuaType.Boolean ? cl.ToBoolean(-1) : defaultValue;
        cl.Pop(1);
        return result;
    }

    /// <summary>Push <c>t[key]</c> WITHOUT metamethods (<c>lua_rawget</c>). <c>lua_getfield</c>
    /// would run a script's <c>__index</c> inside this managed binding, and a Lua error there
    /// longjmps straight through the managed frames — undefined behaviour. API arguments are
    /// plain data tables; a metatable on one is ignored. <paramref name="tblIndex"/> must be
    /// absolute (the key is pushed first). Needs one free slot beyond the result.</summary>
    private static void RawField(NativeLua cl, int tblIndex, string key)
    {
        cl.PushString(key);
        cl.RawGet(tblIndex);   // pops the key, pushes the value
    }

    // ---- scrye.data ----------------------------------------------------------

    /// <summary>Push the manifest's declared data files as a Lua table. Problems are
    /// printed rather than thrown, same as MoonSharp: a plugin with a malformed word list
    /// should still load and be able to say so.</summary>
    private void BuildData(NativeLua l)
    {
        l.NewTable();
        foreach (KeyValuePair<string, object?> kv in PluginAssets.Load(
                     _descriptor.FolderPath, _descriptor.Manifest.Data, msg => _host.Print(Id, msg)))
        {
            PushClrValue(l, kv.Value);
            l.SetField(-2, kv.Key);
        }
    }

    /// <summary>Object graph (dictionary / list / string / double / bool / null) onto the
    /// stack. Lists become 1-based arrays, which is what a Lua author expects from JSON.
    /// Integral doubles push as Lua INTEGERS: JSON parsing hands us doubles, and on 5.4 a
    /// room number that arrives as 2.0 would <c>tostring()</c> to "2.0" where MoonSharp
    /// (doubles-only) printed "2" — the integer subtype is the parity-preserving choice,
    /// and what an author expects to feed <c>string.format("%d", …)</c>.</summary>
    private static void PushClrValue(NativeLua l, object? v)
    {
        // One level of nesting holds a table plus the value being built: make sure both fit.
        if (!l.CheckStack(3)) throw new InvalidOperationException("scrye.data nested too deeply for the Lua stack");
        switch (v)
        {
            case null: l.PushNil(); break;
            case string s: l.PushString(s); break;
            case bool b: l.PushBoolean(b); break;
            case double d:
                if (double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) <= 9007199254740992d)
                    l.PushInteger((long)d);
                else
                    l.PushNumber(d);
                break;
            case int i: l.PushInteger(i); break;
            case long i: l.PushInteger(i); break;
            case IReadOnlyList<object?> list:
                l.NewTable();
                for (int i = 0; i < list.Count; i++)
                {
                    PushClrValue(l, list[i]);
                    l.RawSetInteger(-2, i + 1);
                }
                break;
            case IReadOnlyDictionary<string, object?> map:
                l.NewTable();
                foreach (KeyValuePair<string, object?> kv in map)
                {
                    PushClrValue(l, kv.Value);
                    l.SetField(-2, kv.Key);
                }
                break;
            default: l.PushString(v.ToString() ?? ""); break;
        }
    }
}

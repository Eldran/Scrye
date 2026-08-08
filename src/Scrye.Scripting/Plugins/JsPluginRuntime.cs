using Jint;
using Jint.Native;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Runs one plugin whose entry script is JavaScript, using a sandboxed Jint
/// <see cref="Engine"/> with a bound <c>scrye.*</c> API object backed by an
/// <see cref="IPluginHost"/>. This mirrors <c>KeraLuaPluginRuntime</c> feature-for-feature
/// (hooks, timers, rules, panels) so a plugin author picks Lua or JS by the manifest's
/// <c>lang</c> field with no change in capabilities. Plugin rules live HERE (not in the
/// shared automation engine), so a live rule-reload can't wipe them. All execution is on the
/// session loop thread, so the per-plugin engine is never re-entered concurrently.
/// </summary>
public sealed class JsPluginRuntime : IPluginRuntime
{
    private sealed class PluginRule
    {
        public CompiledPattern Pattern = null!;
        public string? Send;
        public JsValue? Run;
    }

    private readonly PluginDescriptor _descriptor;
    private readonly IPluginHost _host;
    private readonly PluginDiagnostics? _diagnostics;
    private readonly Engine _engine;

    private readonly List<JsValue> _lineHooks = new();
    private readonly List<(string chan, JsValue fn)> _channelHooks = new();
    private readonly List<(string pkg, JsValue fn)> _gmcpHooks = new();
    private readonly List<JsValue> _connectHooks = new();
    private readonly List<JsValue> _disconnectHooks = new();
    private readonly List<JsValue> _promptHooks = new();
    private readonly List<JsValue> _idleHooks = new();
    private readonly List<JsValue> _commandHooks = new();                 // scrye.onCommand (1.6)
    private readonly List<(string name, JsValue fn)> _eventHooks = new(); // scrye.on (1.6)
    private readonly List<PluginRule> _triggers = new();   // match output lines
    private readonly List<PluginRule> _aliases = new();    // match user input
    private readonly Dictionary<string, JsValue> _actions = new();   // panel-button callbacks by id
    private readonly List<IDisposable> _subscriptions = new();
    private readonly TimerWheel _timers = new();
    private readonly VariableStore _vars = new();          // for %-expansion in rule 'send'
    private int _nextActionId = 1;

    public string Id => _descriptor.Manifest.Id;

    public string EngineName => "JS (Jint)";

    /// <param name="diagnostics">Failure/cost accounting. Optional so a headless host or a test
    /// can construct a runtime without one; when supplied, every swallowed callback exception is
    /// reported to it, which is what makes repeated-failure quarantine possible.</param>
    public JsPluginRuntime(PluginDescriptor descriptor, IPluginHost host, PluginDiagnostics? diagnostics = null)
    {
        _descriptor = descriptor;
        _host = host;
        _diagnostics = diagnostics;
        // Default Jint Engine is sandboxed: no CLR access, no file/network, and we never
        // call AllowClr(). Only the scrye API object is reachable from script.
        _engine = new Engine();
        _engine.SetValue("scrye", BuildApi());
    }

    /// <summary>Read and run the entry script (registers the plugin's hooks). Throws on script error.</summary>
    public void Load()
    {
        string code = File.ReadAllText(_descriptor.EntryPath);
        _engine.Execute(code);
    }

    /// <summary>Run a line through this plugin: fire <c>onLine</c> hooks (which may gag or
    /// rewrite it) and evaluate plugin triggers. Returns whether to gag, and a rewritten
    /// string if one was produced. Triggers match the ORIGINAL text.</summary>
    public (bool Gag, string? Rewrite) ProcessLine(string text)
    {
        bool gag = false;
        string current = text;
        for (int i = 0; i < _lineHooks.Count; i++)
        {
            JsValue? r = SafeCall("onLine", _lineHooks[i], current);
            if (r is null) continue;
            if (r.IsBoolean() && !r.AsBoolean()) gag = true;      // return false -> gag
            else if (r.IsString()) current = r.AsString();        // return "text" -> rewrite
        }
        for (int i = 0; i < _triggers.Count; i++)
        {
            MatchResult? m = _triggers[i].Pattern.Match(text);
            if (m is not null) Apply(_triggers[i], m);
        }
        return (gag, current != text ? current : null);
    }

    /// <summary>Run user input through this plugin's aliases. Returns (consumed, rewrite):
    /// the first matching alias consumes the input.</summary>
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
        if (rule.Run is not null)
            Safe("rule", () => _engine.Invoke(rule.Run!, m.Wildcards.Cast<object>().ToArray()));
    }

    /// <summary>Fire <c>onChannel</c> hooks for a structured MIP chat message.</summary>
    public void DispatchChannel(string channel, string message)
    {
        for (int i = 0; i < _channelHooks.Count; i++)
        {
            (string chan, JsValue fn) = _channelHooks[i];
            if (chan.Length == 0 || string.Equals(chan, channel, StringComparison.OrdinalIgnoreCase))
                Safe("onChannel", () => _engine.Invoke(fn, channel, message));
        }
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _gmcpHooks.Count; i++)
        {
            (string pkg, JsValue fn) = _gmcpHooks[i];
            if (pkg.Length == 0 || string.Equals(pkg, package, StringComparison.OrdinalIgnoreCase))
                Safe("onGmcp", () => _engine.Invoke(fn, json, package));
        }
    }

    /// <summary>Advance this plugin's timers (called on the session loop each tick).</summary>
    public void Tick(double dtSeconds) => _timers.Tick(dtSeconds);

    public void DispatchConnect() => FireAll(_connectHooks, "onConnect");
    public void DispatchDisconnect() => FireAll(_disconnectHooks, "onDisconnect");
    public void DispatchPrompt() => FireAll(_promptHooks, "onPrompt");
    public void DispatchIdle() => FireAll(_idleHooks, "onIdle");

    /// <summary>Fire <c>scrye.onCommand</c> hooks for a command that went to the MUD (observe-only).</summary>
    public void DispatchCommand(string text)
    {
        for (int i = 0; i < _commandHooks.Count; i++)
        {
            JsValue fn = _commandHooks[i];
            Safe("onCommand", () => _engine.Invoke(fn, text));
        }
    }

    /// <summary>Fire matching <c>scrye.on(name, fn)</c> handlers for an inter-plugin event.</summary>
    public void DispatchPluginEvent(string name, string data, string sourceId)
    {
        for (int i = 0; i < _eventHooks.Count; i++)
        {
            (string hookName, JsValue fn) = _eventHooks[i];
            if (string.Equals(hookName, name, StringComparison.OrdinalIgnoreCase))
                Safe("on:" + name, () => _engine.Invoke(fn, data, name, sourceId));
        }
    }

    /// <summary>Invoke a panel-button callback by its action id (called on the loop thread).</summary>
    public void InvokeAction(string actionId)
    {
        if (_actions.TryGetValue(actionId, out JsValue? fn))
            Safe("action", () => _engine.Invoke(fn!));
    }

    /// <summary>Invoke a colorgrid cell-click callback with (col, row, char).</summary>
    public void InvokeCellAction(string actionId, int col, int row, string ch)
    {
        if (_actions.TryGetValue(actionId, out JsValue? fn))
            Safe("cellAction", () => _engine.Invoke(fn!, col, row, ch));
    }

    /// <summary>Invoke an input widget's submit callback with the entered text.</summary>
    public void InvokeSubmit(string actionId, string text)
    {
        if (_actions.TryGetValue(actionId, out JsValue? fn))
            Safe("submit", () => _engine.Invoke(fn!, text));
    }

    private void FireAll(List<JsValue> hooks, string what)
    {
        for (int i = 0; i < hooks.Count; i++)
            Safe(what, () => _engine.Invoke(hooks[i]));
    }

    public void Dispose()
    {
        foreach (IDisposable sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        _timers.Clear();
        _lineHooks.Clear();
        _channelHooks.Clear();
        _gmcpHooks.Clear();
        _connectHooks.Clear();
        _disconnectHooks.Clear();
        _promptHooks.Clear();
        _idleHooks.Clear();
        _commandHooks.Clear();
        _eventHooks.Clear();
        _triggers.Clear();
        _aliases.Clear();
        _actions.Clear();
        // Jint's Engine holds no unmanaged resources we must release; letting it be
        // collected is sufficient (and avoids a hard dependency on Engine being IDisposable).
    }

    // ---- the scrye.* API object ----------------------------------------------

    private object BuildApi() => new
    {
        id = Id,

        // scrye.data.<key> — the manifest's declared data files, already parsed
        data = BuildData(),

        print = (Action<string>)(s => _host.Print(Id, s ?? "")),
        send  = (Action<string>)(s => _host.Send(s ?? "")),
        log   = (Action<string>)(s => _host.Log(Id, s ?? "")),

        getVariable = (Func<string, string>)(name => _host.GetVariable(name) ?? ""),
        setVariable = (Action<string, string>)((name, value) => _host.SetVariable(name, value ?? "")),
        getState    = (Func<string, string>)(path => _host.GetState(path)),
        setState    = (Action<string, string>)((path, value) => _host.SetState(path, value ?? "")),

        // scrye.watch(path, function(value, path) { ... })
        watch = (Action<string, JsValue>)((path, fn) =>
        {
            if (IsFn(fn))
                _subscriptions.Add(_host.WatchState(path, (p, v) =>
                    Safe("watch", () => _engine.Invoke(fn, v, p))));
        }),

        // routing + alerts (parity with trigger CapturePane / Sound / Notify)
        capture = (Action<string, string>)((pane, text) => _host.Capture(Id, pane ?? "", text ?? "")),
        sound   = (Action<string>)(s => _host.PlaySound(s ?? "")),
        notify  = (Action<string>)(s => _host.Notify(Id, s ?? "")),

        // timers: scrye.after(seconds, fn) -> id (one-shot); scrye.every(seconds, fn) -> id (repeating)
        after  = (Func<double, JsValue, double>)((secs, fn) => AddTimer(secs, fn, repeat: false)),
        every  = (Func<double, JsValue, double>)((secs, fn) => AddTimer(secs, fn, repeat: true)),
        cancel = (Action<double>)(id => _timers.Cancel((int)id)),

        // lifecycle hooks
        onConnect    = (Action<JsValue>)(fn => AddHook(fn, _connectHooks)),
        onDisconnect = (Action<JsValue>)(fn => AddHook(fn, _disconnectHooks)),
        onPrompt     = (Action<JsValue>)(fn => AddHook(fn, _promptHooks)),
        // scrye.onIdle(fn) — the client decided nobody is here. Stop what you are driving.
        onIdle       = (Action<JsValue>)(fn => AddHook(fn, _idleHooks)),
        // scrye.onCommand(fn) — observe every command sent to the MUD, whatever sent it (1.6).
        // Observe-only; do not scrye.send from inside a handler (it re-fires every hook).
        onCommand    = (Action<JsValue>)(fn => AddHook(fn, _commandHooks)),

        // inter-plugin events (1.6): scrye.emit(name, data) / scrye.on(name, (data, name, source) => {})
        emit = (Action<string, string>)((name, data) => _host.EmitEvent(Id, name ?? "", data ?? "")),
        on = (Action<JsValue, JsValue>)((name, fn) =>
        {
            if (name.IsString() && IsFn(fn)) _eventHooks.Add((name.AsString(), fn));
        }),

        // scrye.json (1.6): parity with the Lua runtime so cross-language snippets translate
        // one-for-one. JS plugins can equally use the global JSON object — these are the same
        // engine functions. decode returns null (not a throw) on malformed input, matching
        // Lua's nil,err convention as closely as JS return values allow.
        json = (object)new
        {
            encode = (Func<JsValue, JsValue>)(v =>
            {
                try { return _engine.Invoke(_engine.Evaluate("JSON.stringify"), v); }
                catch { return JsValue.Null; }
            }),
            decode = (Func<string, JsValue>)(s =>
            {
                try { return _engine.Invoke(_engine.Evaluate("JSON.parse"), s ?? ""); }
                catch { return JsValue.Null; }
            }),
        },

        // scrye.onLine(function(line) { ... })  — return false to gag, a string to rewrite
        onLine = (Action<JsValue>)(fn => AddHook(fn, _lineHooks)),

        // scrye.onChannel(fn)  OR  scrye.onChannel("Party", fn) — MIP chat messages as
        // (channel, message); tells arrive with channel "Tell".
        onChannel = (Action<JsValue, JsValue>)((a, b) =>
        {
            if (a.IsString() && IsFn(b)) _channelHooks.Add((a.AsString(), b));
            else if (IsFn(a)) _channelHooks.Add(("", a));
        }),

        // scrye.onGmcp(fn)  OR  scrye.onGmcp("Char.Vitals", fn)
        onGmcp = (Action<JsValue, JsValue>)((a, b) =>
        {
            if (a.IsString() && IsFn(b)) _gmcpHooks.Add((a.AsString(), b));
            else if (IsFn(a)) _gmcpHooks.Add(("", a));
        }),

        // rules: scrye.addTrigger({ pattern, regex, ignoreCase, send, run })
        //        scrye.addAlias({ ... })  (matches typed input; a match consumes it)
        addTrigger = (Action<JsValue>)(def => AddRule(def, _triggers)),
        addAlias   = (Action<JsValue>)(def => AddRule(def, _aliases)),

        // scrye.addPanel({ title, widgets: [ { type, ... }, ... ] })
        addPanel = (Action<JsValue>)(def =>
        {
            if (def.IsObject()) _host.AddPanel(Id, ToPanelSpec(def));
        }),

        // persistent per-plugin storage (survives sessions and restarts):
        //   scrye.store.get(key) -> string|null    scrye.store.set(key, value)
        //   scrye.store.delete(key)                scrye.store.keys() -> [k1, k2, ...]
        store = (object)new
        {
            get = (Func<string, object?>)(key => _host.StoreGet(Id, key ?? "")),
            set = (Action<string, string>)((key, value) => _host.StoreSet(Id, key ?? "", value ?? "")),
            // scrye.store.setMany({ k1: v1, ... }) — N keys, ONE disk write (1.6). Values are
            // stringified. Round-trips through the engine's JSON.stringify, the same
            // enumeration trick ToPalette uses.
            setMany = (Action<JsValue>)(obj =>
            {
                if (obj is null || !obj.IsObject()) return;
                try
                {
                    JsValue stringify = _engine.Evaluate("JSON.stringify");
                    JsValue json = _engine.Invoke(stringify, obj);
                    if (!json.IsString()) return;
                    var raw = System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json.AsString());
                    if (raw is null || raw.Count == 0) return;
                    var batch = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach ((string k, System.Text.Json.JsonElement v) in raw)
                        batch[k] = v.ValueKind == System.Text.Json.JsonValueKind.String
                            ? v.GetString() ?? "" : v.GetRawText();
                    _host.StoreSetMany(Id, batch);
                }
                catch (Exception ex) { _host.Print(Id, "store.setMany: " + ex.Message); }
            }),
            @delete = (Action<string>)(key => _host.StoreDelete(Id, key ?? "")),
            keys = (Func<string[]>)(() => _host.StoreKeys(Id)),
        },
    };

    private void AddRule(JsValue def, List<PluginRule> into)
    {
        if (!def.IsObject()) return;
        string pattern = Str(def, "pattern") ?? "";
        if (pattern.Length == 0) { _host.Print(Id, "addRule: missing 'pattern'"); return; }

        bool isRegex = Bool(def, "regex", false);
        bool ignoreCase = Bool(def, "ignoreCase", true);   // default true
        JsValue run = Get(def, "run");

        try
        {
            into.Add(new PluginRule
            {
                Pattern = new CompiledPattern(pattern, isRegex, ignoreCase),
                Send = Str(def, "send"),
                Run = IsFn(run) ? run : null,
            });
        }
        catch (Exception ex) { _host.Print(Id, "addRule: bad pattern — " + ex.Message); }
    }

    private double AddTimer(double seconds, JsValue fn, bool repeat)
    {
        if (!IsFn(fn)) return 0;
        int id = _timers.Add(seconds, repeat, () => Safe(repeat ? "every" : "after", () => _engine.Invoke(fn)));
        return id;
    }

    private static void AddHook(JsValue fn, List<JsValue> hooks)
    {
        if (IsFn(fn)) hooks.Add(fn);
    }

    private PanelSpec ToPanelSpec(JsValue tbl)
    {
        var widgets = ToWidgetList(Get(tbl, "widgets"));

        // tabbed panel: tabs: [ { title, widgets: [...] }, ... ]
        var tabs = new List<PanelTabSpec>();
        JsValue tv = Get(tbl, "tabs");
        if (tv.IsObject())
        {
            int len = (int)ToNum(Get(tv, "length"));
            for (int i = 0; i < len; i++)
            {
                JsValue item = tv.AsObject().Get(i.ToString());
                if (item.IsObject())
                    tabs.Add(new PanelTabSpec
                    {
                        Title = Str(item, "title") ?? $"Tab {i + 1}",
                        Widgets = ToWidgetList(Get(item, "widgets")),
                    });
            }
        }

        return new PanelSpec
        {
            Title = Str(tbl, "title") ?? "",
            Widgets = widgets,
            Tabs = tabs,
            Width = ToNum(Get(tbl, "width")),
            Background = Str(tbl, "background"),
            Accent = Str(tbl, "accent"),
            Foreground = Str(tbl, "color"),
        };
    }

    private List<WidgetSpec> ToWidgetList(JsValue w)
    {
        var widgets = new List<WidgetSpec>();
        if (w.IsObject())
        {
            int len = (int)ToNum(Get(w, "length"));
            for (int i = 0; i < len; i++)
            {
                JsValue item = w.AsObject().Get(i.ToString());
                if (item.IsObject()) widgets.Add(ToWidgetSpec(item));
            }
        }
        return widgets;
    }

    /// <summary>The manifest's declared data files as a native JS value. Goes in through the
    /// engine's own JSON.parse — the mirror of <see cref="ToPalette"/>'s trick — so the script
    /// gets real objects and arrays rather than wrapped CLR collections. A failure here yields an
    /// empty object: a plugin whose data is broken should still load and be able to say so.</summary>
    private JsValue BuildData()
    {
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(PluginAssets.Load(
                _descriptor.FolderPath, _descriptor.Manifest.Data, msg => _host.Print(Id, msg)));
            JsValue parse = _engine.Evaluate("JSON.parse");
            return _engine.Invoke(parse, json);
        }
        catch
        {
            return _engine.Evaluate("({})");
        }
    }

    /// <summary>Read a JS palette object as char→"#RRGGBB". Round-trips through the
    /// engine's own JSON.stringify — the one enumeration API guaranteed public.</summary>
    private Dictionary<string, string>? ToPalette(JsValue pal)
    {
        if (!pal.IsObject()) return null;
        try
        {
            JsValue stringify = _engine.Evaluate("JSON.stringify");
            JsValue json = _engine.Invoke(stringify, pal);
            if (!json.IsString()) return null;
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json.AsString());
        }
        catch { return null; }
    }

    private WidgetSpec ToWidgetSpec(JsValue w)
    {
        // 'action' (button) / 'onClick' (colorgrid) / 'onSubmit' (input) — a function stored under an id.
        string? actionId = null;
        JsValue action = Get(w, "action");
        if (!IsFn(action)) action = Get(w, "onClick");
        if (!IsFn(action)) action = Get(w, "onSubmit");
        if (IsFn(action))
        {
            actionId = "a" + _nextActionId++;
            _actions[actionId] = action;
        }
        // 'onHover' (colorgrid, 1.6): a second callback on the same widget, own id — a grid
        // can be both clickable and hoverable. Same (col, row, char) invoke path as onClick.
        string? hoverId = null;
        JsValue hover = Get(w, "onHover");
        if (IsFn(hover))
        {
            hoverId = "a" + _nextActionId++;
            _actions[hoverId] = hover;
        }
        // buttonrow children: buttons = [ {text, action}, ... ]
        JsValue btns = Get(w, "buttons");
        List<WidgetSpec>? children = btns.IsObject() ? ToWidgetList(btns) : null;
        return new WidgetSpec
        {
            Type = Str(w, "type") ?? "label",
            Text = Str(w, "text"),
            Bind = Str(w, "bind"),
            Value = Str(w, "value"),
            Max = Str(w, "max"),
            Color = Str(w, "color"),
            Dim = Get(w, "dim") is { } dv && dv.IsBoolean() && dv.AsBoolean(),
            Weave = Get(w, "weave") is { } wv && wv.IsBoolean() && wv.AsBoolean(),
            Palette = ToPalette(Get(w, "palette")),
            Columns = ToStringList(Get(w, "columns")),
            Separator = Str(w, "separator"),
            Labels = Str(w, "labels"),
            Align = Str(w, "align"),
            Action = actionId,
            HoverAction = hoverId,
            Children = children,
        };
    }

    /// <summary>A JS string array (a table's <c>columns</c>) as a CLR list; null when absent.
    /// Uses the same length + indexed-Get walk as <see cref="ToWidgetList"/> rather than Jint's
    /// array types, so it stays valid across the whole 3.x range the csproj allows.</summary>
    private List<string>? ToStringList(JsValue v)
    {
        if (v is null || !v.IsObject()) return null;
        int len = (int)ToNum(Get(v, "length"));
        if (len <= 0) return null;
        var result = new List<string>(len);
        for (int i = 0; i < len; i++)
        {
            JsValue item = v.AsObject().Get(i.ToString());
            result.Add(item.IsUndefined() || item.IsNull() ? "" : item.ToString());
        }
        return result;
    }

    // ---- JsValue helpers -----------------------------------------------------

    private static JsValue Get(JsValue obj, string key) =>
        obj.IsObject() ? obj.AsObject().Get(key) : JsValue.Undefined;

    private static string? Str(JsValue obj, string key)
    {
        JsValue v = Get(obj, key);
        if (v.IsUndefined() || v.IsNull()) return null;
        return v.IsString() ? v.AsString() : v.ToString();
    }

    private static bool Bool(JsValue obj, string key, bool dflt)
    {
        JsValue v = Get(obj, key);
        return v.IsBoolean() ? v.AsBoolean() : dflt;
    }

    private static double ToNum(JsValue v) => v.IsNumber() ? v.AsNumber() : 0;

    /// <summary>True if the value can be treated as a JS callback. Jint's callability tests
    /// (<c>ICallable</c>, <c>JsValue.IsCallable</c>) are both internal, so we use the public
    /// <see cref="JsValue.IsObject"/>: a JS function IS an object, while strings / numbers /
    /// booleans / null / undefined are not — enough to tell a callback from a plain value.
    /// A non-function object slipped in as a callback simply throws when invoked, and the
    /// invoke is wrapped in <see cref="Safe"/>/<see cref="SafeCall"/>.</summary>
    private static bool IsFn(JsValue v) => v is not null && v.IsObject();

    // Every plugin callback goes through one of these two. They swallow the exception (one bad
    // plugin must never take down line processing) but no longer swallow the *fact* of it:
    // diagnostics counts consecutive failures and the manager quarantines a plugin that is
    // failing on every line rather than letting it print the same error forever.
    private void Safe(string what, Action action)
    {
        try
        {
            action();
            _diagnostics?.RecordSuccess(Id);
        }
        catch (Exception ex)
        {
            _host.Print(Id, $"{what} error: {ex.Message}");
            _diagnostics?.RecordFailure(Id, what, ex.Message);
        }
    }

    private JsValue? SafeCall(string what, JsValue fn, params object[] args)
    {
        try
        {
            JsValue result = _engine.Invoke(fn, args);
            _diagnostics?.RecordSuccess(Id);
            return result;
        }
        catch (Exception ex)
        {
            _host.Print(Id, $"{what} error: {ex.Message}");
            _diagnostics?.RecordFailure(Id, what, ex.Message);
            return null;
        }
    }
}

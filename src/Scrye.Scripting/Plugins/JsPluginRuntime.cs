using Jint;
using Jint.Native;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Runs one plugin whose entry script is JavaScript, using a sandboxed Jint
/// <see cref="Engine"/> with a bound <c>scrye.*</c> API object backed by an
/// <see cref="IPluginHost"/>. This mirrors <see cref="LuaPluginRuntime"/> feature-for-feature
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
    private readonly Engine _engine;

    private readonly List<JsValue> _lineHooks = new();
    private readonly List<(string pkg, JsValue fn)> _gmcpHooks = new();
    private readonly List<JsValue> _connectHooks = new();
    private readonly List<JsValue> _disconnectHooks = new();
    private readonly List<JsValue> _promptHooks = new();
    private readonly List<PluginRule> _triggers = new();   // match output lines
    private readonly List<PluginRule> _aliases = new();    // match user input
    private readonly Dictionary<string, JsValue> _actions = new();   // panel-button callbacks by id
    private readonly List<IDisposable> _subscriptions = new();
    private readonly TimerWheel _timers = new();
    private readonly VariableStore _vars = new();          // for %-expansion in rule 'send'
    private int _nextActionId = 1;

    public string Id => _descriptor.Manifest.Id;

    public JsPluginRuntime(PluginDescriptor descriptor, IPluginHost host)
    {
        _descriptor = descriptor;
        _host = host;
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
        if (rule.Send is not null) _host.Send(Template.Expand(rule.Send, m, _vars));
        if (rule.Run is not null)
            Safe("rule", () => _engine.Invoke(rule.Run!, m.Wildcards.Cast<object>().ToArray()));
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

    /// <summary>Invoke a panel-button callback by its action id (called on the loop thread).</summary>
    public void InvokeAction(string actionId)
    {
        if (_actions.TryGetValue(actionId, out JsValue? fn))
            Safe("action", () => _engine.Invoke(fn!));
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
        _gmcpHooks.Clear();
        _connectHooks.Clear();
        _disconnectHooks.Clear();
        _promptHooks.Clear();
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

        print = (Action<string>)(s => _host.Print(Id, s ?? "")),
        send  = (Action<string>)(s => _host.Send(s ?? "")),

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

        // timers: scrye.after(seconds, fn) -> id (one-shot); scrye.every(seconds, fn) -> id (repeating)
        after  = (Func<double, JsValue, double>)((secs, fn) => AddTimer(secs, fn, repeat: false)),
        every  = (Func<double, JsValue, double>)((secs, fn) => AddTimer(secs, fn, repeat: true)),
        cancel = (Action<double>)(id => _timers.Cancel((int)id)),

        // lifecycle hooks
        onConnect    = (Action<JsValue>)(fn => AddHook(fn, _connectHooks)),
        onDisconnect = (Action<JsValue>)(fn => AddHook(fn, _disconnectHooks)),
        onPrompt     = (Action<JsValue>)(fn => AddHook(fn, _promptHooks)),

        // scrye.onLine(function(line) { ... })  — return false to gag, a string to rewrite
        onLine = (Action<JsValue>)(fn => AddHook(fn, _lineHooks)),

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
        // A 'button' widget with an action=function is registered as a callback and
        // referenced by an opaque id the host calls back with on click.
        string? actionId = null;
        JsValue action = Get(w, "action");
        if (IsFn(action))
        {
            actionId = "a" + _nextActionId++;
            _actions[actionId] = action;
        }
        return new WidgetSpec
        {
            Type = Str(w, "type") ?? "label",
            Text = Str(w, "text"),
            Bind = Str(w, "bind"),
            Value = Str(w, "value"),
            Max = Str(w, "max"),
            Color = Str(w, "color"),
            Palette = ToPalette(Get(w, "palette")),
            Action = actionId,
        };
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

    private void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _host.Print(Id, $"{what} error: {ex.Message}"); }
    }

    private JsValue? SafeCall(string what, JsValue fn, params object[] args)
    {
        try { return _engine.Invoke(fn, args); }
        catch (Exception ex) { _host.Print(Id, $"{what} error: {ex.Message}"); return null; }
    }
}

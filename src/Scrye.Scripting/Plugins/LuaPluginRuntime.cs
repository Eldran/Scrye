using MoonSharp.Interpreter;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Runs one plugin: its own sandboxed MoonSharp <see cref="Script"/> with a bound
/// <c>scrye.*</c> API table backed by an <see cref="IPluginHost"/>. The plugin's entry
/// script registers hooks (<c>onLine</c>/<c>onGmcp</c>/<c>watch</c>/<c>onConnect</c>…),
/// timers (<c>after</c>/<c>every</c>), and rules (<c>addTrigger</c>/<c>addAlias</c>). The
/// <see cref="PluginManager"/> feeds it session events. Plugin rules live HERE (not in the
/// shared automation engine), so a live rule-reload can't wipe them. All execution is on
/// the session loop thread, so the per-plugin Script is never re-entered concurrently.
/// </summary>
public sealed class LuaPluginRuntime : IPluginRuntime
{
    private sealed class PluginRule
    {
        public CompiledPattern Pattern = null!;
        public string? Send;
        public DynValue? Run;
    }

    private readonly PluginDescriptor _descriptor;
    private readonly IPluginHost _host;
    private readonly Script _script;

    private readonly List<DynValue> _lineHooks = new();
    private readonly List<(string pkg, DynValue fn)> _gmcpHooks = new();
    private readonly List<DynValue> _connectHooks = new();
    private readonly List<DynValue> _disconnectHooks = new();
    private readonly List<DynValue> _promptHooks = new();
    private readonly List<PluginRule> _triggers = new();   // match output lines
    private readonly List<PluginRule> _aliases = new();    // match user input
    private readonly Dictionary<string, DynValue> _actions = new();   // panel-button callbacks by id
    private readonly List<IDisposable> _subscriptions = new();
    private readonly TimerWheel _timers = new();
    private readonly VariableStore _vars = new();          // for %-expansion in rule 'send'
    private int _nextActionId = 1;

    public string Id => _descriptor.Manifest.Id;

    public LuaPluginRuntime(PluginDescriptor descriptor, IPluginHost host)
    {
        _descriptor = descriptor;
        _host = host;
        _script = new Script(CoreModules.Preset_HardSandbox);   // no io/os/file from Lua
        _script.Globals["scrye"] = BuildApi();
    }

    /// <summary>Read and run the entry script (registers the plugin's hooks). Throws on script error.</summary>
    public void Load()
    {
        string code = File.ReadAllText(_descriptor.EntryPath);
        _script.DoString(code, codeFriendlyName: Id);
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
            DynValue? r = SafeCall("onLine", _lineHooks[i], current);
            if (r is null) continue;
            if (r.Type == DataType.Boolean && !r.Boolean) gag = true;     // return false -> gag
            else if (r.Type == DataType.String) current = r.String;       // return "text" -> rewrite
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
        if (rule.Run is not null) Safe("rule", () => _script.Call(rule.Run!, m.Wildcards.ToArray()));
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _gmcpHooks.Count; i++)
        {
            (string pkg, DynValue fn) = _gmcpHooks[i];
            if (pkg.Length == 0 || string.Equals(pkg, package, StringComparison.OrdinalIgnoreCase))
                Safe("onGmcp", () => _script.Call(fn, json, package));
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
        if (_actions.TryGetValue(actionId, out DynValue? fn)) Safe("action", () => _script.Call(fn!));
    }

    private void FireAll(List<DynValue> hooks, string what)
    {
        for (int i = 0; i < hooks.Count; i++)
            Safe(what, () => _script.Call(hooks[i]));
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
    }

    // ---- the scrye.* table ---------------------------------------------------

    private Table BuildApi()
    {
        var t = new Table(_script);
        t["id"] = Id;

        t["print"] = Fn(a => { _host.Print(Id, Arg(a, 0)); return DynValue.Nil; });
        t["send"]  = Fn(a => { _host.Send(Arg(a, 0)); return DynValue.Nil; });

        t["getVariable"] = Fn(a => DynValue.NewString(_host.GetVariable(Arg(a, 0)) ?? ""));
        t["setVariable"] = Fn(a => { _host.SetVariable(Arg(a, 0), Arg(a, 1)); return DynValue.Nil; });
        t["getState"]    = Fn(a => DynValue.NewString(_host.GetState(Arg(a, 0))));

        // scrye.watch(path, function(value, path) ... end)
        t["watch"] = Fn(a =>
        {
            string path = Arg(a, 0);
            if (a.Count >= 2 && a[1].Type == DataType.Function)
            {
                DynValue fn = a[1];
                _subscriptions.Add(_host.WatchState(path, (p, v) =>
                    Safe("watch", () => _script.Call(fn, v, p))));
            }
            return DynValue.Nil;
        });

        // timers: scrye.after(seconds, fn) -> id (one-shot);  scrye.every(seconds, fn) -> id (repeating)
        t["after"] = Fn(a => AddTimer(a, repeat: false));
        t["every"] = Fn(a => AddTimer(a, repeat: true));
        t["cancel"] = Fn(a => { _timers.Cancel((int)Num(a, 0)); return DynValue.Nil; });

        // lifecycle hooks
        t["onConnect"]    = Fn(a => AddHook(a, _connectHooks));
        t["onDisconnect"] = Fn(a => AddHook(a, _disconnectHooks));
        t["onPrompt"]     = Fn(a => AddHook(a, _promptHooks));

        // scrye.onLine(function(line) ... end)  — return false to gag, a string to rewrite
        t["onLine"] = Fn(a =>
        {
            if (a.Count >= 1 && a[0].Type == DataType.Function) _lineHooks.Add(a[0]);
            return DynValue.Nil;
        });

        // scrye.onGmcp(fn)  OR  scrye.onGmcp("Char.Vitals", fn)
        t["onGmcp"] = Fn(a =>
        {
            if (a.Count == 1 && a[0].Type == DataType.Function)
                _gmcpHooks.Add(("", a[0]));
            else if (a.Count >= 2 && a[1].Type == DataType.Function)
                _gmcpHooks.Add((Arg(a, 0), a[1]));
            return DynValue.Nil;
        });

        // rules: scrye.addTrigger{ pattern=, regex=, ignoreCase=, send=, run=fn }
        //        scrye.addAlias{ ... }  (matches typed input; a match consumes it)
        t["addTrigger"] = Fn(a => AddRule(a, _triggers));
        t["addAlias"]   = Fn(a => AddRule(a, _aliases));

        // scrye.addPanel({ title=..., widgets={ {type=...,...}, ... } })
        t["addPanel"] = Fn(a =>
        {
            if (a.Count >= 1 && a[0].Type == DataType.Table)
                _host.AddPanel(Id, ToPanelSpec(a[0].Table));
            return DynValue.Nil;
        });

        return t;
    }

    private DynValue AddRule(CallbackArguments a, List<PluginRule> into)
    {
        if (a.Count < 1 || a[0].Type != DataType.Table) return DynValue.Nil;
        Table def = a[0].Table;
        string pattern = Field(def, "pattern") ?? "";
        if (pattern.Length == 0) { _host.Print(Id, "addRule: missing 'pattern'"); return DynValue.Nil; }

        DynValue regex = def.Get("regex");
        bool isRegex = regex.Type == DataType.Boolean && regex.Boolean;
        DynValue ic = def.Get("ignoreCase");
        bool ignoreCase = ic.Type != DataType.Boolean || ic.Boolean;   // default true
        DynValue run = def.Get("run");

        try
        {
            into.Add(new PluginRule
            {
                Pattern = new CompiledPattern(pattern, isRegex, ignoreCase),
                Send = Field(def, "send"),
                Run = run.Type == DataType.Function ? run : null,
            });
        }
        catch (Exception ex) { _host.Print(Id, "addRule: bad pattern — " + ex.Message); }
        return DynValue.Nil;
    }

    private DynValue AddTimer(CallbackArguments a, bool repeat)
    {
        if (a.Count >= 2 && a[1].Type == DataType.Function)
        {
            DynValue fn = a[1];
            int id = _timers.Add(Num(a, 0), repeat, () => Safe(repeat ? "every" : "after", () => _script.Call(fn)));
            return DynValue.NewNumber(id);
        }
        return DynValue.Nil;
    }

    private static DynValue AddHook(CallbackArguments a, List<DynValue> hooks)
    {
        if (a.Count >= 1 && a[0].Type == DataType.Function) hooks.Add(a[0]);
        return DynValue.Nil;
    }

    private PanelSpec ToPanelSpec(Table tbl)
    {
        var widgets = new List<WidgetSpec>();
        DynValue w = tbl.Get("widgets");
        if (w.Type == DataType.Table)
        {
            Table arr = w.Table;
            for (int i = 1; i <= arr.Length; i++)
            {
                DynValue item = arr.Get(i);
                if (item.Type == DataType.Table) widgets.Add(ToWidgetSpec(item.Table));
            }
        }
        return new PanelSpec { Title = Field(tbl, "title") ?? "", Widgets = widgets };
    }

    private WidgetSpec ToWidgetSpec(Table w)
    {
        // A 'button' widget with an action=function is registered as a callback and
        // referenced by an opaque id the host calls back with on click.
        string? actionId = null;
        DynValue action = w.Get("action");
        if (action.Type == DataType.Function)
        {
            actionId = "a" + _nextActionId++;
            _actions[actionId] = action;
        }
        return new WidgetSpec
        {
            Type = Field(w, "type") ?? "label",
            Text = Field(w, "text"),
            Bind = Field(w, "bind"),
            Value = Field(w, "value"),
            Max = Field(w, "max"),
            Color = Field(w, "color"),
            Action = actionId,
        };
    }

    private static string? Field(Table t, string key)
    {
        DynValue v = t.Get(key);
        return v.IsNil() ? null : v.CastToString();
    }

    private static DynValue Fn(Func<CallbackArguments, DynValue> f) =>
        DynValue.NewCallback((_, args) => f(args));

    private static string Arg(CallbackArguments a, int i) =>
        i < a.Count && !a[i].IsNil() ? a[i].CastToString() : "";

    private static double Num(CallbackArguments a, int i)
    {
        if (i >= a.Count || a[i].IsNil()) return 0;
        if (a[i].Type == DataType.Number) return a[i].Number;
        return double.TryParse(a[i].CastToString(), out double d) ? d : 0;
    }

    private void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _host.Print(Id, $"{what} error: {ex.Message}"); }
    }

    private DynValue? SafeCall(string what, DynValue fn, params object[] args)
    {
        try { return _script.Call(fn, args); }
        catch (Exception ex) { _host.Print(Id, $"{what} error: {ex.Message}"); return null; }
    }
}

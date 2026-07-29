using MoonSharp.Interpreter;
using Scrye.Core.Plugins;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Runs one plugin: its own sandboxed MoonSharp <see cref="Script"/> with a bound
/// <c>scrye.*</c> API table backed by an <see cref="IPluginHost"/>. The plugin's entry
/// script registers hooks (<c>scrye.onLine</c>, <c>scrye.onGmcp</c>, <c>scrye.watch</c>);
/// the <see cref="PluginManager"/> feeds it session events via <see cref="DispatchLine"/>
/// / <see cref="DispatchGmcp"/>. All execution is on the session loop thread, so the
/// per-plugin Script is never re-entered concurrently.
/// </summary>
public sealed class LuaPluginRuntime : IDisposable
{
    private readonly PluginDescriptor _descriptor;
    private readonly IPluginHost _host;
    private readonly Script _script;

    // Hook functions are stored as DynValues and invoked with plain-string args, matching
    // how LuaScriptHost calls trigger callbacks (_script.Call(fn, args)).
    private readonly List<DynValue> _lineHooks = new();
    private readonly List<(string pkg, DynValue fn)> _gmcpHooks = new();
    private readonly List<IDisposable> _subscriptions = new();

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

    public void DispatchLine(string line)
    {
        for (int i = 0; i < _lineHooks.Count; i++)
        {
            DynValue fn = _lineHooks[i];
            Safe("onLine", () => _script.Call(fn, line));
        }
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

    public void Dispose()
    {
        foreach (IDisposable sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        _lineHooks.Clear();
        _gmcpHooks.Clear();
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

        // scrye.onLine(function(line) ... end)
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

        return t;
    }

    private static DynValue Fn(Func<CallbackArguments, DynValue> f) =>
        DynValue.NewCallback((_, args) => f(args));

    private static string Arg(CallbackArguments a, int i) =>
        i < a.Count && !a[i].IsNil() ? a[i].CastToString() : "";

    private void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _host.Print(Id, $"{what} error: {ex.Message}"); }
    }
}

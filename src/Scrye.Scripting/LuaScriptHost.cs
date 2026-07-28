using MoonSharp.Interpreter;

namespace Scrye.Scripting;

/// <summary>
/// MoonSharp-backed Lua host. Binds an <see cref="IWorldApi"/> to the Lua global
/// <c>world</c>. Runs on the owning session's loop, so scripts never execute
/// concurrently with trigger processing (see the architecture doc, §5).
/// </summary>
public sealed class LuaScriptHost : IScriptHost
{
    private readonly Script _script;

    public LuaScriptHost(IWorldApi world)
    {
        // Hardened sandbox preset: no io/os/file access by default.
        _script = new Script(CoreModules.Preset_HardSandbox);
        UserData.RegisterType<IWorldApi>();
        _script.Globals["world"] = world;
    }

    public void Execute(string code) => _script.DoString(code);

    public void CallFunction(string name, params object[] args)
    {
        DynValue fn = _script.Globals.Get(name);
        if (fn.Type == DataType.Function)
            _script.Call(fn, args);
    }
}

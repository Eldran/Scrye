using Scrye.Scripting.Lua;
using NativeLua = KeraLua.Lua;

namespace Scrye.Scripting;

/// <summary>
/// Native Lua 5.4 (KeraLua) host for world scripting. Binds an <see cref="IWorldApi"/> to
/// the Lua global <c>world</c> as explicit closures — no reflection binding, same style as
/// the plugin runtime's <c>scrye.*</c> table. Runs on the owning session's loop, so scripts
/// never execute concurrently with trigger processing (see the architecture doc, §5).
/// Sandboxed by <see cref="LuaSandbox"/>: no io/os-process/require/load/debug.
/// </summary>
public sealed class LuaScriptHost : IScriptHost, IDisposable
{
    private readonly LuaHost _lua;

    public LuaScriptHost(IWorldApi world)
    {
        _lua = new LuaHost();
        LuaSandbox.Apply(_lua);
        _lua.EnableDispatchBudget();   // world scripts get the same runaway seatbelt

        NativeLua l = _lua.State;
        l.NewTable();                                            // world
        Bind(l, "Send", cl => { world.Send(LuaHost.ArgString(cl, 1)); return 0; });
        Bind(l, "Note", cl => { world.Note(LuaHost.ArgString(cl, 1)); return 0; });
        Bind(l, "GetVariable", cl =>
        {
            string? v = world.GetVariable(LuaHost.ArgString(cl, 1));
            if (v is null) cl.PushNil(); else cl.PushString(v);
            return 1;
        });
        Bind(l, "SetVariable", cl =>
        {
            world.SetVariable(LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2));
            return 0;
        });
        Bind(l, "AddTrigger", cl =>
        {
            world.AddTrigger(LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2), LuaHost.ArgString(cl, 3));
            return 0;
        });
        Bind(l, "AddAlias", cl =>
        {
            world.AddAlias(LuaHost.ArgString(cl, 1), LuaHost.ArgString(cl, 2), LuaHost.ArgString(cl, 3));
            return 0;
        });
        Bind(l, "DeleteTrigger", cl => { cl.PushBoolean(world.DeleteTrigger(LuaHost.ArgString(cl, 1))); return 1; });
        Bind(l, "DeleteAlias",   cl => { cl.PushBoolean(world.DeleteAlias(LuaHost.ArgString(cl, 1))); return 1; });
        l.SetGlobal("world");
    }

    /// <summary>Set <c>world[name]</c> to a boundary-safe binding (see
    /// <see cref="LuaHost"/>'s longjmp/exception rules). The table must be on top.</summary>
    private void Bind(NativeLua l, string name, Func<NativeLua, int> body)
    {
        _lua.PushCallback(ptr =>
        {
            NativeLua cl = NativeLua.FromIntPtr(ptr);
            return LuaHost.Protect(cl, () => body(cl));
        });
        l.SetField(-2, name);
    }

    /// <summary>Execute a chunk. Throws <see cref="LuaHostException"/> on load or runtime
    /// error, matching the previous host's throw-on-error contract.</summary>
    public void Execute(string code) => _lua.DoText(code, "script");

    /// <summary>Call a named global function, if defined. Arguments arrive as strings
    /// (trigger wildcards). Throws <see cref="LuaHostException"/> on script error.</summary>
    public void CallFunction(string name, params object[] args)
    {
        NativeLua l = _lua.State;
        l.GetGlobal(name);
        if (!l.IsFunction(-1)) { l.Pop(1); return; }
        foreach (object a in args) l.PushString(a?.ToString() ?? "");
        if (!_lua.PCall(args.Length, 0, out string? error))
            throw new LuaHostException(error ?? $"error calling {name}");
    }

    public void Dispose() => _lua.Dispose();
}

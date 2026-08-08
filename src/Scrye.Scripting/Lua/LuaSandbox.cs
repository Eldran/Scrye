using NativeLua = KeraLua.Lua;

namespace Scrye.Scripting.Lua;

/// <summary>
/// Reduces a freshly-opened native Lua 5.4 state to the surface MoonSharp's soft sandbox
/// gave Scrye plugins: base library + tables, strings, math, utf8, coroutines, error
/// handling, metatables, and a curated <c>os</c> with time functions only. NO io, NO
/// process/filesystem os, NO require/package, NO load/dofile, NO debug.
///
/// <para>KeraLua exposes <c>luaL_openlibs</c> but not the individual <c>luaopen_*</c>
/// pointers, so the allowlist is enforced by opening everything and then removing what is
/// not allowed — the end state is the same; what matters is the list below, which is
/// written as the authoritative inventory. Chunk loading is separately restricted to text
/// mode by <see cref="LuaHost.DoText"/>, so with <c>load</c>/<c>string.dump</c> gone there
/// is no path to bytecode in or out.</para>
///
/// <para>Differences from MoonSharp's soft sandbox, on purpose:</para>
/// <list type="bullet">
/// <item><c>utf8</c> is present (native 5.4 has it; harmless, useful).</item>
/// <item><c>bit32</c> is absent — 5.4 removed it in favour of bitwise operators. No bundled
/// plugin uses it (audited 2026-08-06); a compat shim can be injected here if a user plugin
/// ever needs one.</item>
/// <item><c>print</c> is a no-op stub rather than stdout — the plugin runtime rebinds it to
/// <c>scrye.print</c> so stray debug prints land in the world output instead of nowhere.</item>
/// </list>
/// </summary>
public static class LuaSandbox
{
    /// <summary>Globals removed outright. <c>collectgarbage</c> goes too: a plugin has no
    /// business steering the GC of a state it shares with nobody, and 'count' would only
    /// feed cargo-cult memory tuning.</summary>
    private static readonly string[] RemovedGlobals =
    {
        "io", "package", "debug",
        "require", "dofile", "loadfile", "load", "loadstring",
        "collectgarbage",
    };

    /// <summary>The only <c>os</c> members that survive — MoonSharp's OsTime module.</summary>
    private static readonly string[] KeptOsMembers = { "time", "date", "clock", "difftime" };

    /// <summary>Apply the sandbox. Call once, immediately after construction, before any
    /// plugin code runs. Leaves the stack as it found it.</summary>
    public static void Apply(LuaHost host)
    {
        NativeLua l = host.State;

        foreach (string name in RemovedGlobals)
        {
            l.PushNil();
            l.SetGlobal(name);
        }

        // os → a fresh table holding only the kept members (copied from the real library,
        // then the original table is unreachable). Rebuilding beats deleting: nobody has to
        // maintain a list of every dangerous member current-or-future Lua adds to os.
        l.GetGlobal("os");
        l.NewTable();
        foreach (string member in KeptOsMembers)
        {
            l.GetField(-2, member);      // real os[member]
            l.SetField(-2, member);      // curated[member] = it
        }
        l.Remove(-2);                    // drop the real os table
        l.SetGlobal("os");

        // string.dump serializes a function to bytecode — the read half of the bytecode
        // hole DoText's text-only mode closes on the write side.
        l.GetGlobal("string");
        l.PushNil();
        l.SetField(-2, "dump");
        l.Pop(1);

        // print: harmless no-op unless a runtime rebinds it (the plugin runtime does).
        host.PushCallback(static _ => 0);
        l.SetGlobal("print");
    }
}

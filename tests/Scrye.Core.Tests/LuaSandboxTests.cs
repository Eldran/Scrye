using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The native-Lua (KeraLua) sandbox and error boundaries — the checks that have no
/// MoonSharp twin because MoonSharp's preset did this for us. The migration plan's rule:
/// a plugin state exposes base+table+string+math+utf8+coroutine and a curated os
/// (time/date/clock/difftime), and NOTHING that reaches the filesystem, the process, other
/// code, or bytecode. See <see cref="LuaSandbox"/> for the inventory.
/// </summary>
public sealed class LuaSandboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-sbx-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Printed = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public void Send(string text) { }
        public void Print(string pluginId, string text) => Printed.Add(text);
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => State[path] = value;
        public IDisposable WatchState(string path, Action<string, string> onChange) => new Nothing();
        public void AddPanel(string pluginId, PanelSpec panel) { }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private KeraLuaPluginRuntime Load(string id, string script, FakeHost host)
    {
        string folder = Path.Combine(_dir, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.lua"), script);
        var rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = id, Name = id }, folder), host);
        rt.Load();
        return rt;
    }

    [Fact]
    public void ForbiddenGlobalsAreAllNil()
    {
        var host = new FakeHost();
        using var rt = Load("probe", """
            local leaked = {}
            for _, name in ipairs({ "io", "package", "debug", "require", "dofile",
                                    "loadfile", "load", "loadstring", "collectgarbage" }) do
                if _G[name] ~= nil then leaked[#leaked+1] = name end
            end
            if string.dump ~= nil then leaked[#leaked+1] = "string.dump" end
            for _, name in ipairs({ "execute", "exit", "getenv", "remove", "rename",
                                    "setlocale", "tmpname" }) do
                if os[name] ~= nil then leaked[#leaked+1] = "os." .. name end
            end
            scrye.setState("leaked", table.concat(leaked, ","))
            """, host);
        Assert.Equal("", host.State["leaked"]);
    }

    [Fact]
    public void AllowedSurfaceIsPresent()
    {
        var host = new FakeHost();
        using var rt = Load("allow", """
            scrye.setState("os", type(os.time) .. type(os.date) .. type(os.clock) .. type(os.difftime))
            scrye.setState("libs", type(table.concat) .. type(string.match) .. type(math.floor)
                                  .. type(utf8.char) .. type(coroutine.create))
            scrye.setState("meta", type(setmetatable) .. type(getmetatable))
            scrye.setState("err", type(pcall) .. type(xpcall) .. type(error))
            """, host);
        Assert.Equal("functionfunctionfunctionfunction", host.State["os"]);
        Assert.Equal("functionfunctionfunctionfunctionfunction", host.State["libs"]);
        Assert.Equal("functionfunction", host.State["meta"]);
        Assert.Equal("functionfunctionfunction", host.State["err"]);
    }

    [Fact]
    public void BytecodeChunksAreRefused()
    {
        // "\x1bLua" is the binary-chunk signature; text-only loading must refuse it
        // rather than execute precompiled (unverifiable) bytecode.
        var host = new FakeHost();
        Assert.Throws<LuaHostException>(() => Load("bc", "\x1bLua\x54", host));
    }

    [Fact]
    public void HookErrorsAreReportedNotThrownAndLaterHooksStillRun()
    {
        var host = new FakeHost();
        using var rt = Load("err", """
            scrye.onLine(function(l) error("boom") end)
            scrye.onLine(function(l) scrye.setState("second", l) end)
            """, host);
        (bool gag, string? rewrite) = rt.ProcessLine("hello");
        Assert.False(gag);
        Assert.Null(rewrite);
        Assert.Equal("hello", host.State["second"]);
        string report = Assert.Single(host.Printed);
        Assert.Contains("onLine error:", report);
        Assert.Contains("boom", report);
    }

    [Fact]
    public void EntryScriptErrorsThrowWithAuthorReadableMessages()
    {
        var syntax = Assert.Throws<LuaHostException>(() => Load("syn", "this is not lua", new FakeHost()));
        Assert.Contains("syn", syntax.Message);          // chunk is named after the plugin
        var runtime = Assert.Throws<LuaHostException>(() => Load("run", """error("died")""", new FakeHost()));
        Assert.Contains("died", runtime.Message);
    }

    [Fact]
    public void ScryeApiWorksFromCoroutines()
    {
        // Bindings must read arguments from the CALLING thread's stack, not the main one.
        var host = new FakeHost();
        using var rt = Load("co", """
            local co = coroutine.create(function()
                scrye.setState("inside", "co-value")
                coroutine.yield()
                scrye.setState("resumed", "yes")
            end)
            coroutine.resume(co)
            coroutine.resume(co)
            """, host);
        Assert.Equal("co-value", host.State["inside"]);
        Assert.Equal("yes", host.State["resumed"]);
    }

    [Fact]
    public void IntegerSubtypeBehavesAsLua54()
    {
        var host = new FakeHost();
        using var rt = Load("int", """
            scrye.setState("floor", tostring(math.floor(7.9)))       -- integer, no ".0"
            scrye.setState("div", tostring(7 // 2))                  -- 5.4 integer division
            scrye.setState("band", tostring(6 & 3))                  -- 5.4 bitwise operator
            scrye.setState("fmt", string.format("%d", math.floor(2.0)))
            """, host);
        Assert.Equal("7", host.State["floor"]);
        Assert.Equal("3", host.State["div"]);
        Assert.Equal("2", host.State["band"]);
        Assert.Equal("2", host.State["fmt"]);
    }

    [Fact]
    public void StrayPrintRoutesToHostPrint()
    {
        var host = new FakeHost();
        using var rt = Load("prn", """print("dbg", 1, true)""", host);
        Assert.Equal("dbg\t1\ttrue", Assert.Single(host.Printed));
    }

    // ---- instruction budget (lua_sethook count hook) ----

    [Fact]
    public void SpinningScriptAbortsOnItsInstructionBudget()
    {
        // Mechanics with a small budget for test speed: the abort is a normal Lua error,
        // so it surfaces as LuaHostException and the state survives for the next dispatch.
        using var lh = new LuaHost();
        LuaSandbox.Apply(lh);
        lh.EnableDispatchBudget(1_000_000);
        var ex = Assert.Throws<LuaHostException>(() => lh.DoText("while true do end", "spin"));
        Assert.Contains("execution budget", ex.Message);
        // budget resets per dispatch — honest work keeps running afterwards
        for (int i = 0; i < 5; i++)
            lh.DoText("local s = 0 for j = 1, 200000 do s = s + j end", "work" + i);
    }

    [Fact]
    public void SpinningHookIsReportedAndThePluginSurvives()
    {
        // Runtime level with the DEFAULT budget: an accidental infinite loop in a hook
        // aborts (instead of freezing the session loop), is reported like any callback
        // error, and later dispatches still work.
        var host = new FakeHost();
        using var rt = Load("spinner", """
            scrye.onLine(function(l)
                if l == "go" then local x = 0 while true do x = x + 1 end end
                scrye.setState("alive", l)
            end)
            """, host);
        rt.ProcessLine("go");
        Assert.Contains(host.Printed, l => l.Contains("onLine error:") && l.Contains("execution budget"));
        rt.ProcessLine("still-here");
        Assert.Equal("still-here", host.State["alive"]);
    }
}

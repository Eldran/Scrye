using System.Runtime.CompilerServices;
using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Xunit;
using NativeLua = KeraLua.Lua;

namespace Scrye.Core.Tests;

/// <summary>
/// The Lua memory limit (1.10.7). The point is not only that a greedy plugin is stopped, but
/// WHERE: a refused allocation raises an error at that spot, and a spot outside a pcall or
/// inside a C# binding would abort the process or longjmp across .NET. These pin that the
/// limit bites only in plugin code, under a pcall, and that the state stays usable after.
/// </summary>
public sealed class LuaMemoryLimitTests : IDisposable
{
    private const long Limit = 16L * 1024 * 1024;
    private const string GrowForever =
        "local t = {} while true do t[#t + 1] = string.rep('x', 65536) .. #t end";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-mem-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LuaHost Host()
    {
        var h = new LuaHost();
        h.EnableMemoryLimit(Limit);
        return h;
    }

    [Fact]
    public void A_table_grown_forever_is_stopped_with_a_readable_error_and_the_state_lives_on()
    {
        using LuaHost h = Host();
        var ex = Assert.Throws<LuaHostException>(() => h.DoText(GrowForever, "grow"));

        Assert.Contains("not enough memory", ex.Message);
        Assert.Contains("16 MB memory limit", ex.Message);
        h.DoText("after = string.rep('a', 100000)", "after");     // the garbage went; this fits
        h.State.GetGlobal("after");
        Assert.Equal(100000, h.State.ToString(-1, false).Length);
        h.State.Pop(1);
    }

    [Fact]
    public void Without_the_limit_the_same_script_is_not_stopped_at_16_MB()
    {
        // the control: what stops the script above is the limit, not something else
        using var h = new LuaHost();
        h.DoText("t = {} for i = 1, 400 do t[i] = string.rep('x', 65536) .. i end", "big");   // ~26 MB
        h.State.GetGlobal("t");
        Assert.Equal(400, (int)h.State.RawLen(-1));
        h.State.Pop(1);
    }

    [Fact]
    public void The_host_can_still_push_when_the_state_is_full()
    {
        // Outside a pcall a refused allocation is a Lua PANIC, which aborts the process. The
        // host pushes a line's text before every onLine, whatever the plugin has used.
        using LuaHost h = Host();
        Assert.Throws<LuaHostException>(() => h.DoText(
            "held = {} while true do held[#held + 1] = string.rep('y', 65536) .. #held end", "fill"));

        h.State.PushString(new string('z', 2_000_000));         // must not be refused
        h.State.SetGlobal("big");

        Assert.True(h.MemoryUsed > Limit, $"used {h.MemoryUsed} - the push should have gone past the limit");
    }

    /// <summary>A state holding ~8 MB of live data whose limit is then set just above what
    /// it uses: really at the limit, with no garbage a collection could free.</summary>
    private static LuaHost AtTheLimit()
    {
        var h = new LuaHost();
        h.EnableMemoryLimit(Limit);
        h.DoText("held = {} for i = 1, 120 do held[i] = string.rep('y', 65536) .. i end", "fill");
        h.State.GarbageCollector(KeraLua.LuaGC.Collect, 0);
        h.EnableMemoryLimit(h.MemoryUsed + 64 * 1024);
        return h;
    }

    [Fact]
    public void A_binding_called_at_the_limit_still_gets_its_memory()
    {
        // Inside a C# binding a refused allocation would longjmp across the .NET frame.
        using LuaHost h = AtTheLimit();
        h.PushCallback(ptr =>
        {
            NativeLua l = NativeLua.FromIntPtr(ptr);
            l.PushString(new string('q', 1_000_000));
            return 1;
        });
        h.State.SetGlobal("make");
        // the control: Lua itself can no longer allocate a megabyte here
        Assert.Throws<LuaHostException>(() => h.DoText("local x = string.rep('w', 1000000)", "control"));

        h.DoText("got = #make()", "bind");

        h.State.GetGlobal("got");
        Assert.Equal(1_000_000, (int)h.State.ToInteger(-1));
        h.State.Pop(1);
    }

    [Fact]
    public void Lua_called_back_from_a_binding_is_limited_and_fails_into_its_own_pcall()
    {
        using LuaHost h = Host();
        string? nestedError = null;
        long usedAfterNested = 0;
        h.DoText("function grow() " + GrowForever + " end", "def");
        h.PushCallback(ptr =>
        {
            NativeLua l = NativeLua.FromIntPtr(ptr);
            l.GetGlobal("grow");
            bool ok = h.PCall(0, 0, out nestedError);
            usedAfterNested = h.MemoryUsed;
            l.PushBoolean(ok);
            return 1;
        });
        h.State.SetGlobal("call_grow");

        h.DoText("nested_ok = call_grow(); outer_done = true", "outer");   // the outer chunk finishes

        Assert.Contains("not enough memory", nestedError);
        // stopped by OUR limit - not by the machine running out, which says the same words
        Assert.True(usedAfterNested < Limit + 2 * 1024 * 1024, $"used {usedAfterNested} before it stopped");
        h.State.GetGlobal("outer_done");
        Assert.True(h.State.ToBoolean(-1));
        h.State.Pop(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UseAndAbandon()
    {
        LuaHost h = Host();
        h.DoText("t = {} for i = 1, 2000 do t[i] = tostring(i) end", "x");
        // no Dispose: KeraLua's finalizer will lua_close this state, through the allocator
    }

    [Fact]
    public void A_host_dropped_without_Dispose_is_closed_by_the_finalizer_without_crashing()
    {
        // lua_close frees through the allocator. Unrooted, its delegate could be collected
        // first, and the finalizer's close was "a callback on a garbage collected delegate" -
        // a process abort, found when the real Lua ran under this suite.
        for (int i = 0; i < 20; i++) UseAndAbandon();
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.True(true);   // reaching here is the test
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

    [Fact]
    public void A_plugin_runtime_has_the_limit_and_reports_hitting_it()
    {
        string folder = Path.Combine(_dir, "greedy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.lua"), """
            local hoard = {}
            scrye.onLine(function(l)
                if l == "eat" then
                    while true do hoard[#hoard + 1] = string.rep("x", 1048576) .. #hoard end
                end
                scrye.setState("alive", l)
            end)
            """);
        var host = new FakeHost();
        using var rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = "greedy", Name = "greedy" }, folder), host);
        rt.Load();

        rt.ProcessLine("eat");
        Assert.Contains(host.Printed, p => p.Contains("onLine error:") && p.Contains("not enough memory")
                                           && p.Contains("256 MB memory limit"));
        rt.ProcessLine("still-here");                               // the plugin survives it
        Assert.Equal("still-here", host.State["alive"]);
    }
}

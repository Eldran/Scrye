using Scrye.Core.Plugins;
using Scrye.Scripting.Wasm;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The wasm runtime (scrye-wasm-abi v1 — docs/scrye-wasm-abi.md) exercised through the
/// checked-in fixture modules in tests/fixtures/wasm (built from checked-in C by
/// build.sh, so the tested binary has readable source). Covers the full
/// <c>IPluginRuntime</c> surface plus the two properties unique to wasm: the epoch
/// deadline (a spinning plugin traps instead of freezing the loop) and ENFORCED
/// permissions (undeclared imports trap by name at first use).
/// </summary>
public sealed class WasmPluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-wasm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrye.sln")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        string folder = Path.Combine(dir!.FullName, "tests", "fixtures", "wasm");
        Assert.True(File.Exists(Path.Combine(folder, "test-plugin.wasm")), $"fixtures not found at {folder}");
        return folder;
    }

    private static readonly string[] AllPermissions =
    {
        "output.read", "output.modify", "commands.send", "state.read", "state.write",
        "variables.read", "variables.write", "storage.private", "timers.manage",
        "triggers.manage", "aliases.manage", "ui.panels",
    };

    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Sent = new();
        public readonly List<string> Printed = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Vars = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyDictionary<string, string>> Batches = new();
        public readonly List<PanelSpec> Panels = new();
        public readonly List<(string Path, Action<string, string> Cb)> Watches = new();

        public void Send(string text) => Sent.Add(text);
        public void Print(string pluginId, string text) => Printed.Add(text);
        public string? GetVariable(string name) => Vars.TryGetValue(name, out string? v) ? v : null;
        public void SetVariable(string name, string value) => Vars[name] = value;
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => State[path] = value;
        public IDisposable WatchState(string path, Action<string, string> onChange)
        {
            Watches.Add((path, onChange));
            return new Nothing();
        }
        public void FireWatch(string path, string value)
        {
            foreach ((string p, Action<string, string> cb) in Watches.ToList())
                if (p == path) cb(path, value);
        }
        public void AddPanel(string pluginId, PanelSpec panel) => Panels.Add(panel);
        public string? StoreGet(string pluginId, string key) => Store.TryGetValue(key, out string? v) ? v : null;
        public void StoreSet(string pluginId, string key, string value) => Store[key] = value;
        public void StoreDelete(string pluginId, string key) => Store.Remove(key);
        public string[] StoreKeys(string pluginId) => Store.Keys.ToArray();
        public void StoreSetMany(string pluginId, IReadOnlyDictionary<string, string> values)
        {
            Batches.Add(new Dictionary<string, string>(values));
            foreach (KeyValuePair<string, string> kv in values) Store[kv.Key] = kv.Value;
        }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private WasmPluginRuntime Load(FakeHost host, string[]? permissions = null, string fixture = "test-plugin.wasm")
    {
        string folder = Path.Combine(_dir, Path.GetFileNameWithoutExtension(fixture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(FixtureDir(), fixture), Path.Combine(folder, "main.wasm"));
        File.WriteAllText(Path.Combine(folder, "areas.json"), """{ "town": { "rooms": 3 } }""");
        var rt = new WasmPluginRuntime(new PluginDescriptor(new PluginManifest
        {
            Id = "wasm-test",
            Name = "Wasm Test",
            Lang = "wasm",
            Entry = "main.wasm",
            Data = new Dictionary<string, string> { ["areas"] = "areas.json" },
            Permissions = permissions ?? AllPermissions,
        }, folder), host);
        rt.Load();
        return rt;
    }

    [Fact]
    public void InitRunsAndHostServicesRoundTrip()
    {
        var host = new FakeHost();
        using WasmPluginRuntime rt = Load(host);
        Assert.Contains("init-ok", host.Printed);
        Assert.Contains("\"rooms\":3", host.State["data"]);            // get_data
        Assert.Equal("v", host.State["store.k"]);                       // store set/get
        Assert.Equal("<nil>", host.State["store.miss"]);                // packed-0 nil
        Assert.Contains("\"b\"", host.State["store.keys"]);
        IReadOnlyDictionary<string, string> batch = Assert.Single(host.Batches);
        Assert.Equal(2, batch.Count);                                   // setMany = one batch
        Assert.Equal("val1", host.State["var1"]);                       // variables
    }

    [Fact]
    public void LinesGagRewriteTriggersAndAliases()
    {
        var host = new FakeHost();
        using WasmPluginRuntime rt = Load(host);
        Assert.Equal((true, null), rt.ProcessLine("secret stuff"));
        Assert.Equal((false, "new-line"), rt.ProcessLine("an old-line here"));
        Assert.Equal(((bool)false, (string?)null), rt.ProcessLine("plain"));

        rt.ProcessLine("You have 250 gold");
        Assert.Contains("250", host.State["gold"]);                     // wildcards reach run hook
        Assert.Contains("buy ale", host.Sent);                          // guest sends from inside a hook
        rt.ProcessLine("the gong rings");
        Assert.Contains("bow", host.Sent);                              // send template rule

        Assert.Equal((true, null), rt.ProcessInput("gt hello there"));
        Assert.Contains("hello there", host.State["alias"]);
        Assert.Equal(((bool)false, (string?)null), rt.ProcessInput("say hi"));
    }

    [Fact]
    public void EventsChannelsGmcpLifecycleAndWatch()
    {
        var host = new FakeHost();
        using WasmPluginRuntime rt = Load(host);
        rt.DispatchChannel("Party", "heal plz");
        Assert.Contains("heal plz", host.State["party"]);
        rt.DispatchChannel("Gossip", "nope");
        Assert.DoesNotContain("nope", host.State["party"]);
        rt.DispatchGmcp("Char.Vitals", """{"hp":10}""");
        Assert.Contains("hp", host.State["vitals"]);
        rt.DispatchConnect();
        Assert.Equal("yes", host.State["conn"]);
        rt.DispatchCommand("kill rat");
        Assert.Contains("kill rat", host.State["cmd"]);
        rt.DispatchPluginEvent("ping", "payload-x", "other");
        Assert.Contains("payload-x", host.State["evt"]);
        Assert.Contains("other", host.State["evt"]);
        host.FireWatch("character.hp", "42");
        Assert.Contains("42", host.State["watched"]);
    }

    [Fact]
    public void TimersFireRepeatAndCancel()
    {
        var host = new FakeHost();
        using WasmPluginRuntime rt = Load(host);
        rt.Tick(0.5);
        Assert.Equal("1", host.State["ticks"]);
        Assert.False(host.State.ContainsKey("once"));
        rt.Tick(0.5);
        Assert.Equal("fired", host.State["once"]);
        Assert.Equal("2", host.State["ticks"]);
        Assert.False(host.State.ContainsKey("cancelled"));              // cancelled timer never fires
    }

    [Fact]
    public void PanelsParseAndAllCallbackKindsFire()
    {
        var host = new FakeHost();
        using WasmPluginRuntime rt = Load(host);
        PanelSpec p = Assert.Single(host.Panels);
        Assert.Equal("Wasm", p.Title);
        Assert.Equal(30, p.Width);
        Assert.Equal(5, p.Widgets.Count);
        Assert.True(p.Widgets[0].Dim);
        Assert.Equal("100", p.Widgets[1].Max);                          // numeric JSON accepted
        Assert.Single(p.Tabs);

        rt.InvokeAction(p.Widgets[2].Action!);
        Assert.Equal("1", host.State["click"]);
        WidgetSpec grid = p.Widgets[3];
        Assert.True(grid.Weave);
        Assert.NotNull(grid.Palette);
        rt.InvokeCellAction(grid.Action!, 3, 4, "#");
        Assert.Contains("#", host.State["cell"]);
        rt.InvokeCellAction(grid.HoverAction!, 1, 1, ".");
        Assert.Contains(".", host.State["hover"]);
        WidgetSpec rowChild = p.Widgets[4].Children![0];
        rt.InvokeChoice(rowChild.Action!, "A", 1);
        Assert.Contains("A", host.State["row"]);
    }

    [Fact]
    public void SpinningHookTrapsOnDeadlineAndInstanceSurvives()
    {
        var host = new FakeHost();
        using WasmPluginRuntime rt = Load(host);
        int before = host.Printed.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rt.DispatchPluginEvent("spin", "", "test");                     // guest infinite-loops
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"trap took {sw.ElapsedMilliseconds} ms");
        Assert.Contains(host.Printed.Skip(before), l => l.Contains("on:spin error:"));
        Assert.Equal(((bool)false, (string?)null), rt.ProcessLine("plain"));   // still alive
    }

    [Fact]
    public void UndeclaredPermissionTrapsAtFirstUseNamingIt()
    {
        // No permissions declared: init's first gated import (set_state) must trap with a
        // message naming 'state.write' — permissions are ENFORCED for wasm, not declarative.
        var ex = Assert.ThrowsAny<Exception>(() => Load(new FakeHost(), permissions: Array.Empty<string>()));
        Assert.Contains("state.write", ex.Message);
    }

    [Fact]
    public void FutureAbiVersionIsRefusedByName()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Load(new FakeHost(), fixture: "abi-v2.wasm"));
        Assert.Contains("v2", ex.Message);
        Assert.Contains("v1", ex.Message);
    }
}

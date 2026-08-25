using Scrye.Core.Plugins;
using Scrye.Scripting.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The MUD-shared store (scrye.shared, API 1.14): the scrye.store surface scoped by HOST
/// instead of by profile, so a map built on one character exists for the next. Exercised at
/// both layers - the PluginDataStore scoping that makes sharing true on disk, and the real
/// native-Lua runtime binding a script actually calls.
/// </summary>
public sealed class SharedStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-shared-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- the disk truth: one host scope, many profiles, one file --------------

    /// <summary>Two profiles on the same MUD see each other's shared writes; their private
    /// stores stay their own. This is the whole point of the feature.</summary>
    [Fact]
    public void SharedScopeIsTheHostNotTheProfile()
    {
        string dataDir = Path.Combine(_dir, "plugin-data");
        string sharedDir = Path.Combine(_dir, "plugin-shared");

        // Character A's session: private store scoped by profile, shared store by host.
        var privateA = new PluginDataStore(dataDir, "3Scapes Goran");
        var sharedA = new PluginDataStore(sharedDir, "3scapes.org");
        privateA.Set("3s-map-gmcp", "talking", "1");
        sharedA.Set("3s-map-gmcp", "graph", "the-map");

        // Character B's session, fresh instances as a new session would make them.
        var privateB = new PluginDataStore(dataDir, "3Scapes Nille");
        var sharedB = new PluginDataStore(sharedDir, "3scapes.org");

        Assert.Equal("the-map", sharedB.Get("3s-map-gmcp", "graph"));   // the map came along
        Assert.Null(privateB.Get("3s-map-gmcp", "talking"));            // the preference did not

        // A DIFFERENT MUD's shared scope sees nothing: host scoping, not a global bucket.
        var sharedElsewhere = new PluginDataStore(sharedDir, "otherworld.example");
        Assert.Null(sharedElsewhere.Get("3s-map-gmcp", "graph"));
    }

    /// <summary>Plugins do not read each other's shared data: the per-plugin file split
    /// applies in the shared root exactly as it does in the private one.</summary>
    [Fact]
    public void SharedStoreIsStillPerPlugin()
    {
        var shared = new PluginDataStore(Path.Combine(_dir, "plugin-shared"), "3scapes.org");
        shared.Set("3s-map-gmcp", "graph", "rooms");
        Assert.Null(shared.Get("3s-farmer", "graph"));
    }

    // ---- the script binding: a real Lua runtime against a host with both roots ----

    private sealed class FakeHost : IPluginHost
    {
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Shared = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyDictionary<string, string>> SharedBatches = new();

        public void Send(string text) { }
        public void Print(string pluginId, string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => State[path] = value;
        public IDisposable WatchState(string path, Action<string, string> onChange) => new Nothing();
        public void AddPanel(string pluginId, PanelSpec panel) { }
        public string? StoreGet(string pluginId, string key) => Store.TryGetValue(key, out string? v) ? v : null;
        public void StoreSet(string pluginId, string key, string value) => Store[key] = value;
        public string? SharedGet(string pluginId, string key) => Shared.TryGetValue(key, out string? v) ? v : null;
        public void SharedSet(string pluginId, string key, string value) => Shared[key] = value;
        public void SharedDelete(string pluginId, string key) => Shared.Remove(key);
        public string[] SharedKeys(string pluginId) => Shared.Keys.ToArray();
        public void SharedSetMany(string pluginId, IReadOnlyDictionary<string, string> values)
        {
            SharedBatches.Add(new Dictionary<string, string>(values));
            foreach (KeyValuePair<string, string> kv in values) Shared[kv.Key] = kv.Value;
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private IPluginRuntime LoadLua(string id, string script, FakeHost host)
    {
        string folder = Path.Combine(_dir, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.lua"), script);
        IPluginRuntime rt = PluginRuntimeFactory.Create(
            new PluginDescriptor(new PluginManifest { Id = id, Name = id, Entry = "main.lua" }, folder), host);
        rt.Load();
        return rt;
    }

    /// <summary>scrye.shared reads and writes reach the shared backing, not the private one -
    /// and the two are visibly different stores from the script's side.</summary>
    [Fact]
    public void LuaSharedBindingRoutesToTheSharedBacking()
    {
        var host = new FakeHost();
        host.Shared["graph"] = "carried-over";
        LoadLua("mapper", """
            scrye.setState("seen", tostring(scrye.shared.get("graph")))
            scrye.shared.set("mark", "hello")
            scrye.store.set("private-mark", "mine")
            scrye.setState("missing", tostring(scrye.shared.get("private-mark")))
            """, host);

        Assert.Equal("carried-over", host.State["seen"]);
        Assert.Equal("hello", host.Shared["mark"]);
        Assert.False(host.Shared.ContainsKey("private-mark"));   // store and shared are separate
        Assert.Equal("mine", host.Store["private-mark"]);
        Assert.Equal("nil", host.State["missing"]);
    }

    /// <summary>scrye.shared.setMany batches into ONE host call, like store.setMany.</summary>
    [Fact]
    public void LuaSharedSetManyBatches()
    {
        var host = new FakeHost();
        LoadLua("mapper", """
            scrye.shared.setMany{ a = "1", b = "2" }
            """, host);
        Assert.Single(host.SharedBatches);
        Assert.Equal("1", host.Shared["a"]);
        Assert.Equal("2", host.Shared["b"]);
    }

    /// <summary>The fallback idiom plugins ship with: `scrye.shared or scrye.store` picks the
    /// shared table on this runtime. (On a pre-1.14 host scrye.shared is absent and the idiom
    /// degrades to the private store - that half is the older client's behaviour, not ours to
    /// test here; this half proves the idiom does not accidentally shadow shared with store.)</summary>
    [Fact]
    public void FallbackIdiomPrefersShared()
    {
        var host = new FakeHost();
        LoadLua("mapper", """
            local ST = scrye.shared or scrye.store
            ST.set("where", "shared-side")
            """, host);
        Assert.Equal("shared-side", host.Shared.GetValueOrDefault("where"));
        Assert.False(host.Store.ContainsKey("where"));
    }
}

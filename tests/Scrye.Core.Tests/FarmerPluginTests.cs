using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Scrye.Scripting.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Acceptance tests for the shipped 3s-farmer (promoted from the lab at 1.0.0), run against
/// the REAL plugin folder in src/Scrye.App/plugins/3s-farmer so the shipped script is the
/// tested script - the pattern <see cref="ChaosSeaDelegationTests"/> set. The Lua harness in
/// _lab/farmer_test.lua carries the fine grain (220+ checks); these are the contracts the
/// client itself is party to: what the farmer sends, when, and how it delegates the trip to
/// the mapper over plugin events.
/// </summary>
public sealed class FarmerPluginTests
{
    private static string PluginFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrye.sln")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        string folder = Path.Combine(dir!.FullName, "src", "Scrye.App", "plugins", "3s-farmer");
        Assert.True(File.Exists(Path.Combine(folder, "main.lua")), $"3s-farmer not found at {folder}");
        return folder;
    }

    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Printed = new();
        public readonly List<string> Sent = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public readonly List<(string Name, string Data)> Emits = new();
        public Action<string, string>? EventSink;   // (name, data) - play the mapper's part

        public void Send(string text) => Sent.Add(text);
        public void Print(string pluginId, string text) => Printed.Add(text);
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => State[path] = value;
        public IDisposable WatchState(string path, Action<string, string> onChange) => new Nothing();
        public void AddPanel(string pluginId, PanelSpec panel) { }
        public string? StoreGet(string pluginId, string key) => Store.TryGetValue(key, out string? v) ? v : null;
        public void StoreSet(string pluginId, string key, string value) => Store[key] = value;
        public void StoreDelete(string pluginId, string key) => Store.Remove(key);
        public string[] StoreKeys(string pluginId) => Store.Keys.ToArray();
        public void EmitEvent(string sourceId, string name, string data)
        {
            Emits.Add((name, data));
            EventSink?.Invoke(name, data);
        }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private const string Barn  = """{ "num": 1, "name": "Barn",  "area": "Farmland", "exits": { "e": 2 } }""";
    private const string Yard  = """{ "num": 2, "name": "Yard",  "area": "Farmland", "exits": { "w": 1, "e": 3 } }""";
    private const string Fence = """{ "num": 3, "name": "Fence", "area": "Farmland", "exits": { "w": 2 } }""";
    private const string Cur   = """{ "full": 1, "items": [ { "name": "Small cur", "type": "monster" } ] }""";

    private static IPluginRuntime Load(FakeHost host)
    {
        // A fit character: the HP floor refuses to start with a floor set and no feed.
        host.State["char.vitals.hp"] = "1000";
        host.State["char.vitals.maxhp"] = "1000";
        IPluginRuntime rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = "3s-farmer", Name = "3S Farmer" }, PluginFolder()), host);
        rt.Load();
        return rt;
    }

    /// <summary>Stand in each room once, so the farmer's own graph knows the farmland; the
    /// patrol only ever takes exits that lead to rooms it has stood in.</summary>
    private static void Explore(IPluginRuntime rt)
    {
        rt.DispatchGmcp("Room.Info", Barn);  rt.Tick(1);
        rt.DispatchGmcp("Room.Info", Yard);  rt.Tick(1);
        rt.DispatchGmcp("Room.Info", Fence); rt.Tick(1);
        rt.DispatchGmcp("Room.Info", Yard);  rt.Tick(1);
    }

    /// <summary>The chassis: one bare direction per confirmed arrival, and nothing more until
    /// the server confirms the step - the property every other walker lacks.</summary>
    [Fact]
    public void PatrolSendsOneBareDirectionPerConfirmedArrival()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        Explore(rt);
        rt.ProcessInput("farm start");
        Assert.Contains(host.Printed, l => l.Contains("patrolling Farmland"));

        host.Sent.Clear();
        for (int i = 0; i < 8; i++) rt.Tick(1);            // well past the pace, no arrival
        Assert.Single(host.Sent);
        Assert.Contains(host.Sent[0], new[] { "w", "e" });   // a bare direction, nothing else

        // The arrival the step expected releases the next one; before it, nothing.
        string landed = host.Sent[0] == "w" ? Barn : Fence;
        rt.DispatchGmcp("Room.Info", landed);
        for (int i = 0; i < 3; i++) rt.Tick(1);
        Assert.Equal(2, host.Sent.Count);
        Assert.Contains(host.Sent[1], new[] { "w", "e" });
    }

    /// <summary>The fists: a monster the server lists is attacked by the plain word of its
    /// name - and by the patrol's own tick, no prompt needed - while the step is held.</summary>
    [Fact]
    public void AListedMonsterIsAttackedByItsKeywordAndHoldsTheStep()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        Explore(rt);
        rt.ProcessInput("farm start");
        host.Sent.Clear();
        rt.DispatchGmcp("Room.Contents", Cur);
        for (int i = 0; i < 3; i++) rt.Tick(1);
        Assert.Single(host.Sent);
        Assert.Equal("kill cur", host.Sent[0]);
        rt.DispatchPrompt();
        Assert.Single(host.Sent);                            // the prompt does not swing twice
    }

    /// <summary>An excluded mob is left alone and the patrol simply walks on.</summary>
    [Fact]
    public void AnExcludedMonsterIsLeftAloneAndThePatrolWalksOn()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        Explore(rt);
        rt.ProcessInput("farm start");
        rt.ProcessInput("farm exclude small cur");
        host.Sent.Clear();
        rt.DispatchGmcp("Room.Contents", Cur);
        rt.DispatchPrompt();
        for (int i = 0; i < 3; i++) rt.Tick(1);
        Assert.Single(host.Sent);
        Assert.DoesNotContain("kill", host.Sent[0]);
        Assert.Contains(host.Sent[0], new[] { "w", "e" });
    }

    /// <summary>The delegation: 'farm go' asks the mapper over map.goto and never walks the
    /// trip itself; the mapper's map.walk.arrived starts the patrol where the walk landed,
    /// and its map.walk.stopped ends the trip with the mapper's own reason.</summary>
    [Fact]
    public void FarmGoDelegatesTheTripToTheMapperAndStartsOnArrival()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        Explore(rt);

        rt.ProcessInput("farm go farmland");
        (string Name, string Data) ask = Assert.Single(host.Emits, e => e.Name == "map.goto");
        Assert.Contains("\"area\":\"farmland\"", ask.Data.Replace(" ", ""));
        Assert.Empty(host.Sent);                             // the farmer walks nothing itself

        rt.DispatchPluginEvent("map.walk.started", """{ "target": 1, "steps": 2 }""", "3s-map-gmcp");
        Assert.Contains(host.Printed, l => l.Contains("walking us toward"));
        host.Printed.Clear();
        rt.DispatchPluginEvent("map.walk.arrived", """{ "num": 2 }""", "3s-map-gmcp");
        Assert.Contains(host.Printed, l => l.Contains("patrolling Farmland"));
        rt.ProcessInput("farm stop");

        host.Printed.Clear();
        rt.ProcessInput("farm go farmland");
        rt.DispatchPluginEvent("map.walk.stopped", """{ "reason": "no known route into 'farmland'" }""", "3s-map-gmcp");
        Assert.Contains(host.Printed, l => l.Contains("did not finish - no known route"));
    }

    /// <summary>A mapper that never answers is reported, not waited on forever.</summary>
    [Fact]
    public void ASilentMapperIsReportedAfterTheGracePeriod()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        Explore(rt);
        rt.ProcessInput("farm go farmland");
        host.Printed.Clear();
        for (int i = 0; i < 5; i++) rt.Tick(1);
        Assert.Contains(host.Printed, l => l.Contains("no mapper answered"));
    }

    /// <summary>The 23 Sep server reports a kill twice - the text line and GMCP Room.Death.
    /// It is one kill (payload shape from the 23 Sep 15:34 capture: corpse and npc are numbers); the pairing names your character, kept in the store, after which a
    /// Room.Death alone counts as yours.</summary>
    [Fact]
    public void RoomDeathAndItsTextLineAreOneKillAndNameYou()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        Explore(rt);
        rt.ProcessLine("You dealt the killing blow to the Wiremouth guard.");
        rt.DispatchGmcp("Room.Death",
            """{ "corpse": 1, "killer": "Goran", "npc": 1, "name": "Wiremouth guard" }""");
        Assert.Equal("Goran", host.Store["me"]);
        for (int i = 0; i < 5; i++) rt.Tick(1);
        rt.DispatchGmcp("Room.Death",
            """{ "corpse": 1, "killer": "Goran", "npc": 1, "name": "Wiremouth guard" }""");
        rt.DispatchGmcp("Room.Death",
            """{ "corpse": 1, "killer": "Wiremouth guard", "npc": 0, "name": "Goran" }""");
        host.Printed.Clear();
        rt.ProcessInput("farm");
        Assert.Contains(host.Printed, l => l.Contains("tally: 2 kill(s)") && l.Contains("(you 2)"));
    }
}

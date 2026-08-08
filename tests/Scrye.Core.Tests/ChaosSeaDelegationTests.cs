using System.Text.Json;
using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Scrye.Scripting.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// 3s-chaossea's wasm-pathfinder delegation (sdk/rust/plugins/3s-pathfinder): exploration
/// resolves its whole priority-ordered frontier in ONE multi-target request, `cs find`/
/// `cs leave` use single-target asks, and everything falls back to the plugin's own BFS
/// when no pathfinder answers. Tests script the pathfinder's half of the protocol.
/// </summary>
public sealed class ChaosSeaDelegationTests
{
    private static string PluginFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrye.sln")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        string folder = Path.Combine(dir!.FullName, "src", "Scrye.App", "plugins", "3s-chaossea");
        Assert.True(File.Exists(Path.Combine(folder, "main.lua")), $"3s-chaossea not found at {folder}");
        return folder;
    }

    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Printed = new();
        public readonly List<string> Sent = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public Action<string, string>? EventSink;   // (name, data)

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
        public void EmitEvent(string sourceId, string name, string data) => EventSink?.Invoke(name, data);
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>A 3-room north line with the player at the top, plus a disconnected
    /// island whose frontier entry sits on TOP of the LIFO pile — highest priority but
    /// unreachable, exactly the case the priority-ordered sweep must skip. Seeded through
    /// the store, which load_state reads at the end of the entry script.</summary>
    private static IPluginRuntime Load(FakeHost host)
    {
        host.Store["map"] = "0|0|0|n,e\n0|0|1|n,s\n0|0|2|s\n0|5|5|e";
        host.Store["frontier"] = "0|0|0|e\n0|5|5|e";   // last line = newest = popped first
        host.Store["pos"] = "0|2|0";
        IPluginRuntime rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = "3s-chaossea", Name = "CSS" }, PluginFolder()), host);
        rt.Load();
        return rt;
    }

    [Fact]
    public void ExplorationResolvesTheWholeFrontierInOneRequest()
    {
        var host = new FakeHost();
        var requests = new List<JsonElement>();
        long knownSerial = -1;
        IPluginRuntime rt = null!;
        host.EventSink = (name, data) =>
        {
            if (name != "map.path.find") return;
            JsonElement req = JsonDocument.Parse(data).RootElement.Clone();
            requests.Add(req);
            long id = req.GetProperty("id").GetInt64();
            long serial = req.GetProperty("serial").GetInt64();
            if (req.TryGetProperty("rooms", out _)) knownSerial = serial;
            if (knownSerial != serial)
            {
                rt.DispatchPluginEvent("map.path.result", $"{{\"id\":{id},\"needArea\":true}}", "pf");
                return;
            }
            // the island (targets[1]) is unreachable; the line room (targets[2]) wins
            rt.DispatchPluginEvent("map.path.result",
                $"{{\"id\":{id},\"found\":true,\"index\":2,\"dirs\":[\"s\",\"s\"]}}", "pf");
        };
        rt = Load(host);
        rt.ProcessInput("cs enable");
        host.Sent.Clear();
        requests.Clear();

        rt.ProcessInput("cs step");
        Assert.Equal(2, requests.Count);                              // needArea handshake
        JsonElement warm = requests[1];
        Assert.Equal(2, warm.GetProperty("targets").GetArrayLength());
        Assert.Equal(5, warm.GetProperty("targets")[0].GetProperty("x").GetInt64());   // priority order kept
        Assert.Equal(JsonValueKind.False, warm.GetProperty("allowUp").ValueKind);      // never climbs
        Assert.Equal(4, warm.GetProperty("rooms").GetArrayLength());                   // canonicalized graph
        Assert.Equal("s", host.Sent[0]);                              // walks the winner's path
    }

    [Fact]
    public void FindUsesSingleTargetWithClimbingAndAWarmCache()
    {
        var host = new FakeHost();
        var requests = new List<JsonElement>();
        long knownSerial = -1;
        IPluginRuntime rt = null!;
        host.EventSink = (name, data) =>
        {
            if (name != "map.path.find") return;
            JsonElement req = JsonDocument.Parse(data).RootElement.Clone();
            requests.Add(req);
            long id = req.GetProperty("id").GetInt64();
            long serial = req.GetProperty("serial").GetInt64();
            if (req.TryGetProperty("rooms", out _)) knownSerial = serial;
            rt.DispatchPluginEvent("map.path.result", knownSerial == serial
                ? $"{{\"id\":{id},\"found\":true,\"dirs\":[\"s\",\"s\"]}}"
                : $"{{\"id\":{id},\"needArea\":true}}", "pf");
        };
        rt = Load(host);
        host.Printed.Clear();

        rt.ProcessInput("cs find 0 0 0");
        Assert.Contains(host.Printed, l => l.Contains("Result is:") && l.Contains("s s"));
        Assert.Equal(JsonValueKind.True, requests[^1].GetProperty("allowUp").ValueKind);

        requests.Clear();
        rt.ProcessInput("cs find 0 1 0");
        JsonElement warm = Assert.Single(requests);                   // cache warm: one ask,
        Assert.False(warm.TryGetProperty("rooms", out _));            // no graph reshipped
    }

    /// <summary>An unexplored DOWN exit in the player's own room: the dive candidate is
    /// synthesized from the room's exits rather than taken from the frontier pile, and the
    /// candidate collection must produce it exactly once and terminate. (The original
    /// drain loop re-synthesized it forever — the instruction budget tripped mid-game with
    /// "script exceeded its execution budget" the moment exploration stood on a down exit.)</summary>
    [Fact]
    public void ADownExitInTheCurrentRoomBecomesOneDiveCandidateAndStepTerminates()
    {
        var host = new FakeHost();
        var requests = new List<JsonElement>();
        long knownSerial = -1;
        IPluginRuntime rt = null!;
        host.EventSink = (name, data) =>
        {
            if (name != "map.path.find") return;
            JsonElement req = JsonDocument.Parse(data).RootElement.Clone();
            requests.Add(req);
            long id = req.GetProperty("id").GetInt64();
            long serial = req.GetProperty("serial").GetInt64();
            if (req.TryGetProperty("rooms", out _)) knownSerial = serial;
            rt.DispatchPluginEvent("map.path.result", knownSerial == serial
                ? $"{{\"id\":{id},\"found\":true,\"index\":1,\"dirs\":[]}}"   // dive wins: empty walk + its own dir
                : $"{{\"id\":{id},\"needArea\":true}}", "pf");
        };
        // same seed as Load(), but the player's room (map line z|x|y = 0|0|2) also has 'd'
        host.Store["map"] = "0|0|0|n,e\n0|0|1|n,s\n0|0|2|s,d\n0|5|5|e";
        host.Store["frontier"] = "0|0|0|e\n0|5|5|e";
        host.Store["pos"] = "0|2|0";
        rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = "3s-chaossea", Name = "CSS" }, PluginFolder()), host);
        rt.Load();
        rt.ProcessInput("cs enable");
        host.Sent.Clear();

        rt.ProcessInput("cs step");                                   // hung forever before the fix
        JsonElement warm = requests[^1];
        Assert.Equal(3, warm.GetProperty("targets").GetArrayLength()); // dive + 2 frontier entries
        JsonElement first = warm.GetProperty("targets")[0];
        Assert.Equal(2, first.GetProperty("y").GetInt64());            // the dive (player's room) leads
        Assert.Equal("d", host.Sent[0]);                               // and it dives
    }

    [Fact]
    public void WithoutAPathfinderEverythingFallsBackToTheLocalBfs()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        host.Printed.Clear();
        rt.ProcessInput("cs find 0 0 0");
        Assert.Contains(host.Printed, l => l.Contains("Result is:") && l.Contains("s s"));

        rt.ProcessInput("cs enable");
        host.Sent.Clear();
        rt.ProcessInput("cs step");
        Assert.True(host.Sent.Count > 0 && host.Sent[0] == "s",
            "local fallback should walk the reachable frontier entry (island stashed)");
    }
}

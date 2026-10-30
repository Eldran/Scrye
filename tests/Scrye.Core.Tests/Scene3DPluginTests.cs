using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Scrye.Scripting.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The shipped plugins' 3D views (plugin API 1.24), end to end in the REAL native-Lua runtime:
/// the panel a plugin builds when <c>scrye.api</c> says 1.24, and the scene JSON its real
/// <c>scrye.json.encode</c> produces, read back by the same <see cref="Scene3D.Parse"/> the
/// desktop view uses. The Lua harnesses pin the plugins' logic with their own JSON codec;
/// this is the seam between the two that only a real runtime can check.
/// </summary>
public sealed class Scene3DPluginTests
{
    private static string PluginFolder(string id)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrye.sln")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        string folder = Path.Combine(dir!.FullName, "src", "Scrye.App", "plugins", id);
        Assert.True(File.Exists(Path.Combine(folder, "main.lua")), $"{id} not found at {folder}");
        return folder;
    }

    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Printed = new();
        public readonly List<PanelSpec> Panels = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);

        public void Send(string text) { }
        public void Print(string pluginId, string text) => Printed.Add(text);
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => State[path] = value;
        public IDisposable WatchState(string path, Action<string, string> onChange) => new Nothing();
        public void AddPanel(string pluginId, PanelSpec panel) => Panels.Add(panel);
        public string? StoreGet(string pluginId, string key) => Store.TryGetValue(key, out string? v) ? v : null;
        public void StoreSet(string pluginId, string key, string value) => Store[key] = value;
        public void StoreDelete(string pluginId, string key) => Store.Remove(key);
        public string[] StoreKeys(string pluginId) => Store.Keys.ToArray();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static IPluginRuntime Load(string id, FakeHost host)
    {
        IPluginRuntime rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = id, Name = id }, PluginFolder(id)), host);
        rt.Load();
        return rt;
    }

    /// <summary>The widget a panel's tabs (or flat list) carry of the given type, searching one
    /// level into containers.</summary>
    private static WidgetSpec? Find(PanelSpec panel, string type)
    {
        IEnumerable<WidgetSpec> all = panel.Tabs.Count > 0 ? panel.Tabs.SelectMany(t => t.Widgets) : panel.Widgets;
        foreach (WidgetSpec w in all)
        {
            if (w.Type == type) return w;
            foreach (WidgetSpec c in w.Children ?? Array.Empty<WidgetSpec>())
                if (c.Type == type) return c;
        }
        return null;
    }

    [Fact]
    public void TheMapBuildsAScene3DPanelAndAParseableScene()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load("3s-map-gmcp", host);
        // the hall, east to the yard, a loft above the hall
        rt.DispatchGmcp("Room.Info", """{ "num": 2, "name": "Yard", "area": "Keep", "exits": { "w": 1, "n": 0 } }""");
        rt.DispatchCommand("w");
        rt.DispatchGmcp("Room.Info", """{ "num": 1, "name": "Hall", "area": "Keep", "exits": { "e": 2, "u": 3 } }""");
        rt.DispatchCommand("u");
        rt.DispatchGmcp("Room.Info", """{ "num": 3, "name": "Loft", "area": "Keep", "exits": { "d": 1 } }""");

        WidgetSpec scene = Assert.IsType<WidgetSpec>(Find(host.Panels[^1], "scene3d"));
        Assert.Equal("plugin.3s-map-gmcp.scene3d", scene.Bind);
        Assert.Equal("colorgrid", Assert.Single(scene.Children!).Type);
        Assert.False(string.IsNullOrEmpty(scene.Action));
        Assert.False(string.IsNullOrEmpty(scene.HoverAction));
        Assert.False(string.IsNullOrEmpty(scene.ContextAction));
        Assert.Equal("", host.GetState("plugin.3s-map-gmcp.scene3d"));          // 2D until asked

        rt.ProcessInput("mapg view 3d");
        Scene3D s = Scene3D.Parse(host.GetState("plugin.3s-map-gmcp.scene3d"));
        Assert.False(s.IsEmpty);
        Assert.Equal("3", s.FocusId);                                            // you, in the loft
        SceneNode loft = s.Nodes.Single(n => n.Id == "3");
        SceneNode hall = s.Nodes.Single(n => n.Id == "1");
        Assert.Equal('@', loft.Ch);
        Assert.Equal(hall.Z + 1, loft.Z);
        Assert.Contains(s.Nodes, n => n.Id == "" && n.Ch == '?');                // the yard's north
        Assert.Contains(s.Links, l => s.Nodes[l.A].Id == "1" && s.Nodes[l.B].Id == "3" && l.Ch == '|');
        Assert.Equal(1.0, s.Level);
        Assert.Equal(2.5, s.ZScale);

        // the 3D callbacks answer through the choice path: a right-click is the room's menu
        IReadOnlyList<MenuEntry>? menu = rt.InvokeChoice(scene.ContextAction!, "2", 1);
        Assert.Contains(new MenuEntry("Walk there", "mapg go 2"), menu!);
        rt.InvokeChoice(scene.HoverAction!, "1", 0);
        Assert.Contains("Hall", host.GetState("plugin.3s-map-gmcp.peek"));

        rt.ProcessInput("mapg view 2d");
        Assert.Equal("", host.GetState("plugin.3s-map-gmcp.scene3d"));
    }

    [Fact]
    public void TheChaosSeaBuildsAScene3DPanelAndAParseableScene()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load("3s-chaossea", host);
        WidgetSpec scene = Assert.IsType<WidgetSpec>(Find(host.Panels[^1], "scene3d"));
        Assert.Equal("plugin.3s-chaossea.scene3d", scene.Bind);
        Assert.Equal("plugin.3s-chaossea.map", Assert.Single(scene.Children!).Bind);

        rt.ProcessInput("cs enable");
        rt.DispatchGmcp("Room.Info",
            """{ "exits": { "n": 60494, "s": 60494 }, "area": "Unknown", "name": "Layer three of the Sea of Chaos", "num": 60494 }""");
        rt.DispatchGmcp("Room.Map", """{ "rows": [ "        @        " ], "w": 17, "h": 1, "kind": "los" }""");
        rt.ProcessInput("cs view 3d");

        Scene3D s = Scene3D.Parse(host.GetState("plugin.3s-chaossea.scene3d"));
        Assert.False(s.IsEmpty);
        Assert.True(s.Focus >= 0);
        Assert.Equal('@', s.Nodes[s.Focus].Ch);
        Assert.Equal(2, s.Nodes.Count(n => n.Ch == 'f'));                     // n and s, unexplored
        Assert.All(s.Nodes, n => Assert.Equal("", n.Id));                     // nothing to click
        // the panel was rebuilt with its 3D button lit
        WidgetSpec three = host.Panels[^1].Widgets
            .SelectMany(w => w.Children ?? Array.Empty<WidgetSpec>())
            .First(b => b.Text is "3D" or "3D ON");
        Assert.Equal("3D ON", three.Text);
    }
}

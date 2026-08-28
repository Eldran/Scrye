using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Scrye.Scripting.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Plugin API 1.6 runtime behaviour — <c>scrye.onCommand</c>, <c>scrye.json</c>,
/// <c>scrye.store.setMany</c>, <c>scrye.emit</c>/<c>scrye.on</c>, sub-second timers and the
/// colorgrid <c>onHover</c> callback — exercised through REAL native-Lua/Jint runtimes loading
/// real scripts from disk, because these are behaviours of the script binding, not shapes a
/// contract test can see.
/// </summary>
public sealed class PluginRuntimeApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-rt-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>An IPluginHost that records everything and routes state through a dictionary,
    /// mirroring what SessionPluginHost does against a live session.</summary>
    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Sent = new();
        public readonly List<string> Printed = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyDictionary<string, string>> Batches = new();
        public readonly List<PanelSpec> Panels = new();
        public Action<string, string, string>? EventSink;   // (sourceId, name, data)

        public void Send(string text) => Sent.Add(text);
        public void Print(string pluginId, string text) => Printed.Add(text);
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => State[path] = value;
        public IDisposable WatchState(string path, Action<string, string> onChange) => new Nothing();
        public void AddPanel(string pluginId, PanelSpec panel) => Panels.Add(panel);
        public string? StoreGet(string pluginId, string key) => Store.TryGetValue(key, out string? v) ? v : null;
        public void StoreSet(string pluginId, string key, string value) => Store[key] = value;
        public void StoreSetMany(string pluginId, IReadOnlyDictionary<string, string> values)
        {
            Batches.Add(new Dictionary<string, string>(values));
            foreach (KeyValuePair<string, string> kv in values) Store[kv.Key] = kv.Value;
        }
        public void EmitEvent(string sourceId, string name, string data) => EventSink?.Invoke(sourceId, name, data);

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private PluginDescriptor WritePlugin(string id, string script, string lang = "lua")
    {
        string folder = Path.Combine(_dir, id);
        Directory.CreateDirectory(folder);
        string entry = lang == "js" ? "main.js" : "main.lua";
        File.WriteAllText(Path.Combine(folder, entry), script);
        return new PluginDescriptor(
            new PluginManifest { Id = id, Name = id, Lang = lang, Entry = entry }, folder);
    }

    private IPluginRuntime LoadLua(string id, string script, FakeHost host)
    {
        IPluginRuntime rt = PluginRuntimeFactory.Create(WritePlugin(id, script), host);
        rt.Load();
        return rt;
    }

    // ---- scrye.onCommand ------------------------------------------------------

    [Fact]
    public void OnCommandHooksSeeEveryDispatchedCommand()
    {
        var host = new FakeHost();
        IPluginRuntime rt = LoadLua("cmd", """
            scrye.onCommand(function(c) scrye.setState("last", c) end)
            """, host);

        rt.DispatchCommand("north");
        Assert.Equal("north", host.State["last"]);
        rt.DispatchCommand("kill troll");
        Assert.Equal("kill troll", host.State["last"]);
    }

    [Fact]
    public void RuntimeWithoutOnCommandHooksIgnoresDispatchQuietly()
    {
        var host = new FakeHost();
        IPluginRuntime rt = LoadLua("nocmd", "-- registers nothing", host);
        rt.DispatchCommand("north");   // must not throw or print
        Assert.Empty(host.Printed);
    }

    // ---- scrye.json -----------------------------------------------------------

    [Fact]
    public void JsonEncodesArraysObjectsAndIntegersDistinctly()
    {
        var host = new FakeHost();
        LoadLua("json1", """
            scrye.setState("arr", scrye.json.encode({1, 2, 3}))
            scrye.setState("obj", scrye.json.encode({x = 3, y = -2}))
            scrye.setState("int", scrye.json.encode(42))
            scrye.setState("empty", scrye.json.encode({}))
            """, host);

        Assert.Equal("[1,2,3]", host.State["arr"]);
        Assert.Equal("42", host.State["int"]);                   // no trailing ".0"
        Assert.Equal("{}", host.State["empty"]);                  // empty table -> object
        Assert.Contains("\"x\":3", host.State["obj"]);
        Assert.Contains("\"y\":-2", host.State["obj"]);
    }

    [Fact]
    public void JsonRoundTripsANestedRoomGraph()
    {
        var host = new FakeHost();
        LoadLua("json2", """
            local area = { name = "sybarus", rooms = { { x = 0, y = 0, exits = { "n", "e" } },
                                                      { x = 0, y = 1, exits = { "s" } } } }
            local t = scrye.json.decode(scrye.json.encode(area))
            scrye.setState("name", t.name)
            scrye.setState("r2x", tostring(t.rooms[2].x))
            scrye.setState("r1e2", t.rooms[1].exits[2])
            scrye.setState("count", tostring(#t.rooms))
            """, host);

        Assert.Equal("sybarus", host.State["name"]);
        Assert.Equal("0", host.State["r2x"]);
        Assert.Equal("e", host.State["r1e2"]);
        Assert.Equal("2", host.State["count"]);
    }

    [Fact]
    public void JsonDecodeMalformedReturnsNilAndError()
    {
        var host = new FakeHost();
        LoadLua("json3", """
            local v, err = scrye.json.decode("{not json")
            scrye.setState("v", tostring(v))
            scrye.setState("err", err or "")
            """, host);

        Assert.Equal("nil", host.State["v"]);
        Assert.StartsWith("json.decode:", host.State["err"]);
    }

    [Fact]
    public void JsonEncodeRejectsFunctionsWithNilAndError()
    {
        var host = new FakeHost();
        LoadLua("json4", """
            local v, err = scrye.json.encode({ f = function() end })
            scrye.setState("v", tostring(v))
            scrye.setState("err", err or "")
            """, host);

        Assert.Equal("nil", host.State["v"]);
        Assert.Contains("function", host.State["err"]);
    }

    // ---- scrye.store.setMany --------------------------------------------------

    [Fact]
    public void SetManyReachesTheHostAsOneBatch()
    {
        var host = new FakeHost();
        LoadLua("batch", """
            scrye.store.setMany{ ["map:town"] = "a|b", ["map:sewers"] = "c", count = 2 }
            """, host);

        IReadOnlyDictionary<string, string> batch = Assert.Single(host.Batches);
        Assert.Equal(3, batch.Count);
        Assert.Equal("a|b", host.Store["map:town"]);
        Assert.Equal("2", host.Store["count"]);   // numbers stringified like set()
    }

    [Fact]
    public void DataStoreSetManyWritesOnceAndSkipsCleanBatches()
    {
        var store = new PluginDataStore(_dir, "3Scapes");
        var batch = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        store.SetMany("mapper", batch);
        Assert.Equal("1", store.Get("mapper", "a"));

        // A clean (no-change) batch must not write: delete the backing file, re-apply the same
        // batch, and the file staying gone is the proof no save happened.
        string file = Directory.GetFiles(Path.Combine(_dir, "3Scapes"))[0];
        File.Delete(file);
        store.SetMany("mapper", batch);
        Assert.False(File.Exists(file));

        // A dirty batch saves — and a fresh store instance sees the whole set.
        store.SetMany("mapper", new Dictionary<string, string> { ["b"] = "3" });
        var reloaded = new PluginDataStore(_dir, "3Scapes");
        Assert.Equal("3", reloaded.Get("mapper", "b"));
        Assert.Equal("1", reloaded.Get("mapper", "a"));
    }

    // ---- scrye.emit / scrye.on ------------------------------------------------

    [Fact]
    public void EmitFansOutThroughTheManagerToOtherPluginsAndSelf()
    {
        var host = new FakeHost();
        PluginDescriptor listener = WritePlugin("listener", """
            scrye.on("mapper.moved", function(data, name, source)
              scrye.setState("heard", source .. ">" .. name .. ">" .. data)
            end)
            """);
        PluginDescriptor talker = WritePlugin("talker", """
            scrye.on("mapper.moved", function(data) scrye.setState("self", data) end)
            scrye.onCommand(function(c) scrye.emit("mapper.moved", c) end)
            """);

        var manager = new PluginManager(new[] { listener, talker }, new[] { "listener", "talker" },
                                        host, _ => { });
        host.EventSink = manager.DispatchPluginEvent;   // what WorldViewModel wires

        manager.DispatchCommand("e");
        Assert.Equal("talker>mapper.moved>e", host.State["heard"]);
        Assert.Equal("e", host.State["self"]);          // the emitter hears its own event
    }

    [Fact]
    public void EmitCyclesAreCutByTheDepthGuardNotAStackOverflow()
    {
        var host = new FakeHost();
        var reports = new List<string>();
        PluginDescriptor loop = WritePlugin("loop", """
            scrye.on("ping", function(d) scrye.emit("ping", d) end)
            """);
        var manager = new PluginManager(new[] { loop }, new[] { "loop" }, host, reports.Add);
        host.EventSink = manager.DispatchPluginEvent;

        manager.DispatchPluginEvent("test", "ping", "x");   // completes instead of recursing forever
        Assert.Contains(reports, r => r.Contains("dropped"));
    }

    // ---- sub-second timers ----------------------------------------------------

    [Fact]
    public void FractionalTimersFireAtTickResolution()
    {
        var host = new FakeHost();
        IPluginRuntime rt = LoadLua("timers", """
            local n = 0
            scrye.after(0.25, function() scrye.setState("quick", "fired") end)
            scrye.every(0.5, function() n = n + 1; scrye.setState("n", tostring(n)) end)
            """, host);

        rt.Tick(0.25);
        Assert.Equal("fired", host.State["quick"]);
        Assert.False(host.State.ContainsKey("n"));   // 0.5s repeat needs two ticks
        rt.Tick(0.25);
        Assert.Equal("1", host.State["n"]);
        rt.Tick(0.25); rt.Tick(0.25);
        Assert.Equal("2", host.State["n"]);
    }

    // ---- colorgrid onHover ----------------------------------------------------

    [Fact]
    public void HoverGetsItsOwnActionIdAlongsideClick()
    {
        var host = new FakeHost();
        IPluginRuntime rt = LoadLua("hover", """
            scrye.addPanel{
              title = "Map",
              widgets = { { type = "colorgrid", bind = "g",
                            onClick = function(c, r, ch) scrye.setState("click", c .. "," .. r .. "," .. ch) end,
                            onHover = function(c, r, ch) scrye.setState("hover", c .. "," .. r .. "," .. ch) end } },
            }
            """, host);

        PanelSpec panel = Assert.Single(host.Panels);
        WidgetSpec grid = Assert.Single(panel.Widgets);
        Assert.False(string.IsNullOrEmpty(grid.Action));
        Assert.False(string.IsNullOrEmpty(grid.HoverAction));
        Assert.NotEqual(grid.Action, grid.HoverAction);

        rt.InvokeCellAction(grid.HoverAction!, 3, 1, "@");
        Assert.Equal("3,1,@", host.State["hover"]);
        Assert.False(host.State.ContainsKey("click"));   // hover never triggers the click fn

        rt.InvokeCellAction(grid.Action!, 2, 2, "#");
        Assert.Equal("2,2,#", host.State["click"]);

        // the leave signal: (-1, -1, "") so a plugin can clear its preview line
        rt.InvokeCellAction(grid.HoverAction!, -1, -1, "");
        Assert.Equal("-1,-1,", host.State["hover"]);
    }

    // ---- list/table onRowClick (API 1.15) -------------------------------------

    /// <summary>A table with <c>onRowClick</c> registers the callback under the widget's
    /// Action id, and the choice invoke path delivers (first cell, 1-based row index) to it —
    /// no new runtime surface, which is the whole design.</summary>
    [Fact]
    public void TableRowClickRidesTheChoicePath()
    {
        var host = new FakeHost();
        IPluginRuntime rt = LoadLua("rows", """
            scrye.addPanel{
              title = "Maps",
              widgets = { { type = "table", bind = "maplist", columns = { "Map", "Rooms" },
                            onRowClick = function(label, index) scrye.setState("picked", label .. "@" .. index) end } },
            }
            """, host);

        PanelSpec panel = Assert.Single(host.Panels);
        WidgetSpec table = Assert.Single(panel.Widgets);
        Assert.Equal("table", table.Type);
        Assert.False(string.IsNullOrEmpty(table.Action));

        rt.InvokeChoice(table.Action!, "Smurfland", 3);
        Assert.Equal("Smurfland@3", host.State["picked"]);
    }

    /// <summary>The same widget through the Jint runtime: onRowClick lands in Action and the
    /// choice invoke reaches the JS function with the same (label, index) pair.</summary>
    [Fact]
    public void JsTableRowClickRidesTheChoicePath()
    {
        var host = new FakeHost();
        IPluginRuntime rt = PluginRuntimeFactory.Create(WritePlugin("jsrows", """
            scrye.addPanel({
              title: "Maps",
              widgets: [ { type: "table", bind: "maplist",
                           onRowClick: function (label, index) { scrye.setState("picked", label + "@" + index); } } ],
            });
            """, lang: "js"), host);
        rt.Load();

        PanelSpec panel = Assert.Single(host.Panels);
        WidgetSpec table = Assert.Single(panel.Widgets);
        Assert.False(string.IsNullOrEmpty(table.Action));

        rt.InvokeChoice(table.Action!, "Smurfland 2", 5);
        Assert.Equal("Smurfland 2@5", host.State["picked"]);
    }

    // ---- JS parity ------------------------------------------------------------

    [Fact]
    public void JsRuntimeSpeaksTheSame16Surface()
    {
        var host = new FakeHost();
        PluginDescriptor desc = WritePlugin("js16", """
            scrye.onCommand(function (c) { scrye.setState("last", c); });
            scrye.on("ping", function (data, name, source) { scrye.setState("heard", source + ">" + data); });
            scrye.store.setMany({ a: "1", b: "2" });
            var t = scrye.json.decode('{"x": 3, "list": [1, 2]}');
            scrye.setState("jx", scrye.json.encode(t.list));
            """, lang: "js");
        var rt = new JsPluginRuntime(desc, host);
        rt.Load();

        rt.DispatchCommand("sw");
        Assert.Equal("sw", host.State["last"]);

        rt.DispatchPluginEvent("ping", "hi", "other");
        Assert.Equal("other>hi", host.State["heard"]);

        IReadOnlyDictionary<string, string> batch = Assert.Single(host.Batches);
        Assert.Equal("1", batch["a"]);
        Assert.Equal("[1,2]", host.State["jx"]);
    }
}

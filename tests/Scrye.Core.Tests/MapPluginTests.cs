using System.Text.Json;
using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Scrye.Scripting.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Acceptance tests for the 3s-map plugin's M1 milestone (docs/Scrye-Map-Design.md), run
/// against the REAL plugin folder in src/Scrye.App/plugins/3s-map — not a copy of its code —
/// so the shipped script is the tested script. The M1 "done when": walking a small area
/// produces a correct, persisted room set (verified via `map export`), and the same walk
/// driven by another plugin's sends maps identically. Both are below.
/// </summary>
public sealed class MapPluginTests
{
    /// <summary>The real plugin folder, found by walking up from the test bin to Scrye.sln.</summary>
    private static string PluginFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrye.sln")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        string folder = Path.Combine(dir!.FullName, "src", "Scrye.App", "plugins", "3s-map");
        Assert.True(File.Exists(Path.Combine(folder, "main.lua")), $"3s-map not found at {folder}");
        return folder;
    }

    private sealed class FakeHost : IPluginHost
    {
        public readonly List<string> Printed = new();
        public readonly List<string> Sent = new();
        public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Store = new(StringComparer.Ordinal);
        public readonly List<PanelSpec> Panels = new();
        public int BatchWrites;
        private readonly List<(string Path, Action<string, string> Cb)> _watchers = new();

        public void Send(string text) => Sent.Add(text);
        public void Print(string pluginId, string text) => Printed.Add(text);
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public string GetState(string path) => State.TryGetValue(path, out string? v) ? v : "";
        public void SetState(string path, string value) => SetGameState(path, value);
        public IDisposable WatchState(string path, Action<string, string> onChange)
        {
            _watchers.Add((path, onChange));
            return new Nothing();
        }
        /// <summary>Set a state path and fire matching watchers — what the real StateStore
        /// does, so scrye.watch-driven behaviour (combat resume) is testable.</summary>
        public void SetGameState(string path, string value)
        {
            State[path] = value;
            foreach ((string p, Action<string, string> cb) in _watchers.ToList())
                if (path == p || path.StartsWith(p + ".", StringComparison.Ordinal)) cb(path, value);
        }
        public void AddPanel(string pluginId, PanelSpec panel) => Panels.Add(panel);
        public string? StoreGet(string pluginId, string key) => Store.TryGetValue(key, out string? v) ? v : null;
        public void StoreSet(string pluginId, string key, string value) => Store[key] = value;
        public void StoreDelete(string pluginId, string key) => Store.Remove(key);
        public string[] StoreKeys(string pluginId) => Store.Keys.ToArray();
        public void StoreSetMany(string pluginId, IReadOnlyDictionary<string, string> values)
        {
            BatchWrites++;
            foreach (KeyValuePair<string, string> kv in values) Store[kv.Key] = kv.Value;
        }
        public readonly List<(string Name, string Data)> Emits = new();
        public Action<string, string>? EventSink;   // (name, data) — play another plugin's part
        public void EmitEvent(string sourceId, string name, string data)
        {
            Emits.Add((name, data));
            EventSink?.Invoke(name, data);
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static IPluginRuntime Load(FakeHost host)
    {
        IPluginRuntime rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest { Id = "3s-map", Name = "3S Map" }, PluginFolder()), host);
        rt.Load();
        return rt;
    }

    private static void Arrive(IPluginRuntime rt, string shortDesc) =>
        rt.ProcessLine($"=S={shortDesc}=S=");

    /// <summary>Run `map export` and parse the JSON line it prints (the plugin's "@@" markup
    /// escaping is undone first — export output is data, not decoration).</summary>
    private static JsonElement Export(IPluginRuntime rt, FakeHost host, string? areaName = null)
    {
        host.Printed.Clear();
        (bool consumed, _) = rt.ProcessInput(areaName is null ? "map export" : $"map export {areaName}");
        Assert.True(consumed, "the map alias should consume 'map export'");
        string json = Assert.Single(host.Printed, line => line.TrimStart().StartsWith("{"));
        return JsonDocument.Parse(json.Replace("@@", "@")).RootElement.Clone();
    }

    private static JsonElement Room(JsonElement export, int x, int y, int z)
    {
        foreach (JsonElement r in export.GetProperty("rooms").EnumerateArray())
            if (r.GetProperty("x").GetInt32() == x && r.GetProperty("y").GetInt32() == y
                                                   && r.GetProperty("z").GetInt32() == z)
                return r;
        Assert.Fail($"no room at {x},{y},{z} in export");
        return default;
    }

    private static string[] Exits(JsonElement room) =>
        room.GetProperty("exits").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

    // ---- M1 "done when", part one: walk by hand, export the correct room set ----

    [Fact]
    public void WalkingASmallAreaProducesTheCorrectPersistedRoomSet()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n, e).");                 // origin, from the login look
        rt.DispatchCommand("n");
        Arrive(rt, "North road (n, s, w).");
        rt.DispatchCommand("n");                            // two fast moves queue FIFO
        rt.DispatchCommand("e");
        Arrive(rt, "Crossroads (s, e).");
        Arrive(rt, "East gate (w, d).");
        rt.DispatchCommand("d");
        Arrive(rt, "Under the gate (u).");

        JsonElement export = Export(rt, host);
        Assert.Equal(5, export.GetProperty("rooms").GetArrayLength());

        JsonElement origin = Room(export, 0, 0, 0);
        Assert.Equal("Temple yard", origin.GetProperty("name").GetString());
        Assert.Equal(new[] { "e", "n" }, Exits(origin));    // sorted, from the parenthetical

        Assert.Equal("North road", Room(export, 0, 1, 0).GetProperty("name").GetString());
        Assert.Equal("Crossroads", Room(export, 0, 2, 0).GetProperty("name").GetString());
        JsonElement gate = Room(export, 1, 2, 0);
        Assert.Equal("East gate", gate.GetProperty("name").GetString());
        Assert.Equal("Under the gate", Room(export, 1, 2, -1).GetProperty("name").GetString());
    }

    // ---- M1 "done when", part two: bot-driven sends map identically ----

    [Fact]
    public void BotStyleAndLongFormMovesMapExactlyLikeTypedShortForms()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Start (ne).");
        // what the stepper's scrye.send("northeast") looks like from onCommand:
        rt.DispatchCommand("northeast");
        Arrive(rt, "Hilltop (sw, up).");
        rt.DispatchCommand("UP");                           // case-insensitive, like a macro key
        Arrive(rt, "Eyrie (down).");

        JsonElement export = Export(rt, host);
        Assert.Equal("Hilltop", Room(export, 1, 1, 0).GetProperty("name").GetString());
        Assert.Equal(new[] { "sw", "u" }, Exits(Room(export, 1, 1, 0)));
        Assert.Equal("Eyrie", Room(export, 1, 1, 1).GetProperty("name").GetString());
    }

    [Fact]
    public void NonMovementCommandsNeverQueueAMove()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Square (n).");
        rt.DispatchCommand("look");
        rt.DispatchCommand("kill troll");
        rt.DispatchCommand("say heading north");            // contains a direction word — not a move
        Arrive(rt, "Square (n).");                          // the look's refresh

        JsonElement export = Export(rt, host);
        Assert.Equal(1, export.GetProperty("rooms").GetArrayLength());
    }

    // ---- failed-move rollback ----

    [Fact]
    public void ARefusedMoveFlushesTheWholePendingQueue()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Cell (n).");
        rt.DispatchCommand("w");                            // both queued before the refusal lands,
        rt.DispatchCommand("n");                            // so BOTH are stale when it does
        rt.ProcessLine("You cannot go west.");
        Arrive(rt, "Cell (n).");                            // a later look must not "move" us

        JsonElement export = Export(rt, host);
        Assert.Equal(1, export.GetProperty("rooms").GetArrayLength());
        Room(export, 0, 0, 0);                              // still exactly the origin
    }

    [Fact]
    public void ALookRefreshUpdatesTheCurrentRoomInPlace()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "A dark room (n).");
        Arrive(rt, "A dark room (n, e).");                  // light a torch, glance again

        JsonElement export = Export(rt, host);
        Assert.Equal(1, export.GetProperty("rooms").GetArrayLength());
        Assert.Equal(new[] { "e", "n" }, Exits(Room(export, 0, 0, 0)));
    }

    // ---- persistence: debounced setMany, survives a reload ----

    [Fact]
    public void TheMapPersistsThroughOneBatchedWriteAndSurvivesAReload()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "North road (s).");
        Assert.Equal(0, host.BatchWrites);                  // debounced — nothing written yet

        for (int i = 0; i < 14; i++) rt.Tick(0.25);         // ride out the 3s debounce
        Assert.Equal(1, host.BatchWrites);                  // ONE setMany for the whole walk
        Assert.Contains("map:default", host.Store.Keys);

        // a fresh runtime over the same store = a client restart
        IPluginRuntime reloaded = Load(host);
        JsonElement export = Export(reloaded, host);
        Assert.Equal(2, export.GetProperty("rooms").GetArrayLength());
        Assert.Equal("North road", Room(export, 0, 1, 0).GetProperty("name").GetString());

        // and the restart resumed at the persisted position: one step south is the origin
        reloaded.DispatchCommand("s");
        Arrive(reloaded, "Temple yard (n).");
        Assert.Equal(2, Export(reloaded, host).GetProperty("rooms").GetArrayLength());
    }

    [Fact]
    public void DisconnectFlushesWithoutWaitingForTheDebounce()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Somewhere (n).");
        rt.DispatchDisconnect();
        Assert.True(host.BatchWrites >= 1);
        Assert.Contains("map:default", host.Store.Keys);
    }

    // ---- areas partition the world ----

    [Fact]
    public void SwitchingAreasIsolatesTheirMapsAndPositions()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Town square (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "High street (s).");

        (bool consumed, _) = rt.ProcessInput("map area sewers");
        Assert.True(consumed);
        Arrive(rt, "Slimy tunnel (e).");
        rt.DispatchCommand("e");
        Arrive(rt, "Junction (w, e).");

        JsonElement sewers = Export(rt, host);
        Assert.Equal("sewers", sewers.GetProperty("name").GetString());
        Assert.Equal(2, sewers.GetProperty("rooms").GetArrayLength());
        Assert.Equal("Slimy tunnel", Room(sewers, 0, 0, 0).GetProperty("name").GetString());

        // town is untouched on disk, exportable by name, and the index knows both
        JsonElement town = Export(rt, host, "default");
        Assert.Equal(2, town.GetProperty("rooms").GetArrayLength());
        Assert.Equal("Town square", Room(town, 0, 0, 0).GetProperty("name").GetString());
        string[] index = JsonDocument.Parse(host.Store["areas"]).RootElement
            .EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        Assert.Equal(new[] { "default", "sewers" }, index);
    }

    // ---- map off means OFF ----

    [Fact]
    public void MapOffStopsCaptureUntilMapOn()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Origin (n).");
        rt.ProcessInput("map off");
        rt.DispatchCommand("n");
        Arrive(rt, "Should not exist (s).");

        rt.ProcessInput("map on");
        JsonElement export = Export(rt, host);
        Assert.Equal(1, export.GetProperty("rooms").GetArrayLength());
        Assert.Equal("Origin", Room(export, 0, 0, 0).GetProperty("name").GetString());
    }

    // ================= M2 — the panel =================
    // Viewport geometry: 21x15 ROOMS centered on the player, north = up, on a
    // WOVEN grid (API 1.7): rooms sit on even 0-based cells of a 41x29 string
    // and the exits between them draw as connector chars on the odd cells. The
    // player is always at (col 20, row 14); a neighbouring room is ±2 away and
    // the connector between them sits on the odd cell they share.

    private const int CenterCol = 20, CenterRow = 14;

    private static string[] Grid(FakeHost host) =>
        host.State["plugin.3s-map.grid"].Split('\n');

    private static WidgetSpec MapWidget(FakeHost host, string type)
    {
        PanelSpec panel = Assert.Single(host.Panels);
        return Assert.Single(panel.Tabs[0].Widgets, w => w.Type == type);
    }

    [Fact]
    public void ThePanelHasTheDesignedShape()
    {
        var host = new FakeHost();
        Load(host);

        PanelSpec panel = Assert.Single(host.Panels);
        Assert.Equal("3S Map", panel.Title);
        Assert.Equal(new[] { "Map", "Rooms" }, panel.Tabs.Select(t => t.Title).ToArray());

        WidgetSpec grid = MapWidget(host, "colorgrid");
        Assert.False(string.IsNullOrEmpty(grid.Action), "grid must be clickable (companion peek)");
        Assert.False(string.IsNullOrEmpty(grid.HoverAction), "grid must be hoverable (desktop peek)");
        Assert.True(grid.Weave, "the map grid renders as a weave (API 1.7)");
        Assert.Equal("accent", grid.Palette!["@"]);        // theme tokens, never hex literals
        Assert.Equal("warning", grid.Palette!["?"]);
        Assert.Equal("line", grid.Palette!["-"]);          // connectors + grid dots are tokens too
        Assert.Equal("inset", grid.Palette!["."]);
        Assert.Equal("info", grid.Palette![">"]);          // boundary rooms stand out
        Assert.Contains("S", grid.Labels!);                // flag letters draw on their tiles
        Assert.Contains("^", grid.Labels!);                // so do the up/down exit marks
        Assert.Contains(">", grid.Labels!);                // and the boundary mark

        WidgetSpec buttons = MapWidget(host, "buttonrow");
        Assert.Equal(new[] { "Up", "Down", "Center", "Stop" }, buttons.Children!.Select(b => b.Text).ToArray());

        WidgetSpec table = Assert.Single(Assert.Single(host.Panels).Tabs[1].Widgets, w => w.Type == "table");
        Assert.Equal(new[] { "Room", "Pos", "Note" }, table.Columns!.ToArray());
    }

    [Fact]
    public void TheGridDrawsYouYourRoomsAndTheFrontier()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n, e).");
        string[] g = Grid(host);
        Assert.Equal(29, g.Length);
        Assert.Equal('@', g[CenterRow][CenterCol]);
        Assert.Equal('?', g[CenterRow - 2][CenterCol]);     // unexplored n exit
        Assert.Equal('?', g[CenterRow][CenterCol + 2]);     // unexplored e exit

        rt.DispatchCommand("n");
        Arrive(rt, "North road (s, n).");
        g = Grid(host);
        Assert.Equal('@', g[CenterRow][CenterCol]);          // viewport recentered on you
        Assert.Equal('#', g[CenterRow + 2][CenterCol]);      // the room you came from
        Assert.Equal('?', g[CenterRow - 2][CenterCol]);      // the frontier ahead
    }

    [Fact]
    public void ExitsDrawAsConnectorsBetweenRoomsAndGridDotsFillTheRest()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n, e, ne).");
        rt.DispatchCommand("n");
        Arrive(rt, "North road (s).");
        string[] g = Grid(host);

        // the room behind us shares its n/s exit with us: a '|' on the odd cell between
        Assert.Equal('|', g[CenterRow + 1][CenterCol]);
        // Temple yard's e and ne exits point at unmapped cells: connector + frontier both draw
        Assert.Equal('-', g[CenterRow + 2][CenterCol + 1]);
        Assert.Equal('?', g[CenterRow + 2][CenterCol + 2]);
        Assert.Equal('/', g[CenterRow + 1][CenterCol + 1]);  // ne rises to the right
        Assert.Equal('?', g[CenterRow][CenterCol + 2]);
        // no exit = no connector: the cell east of us stays blank
        Assert.Equal(' ', g[CenterRow][CenterCol + 1]);
        // every unmapped room position is a faint grid dot; odd cells stay blank
        Assert.Equal('.', g[0][0]);
        Assert.Equal(' ', g[1][1]);
    }

    [Fact]
    public void RoomsWithVerticalExitsAreMarkedOnTheirTiles()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Ladder base (n, u).");
        rt.DispatchCommand("n");
        Arrive(rt, "Pit edge (s, d).");
        rt.DispatchCommand("s");
        Arrive(rt, "Ladder base (n, u, d).");                // deeper look: both ways now

        string[] g = Grid(host);
        Assert.Equal('@', g[CenterRow][CenterCol]);          // you outrank your own mark
        Assert.Equal('v', g[CenterRow - 2][CenterCol]);      // Pit edge, down exit
        rt.DispatchCommand("n");
        Arrive(rt, "Pit edge (s, d).");
        g = Grid(host);
        Assert.Equal('%', g[CenterRow + 2][CenterCol]);      // Ladder base, up AND down
    }

    [Fact]
    public void HoverPeeksAndLeavingRestoresTheCurrentRoom()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "North road (s).");
        string hoverId = MapWidget(host, "colorgrid").HoverAction!;

        rt.InvokeCellAction(hoverId, CenterCol, CenterRow + 2, "#");   // the room behind us
        Assert.Contains("Temple yard", host.State["plugin.3s-map.peek"]);
        Assert.Contains("0,0,0", host.State["plugin.3s-map.peek"]);

        rt.InvokeCellAction(hoverId, 0, 0, ".");                        // empty corner (grid dot)
        Assert.Contains("(unmapped)", host.State["plugin.3s-map.peek"]);

        rt.InvokeCellAction(hoverId, CenterCol, CenterRow + 1, "|");    // a connector edge
        Assert.Contains("North road", host.State["plugin.3s-map.peek"]); // -> back to "where am I"

        rt.InvokeCellAction(hoverId, -1, -1, "");                       // pointer left the grid
        Assert.Contains("North road", host.State["plugin.3s-map.peek"]);
    }

    [Fact]
    public void ClickPeeksTheSameWayForTheCompanion()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        string clickId = MapWidget(host, "colorgrid").Action!;
        rt.InvokeCellAction(clickId, CenterCol, CenterRow, "@");
        Assert.Contains("Temple yard", host.State["plugin.3s-map.peek"]);
    }

    [Fact]
    public void LevelButtonsInspectOtherFloorsAndCenterComesHome()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Cellar (u).");
        rt.DispatchCommand("u");
        Arrive(rt, "Kitchen (d).");                          // now at z=1

        IReadOnlyList<WidgetSpec> buttons = MapWidget(host, "buttonrow").Children!;
        string down = buttons.Single(b => b.Text == "Down").Action!;
        string center = buttons.Single(b => b.Text == "Center").Action!;

        rt.InvokeAction(down);                               // inspect z=0
        string[] g = Grid(host);
        Assert.Equal('^', g[CenterRow][CenterCol]);          // the cellar, marked with its up exit
        Assert.DoesNotContain('@', string.Concat(g));        // you are not on this floor
        Assert.Contains("viewing z=0", host.State["plugin.3s-map.status"]);

        rt.InvokeAction(center);
        Assert.Equal('@', Grid(host)[CenterRow][CenterCol]);
    }

    [Fact]
    public void FlagsDrawOnTheirTilesAndNotesFeedTheRoomsTab()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Weapon shop (e).");
        rt.ProcessInput("map flag s");                       // lowercase in, uppercase tile
        rt.ProcessInput("map note sells vorpal blades");
        rt.DispatchCommand("e");
        Arrive(rt, "High street (w).");

        Assert.Equal('S', Grid(host)[CenterRow][CenterCol - 2]);   // flagged tile, lettered
        Assert.Equal('-', Grid(host)[CenterRow][CenterCol - 1]);   // connected to where you stand

        string list = host.State["plugin.3s-map.roomlist"];
        string row = Assert.Single(list.Split('\n'));
        Assert.Equal("1. Weapon shop\t0,0,0\t[S] sells vorpal blades", row);   // numbered for 'map go <n>'
    }

    [Fact]
    public void FindFiltersTheRoomsTabFromCommandAndSearchBox()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "Temple hall (s, n).");
        rt.DispatchCommand("n");
        Arrive(rt, "Vestry (s).");

        rt.ProcessInput("map find temple");                  // case-insensitive substring
        Assert.Equal(2, host.State["plugin.3s-map.roomlist"].Split('\n').Length);

        PanelSpec panel = Assert.Single(host.Panels);
        string submitId = panel.Tabs[1].Widgets.Single(w => w.Type == "input").Action!;
        rt.InvokeSubmit(submitId, "vestry");                 // the Rooms tab's search box
        string row = Assert.Single(host.State["plugin.3s-map.roomlist"].Split('\n'));
        Assert.StartsWith("1. Vestry\t", row);
    }

    [Fact]
    public void FlagsAndNotesSurviveTheStoreRoundTrip()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Weapon shop (e).");
        rt.ProcessInput("map flag S");
        rt.ProcessInput("map note sells vorpal blades");
        rt.DispatchDisconnect();                             // force the flush

        IPluginRuntime reloaded = Load(host);
        JsonElement room = Room(Export(reloaded, host), 0, 0, 0);
        Assert.Equal("S", room.GetProperty("flag").GetString());
        Assert.Equal("sells vorpal blades", room.GetProperty("note").GetString());
        // you reloaded standing on the flagged tile — the '@' outranks the letter
        Assert.Equal('@', Grid(host)[CenterRow][CenterCol]);
    }

    // ================= M3 — goto / speedwalk =================
    // The engine's contract: one send, one =S= confirmation, 0.25s pacing,
    // then the next send. These helpers play the MUD's half of that.

    /// <summary>Confirm the walk's last-sent step exactly as the live client would:
    /// the session's CommandSent fires for the plugin's own send, then the room lands.</summary>
    private static void ConfirmStep(IPluginRuntime rt, FakeHost host, string roomShort)
    {
        rt.DispatchCommand(host.Sent[^1]);
        Arrive(rt, roomShort);
    }

    private static string Status(FakeHost host) => host.State["plugin.3s-map.status"];

    /// <summary>Maps a vertical shaft: origin, then three rooms straight up, leaving
    /// the player at (0,0,3) — the "three z-levels away" of the M3 done-when.</summary>
    private static void MapShaft(IPluginRuntime rt)
    {
        Arrive(rt, "Shaft bottom (u).");
        rt.DispatchCommand("u");
        Arrive(rt, "Lower gallery (u, d).");
        rt.DispatchCommand("u");
        Arrive(rt, "Upper gallery (u, d).");
        rt.DispatchCommand("u");
        Arrive(rt, "Shaft top (d).");
    }

    // ---- the M3 "done when", part one: a target three z-levels away ----

    [Fact]
    public void GotoWalksThreeLevelsDownAndArrives()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map goto 0 0 0");
        Assert.Contains(host.Printed, l => l.Contains("3 step(s)"));
        Assert.StartsWith("WALKING 1/3", Status(host));

        ConfirmStep(rt, host, "Upper gallery (u, d).");
        rt.Tick(0.25);                                       // pacing releases the next step
        ConfirmStep(rt, host, "Lower gallery (u, d).");
        rt.Tick(0.25);
        ConfirmStep(rt, host, "Shaft bottom (u).");
        rt.Tick(0.25);                                       // the completion check

        Assert.Equal(new[] { "d", "d", "d" }, host.Sent);
        Assert.Contains(host.Printed, l => l.Contains("arrived at 0,0,0"));
        Assert.StartsWith("MAPPING", Status(host));          // the walk is over
        Assert.Contains("Shaft bottom", host.State["plugin.3s-map.peek"]);
    }

    // ---- the M3 "done when", part two: a blocked door aborts clearly ----

    [Fact]
    public void ABlockedDoorAbortsTheWalkWithAClearMessage()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map goto 0 0 0");
        Assert.Single(host.Sent);
        rt.ProcessLine("You cannot go down.");               // the door slammed

        Assert.Contains(host.Printed, l => l.Contains("refused a step"));
        Assert.StartsWith("MAPPING", Status(host));
        for (int i = 0; i < 8; i++) rt.Tick(0.25);           // nothing keeps marching
        Assert.Single(host.Sent);
    }

    [Fact]
    public void ClickingAMappedRoomWalksThereAndMarksTheTarget()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "North road (s).");

        string clickId = MapWidget(host, "colorgrid").Action!;
        rt.InvokeCellAction(clickId, CenterCol, CenterRow + 2, "#");   // the room behind us

        Assert.Equal(new[] { "s" }, host.Sent);              // BFS found the one-step path
        Assert.StartsWith("WALKING 1/1", Status(host));
        Assert.Equal('*', Grid(host)[CenterRow + 2][CenterCol]);       // target marked
        Assert.Contains("Temple yard", host.State["plugin.3s-map.peek"]);

        ConfirmStep(rt, host, "Temple yard (n).");
        rt.Tick(0.25);
        Assert.Contains(host.Printed, l => l.Contains("arrived at 0,0,0"));
    }

    [Fact]
    public void CombatPausesTheWalkAndItResumesWhenTheEnemyDrops()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map goto 0 0 0");
        ConfirmStep(rt, host, "Upper gallery (u, d).");      // step 1 lands...
        host.SetGameState("enemy.name", "a cave troll");     // ...and a troll notices you
        rt.Tick(0.25);                                       // pacing fires into the pause

        Assert.Single(host.Sent, s => s == "d");             // wait — only ONE 'd' so far
        Assert.Contains(host.Printed, l => l.Contains("paused - fighting"));

        host.SetGameState("enemy.name", "");                 // troll handled
        rt.Tick(0.25);                                       // the watcher's pacing timer
        Assert.Equal(2, host.Sent.Count);                    // the walk picked itself back up

        ConfirmStep(rt, host, "Lower gallery (u, d).");
        rt.Tick(0.25);
        ConfirmStep(rt, host, "Shaft bottom (u).");
        rt.Tick(0.25);
        Assert.Contains(host.Printed, l => l.Contains("arrived at 0,0,0"));
    }

    [Fact]
    public void TheWatchdogAbortsAWalkWhoseConfirmationNeverComes()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map goto 0 0 0");
        Assert.Single(host.Sent);
        for (int i = 0; i < 42; i++) rt.Tick(0.25);          // 10.5s of silence

        Assert.Contains(host.Printed, l => l.Contains("no room confirmation"));
        Assert.StartsWith("MAPPING", Status(host));
        Assert.Single(host.Sent);
    }

    [Fact]
    public void TheIdleGuardStopsAWalkAndItStaysStopped()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map goto 0 0 0");
        ConfirmStep(rt, host, "Upper gallery (u, d).");
        rt.DispatchIdle();                                   // nobody is at the keyboard

        Assert.Contains(host.Printed, l => l.Contains("NOT auto-resume"));
        Assert.StartsWith("MAPPING", Status(host));

        // nothing wakes it back up short of a fresh 'map goto' — not time,
        // not a combat state twitch
        host.SetGameState("enemy.name", "x");
        host.SetGameState("enemy.name", "");
        for (int i = 0; i < 8; i++) rt.Tick(0.25);
        Assert.Single(host.Sent);
    }

    [Fact]
    public void MapGoWalksToANumberedRoomsRow()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map find bottom");                  // row 1 = Shaft bottom
        rt.ProcessInput("map go 1");

        Assert.StartsWith("WALKING 1/3", Status(host));
        Assert.Equal(new[] { "d" }, host.Sent);
    }

    [Fact]
    public void GotoRefusesUnmappedAndUnreachableTargetsWithoutSending()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "An island ().");                         // no exits at all
        rt.ProcessInput("map goto 3 3 0");
        Assert.Contains(host.Printed, l => l.Contains("not mapped"));

        rt.ProcessInput("map set 5 5 0");                    // teleport somewhere else
        Arrive(rt, "Another island ().");
        rt.ProcessInput("map goto 0 0 0");                   // mapped, but no path exists
        Assert.Contains(host.Printed, l => l.Contains("no known path"));

        Assert.Empty(host.Sent);
        Assert.StartsWith("MAPPING", Status(host));
    }

    // ================= M4 — trust it =================

    // ---- the drift check ----

    [Fact]
    public void DriftIsDetectedTheRecordIsPreservedAndAMatchingArrivalClearsIt()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "North road (s).");
        rt.DispatchCommand("s");
        Arrive(rt, "Somewhere else entirely (n).");          // the world disagrees

        Assert.StartsWith("DRIFT?", Status(host));
        Assert.Contains(host.Printed, l => l.Contains("DRIFT?") && l.Contains("Temple yard"));
        // OUR record survives — the mapper never quietly overwrites on a mismatch
        Assert.Equal("Temple yard", Room(Export(rt, host), 0, 0, 0).GetProperty("name").GetString());

        rt.DispatchCommand("n");                              // walk on; the world agrees again
        Arrive(rt, "North road (s).");
        Assert.StartsWith("MAPPING", Status(host));
    }

    [Fact]
    public void DriftAbortsAnActiveWalk()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        rt.ProcessInput("map goto 0 0 0");
        rt.DispatchCommand(host.Sent[^1]);
        Arrive(rt, "A shifted maze cell (u, d).");            // not the Upper gallery

        Assert.Contains(host.Printed, l => l.Contains("DRIFT?"));
        Assert.Contains(host.Printed, l => l.Contains("walk aborted"));
        for (int i = 0; i < 8; i++) rt.Tick(0.25);
        Assert.Single(host.Sent);                             // nothing keeps marching
        Assert.StartsWith("DRIFT?", Status(host));
    }

    [Fact]
    public void MapUndoForgetsTheLastLearnedRoomAndReSeatsYou()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Temple yard (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "A phantom room (s).");                    // learned in error

        rt.ProcessInput("map undo");
        Assert.Contains(host.Printed, l => l.Contains("forgotten"));
        JsonElement export = Export(rt, host);
        Assert.Equal(1, export.GetProperty("rooms").GetArrayLength());
        Assert.Contains("Temple yard", host.State["plugin.3s-map.peek"]);   // back where you stood
    }

    // ---- special links ----

    [Fact]
    public void AnArmedLinkBindsToTheUniqueRoomWithThatNameAndGotoWalksThroughIt()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Town square ().");                        // no compass exits anywhere
        rt.ProcessInput("map set 5 5 0");
        Arrive(rt, "Hidden shrine ().");
        rt.ProcessInput("map set 0 0 0");
        Arrive(rt, "Town square ().");

        rt.ProcessInput("map link enter shrine");             // arm...
        rt.DispatchCommand("enter shrine");                   // ...send...
        Arrive(rt, "Hidden shrine ().");                      // ...and the arrival closes it

        Assert.Contains(host.Printed, l => l.Contains("link 'enter shrine' bound: 0,0,0 -> 5,5,0"));
        Assert.Contains("5,5,0", host.State["plugin.3s-map.peek"]);        // we tracked the jump

        rt.ProcessInput("map set 0 0 0");                     // back home, then use the link via goto
        Arrive(rt, "Town square ().");
        rt.ProcessInput("map goto 5 5 0");
        Assert.Equal("enter shrine", host.Sent[^1]);          // BFS pathed THROUGH the link
        rt.DispatchCommand("enter shrine");                   // known link → tracked automatically
        Arrive(rt, "Hidden shrine ().");
        rt.Tick(0.25);
        Assert.Contains(host.Printed, l => l.Contains("arrived at 5,5,0"));
    }

    [Fact]
    public void AnArmedLinkParksAnUnknownDestinationOnAFreeCell()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Wizard's study ().");
        rt.ProcessInput("map link touch orb");
        rt.DispatchCommand("touch orb");
        Arrive(rt, "Pocket dimension ().");                   // a name the map has never seen

        Assert.Contains(host.Printed, l => l.Contains("new cell"));
        JsonElement export = Export(rt, host);
        Assert.Equal(2, export.GetProperty("rooms").GetArrayLength());
        JsonElement study = Room(export, 0, 0, 0);
        JsonElement dest = study.GetProperty("links").GetProperty("touch orb");
        // the parked room really exists, named from the arrival
        JsonElement parked = Room(export, dest.GetProperty("x").GetInt32(),
                                          dest.GetProperty("y").GetInt32(),
                                          dest.GetProperty("z").GetInt32());
        Assert.Equal("Pocket dimension", parked.GetProperty("name").GetString());
    }

    [Fact]
    public void ACrossAreaLinkSwitchesAreasWhenUsed()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Town gate (n).");
        rt.ProcessInput("map link enter grate = sewers 0 0 0");
        rt.DispatchCommand("enter grate");
        Arrive(rt, "Sewer entrance (e).");

        Assert.Equal("sewers", host.State["plugin.3s-map.area"]);
        Assert.Contains("Sewer entrance", host.State["plugin.3s-map.peek"]);

        // the town side persisted its link before the jump
        JsonElement town = Export(rt, host, "default");
        JsonElement link = Room(town, 0, 0, 0).GetProperty("links").GetProperty("enter grate");
        Assert.Equal("sewers", link.GetProperty("area").GetString());
    }

    [Fact]
    public void LinksSurviveTheStoreRoundTrip()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Town square ().");
        rt.ProcessInput("map set 5 5 0");
        Arrive(rt, "Hidden shrine ().");
        rt.ProcessInput("map set 0 0 0");
        Arrive(rt, "Town square ().");
        rt.ProcessInput("map link enter shrine = 5 5 0");     // explicit in-area bind
        rt.DispatchDisconnect();                              // flush

        IPluginRuntime reloaded = Load(host);
        reloaded.ProcessInput("map goto 5 5 0");              // pathable straight after reload
        Assert.Equal("enter shrine", host.Sent[^1]);
    }

    // ---- seeding ----

    /// <summary>A temp copy of the real main.lua plus a maps.json seed, so seeding is
    /// tested through the manifest data path without touching the shipped (empty) seed file.</summary>
    private IPluginRuntime LoadSeeded(FakeHost host, string mapsJson, string dir)
    {
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(PluginFolder(), "main.lua"), Path.Combine(dir, "main.lua"), overwrite: true);
        File.WriteAllText(Path.Combine(dir, "maps.json"), mapsJson);
        IPluginRuntime rt = new KeraLuaPluginRuntime(new PluginDescriptor(
            new PluginManifest
            {
                Id = "3s-map",
                Name = "3S Map",
                Data = new Dictionary<string, string> { ["maps"] = "maps.json" },
            }, dir), host);
        rt.Load();
        return rt;
    }

    private const string SeedTown = """
        { "areas": [ { "name": "seedtown", "rooms": [
            { "x": 0, "y": 0, "z": 0, "name": "Seed square", "exits": ["n"] },
            { "x": 0, "y": 1, "z": 0, "name": "Seed north", "exits": ["s"] } ] } ] }
        """;

    [Fact]
    public void SeededAreasLoadFromMapsJsonUntilYouHaveMappedThemYourself()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrye-map-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new FakeHost();
            IPluginRuntime rt = LoadSeeded(host, SeedTown, dir);

            rt.ProcessInput("map area seedtown");
            Assert.Contains(host.Printed, l => l.Contains("seeded from maps.json"));
            JsonElement export = Export(rt, host);
            Assert.Equal(2, export.GetProperty("rooms").GetArrayLength());
            Assert.Equal("Seed square", Room(export, 0, 0, 0).GetProperty("name").GetString());
            // and it is walkable immediately: the seed's exits feed the BFS
            rt.ProcessInput("map goto 0 1 0");
            Assert.Equal(new[] { "n" }, host.Sent);

            // a store version of the same area beats the seed
            var host2 = new FakeHost();
            host2.Store["map:seedtown"] = """
                { "name": "seedtown", "rooms": [
                  { "x": 9, "y": 9, "z": 0, "name": "My own room", "exits": [] } ] }
                """;
            IPluginRuntime rt2 = LoadSeeded(host2, SeedTown, dir + "-b");
            rt2.ProcessInput("map area seedtown");
            JsonElement mine = Export(rt2, host2);
            Assert.Equal(1, mine.GetProperty("rooms").GetArrayLength());
            Assert.Equal("My own room", Room(mine, 9, 9, 0).GetProperty("name").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            try { Directory.Delete(dir + "-b", recursive: true); } catch (IOException) { }
        }
    }

    // ================= M6 — area boundaries =================
    // 3Scapes is Pinnacle -> three hubs -> areas, and the map is one area per
    // map, stitched at the boundaries: a room can record a link on a plain
    // compass direction (onCommand checks links BEFORE dead reckoning), a
    // compass crossing records its own return link on first use, and
    // 'map enter <area>' arms the next command as the boundary.

    [Fact]
    public void ACompassExitCanBeALinkAndCrossingItSwitchesAreas()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "North gate (n, s).");
        rt.ProcessInput("map link north = fantasy 0 0 0");   // long form stored canonical
        // the linked dir keeps its connector stub but stops drawing a frontier '?'
        Assert.Equal('|', Grid(host)[CenterRow - 1][CenterCol]);
        Assert.Equal('.', Grid(host)[CenterRow - 2][CenterCol]);

        rt.DispatchCommand("north");                          // and the crossing tracks you
        Arrive(rt, "Fantasy trailhead (s).");

        Assert.Equal("fantasy", host.State["plugin.3s-map.area"]);
        Assert.Contains("Fantasy trailhead", host.State["plugin.3s-map.peek"]);
        Assert.Contains("0,0,0", host.State["plugin.3s-map.peek"]);
        // no phantom room was dead-reckoned north of the gate
        JsonElement town = Export(rt, host, "default");
        Assert.Equal(1, town.GetProperty("rooms").GetArrayLength());
    }

    [Fact]
    public void ACompassCrossingRecordsItsReturnLinkAndItLeadsBack()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "North gate (n, s).");
        rt.ProcessInput("map link n = fantasy 0 0 0");
        rt.DispatchCommand("n");
        Arrive(rt, "Fantasy trailhead (s).");

        // n out means s back: recorded automatically, pointing at the old area
        JsonElement back = Room(Export(rt, host), 0, 0, 0)
            .GetProperty("links").GetProperty("s");
        Assert.Equal("default", back.GetProperty("area").GetString());

        rt.DispatchCommand("s");                              // walk back through it
        Arrive(rt, "North gate (n, s).");
        Assert.Equal("default", host.State["plugin.3s-map.area"]);
        Assert.Contains("North gate", host.State["plugin.3s-map.peek"]);
    }

    [Fact]
    public void MapEnterArmsCreatesTheAreaAndBindsTheBoundaryBothWays()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Hub plaza (e).");
        rt.ProcessInput("map enter fantasy-woods");
        rt.DispatchCommand("e");                              // the very next command closes it
        Arrive(rt, "Forest edge (w).");

        Assert.Equal("fantasy-woods", host.State["plugin.3s-map.area"]);
        Assert.Contains("Forest edge", host.State["plugin.3s-map.peek"]);

        JsonElement hubLink = Room(Export(rt, host, "default"), 0, 0, 0)
            .GetProperty("links").GetProperty("e");
        Assert.Equal("fantasy-woods", hubLink.GetProperty("area").GetString());
        JsonElement ret = Room(Export(rt, host), 0, 0, 0)
            .GetProperty("links").GetProperty("w");
        Assert.Equal("default", ret.GetProperty("area").GetString());

        rt.DispatchCommand("w");                              // and the return works
        Arrive(rt, "Hub plaza (e).");
        Assert.Equal("default", host.State["plugin.3s-map.area"]);
    }

    [Fact]
    public void MapEnterWithAPortalCommandRecordsForwardOnly()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Chaos shrine ().");
        rt.ProcessInput("map enter chaos-rift");
        rt.DispatchCommand("touch monolith");
        Arrive(rt, "Rift mouth ().");

        Assert.Equal("chaos-rift", host.State["plugin.3s-map.area"]);
        JsonElement fwd = Room(Export(rt, host, "default"), 0, 0, 0)
            .GetProperty("links").GetProperty("touch monolith");
        Assert.Equal("chaos-rift", fwd.GetProperty("area").GetString());
        // a portal's way back is never guessable, so nothing was recorded
        Assert.False(Room(Export(rt, host), 0, 0, 0).TryGetProperty("links", out _));
    }

    [Fact]
    public void MapBackBindsTheWayHomeToTheRoomYouCrossedFrom()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "Pinnacle gate ().");
        rt.ProcessInput("map set 3 5 0");                     // the gate is not at the origin
        Arrive(rt, "Pinnacle gate ().");
        rt.ProcessInput("map enter chaos");
        rt.DispatchCommand("chaos");                          // a portal-style hub exit
        Arrive(rt, "Chaos hub ().");
        Assert.Equal("chaos", host.State["plugin.3s-map.area"]);

        rt.ProcessInput("map back pinnacle");                 // no coordinates needed
        JsonElement home = Room(Export(rt, host), 0, 0, 0)
            .GetProperty("links").GetProperty("pinnacle");
        Assert.Equal("default", home.GetProperty("area").GetString());
        Assert.Equal(3, home.GetProperty("x").GetInt32());
        Assert.Equal(5, home.GetProperty("y").GetInt32());

        rt.DispatchCommand("pinnacle");                       // and it works
        Arrive(rt, "Pinnacle gate ().");
        Assert.Equal("default", host.State["plugin.3s-map.area"]);
        Assert.Contains("3,5,0", host.State["plugin.3s-map.peek"]);
    }

    [Fact]
    public void BoundaryRoomsAreMarkedOnTheMapAndAFlagStillOutranksThem()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);

        Arrive(rt, "West gate (e, w).");
        rt.ProcessInput("map link w = fantasy 0 0 0");        // this room is now a door out
        rt.DispatchCommand("e");
        Arrive(rt, "High street (w).");

        Assert.Equal('>', Grid(host)[CenterRow][CenterCol - 2]);   // the gate, marked

        rt.DispatchCommand("w");
        Arrive(rt, "West gate (e, w).");
        rt.ProcessInput("map flag F");                        // the user's letter wins
        rt.DispatchCommand("e");
        Arrive(rt, "High street (w).");
        Assert.Equal('F', Grid(host)[CenterRow][CenterCol - 2]);
    }

    // ---- the event feed ----

    [Fact]
    public void ArrivalsAndWalkLifecycleEmitEventsForOtherPlugins()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        MapShaft(rt);

        (string, string) roomEvent = host.Emits.Last(e => e.Item1 == "map.room");
        Assert.Contains("\"name\":\"Shaft top\"", roomEvent.Item2);
        Assert.Contains("\"area\":\"default\"", roomEvent.Item2);

        rt.ProcessInput("map goto 0 0 0");
        (string, string) started = Assert.Single(host.Emits, e => e.Item1 == "map.walk.started");
        Assert.Contains("\"steps\":3", started.Item2);

        rt.ProcessInput("map stop");
        (string, string) stopped = Assert.Single(host.Emits, e => e.Item1 == "map.walk.stopped");
        Assert.Contains("walk stopped", stopped.Item2);
    }

    // ---- wasm pathfinder delegation (sdk/rust/plugins/3s-pathfinder) ----
    // The mapper delegates goto searches over synchronous inter-plugin events and falls
    // back to its own BFS when nothing answers (which is what every OTHER test in this
    // file exercises). These script the pathfinder's half of the protocol.

    private static void MapThreeNorthRooms(IPluginRuntime rt)
    {
        Arrive(rt, "A room (n).");
        rt.DispatchCommand("n");
        Arrive(rt, "B room (n, s).");
        rt.DispatchCommand("n");
        Arrive(rt, "C room (s).");           // player ends at 0,2,0
    }

    [Fact]
    public void GotoDelegatesToAPathfinderAndWalksItsAnswer()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        var requests = new List<JsonElement>();
        long knownSerial = -1;
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
                : $"{{\"id\":{id},\"needArea\":true}}", "3s-pathfinder");
        };
        MapThreeNorthRooms(rt);
        host.Sent.Clear();

        rt.ProcessInput("map goto 0 0 0");
        Assert.Equal(2, requests.Count);                                   // needArea handshake
        Assert.False(requests[0].TryGetProperty("rooms", out _));          // roomless first ask
        Assert.Equal(3, requests[1].GetProperty("rooms").GetArrayLength()); // graph on resend
        Assert.Equal("s", host.Sent[0]);                                   // walks the answer

        // unchanged map: the cache is warm — one request, no graph shipped
        rt.ProcessInput("map stop");
        requests.Clear();
        rt.ProcessInput("map goto 0 0 0");
        JsonElement warm = Assert.Single(requests);
        Assert.False(warm.TryGetProperty("rooms", out _));
    }

    [Fact]
    public void PathfinderUnreachableIsAuthoritativeNotRetriedLocally()
    {
        var host = new FakeHost();
        IPluginRuntime rt = Load(host);
        host.EventSink = (name, data) =>
        {
            if (name != "map.path.find") return;
            long id = JsonDocument.Parse(data).RootElement.GetProperty("id").GetInt64();
            rt.DispatchPluginEvent("map.path.result", $"{{\"id\":{id},\"found\":false}}", "3s-pathfinder");
        };
        MapThreeNorthRooms(rt);
        host.Sent.Clear();
        rt.ProcessInput("map goto 0 0 0");
        Assert.Contains(host.Printed, l => l.Contains("no known path"));
        Assert.Empty(host.Sent);         // the local BFS (which WOULD find s,s) was not consulted
    }

}

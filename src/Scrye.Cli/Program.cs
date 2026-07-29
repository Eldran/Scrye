using System.Text;
using Scrye.Core.Automation;
using Scrye.Core.Events;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Mip;
using Scrye.Core.Profiles;
using Scrye.Core.Session;
using Scrye.Core.Text;

// Scrye.Cli — a dependency-free harness for the engine core.
//   --selftest        canned bytes through telnet + ANSI
//   --automation      the trigger/alias/timer/variable engine
//   --protocol        telnet option negotiation (GMCP/NAWS/TTYPE/MSSP/echo)
//   <host> <port>     connect to a live MUD and stream to the console

if (args.Length >= 1 && args[0] == "--selftest") { SelfTest(); return 0; }
if (args.Length >= 1 && args[0] == "--automation") { AutomationTest(); return 0; }
if (args.Length >= 1 && args[0] == "--protocol") { ProtocolTest(); return 0; }
if (args.Length >= 1 && args[0] == "--mip") { MipTest(); return 0; }
if (args.Length >= 1 && args[0] == "--profile") { ProfileTest(); return 0; }
if (args.Length >= 1 && args[0] == "--worlds") { WorldsTest(); return 0; }
if (args.Length >= 1 && args[0] == "--events") { EventsTest(); return 0; }
if (args.Length >= 2 && int.TryParse(args[1], out int port)) { await ConnectAsync(args[0], port); return 0; }

Console.WriteLine("usage: scrye-cli --selftest | --automation | --protocol | --mip | --profile | --worlds | --events | <host> <port>");
return 1;

static void SelfTest()
{
    Console.WriteLine("== Scrye engine self-test ==\n");
    var telnet = new TelnetLayer();
    var sent = new List<byte>();
    telnet.SendData += b => sent.AddRange(b);
    var ansi = new AnsiParser();
    int lineNo = 0;
    ansi.LineCompleted += line =>
    {
        lineNo++;
        Console.WriteLine($"line {lineNo}{(line.IsPrompt ? " (prompt)" : "")}: \"{line.PlainText}\"");
        foreach (var r in line.Runs)
            Console.WriteLine($"    run: \"{r.Text}\"  fg=#{r.Fore.R:X2}{r.Fore.G:X2}{r.Fore.B:X2} flags={r.Flags}");
    };

    var bytes = new List<byte>();
    bytes.AddRange(new byte[] { 255, 251, 1 });                       // IAC WILL ECHO -> expect IAC DO ECHO
    bytes.AddRange(Encoding.UTF8.GetBytes("\x1b[1;32mHello\x1b[0m world\r\n"));
    bytes.AddRange(Encoding.UTF8.GetBytes("\x1b[38;5;208m256-orange\x1b[0m and \x1b[38;2;10;20;250mtruecolour\x1b[0m\n"));
    bytes.AddRange(Encoding.UTF8.GetBytes("Enter command> "));

    byte[] data = telnet.Process(bytes.ToArray());
    Console.Write("telnet reply bytes:");
    foreach (byte b in sent) Console.Write($" {b}");
    Console.WriteLine(sent.SequenceEqual(new byte[] { 255, 253, 1 }) ? "   (correct: IAC DO ECHO)" : "   (UNEXPECTED)");
    Console.WriteLine();

    ansi.Feed(Encoding.UTF8.GetString(data));
    ansi.FlushAsPrompt();
    Console.WriteLine($"\n{lineNo} lines parsed. Self-test complete.");
}

static void AutomationTest()
{
    Console.WriteLine("== Scrye automation self-test ==\n");
    var vars = new VariableStore();
    var engine = new AutomationEngine(vars);
    var rec = new RecordingActions(vars);

    engine.AddAlias(new AliasDef { Name = "attack", Pattern = "kk *", Send = "kill %1" });
    engine.AddTrigger(new TriggerDef { Name = "hp", IsRegex = true, Pattern = @"^HP:\s*(\d+)/(\d+)", SendTo = SendTo.Variable, Variable = "hp", Send = "%1" });
    engine.AddTrigger(new TriggerDef { Name = "greet", Pattern = "* says hello", Send = "wave", OneShot = true });
    engine.AddTrigger(new TriggerDef { Name = "ontell", Pattern = "* tells you *", SendTo = SendTo.Script, Script = "onTell" });
    engine.AddTimer(new TimerDef { Name = "heartbeat", IntervalSeconds = 5, Send = "save" });
    Console.WriteLine($"registered: {engine.TriggerCount} triggers, {engine.AliasCount} aliases, {engine.TimerCount} timers\n");

    Feed(engine, rec, input: "kk orc");
    Feed(engine, rec, line: "HP: 42/100 MP: 10/10");
    Feed(engine, rec, line: "Bob says hello");
    Feed(engine, rec, line: "Bob says hello");
    Feed(engine, rec, line: "Alice tells you hi");
    Console.WriteLine("\n-- 6-second tick (heartbeat interval 5s) --");
    engine.Tick(6.0, rec);
    Console.WriteLine($"\nvariable hp = {vars.Get("hp") ?? "(unset)"}");
    Console.WriteLine($"triggers remaining: {engine.TriggerCount} (one-shot 'greet' should be gone)");
    Console.WriteLine("\nAutomation self-test complete.");
}

static void ProtocolTest()
{
    Console.WriteLine("== Scrye protocol self-test ==\n");
    var t = new TelnetLayer();
    var sent = new List<byte[]>();
    t.SendData += b => sent.Add(b);
    t.GmcpReceived += (p, j) => Console.WriteLine($"    GMCP: {p} = {j}");
    t.MsspReceived += d => Console.WriteLine($"    MSSP: {string.Join(", ", d.Select(kv => kv.Key + "=" + kv.Value))}");
    t.ServerEchoChanged += on => Console.WriteLine($"    echo: server-echoes={on}");
    t.WindowSize = () => (100, 30);

    FeedT(t, "server: WILL GMCP", new byte[] { 255, 251, 201 });
    FeedT(t, "server: DO NAWS", new byte[] { 255, 253, 31 });
    FeedT(t, "server: DO TTYPE", new byte[] { 255, 253, 24 });
    FeedT(t, "server: SB TTYPE SEND (x3)", new byte[] { 255, 250, 24, 1, 255, 240, 255, 250, 24, 1, 255, 240, 255, 250, 24, 1, 255, 240 });
    FeedT(t, "server: WILL ECHO", new byte[] { 255, 251, 1 });
    FeedT(t, "server: WONT ECHO", new byte[] { 255, 252, 1 });
    FeedT(t, "server: WILL MCCP2 (should refuse)", new byte[] { 255, 251, 86 });

    var gmcp = new List<byte> { 255, 250, 201 };
    gmcp.AddRange(Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42,\"maxhp\":100}"));
    gmcp.Add(255); gmcp.Add(240);
    FeedT(t, "server: SB GMCP Char.Vitals {...}", gmcp.ToArray());

    var mssp = new List<byte> { 255, 250, 70, 1 };
    mssp.AddRange(Encoding.ASCII.GetBytes("NAME")); mssp.Add(2); mssp.AddRange(Encoding.ASCII.GetBytes("3Scapes"));
    mssp.Add(1); mssp.AddRange(Encoding.ASCII.GetBytes("PLAYERS")); mssp.Add(2); mssp.AddRange(Encoding.ASCII.GetBytes("42"));
    mssp.Add(255); mssp.Add(240);
    FeedT(t, "server: SB MSSP NAME=3Scapes PLAYERS=42", mssp.ToArray());

    Console.WriteLine("\n-- replies Scrye sent to the server --");
    foreach (byte[] s in sent) Console.WriteLine("    " + DescribeReply(s));
    Console.WriteLine("\nProtocol self-test complete.");
}

static void MipTest()
{
    Console.WriteLine("== Scrye MIP self-test ==\n");
    var vars = new VariableStore();
    var parser = new MipParser();
    var proc = new MipProcessor(vars);
    parser.MessageReceived += m => { Console.WriteLine($"    frame: id={m.Id} tag={m.Tag} data=\"{m.Data}\""); proc.Handle(m); };
    proc.Notice += t => Console.WriteLine($"    NOTICE: {t}");
    proc.Tell += t => Console.WriteLine($"    TELL: {t}");
    proc.Channel += (c, msg) => Console.WriteLine($"    CHANNEL [{c}]: {msg}");

    const string id = "12345";
    string chunk1 = "You see a goblin.\r\n#K%" + id + "017FFFA~42~B~100~C~10~D~10~K~goblin~L~55\r\nHP: ok\r\n";
    string chunk2 = "#K%" + id + "020BABt~Merlin~hi there\r\n#K%" + id + "030CAAsay~gossip~Bob~hello all\r\n";

    Console.WriteLine("-- feed chunk 1 --");
    Console.WriteLine("  visible: " + parser.Process(chunk1).Replace("\r\n", "\\n"));
    Console.WriteLine("-- feed chunk 2 --");
    Console.WriteLine("  visible: " + parser.Process(chunk2).Replace("\r\n", "\\n"));

    Console.WriteLine($"\nvitals: hp={vars.Get("hp")}/{vars.Get("hpmax")} sp={vars.Get("sp")}/{vars.Get("spmax")} enemy={vars.Get("enemy_name")}({vars.Get("enemy_hp")})");
    Console.WriteLine("\nMIP self-test complete.");
}

static void ProfileTest()
{
    Console.WriteLine("== Scrye profile-cascade self-test ==\n");

    var global = new ProfileLayer
    {
        Kind = LayerKind.Global, Name = "global",
        FontFamily = "Cascadia Mono", FontSize = 14, Theme = "Dark",
        Triggers = { new TriggerDef { Name = "clock", Pattern = "*tick*", Send = "time" } },
    };
    var mud = new ProfileLayer
    {
        Kind = LayerKind.Mud, Name = "3Scapes",
        Host = "3k.org", Port = 3200, EnableMip = true,
        Triggers = { new TriggerDef { Name = "welcome", Pattern = "*Welcome*", Send = "look" } },
        Aliases = { new AliasDef { Name = "gd", Pattern = "gd", Send = "get all from corpse" } },
    };
    var character = new ProfileLayer
    {
        Kind = LayerKind.Character, Name = "Warrior",
        // override the MUD-level 'welcome' trigger, suppress the global 'clock',
        // add a combat package, set a character variable
        Triggers = { new TriggerDef { Name = "welcome", Pattern = "*Welcome*", Send = "wield sword" },
                     new TriggerDef { Name = "flee", Pattern = "*low on health*", Send = "flee" } },
        Suppress = { "clock" },
        Variables = { ["class"] = "viking" },
    };

    var eff = ProfileResolver.Resolve(new[] { global, mud, character });

    Console.WriteLine($"resolved world : {eff.World.Name}  {eff.World.Host}:{eff.World.Port}  mip={eff.World.EnableMip}");
    Console.WriteLine($"app (from global): font={eff.FontFamily} {eff.FontSize}  theme={eff.Theme}");
    Console.WriteLine($"variables      : {string.Join(", ", eff.Variables.Select(kv => kv.Key + "=" + kv.Value))}");
    Console.WriteLine("triggers (merged):");
    foreach (var t in eff.Triggers.OrderBy(t => t.Name))
        Console.WriteLine($"    {t.Name,-10} -> \"{t.Send}\"");
    Console.WriteLine("aliases        :");
    foreach (var a in eff.Aliases) Console.WriteLine($"    {a.Name,-10} -> \"{a.Send}\"");

    Console.WriteLine("\nexpected: 'clock' suppressed, 'welcome' = 'wield sword' (character override), 'flee' added.");

    // JSON round-trip
    string json = ProfileStore.Serialize(character);
    var back = ProfileStore.Deserialize(json);
    Console.WriteLine($"\nJSON round-trip: {back.Name}, {back.Triggers.Count} triggers, suppress=[{string.Join(",", back.Suppress)}]  (bytes={json.Length})");
    Console.WriteLine("\nProfile self-test complete.");
}

static void WorldsTest()
{
    Console.WriteLine("== Scrye world-store self-test ==\n");
    string root = Path.Combine(Path.GetTempPath(), "scrye_worlds_" + Guid.NewGuid().ToString("N"));
    var store = new ProfileStore(root);

    store.SaveGlobal(new ProfileLayer { Kind = LayerKind.Global, Name = "global", Theme = "Dark", FontFamily = "Cascadia Mono" });
    store.SaveWorld("3Scapes", new ProfileLayer { Host = "3k.org", Port = 3200, EnableMip = true,
        Aliases = { new AliasDef { Name = "gd", Send = "get all from corpse" } } });
    store.SaveWorld("Aardwolf", new ProfileLayer { Host = "aardmud.org", Port = 4000 });

    Console.WriteLine("worlds: " + string.Join(", ", store.ListWorlds()));
    var eff = store.ResolveWorld("3Scapes");
    Console.WriteLine($"resolved 3Scapes: {eff.World.Host}:{eff.World.Port} mip={eff.World.EnableMip} theme={eff.Theme} font={eff.FontFamily} aliases={eff.Aliases.Count}");

    var store2 = new ProfileStore(root);
    var reloaded = store2.LoadWorld("3Scapes");
    Console.WriteLine($"reloaded from disk: host={reloaded?.Host} port={reloaded?.Port} enableMip={reloaded?.EnableMip}");

    store.DeleteWorld("Aardwolf");
    Console.WriteLine("after delete Aardwolf: " + string.Join(", ", store.ListWorlds()));

    Directory.Delete(root, true);
    Console.WriteLine("\nWorld-store self-test complete.");
}

static void EventsTest()
{
    Console.WriteLine("== Scrye event-pipeline self-test ==\n");

    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var bus = new EventBus();
    int clockCalls = 0;
    bus.Clock = () => t0.AddMilliseconds(250 * clockCalls++);   // deterministic, monotonic

    var log = new EventLog(200);
    bus.Subscribe(log);
    var rec = new SessionRecorder("3Scapes", t0);
    bus.Subscribe(rec);
    bus.Emitted += ev => Console.WriteLine("  " + ev);

    // Wire an automation engine exactly as the session does: hits -> bus events,
    // and an IWorldActions that emits Sent / Notice / VariableChanged through the bus.
    var vars = new VariableStore();
    var engine = new AutomationEngine(vars);
    var actions = new BusActions(bus, vars);
    engine.Hit += hit =>
    {
        SessionEventKind kind = hit.Kind switch
        {
            AutomationHitKind.Trigger => SessionEventKind.TriggerMatched,
            AutomationHitKind.Alias => SessionEventKind.AliasMatched,
            _ => SessionEventKind.TimerFired,
        };
        bus.Emit(kind, hit.Input, hit.Name, hit.Action);
    };

    engine.AddAlias(new AliasDef { Name = "attack", Pattern = "kk *", Send = "kill %1" });
    engine.AddTrigger(new TriggerDef { Name = "hp", IsRegex = true, Pattern = @"^HP:\s*(\d+)/(\d+)", SendTo = SendTo.Variable, Variable = "hp", Send = "%1" });
    engine.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*You are low*", Send = "flee" });
    engine.AddTimer(new TimerDef { Name = "autosave", IntervalSeconds = 5, Send = "save" });

    Console.WriteLine("-- driving a session flow --");
    bus.Emit(SessionEventKind.Connecting, "3k.org:3200");
    bus.Emit(SessionEventKind.Connected, "3k.org:3200");
    FeedLine(bus, engine, actions, "Welcome to 3Scapes!");
    FeedInput(bus, engine, actions, "kk orc");
    FeedLine(bus, engine, actions, "HP: 42/100 MP: 10/10");
    FeedLine(bus, engine, actions, "You are low on health!");
    Console.WriteLine("-- 5s tick (autosave) --");
    engine.Tick(5.0, actions);

    // --- the automation timeline (the debugger's core view) ---
    Console.WriteLine($"\n-- automation timeline ({log.Count} events buffered) --");
    foreach (SessionEvent ev in log.Snapshot())
        if (ev.Kind is SessionEventKind.TriggerMatched or SessionEventKind.AliasMatched or SessionEventKind.TimerFired)
            Console.WriteLine($"    {ev.TimeUtc:HH:mm:ss.fff}  {ev.Kind,-14} [{ev.Label}] {ev.Detail}");

    // --- recording round-trip (in-memory + on disk) ---
    string jsonl = rec.ToJsonLines();
    SessionRecording parsed = SessionRecorder.Parse(jsonl);
    Console.WriteLine($"\n-- recording --");
    Console.WriteLine($"    captured {rec.Events.Count} events, {jsonl.Length} bytes JSON-lines");
    Console.WriteLine($"    parsed back {parsed.Events.Count} events, duration {parsed.Duration.TotalSeconds:0.###}s");

    string path = Path.Combine(Path.GetTempPath(), "scrye_evt_" + Guid.NewGuid().ToString("N") + ".scryerec");
    rec.Save(path);
    SessionRecording fromDisk = SessionRecorder.Load(path);
    File.Delete(path);
    Console.WriteLine($"    saved + reloaded from disk: {fromDisk.Events.Count} events, world='{fromDisk.Header.World}'");

    // --- replay ---
    var counts = new Dictionary<SessionEventKind, int>();
    new SessionReplayer(fromDisk).Replay(ev => counts[ev.Kind] = counts.GetValueOrDefault(ev.Kind) + 1);
    Console.WriteLine("    replay kind counts: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));

    // --- dry-run simulator (no side effects) ---
    Console.WriteLine("\n-- simulate (dry-run, no side effects) --");
    string hpBefore = vars.Get("hp") ?? "(unset)";
    int trigBefore = engine.TriggerCount;
    foreach (string probe in new[] { "HP: 7/100 MP: 3/3", "You are low on health!", "nothing matches here" })
    {
        var hits = engine.Simulate(probe);
        Console.WriteLine($"    \"{probe}\" -> " + (hits.Count == 0 ? "(no match)" : string.Join("; ", hits.Select(h => $"{h.Name}: {h.Action}"))));
    }
    Console.WriteLine($"    after simulate: hp still={vars.Get("hp") ?? "(unset)"} (was {hpBefore}), triggers still={engine.TriggerCount} (was {trigBefore})");

    Console.WriteLine("\nEvent-pipeline self-test complete.");
}

static void FeedLine(EventBus bus, AutomationEngine engine, IWorldActions actions, string line)
{
    bus.Emit(SessionEventKind.LineReceived, line);
    engine.ProcessLine(line, actions);
}

static void FeedInput(EventBus bus, AutomationEngine engine, IWorldActions actions, string input)
{
    bus.Emit(SessionEventKind.InputSubmitted, input);
    if (!engine.ProcessInput(input, actions))
        bus.Emit(SessionEventKind.Sent, input);
}

static void FeedT(TelnetLayer t, string label, byte[] bytes)
{
    Console.WriteLine("<- " + label);
    t.Process(bytes);
}

static string DescribeReply(byte[] b)
{
    string Cmd(byte c) => c switch { 251 => "WILL", 252 => "WONT", 253 => "DO", 254 => "DONT", 250 => "SB", 240 => "SE", _ => c.ToString() };
    string Opt(byte o) => o switch { 1 => "ECHO", 3 => "SGA", 24 => "TTYPE", 31 => "NAWS", 42 => "CHARSET", 70 => "MSSP", 201 => "GMCP", _ => o.ToString() };
    if (b.Length == 3 && b[0] == 255 && b[1] is 251 or 252 or 253 or 254) return $"IAC {Cmd(b[1])} {Opt(b[2])}";
    if (b.Length >= 5 && b[0] == 255 && b[1] == 250)
    {
        var sb = new StringBuilder($"IAC SB {Opt(b[2])}");
        for (int i = 3; i < b.Length - 2; i++)
            sb.Append(' ').Append(b[i] >= 32 && b[i] < 127 ? ((char)b[i]).ToString() : "[" + b[i] + "]");
        sb.Append(" IAC SE");
        return sb.ToString();
    }
    return string.Join(" ", b);
}

static void Feed(AutomationEngine engine, RecordingActions rec, string? line = null, string? input = null)
{
    if (input is not null)
    {
        Console.WriteLine($"input> {input}");
        if (!engine.ProcessInput(input, rec)) Console.WriteLine($"    (passed through) send: {input}");
    }
    if (line is not null)
    {
        Console.WriteLine($"line : {line}");
        engine.ProcessLine(line, rec);
    }
}

static async Task ConnectAsync(string host, int port)
{
    var session = new MudSession(new WorldProfile { Host = host, Port = port });
    session.StateChanged += s => Console.WriteLine($"[{s}]");
    session.LineReady += line => Console.WriteLine(line.PlainText);
    session.GmcpReceived += (p, j) => Console.WriteLine($"[GMCP {p}] {j}");
    await session.ConnectAsync();
    Console.WriteLine($"connected to {host}:{port}\n");
    string? input;
    while ((input = Console.ReadLine()) is not null) session.Submit(input);
    await session.DisposeAsync();
}

sealed class RecordingActions : IWorldActions
{
    private readonly VariableStore _vars;
    public RecordingActions(VariableStore vars) => _vars = vars;
    public void Send(string text) => Console.WriteLine($"    -> SEND to MUD: \"{text}\"");
    public void Echo(string text) => Console.WriteLine($"    -> ECHO: \"{text}\"");
    public string? GetVariable(string name) => _vars.Get(name);
    public void SetVariable(string name, string value) { _vars.Set(name, value); Console.WriteLine($"    -> SET {name} = \"{value}\""); }
    public void CallScript(string function, IReadOnlyList<string> wildcards) =>
        Console.WriteLine($"    -> SCRIPT {function}({string.Join(", ", wildcards.Select(w => $"\"{w}\""))})");
}

// Mirrors how MudSession implements IWorldActions: rule effects flow back out as
// bus events (Sent / Notice / VariableChanged), so the event pipeline sees them.
sealed class BusActions : IWorldActions
{
    private readonly EventBus _bus;
    private readonly VariableStore _vars;
    public BusActions(EventBus bus, VariableStore vars) { _bus = bus; _vars = vars; }
    public void Send(string text) => _bus.Emit(SessionEventKind.Sent, text);
    public void Echo(string text) => _bus.Emit(SessionEventKind.Notice, text);
    public string? GetVariable(string name) => _vars.Get(name);
    public void SetVariable(string name, string value)
    {
        string? old = _vars.Get(name);
        _vars.Set(name, value);
        _bus.Emit(SessionEventKind.VariableChanged, value, name, old);
    }
    public void CallScript(string function, IReadOnlyList<string> wildcards) =>
        _bus.Emit(SessionEventKind.ScriptRun, function, "script");
}

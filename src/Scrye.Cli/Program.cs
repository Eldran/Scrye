using System.IO.Compression;
using System.Text;
using Scrye.Core.Automation;
using Scrye.Core.Events;
using Scrye.Core.Logging;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Mip;
using Scrye.Core.Plugins;
using Scrye.Core.Profiles;
using Scrye.Core.Session;
using Scrye.Core.State;
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
if (args.Length >= 1 && args[0] == "--replay") { ReplayTest(); return 0; }
if (args.Length >= 1 && args[0] == "--state") { StateTest(); return 0; }
if (args.Length >= 1 && args[0] == "--plugins") { PluginsTest(); return 0; }
if (args.Length >= 1 && args[0] == "--sequence") { SequenceTest(); return 0; }
if (args.Length >= 1 && args[0] == "--history") { HistoryTest(); return 0; }
if (args.Length >= 1 && args[0] == "--logging") { LoggingTest(); return 0; }
if (args.Length >= 1 && args[0] == "--reconnect") { ReconnectTest(); return 0; }
if (args.Length >= 1 && args[0] == "--complete") { CompleteTest(); return 0; }
if (args.Length >= 1 && args[0] == "--plugintimers") { PluginTimersTest(); return 0; }
if (args.Length >= 1 && args[0] == "--pluginpack") { PluginPackTest(); return 0; }
if (args.Length >= 1 && args[0] == "--login") { LoginTest(); return 0; }
if (args.Length >= 1 && args[0] == "--mxp") { MxpTest(); return 0; }
if (args.Length >= 1 && args[0] == "--mccp") { MccpTest(); return 0; }
if (args.Length >= 2 && int.TryParse(args[1], out int port)) { await ConnectAsync(args[0], port); return 0; }

Console.WriteLine("usage: scrye-cli --selftest | --automation | --protocol | --mip | --profile | --worlds | --events | --replay | --state | --plugins | --sequence | --history | --logging | --reconnect | --complete | --plugintimers | --pluginpack | --login | --mxp | --mccp | <host> <port>");
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
    FeedT(t, "server: WILL MCCP2 (accepted since M33)", new byte[] { 255, 251, 86 });

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

    // BBE viking feed: per-key VikingData events (the session maps these to vik.* state)
    var viking = new List<(string k, string v)>();
    proc.VikingData += (k, v) => viking.Add((k, v));
    parser.Process("#K%" + id + "045BBESEID^^12^^MSEID^^20^^VMAPH^^60|24|35|17\r\n");
    Console.WriteLine("viking keys: " + string.Join(", ", viking.Select(p => $"{p.k}={p.v}")));
    if (viking.Count != 3 || viking[0] != ("SEID", "12") || viking[2].k != "VMAPH")
        throw new Exception("BBE VikingData events wrong");
    if (vars.Get("vmip_SEID") != "12") throw new Exception("vmip_ vars must still be set");

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

    // ---- hierarchical model: mud -> [account] -> character -------------------
    Console.WriteLine("\n-- profile tree (mud -> account -> character) --");
    store.SaveMud("3Scapes", new ProfileLayer { Host = "3k.org", Port = 3200, EnableMip = true,
        Triggers = { new TriggerDef { Name = "shared", Pattern = "The sun rises*" } } });
    store.SaveAccount("3Scapes", "eldran", new ProfileLayer { Username = "eldran",
        Aliases = { new AliasDef { Name = "gd", Send = "get all from corpse" } } });
    store.SaveCharacter("3Scapes", "eldran", "warrior", new ProfileLayer {
        Variables = { ["class"] = "warrior" },
        Aliases = { new AliasDef { Name = "bash", Send = "bash %1" } } });
    store.SaveCharacter("3Scapes", "eldran", "mage", new ProfileLayer { Variables = { ["class"] = "mage" } });
    store.SaveCharacter("3Scapes", null, "loner", new ProfileLayer { Variables = { ["class"] = "thief" } });

    Console.WriteLine("muds: " + string.Join(", ", store.ListMuds()));
    Console.WriteLine("accounts(3Scapes): " + string.Join(", ", store.ListAccounts("3Scapes")));
    Console.WriteLine("chars(3Scapes/eldran): " + string.Join(", ", store.ListCharacters("3Scapes", "eldran")));
    Console.WriteLine("chars(3Scapes, no account): " + string.Join(", ", store.ListCharacters("3Scapes")));

    var w = store.ResolveCharacter("3Scapes", "eldran", "warrior");
    Console.WriteLine($"warrior: name={w.World.Name} host={w.World.Host}:{w.World.Port} mip={w.World.EnableMip} " +
                      $"user={w.Username} triggers={w.Triggers.Count} aliases={w.Aliases.Count} class={w.Variables["class"]}");
    if (w.World.Host != "3k.org" || w.Username != "eldran" || w.Aliases.Count != 2 || w.Triggers.Count != 1)
        throw new Exception("warrior cascade resolved wrong");

    var lone = store.ResolveCharacter("3Scapes", null, "loner");
    Console.WriteLine($"loner (no account): host={lone.World.Host} user={lone.Username ?? "(none)"} " +
                      $"aliases={lone.Aliases.Count} class={lone.Variables["class"]}");
    if (lone.Username is not null || lone.Aliases.Count != 0) throw new Exception("loner should not inherit account");

    var acct = store.ResolveAccount("3Scapes", "eldran");
    var bare = store.ResolveMud("3Scapes");
    Console.WriteLine($"account resolve: user={acct.Username} aliases={acct.Aliases.Count}; bare mud: host={bare.World.Host} aliases={bare.Aliases.Count}");

    store.DeleteCharacter("3Scapes", "eldran", "mage");
    Console.WriteLine("after delete mage: " + string.Join(", ", store.ListCharacters("3Scapes", "eldran")));
    store.DeleteAccount("3Scapes", "eldran");
    Console.WriteLine("after delete account: accounts=" + string.Join(", ", store.ListAccounts("3Scapes")) +
                      " | direct chars=" + string.Join(", ", store.ListCharacters("3Scapes")));
    store.DeleteMud("3Scapes");
    Console.WriteLine("after delete mud: muds=" + string.Join(", ", store.ListMuds()));

    Directory.Delete(root, true);
    Console.WriteLine("\nWorld-store self-test complete.");
}

static void LoginTest()
{
    Console.WriteLine("== Scrye auto-login self-test ==\n");

    // full flow: banner noise, then name prompt, then password prompt
    var login = new AutoLogin("eldran", "hunter2");
    if (login.Feed("Welcome to 3Scapes!", out _) is not null) throw new Exception("replied to banner");
    if (login.Feed("  ***  The Realm Awaits  ***", out _) is not null) throw new Exception("replied to banner art");
    string? r1 = login.Feed("By what name do you wish to be known?", out bool p1);
    Console.WriteLine($"name prompt -> '{r1}' (password={p1})");
    if (r1 != "eldran" || p1) throw new Exception("username not sent on name prompt");
    if (login.Feed("Welcome back, Eldran.", out _) is not null) throw new Exception("replied to greeting");
    string? r2 = login.Feed("Password:", out bool p2);
    Console.WriteLine($"password prompt -> {(r2 is null ? "null" : "(secret)")} (password={p2})");
    if (r2 != "hunter2" || !p2) throw new Exception("password not sent on password prompt");
    if (!login.Done) throw new Exception("should be done after both");
    if (login.Feed("Password:", out _) is not null) throw new Exception("replied after done");

    // variant prompt styles
    var alt = new AutoLogin("eldran", "x");
    if (alt.Feed("What is your name:", out _) != "eldran") throw new Exception("'What is your name:' not matched");
    var alt2 = new AutoLogin("eldran", "x");
    if (alt2.Feed("login:", out _) != "eldran") throw new Exception("'login:' not matched");

    // username only (no stored password): done after the name reply
    var noPass = new AutoLogin("eldran", null);
    noPass.Feed("What is your name:", out _);
    Console.WriteLine($"no-password variant done={noPass.Done}");
    if (!noPass.Done) throw new Exception("username-only should finish after name");

    // unrecognised flow: gives up quietly after MaxLines
    var giveUp = new AutoLogin("eldran", "x");
    for (int i = 0; i < AutoLogin.MaxLines; i++)
        if (giveUp.Feed($"scrolling combat spam {i}", out _) is not null) throw new Exception("replied to spam");
    Console.WriteLine($"gave up after {AutoLogin.MaxLines} lines: done={giveUp.Done}");
    if (!giveUp.Done) throw new Exception("should give up after MaxLines");

    // a password prompt must not fire before the username was requested
    var early = new AutoLogin("eldran", "x");
    if (early.Feed("Password:", out _) is not null) throw new Exception("password sent before username");

    Console.WriteLine("\nAuto-login self-test complete.");
}

static void MxpTest()
{
    Console.WriteLine("== Scrye MXP self-test ==\n");

    var lines = new List<Scrye.Core.Text.Line>();
    var replies = new List<string>();
    var p = new AnsiParser { MxpEnabled = true };
    p.LineCompleted += lines.Add;
    p.MxpResponse += replies.Add;

    // 1. secure line: SEND link with href
    p.Feed("\x1b[1z<SEND href=\"kill rat\">a scrawny rat</SEND> scurries by.\n");
    var l = lines[^1];
    Console.WriteLine($"send: '{l.PlainText}' links={l.Links.Count} action='{l.Links[0].Link.Action}' span={l.Links[0].Start}+{l.Links[0].Length}");
    if (l.PlainText != "a scrawny rat scurries by." || l.Links.Count != 1 ||
        l.Links[0].Link.Action != "kill rat" || l.Links[0].Start != 0 || l.Links[0].Length != 13)
        throw new Exception("SEND link wrong");

    // 2. SEND without href -> the link text IS the command; &text; substitutes
    p.Feed("\x1b[1zGo <SEND>north</SEND> or <SEND href=\"look &text;\">east</SEND>.\n");
    l = lines[^1];
    if (l.Links.Count != 2 || l.Links[0].Link.Action != "north" || l.Links[1].Link.Action != "look east")
        throw new Exception("SEND defaults/&text; wrong");
    Console.WriteLine($"defaults: '{l.Links[0].Link.Action}' / '{l.Links[1].Link.Action}'");

    // 3. open mode blocks SEND (tag stripped, text kept, no link) — line modes reset each newline
    p.Feed("<SEND \"steal gold\">gold pile</SEND> here\n");
    l = lines[^1];
    Console.WriteLine($"open-mode: '{l.PlainText}' links={l.Links.Count}");
    if (l.PlainText != "gold pile here" || l.Links.Count != 0) throw new Exception("open mode must block SEND");

    // 4. entities
    p.Feed("\x1b[1zscore &gt; 100 &amp; rising &#33;\n");
    l = lines[^1];
    Console.WriteLine($"entities: '{l.PlainText}'");
    if (l.PlainText != "score > 100 & rising !") throw new Exception("entities wrong");

    // 5. formatting + colour tags (open-category: work in open mode too)
    p.Feed("<B>bold</B> and <COLOR red>red</COLOR> text\n");
    l = lines[^1];
    var boldRun = l.Runs.First(r => r.Text == "bold");
    var redRun = l.Runs.First(r => r.Text == "red");
    Console.WriteLine($"format: bold={boldRun.Flags} red=({redRun.Fore.R},{redRun.Fore.G},{redRun.Fore.B})");
    if ((boldRun.Flags & RunFlags.Bold) == 0) throw new Exception("<B> wrong");
    if (redRun.Fore.R != 0xCD || redRun.Fore.G != 0) throw new Exception("<COLOR red> wrong");
    var after = l.Runs.Last();
    if ((after.Flags & RunFlags.Bold) != 0 || after.Fore.R == 0xCD) throw new Exception("close tags must restore");

    // 6. a '<' that is not a tag is preserved, not eaten
    p.Feed("\x1b[1zHP <-- getting low\n");
    l = lines[^1];
    Console.WriteLine($"literal <: '{l.PlainText}'");
    if (l.PlainText != "HP <-- getting low") throw new Exception("literal '<' must be preserved");

    // 7. VERSION / SUPPORT replies (secure only)
    p.Feed("\x1b[1z<VERSION>\n\x1b[1z<SUPPORT>\n");
    Console.WriteLine($"replies: {replies.Count} — {(replies.Count > 0 ? replies[0].Trim() : "")}");
    if (replies.Count != 2 || !replies[0].Contains("CLIENT=Scrye") || !replies[1].Contains("+send"))
        throw new Exception("VERSION/SUPPORT replies wrong");

    // 8. lock-secure persists across lines
    p.Feed("\x1b[6zline one <SEND>n</SEND>\nline two <SEND>s</SEND>\n");
    if (lines[^1].Links.Count != 1 || lines[^2].Links.Count != 1) throw new Exception("ESC[6z lock secure wrong");
    Console.WriteLine("lock-secure: links live on both lines");

    // 9. MXP off => tags are plain text (zero behaviour change)
    var plainParser = new AnsiParser();
    var plainLines = new List<Scrye.Core.Text.Line>();
    plainParser.LineCompleted += plainLines.Add;
    plainParser.Feed("<B>not a tag</B>\n");
    if (plainLines[0].PlainText != "<B>not a tag</B>") throw new Exception("MXP off must not parse tags");
    Console.WriteLine($"mxp off: '{plainLines[0].PlainText}'");

    // 10. plain-URL auto-detection (independent of MXP)
    var url = Scrye.Core.Text.Line.FromText("docs at https://3scapes.org/help, then rest");
    Console.WriteLine($"url: links={url.Links.Count} action='{url.Links[0].Link.Action}' isUrl={url.Links[0].Link.IsUrl}");
    if (url.Links.Count != 1 || url.Links[0].Link.Action != "https://3scapes.org/help" || !url.Links[0].Link.IsUrl)
        throw new Exception("URL detection wrong");

    Console.WriteLine("\nMXP self-test complete.");
}

static void MccpTest()
{
    Console.WriteLine("== Scrye MCCP2 self-test ==\n");

    static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionMode.Compress)) z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    // 1. negotiation: WILL COMPRESS2 -> DO
    var t = new TelnetLayer();
    var sent = new List<byte[]>();
    t.SendData += sent.Add;
    t.Process(new byte[] { 255, 251, 86 });
    if (!sent.Any(r => r.Length == 3 && r[1] == 253 && r[2] == 86)) throw new Exception("must reply DO COMPRESS2");
    Console.WriteLine("negotiation: WILL 86 -> DO 86");

    // 2. mid-chunk switch-over: plain text + IAC SB 86 IAC SE + zlib data in ONE chunk
    string bigText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 200));
    byte[] payload = Encoding.ASCII.GetBytes(bigText + "\r\nfinal line after 8KB\r\n");
    byte[] compressed = Zlib(payload);
    var chunk = new List<byte>(Encoding.ASCII.GetBytes("plain before "));
    chunk.AddRange(new byte[] { 255, 250, 86, 255, 240 });
    chunk.AddRange(compressed);
    byte[] inband = t.Process(chunk.ToArray());
    Console.WriteLine($"switch-over: inband='{Encoding.ASCII.GetString(inband)}' active={t.CompressionActive}");
    if (Encoding.ASCII.GetString(inband) != "plain before " || !t.CompressionActive)
        throw new Exception("switch-over wrong");
    byte[] tail = t.TakePendingCompressed() ?? throw new Exception("no compressed tail");
    if (!tail.SequenceEqual(compressed)) throw new Exception("compressed tail mangled");
    if (t.TakePendingCompressed() is not null) throw new Exception("tail must be taken once");

    // 3. streaming inflate: feed the tail in awkward 7-byte chunks; expect the exact payload
    var collected = new List<byte>();
    bool ended = false;
    using (var mccp = new MccpDecompressor(b => { lock (collected) collected.AddRange(b); }, () => ended = true))
    {
        for (int i = 0; i < tail.Length; i += 7)
            mccp.Feed(tail.Skip(i).Take(7).ToArray());

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (collected) if (collected.Count >= payload.Length && ended) break;
            Thread.Sleep(10);
        }
        lock (collected)
        {
            Console.WriteLine($"inflate: {tail.Length} compressed bytes -> {collected.Count} (expected {payload.Length}), ended={ended}");
            if (!collected.SequenceEqual(payload)) throw new Exception("inflated payload mismatch");
        }
        if (!ended) throw new Exception("zlib end-of-stream must fire onEnded");
    }

    // 4. end-to-end: the INFLATED stream is itself telnet (GMCP inside the compressed data)
    var inner = new List<byte>(Encoding.ASCII.GetBytes("room description\r\n"));
    inner.AddRange(new byte[] { 255, 250, 201 });
    inner.AddRange(Encoding.ASCII.GetBytes("Char.Vitals {\"hp\":42}"));
    inner.AddRange(new byte[] { 255, 240 });
    inner.AddRange(Encoding.ASCII.GetBytes("after gmcp\r\n"));
    byte[] innerCompressed = Zlib(inner.ToArray());

    var t2 = new TelnetLayer();
    t2.SendData += _ => { };
    string? gmcpPkg = null;
    t2.GmcpReceived += (pkg, _) => gmcpPkg = pkg;
    t2.Process(new byte[] { 255, 251, 86 });                       // negotiate
    t2.Process(new byte[] { 255, 250, 86, 255, 240 });             // switch-over marker alone
    if (!t2.CompressionActive) throw new Exception("marker alone must activate");
    var text2 = new StringBuilder();
    var done2 = new ManualResetEventSlim();
    using (var mccp2 = new MccpDecompressor(
        b => { lock (text2) text2.Append(Encoding.ASCII.GetString(t2.Process(b))); },
        () => done2.Set()))
    {
        mccp2.Feed(innerCompressed);
        if (!done2.Wait(3000)) throw new Exception("pipeline did not finish");
    }
    lock (text2)
    {
        Console.WriteLine($"pipeline: text='{text2.ToString().Replace("\r\n", "/")}' gmcp={gmcpPkg}");
        if (!text2.ToString().Contains("room description") || !text2.ToString().Contains("after gmcp") || gmcpPkg != "Char.Vitals")
            throw new Exception("end-to-end pipeline wrong");
    }

    // 5. reset for reconnect
    t2.ResetCompression();
    if (t2.CompressionActive) throw new Exception("reset must clear compression");
    Console.WriteLine("reset: compression cleared for reconnect");

    Console.WriteLine("\nMCCP2 self-test complete.");
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

static void HistoryTest()
{
    Console.WriteLine("== Scrye command-history self-test ==\n");
    var h = new CommandHistory();
    h.Add("look"); h.Add("north"); h.Add("north"); h.Add("kill orc");   // dup 'north' collapsed
    Console.WriteLine($"items: [{string.Join(", ", h.Items)}]  (consecutive dup 'north' collapsed)");

    Console.WriteLine("\n-- arrow up from a half-typed 'sc' --");
    Console.WriteLine($"    up   -> {h.Previous("sc")}");   // kill orc
    Console.WriteLine($"    up   -> {h.Previous("sc")}");   // north
    Console.WriteLine($"    up   -> {h.Previous("sc")}");   // look
    Console.WriteLine($"    up   -> {h.Previous("sc")}");   // look (clamped)
    Console.WriteLine("-- back down --");
    Console.WriteLine($"    down -> {h.Next()}");           // north
    Console.WriteLine($"    down -> {h.Next()}");           // kill orc
    Console.WriteLine($"    down -> \"{h.Next()}\"");       // draft "sc" restored
    Console.WriteLine($"    down -> {(h.Next() is null ? "(nothing)" : h.Next())}");  // null past the end

    Console.WriteLine("\nCommand-history self-test complete.");
}

static void LoggingTest()
{
    Console.WriteLine("== Scrye session-logging self-test ==\n");
    var t0 = new DateTimeOffset(2026, 1, 1, 20, 15, 0, TimeSpan.Zero);
    int tick = 0;
    Func<DateTimeOffset> clock = () => t0.AddSeconds(tick++);

    // A couple of representative lines: a plain server line, a coloured multi-run line,
    // an echoed command, and one with HTML-hostile characters.
    Line plain = Line.FromText("You enter the Plaza.");
    Line coloured = new(new[]
    {
        new StyledRun("HP:", new Rgb(0xF0, 0xC0, 0x40), Rgb.DefaultBack, RunFlags.Bold),
        new StyledRun(" 42/100", new Rgb(0x55, 0xFF, 0x55), Rgb.DefaultBack, RunFlags.None),
    }, false, t0);
    Line risky = Line.FromText("<script> & \"tags\" should be escaped");

    Console.WriteLine("-- TEXT format --");
    var sw = new StringWriter();
    var text = new SessionLogger("3Scapes", sw, LogFormat.Text, timestamps: true, clock: clock);
    text.Log(plain); text.Log(coloured); text.Log("> north", new Rgb(0x60, 0xC0, 0xF0)); text.Log(risky);
    text.Close();
    Console.WriteLine(sw.ToString());
    Console.WriteLine($"   lines logged: {text.LineCount} (expect 4)");

    Console.WriteLine("\n-- HTML format --");
    var sh = new StringWriter();
    var html = new SessionLogger("3Scapes", sh, LogFormat.Html, timestamps: false, clock: clock);
    html.Log(coloured); html.Log(risky);
    html.Close();
    string h = sh.ToString();
    Console.WriteLine(h);
    Console.WriteLine($"   contains <span colour run: {h.Contains("<span style=\"color:#55FF55")}");
    Console.WriteLine($"   escaped '<script>' as &lt;script&gt;: {h.Contains("&lt;script&gt;")}");
    Console.WriteLine($"   wrapped in <pre>: {h.Contains("<pre>") && h.Contains("</pre>")}");

    Console.WriteLine("\n-- round-trip through a real file --");
    string dir = Path.Combine(Path.GetTempPath(), "scrye_log_" + Guid.NewGuid().ToString("N"));
    var file = SessionLogger.CreateFile(dir, "3Scapes", LogFormat.Text, timestamps: false, clock: clock);
    file.Log(plain);
    string path = file.Path!;
    file.Close();
    string onDisk = File.ReadAllText(path);
    Console.WriteLine($"   wrote {Path.GetFileName(path)}, contains the line: {onDisk.Contains("You enter the Plaza.")}");
    Directory.Delete(dir, true);

    Console.WriteLine("\nSession-logging self-test complete.");
}

static void ReconnectTest()
{
    Console.WriteLine("== Scrye auto-reconnect self-test ==\n");
    var p = new ReconnectPolicy
    {
        BaseDelay = TimeSpan.FromSeconds(2),
        Factor = 2.0,
        MaxDelay = TimeSpan.FromSeconds(60),
        MaxAttempts = 8,
    };
    Console.WriteLine($"policy: base={p.BaseDelay.TotalSeconds}s factor={p.Factor} cap={p.MaxDelay.TotalSeconds}s maxAttempts={p.MaxAttempts}\n");
    Console.WriteLine("attempt  delay");
    for (int a = 1; a <= p.MaxAttempts + 1; a++)
    {
        if (!p.ShouldRetry(a - 1)) { Console.WriteLine($"   (would give up before attempt {a})"); break; }
        Console.WriteLine($"   {a,2}    {p.Delay(a).TotalSeconds,5:0}s");
    }
    Console.WriteLine("\nexpected: 2, 4, 8, 16, 32, 60, 60, 60 (exponential, capped at 60)");

    var inf = new ReconnectPolicy { MaxAttempts = 0 };
    Console.WriteLine($"\nunlimited policy ShouldRetry(1000) = {inf.ShouldRetry(1000)} (expect True)");

    Console.WriteLine("\nAuto-reconnect self-test complete.");
}

static void CompleteTest()
{
    Console.WriteLine("== Scrye tab-completion self-test ==\n");
    var e = new CompletionEngine(minLength: 3);
    e.Observe("You see a goblin guarding the gate.");
    e.Observe("The gatekeeper greets you.");
    e.Add("gossip");   // a recently-typed command
    Console.WriteLine($"words harvested: {e.Count}");

    foreach (string stub in new[] { "go", "gat", "gr", "zz" })
        Console.WriteLine($"   '{stub}' -> [{string.Join(", ", e.Complete(stub))}]");

    Console.WriteLine("\n-- recency: retype 'gate' then complete 'ga' (gate should lead) --");
    e.Add("gate");
    Console.WriteLine($"   'ga' -> [{string.Join(", ", e.Complete("ga"))}]");

    Console.WriteLine("\nTab-completion self-test complete.");
}

static void PluginTimersTest()
{
    Console.WriteLine("== Scrye plugin-timer self-test ==\n");
    var wheel = new Scrye.Core.Plugins.TimerWheel();
    int oneShot = 0, repeats = 0;

    int idA = wheel.Add(2, repeat: false, () => oneShot++);
    int idB = wheel.Add(1, repeat: true, () => repeats++);
    Console.WriteLine($"registered: one-shot #{idA} @2s, repeating #{idB} @1s; live count = {wheel.Count}");

    Console.WriteLine("\n-- tick 1s at a time --");
    for (int s = 1; s <= 5; s++)
    {
        wheel.Tick(1.0);
        Console.WriteLine($"   t={s}s: oneShot fired={oneShot}, repeats fired={repeats}, live={wheel.Count}");
    }
    Console.WriteLine("   expect: one-shot fires once at t=2 then gone; repeating fires each second.");

    Console.WriteLine("\n-- cancel the repeating timer --");
    Console.WriteLine($"   cancel(#{idB}) = {wheel.Cancel(idB)} (true);  cancel(#{idB}) again = {wheel.Cancel(idB)} (false)");
    int before = repeats;
    wheel.Tick(1.0); wheel.Tick(1.0);
    Console.WriteLine($"   after cancel, 2 more ticks: repeats still = {repeats} (was {before}); live = {wheel.Count}");

    Console.WriteLine("\n-- re-entrant add + self-cancel --");
    var w2 = new Scrye.Core.Plugins.TimerWheel();
    int hits = 0, selfId = 0;
    selfId = w2.Add(1, repeat: true, () => { hits++; if (hits >= 2) w2.Cancel(selfId); });
    w2.Add(1, repeat: false, () => w2.Add(1, repeat: false, () => hits += 100));  // adds a timer mid-tick
    for (int s = 1; s <= 4; s++) w2.Tick(1.0);
    Console.WriteLine($"   self-cancelling timer fired {hits % 100} times (expect 2); nested one-shot ran = {(hits >= 100 ? "yes" : "no")}");

    Console.WriteLine("\nPlugin-timer self-test complete.");
}

static void PluginPackTest()
{
    Console.WriteLine("== Scrye plugin-package self-test ==\n");
    string tmp = Path.Combine(Path.GetTempPath(), "scrye_pkg_" + Guid.NewGuid().ToString("N"));
    string content = Path.Combine(tmp, "content");        // becomes the archive root
    Directory.CreateDirectory(content);
    File.WriteAllText(Path.Combine(content, "plugin.json"),
        "{\"id\":\"cool-plugin\",\"name\":\"Cool Plugin\",\"version\":\"1.2.3\",\"mudIds\":[\"*\"]}");
    File.WriteAllText(Path.Combine(content, "main.lua"), "scrye.print('hi from cool-plugin')");

    string userRoot = Path.Combine(tmp, "userplugins");
    Directory.CreateDirectory(userRoot);
    string pkg = Path.Combine(userRoot, "cool.scryeplugin");   // dropped into the user folder
    System.IO.Compression.ZipFile.CreateFromDirectory(content, pkg);   // plugin.json at archive root

    Console.WriteLine("-- install every *.scryeplugin dropped in the user folder --");
    var installed = PluginPackage.InstallAllIn(userRoot, s => Console.WriteLine("    " + s));
    Console.WriteLine($"   installed ids : [{string.Join(", ", installed)}]");
    Console.WriteLine($"   folder created: {Directory.Exists(Path.Combine(userRoot, "cool-plugin"))}");
    Console.WriteLine($"   main.lua there: {File.Exists(Path.Combine(userRoot, "cool-plugin", "main.lua"))}");
    Console.WriteLine($"   archive removed: {!File.Exists(pkg)}");

    var found = PluginCatalog.ForMud("AnyMud", userRoot).Select(d => $"{d.Manifest.Id} v{d.Manifest.Version}");
    Console.WriteLine($"   discovered now : [{string.Join(", ", found)}]");

    Directory.Delete(tmp, true);
    Console.WriteLine("\nPlugin-package self-test complete.");
}

static void SequenceTest()
{
    Console.WriteLine("== Scrye command-sequence self-test ==\n");

    Console.WriteLine("-- parse: \"enter; north x3; wait 2; west*2\" --");
    SequenceDef def = SequenceParser.Parse("demo", "enter; north x3; wait 2; west*2");
    foreach (var s in def.Steps)
        Console.WriteLine($"    {(s.Kind == "wait" ? $"wait {s.Seconds}s" : $"send '{s.Text}' x{s.Count}")}");

    var engine = new SequenceEngine();
    engine.Send += cmd => Console.WriteLine($"    -> SEND: {cmd}");
    engine.StatusChanged += st => Console.WriteLine($"       [{st.State}] {st.Sent}/{st.Total} {st.Command}");

    Console.WriteLine("\n-- run prompt-gated; feed a prompt after each send --");
    engine.RunAdHoc(def);                 // sends 'enter' immediately, then waits for prompt
    for (int i = 0; i < 5 && engine.State == SequenceState.WaitingForPrompt; i++)
        engine.OnPrompt();                // advance enter->north->north->north (then hits the wait step)
    Console.WriteLine("    (now at the 'wait 2' step — advance with ticks)");
    engine.Tick(1.0);
    engine.Tick(1.0);                     // wait satisfied -> sends west
    engine.OnPrompt();                    // west #1 -> west #2 (last send finishes)

    Console.WriteLine("\n-- prompt-timeout safety: gated, but no prompt arrives --");
    var e2 = new SequenceEngine();
    e2.Send += cmd => Console.WriteLine($"    -> SEND: {cmd}");
    e2.RunAdHoc(SequenceParser.Parse("t", "kick; punch"));   // StepTimeout default 2s
    Console.WriteLine("    ticking 2s with no prompt (should advance to 'punch'):");
    e2.Tick(1.0); e2.Tick(1.0);

    Console.WriteLine("\n-- pause / resume / stop --");
    var e3 = new SequenceEngine();
    e3.Send += cmd => Console.WriteLine($"    -> SEND: {cmd}");
    e3.StatusChanged += st => Console.WriteLine($"       [{st.State}] {st.Command}");
    e3.RunAdHoc(SequenceParser.Parse("t", "a; b; c"));
    e3.Pause();
    Console.WriteLine("    (paused — a tick should do nothing)");
    e3.Tick(5.0);
    e3.Resume();
    e3.OnPrompt();                        // b
    e3.Stop();                            // stop before c

    Console.WriteLine("\n-- persisted SequenceSpec (profile form) round-trip --");
    var spec = new SequenceSpec { Name = "toBank", Source = "out; north x2; wait 1; east", PromptGated = true, StepTimeoutSeconds = 3 };
    SequenceDef fromSpec = spec.ToDef();
    Console.WriteLine($"   spec '{spec.Name}' source=\"{spec.Source}\" -> {fromSpec.Steps.Count} steps, gated={fromSpec.PromptGated}, timeout={fromSpec.StepTimeoutSeconds}s");
    var reg = new SequenceEngine();
    reg.Register(fromSpec);
    Console.WriteLine($"   registered names: [{string.Join(", ", reg.Names)}]");
    Console.WriteLine($"   Run(\"toBank\") = {reg.Run("toBank")} (true), Run(\"nope\") = {reg.Run("nope")} (false)");
    reg.ClearRegistry();
    Console.WriteLine($"   after ClearRegistry: {reg.Names.Count} names");

    Console.WriteLine("\nCommand-sequence self-test complete.");
}

static void PluginsTest()
{
    Console.WriteLine("== Scrye plugin-catalog self-test ==\n");
    string root = Path.Combine(Path.GetTempPath(), "scrye_plugins_" + Guid.NewGuid().ToString("N"));

    void Make(string id, string manifest, bool withEntry = true)
    {
        string dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest);
        if (withEntry) File.WriteAllText(Path.Combine(dir, "main.lua"), "-- entry");
    }

    Make("hello", "{\"id\":\"hello\",\"name\":\"Hello\",\"version\":\"1.0.0\"}");                       // mudIds defaults to ["*"]
    Make("threes", "{\"id\":\"threes-pack\",\"name\":\"3Scapes Pack\",\"mudIds\":[\"3Scapes\"]}");
    Make("aard", "{\"id\":\"aard\",\"name\":\"Aard\",\"mudIds\":[\"Aardwolf\"],\"enabled\":false}");    // disabled
    Make("broken", "{ this is not json ]");                                                              // ignored
    Directory.CreateDirectory(Path.Combine(root, "notaplugin"));                                         // no manifest, ignored

    Console.WriteLine("-- discover all --");
    foreach (var d in PluginCatalog.Discover(root))
        Console.WriteLine($"    {d.Manifest.Id,-14} v{d.Manifest.Version,-7} mudIds=[{string.Join(",", d.Manifest.MudIds)}] enabled={d.Manifest.Enabled} entry={Path.GetFileName(d.EntryPath)}");

    Console.WriteLine("\n-- for mud '3Scapes' (enabled + applicable) --");
    foreach (var d in PluginCatalog.ForMud("3Scapes", root)) Console.WriteLine($"    {d.Manifest.Id}");
    Console.WriteLine("-- for mud 'Aardwolf' --");
    foreach (var d in PluginCatalog.ForMud("Aardwolf", root)) Console.WriteLine($"    {d.Manifest.Id}");
    Console.WriteLine("    (aard is disabled → should NOT appear; hello applies via '*')");

    Console.WriteLine("\n-- rescan reflects disk changes (what add/remove relies on) --");
    Directory.Delete(Path.Combine(root, "threes"), true);                               // remove a plugin folder
    Make("newbie", "{\"id\":\"newbie\",\"name\":\"Newbie\",\"version\":\"0.1.0\"}");    // add a new one (mudIds "*")
    var now = PluginCatalog.ForMud("3Scapes", root).Select(d => d.Manifest.Id).OrderBy(x => x, StringComparer.Ordinal);
    Console.WriteLine("    for '3Scapes' now: [" + string.Join(", ", now) + "]   (threes-pack gone, newbie added, hello stays)");

    Directory.Delete(root, true);
    Console.WriteLine("\nPlugin-catalog self-test complete.");
}

static void StateTest()
{
    Console.WriteLine("== Scrye state-model self-test ==\n");
    var state = new StateStore();

    Console.WriteLine("-- feed a GMCP Char.Vitals package --");
    state.SetJson("Char.Vitals", "{\"hp\":42,\"maxhp\":100,\"sp\":10,\"alignment\":\"good\",\"fighting\":true}");
    foreach (var kv in state.Snapshot()) Console.WriteLine($"    {kv.Key} = {kv.Value} [{kv.Value.Kind}]");

    Console.WriteLine("\n-- watch a leaf (char.vitals.hp) and a subtree (char.vitals) --");
    using var w1 = state.Watch("char.vitals.hp", (p, v) => Console.WriteLine($"    leaf watch fired: {p} -> {v}"));
    using var w2 = state.Watch("char.vitals", (p, v) => Console.WriteLine($"    subtree watch fired: {p} -> {v}"));
    Console.WriteLine("  set char.vitals.hp = 35 (both watchers should fire):");
    state.Set("char.vitals.hp", StateValue.Num(35));
    Console.WriteLine("  set char.vitals.sp = 8 (only subtree should fire):");
    state.Set("char.vitals.sp", StateValue.Num(8));
    Console.WriteLine($"  hp as number = {state.Get("char.vitals.hp").AsNumber()}");

    Console.WriteLine("\n-- nested object + array (Room.Info) --");
    state.SetJson("Room.Info", "{\"name\":\"The Plaza\",\"num\":1234,\"exits\":[\"north\",\"south\",\"east\"]}");
    foreach (var kv in state.Snapshot()) if (kv.Key.StartsWith("room.")) Console.WriteLine($"    {kv.Key} = {kv.Value}");

    Console.WriteLine("\n-- resend Char.Vitals WITHOUT 'alignment' (subtree cleared first) --");
    state.SetJson("Char.Vitals", "{\"hp\":50,\"maxhp\":100}");
    Console.WriteLine($"    char.vitals.alignment still present? {state.Has("char.vitals.alignment")}  (expect False)");
    Console.WriteLine($"    char.vitals.hp = {state.Get("char.vitals.hp")}");

    Console.WriteLine($"\n{state.Count} leaves in the tree. State-model self-test complete.");
}

static void ReplayTest()
{
    Console.WriteLine("== Scrye replay-analyzer self-test ==\n");
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var bus = new EventBus();
    int c = 0;
    bus.Clock = () => t0.AddMilliseconds(200 * c++);
    var rec = new SessionRecorder("3Scapes", t0);
    bus.Subscribe(rec);

    var vars = new VariableStore();
    var engine = new AutomationEngine(vars);
    var actions = new BusActions(bus, vars);
    engine.Hit += hit => { if (hit.Kind == AutomationHitKind.Trigger) bus.Emit(SessionEventKind.TriggerMatched, hit.Input, hit.Name, hit.Action); };

    // rules as they were WHEN RECORDED
    engine.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low on health*", Send = "flee" });
    engine.AddTrigger(new TriggerDef { Name = "greet", Pattern = "* says hello", Send = "wave" });

    Console.WriteLine("-- record a short session under the OLD rules --");
    FeedLine(bus, engine, actions, "A wiremouth lunges at you!");
    FeedLine(bus, engine, actions, "You are low on health!");
    FeedLine(bus, engine, actions, "Bob says hello");
    SessionRecording recording = SessionRecorder.Parse(rec.ToJsonLines());   // via serialize round-trip
    Console.WriteLine($"   recorded {recording.Events.Count} events\n");

    // rules as they are NOW: keep flee, drop greet, add a new 'defend'
    var engineNow = new AutomationEngine(new VariableStore());
    engineNow.AddTrigger(new TriggerDef { Name = "flee", Pattern = "*low on health*", Send = "flee" });
    engineNow.AddTrigger(new TriggerDef { Name = "defend", Pattern = "*lunges at you*", Send = "defend" });

    Console.WriteLine("-- replay the recording against the CURRENT rules --");
    foreach (ReplayLineAnalysis a in ReplayAnalyzer.Analyze(recording, engineNow))
    {
        string then = a.FiredThen.Count == 0 ? "-" : string.Join(",", a.FiredThen);
        string now = a.WouldFireNow.Count == 0 ? "-" : string.Join(",", a.WouldFireNow.Select(h => h.Name));
        Console.WriteLine($"   \"{a.Line}\"");
        Console.WriteLine($"       then: {then}   now: {now}{(a.Differs ? "   << DIFF" : "")}");
        if (a.Added.Count > 0) Console.WriteLine($"       + would now fire: {string.Join(",", a.Added)}");
        if (a.Removed.Count > 0) Console.WriteLine($"       - no longer fires: {string.Join(",", a.Removed)}");
    }
    Console.WriteLine($"\n   {ReplayAnalyzer.Diffs(recording, engineNow).Count} line(s) behave differently under current rules.");
    Console.WriteLine("\nReplay-analyzer self-test complete.");
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

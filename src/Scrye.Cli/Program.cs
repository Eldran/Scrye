using System.Text;
using Scrye.Core.Automation;
using Scrye.Core.Model;
using Scrye.Core.Net;
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
if (args.Length >= 2 && int.TryParse(args[1], out int port)) { await ConnectAsync(args[0], port); return 0; }

Console.WriteLine("usage: scrye-cli --selftest | --automation | --protocol | <host> <port>");
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

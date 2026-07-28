using System.Text;
using Scrye.Core.Automation;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Session;
using Scrye.Core.Text;

// Scrye.Cli — a dependency-free harness for the engine core.
//   --selftest        run canned bytes through telnet + ANSI and print the result
//   --automation      exercise the trigger/alias/timer/variable engine offline
//   <host> <port>     connect to a live MUD and stream to the console

if (args.Length >= 1 && args[0] == "--selftest") { SelfTest(); return 0; }
if (args.Length >= 1 && args[0] == "--automation") { AutomationTest(); return 0; }
if (args.Length >= 2 && int.TryParse(args[1], out int port)) { await ConnectAsync(args[0], port); return 0; }

Console.WriteLine("usage: scrye-cli --selftest | --automation | <host> <port>");
return 1;

static void SelfTest()
{
    Console.WriteLine("== Scrye engine self-test ==\n");

    var telnet = new TelnetLayer();
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
    bytes.AddRange(new byte[] { 255, 251, 1 });                       // IAC WILL ECHO
    bytes.AddRange(Encoding.UTF8.GetBytes("\x1b[1;32mHello\x1b[0m world\r\n"));
    bytes.AddRange(Encoding.UTF8.GetBytes("\x1b[38;5;208m256-orange\x1b[0m and \x1b[38;2;10;20;250mtruecolour\x1b[0m\n"));
    bytes.AddRange(Encoding.UTF8.GetBytes("Enter command> "));

    byte[] data = telnet.Process(bytes.ToArray(), out byte[] response);
    Console.Write("telnet response bytes:");
    foreach (byte b in response) Console.Write($" {b}");
    Console.WriteLine(response.SequenceEqual(new byte[] { 255, 254, 1 }) ? "   (correct: IAC DONT ECHO)" : "   (UNEXPECTED)");
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

    // an alias: "kk *"  ->  "kill %1"
    engine.AddAlias(new AliasDef { Name = "attack", Pattern = "kk *", Send = "kill %1" });
    // a regex trigger capturing hp, storing it in a variable
    engine.AddTrigger(new TriggerDef
    {
        Name = "hp", IsRegex = true, Pattern = @"^HP:\s*(\d+)/(\d+)",
        SendTo = SendTo.Variable, Variable = "hp", Send = "%1"
    });
    // a trigger that reacts and sends, only once
    engine.AddTrigger(new TriggerDef { Name = "greet", Pattern = "* says hello", Send = "wave", OneShot = true });
    // a trigger that runs a script
    engine.AddTrigger(new TriggerDef { Name = "ontell", Pattern = "* tells you *", SendTo = SendTo.Script, Script = "onTell" });
    // an interval timer
    engine.AddTimer(new TimerDef { Name = "heartbeat", IntervalSeconds = 5, Send = "save" });

    Console.WriteLine($"registered: {engine.TriggerCount} triggers, {engine.AliasCount} aliases, {engine.TimerCount} timers\n");

    Feed(engine, rec, input: "kk orc");                 // alias -> kill orc
    Feed(engine, rec, line: "HP: 42/100 MP: 10/10");    // trigger -> var hp=42
    Feed(engine, rec, line: "Bob says hello");          // one-shot -> wave (fires once)
    Feed(engine, rec, line: "Bob says hello");          // ...should NOT fire again
    Feed(engine, rec, line: "Alice tells you hi");      // script -> onTell(Alice, hi)

    Console.WriteLine("\n-- 6-second tick (heartbeat interval 5s) --");
    engine.Tick(6.0, rec);                              // timer -> save

    Console.WriteLine($"\nvariable hp = {vars.Get("hp") ?? "(unset)"}");
    Console.WriteLine($"one-shot 'greet' still registered? {engine.TriggerCount} triggers remain (expect greet gone)");
    Console.WriteLine("\nAutomation self-test complete.");
}

static void Feed(AutomationEngine engine, RecordingActions rec, string? line = null, string? input = null)
{
    if (input is not null)
    {
        Console.WriteLine($"input> {input}");
        bool consumed = engine.ProcessInput(input, rec);
        if (!consumed) Console.WriteLine($"    (passed through) send: {input}");
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
    await session.ConnectAsync();
    Console.WriteLine($"connected to {host}:{port} — type to send, Ctrl+C to quit\n");
    string? input;
    while ((input = Console.ReadLine()) is not null)
        session.Submit(input);
    await session.DisposeAsync();
}

/// <summary>An IWorldActions that prints what the engine asks it to do.</summary>
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

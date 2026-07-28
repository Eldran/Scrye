using System.Text;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Session;
using Scrye.Core.Text;

// Scrye.Cli — a dependency-free harness for the engine core.
//   --selftest        run canned bytes through telnet + ANSI and print the result
//   <host> <port>     connect to a live MUD and stream to the console

if (args.Length >= 1 && args[0] == "--selftest")
{
    SelfTest();
    return 0;
}

if (args.Length >= 2 && int.TryParse(args[1], out int port))
{
    await ConnectAsync(args[0], port);
    return 0;
}

Console.WriteLine("usage: scrye-cli --selftest | <host> <port>");
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

    // A server chunk: IAC WILL ECHO, then coloured text with newlines, then a bare prompt.
    var bytes = new List<byte>();
    bytes.AddRange(new byte[] { 255, 251, 1 });                       // IAC WILL ECHO -> expect IAC DONT ECHO
    bytes.AddRange(Encoding.UTF8.GetBytes("\x1b[1;32mHello\x1b[0m world\r\n"));
    bytes.AddRange(Encoding.UTF8.GetBytes("\x1b[38;5;208m256-orange\x1b[0m and \x1b[38;2;10;20;250mtruecolour\x1b[0m\n"));
    bytes.AddRange(Encoding.UTF8.GetBytes("Enter command> "));         // no newline -> prompt

    byte[] data = telnet.Process(bytes.ToArray(), out byte[] response);

    Console.Write("telnet response bytes:");
    foreach (byte b in response) Console.Write($" {b}");
    Console.WriteLine(response.SequenceEqual(new byte[] { 255, 254, 1 }) ? "   (correct: IAC DONT ECHO)" : "   (UNEXPECTED)");
    Console.WriteLine();

    ansi.Feed(Encoding.UTF8.GetString(data));
    ansi.FlushAsPrompt();

    Console.WriteLine($"\n{lineNo} lines parsed. Self-test complete.");
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

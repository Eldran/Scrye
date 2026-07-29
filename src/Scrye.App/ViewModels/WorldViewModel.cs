using System.Collections.Concurrent;
using Avalonia.Threading;
using Scrye.Core.Model;
using Scrye.Core.Session;
using Scrye.Core.Text;
using Scrye.Scripting;

namespace Scrye.App.ViewModels;

/// <summary>
/// Wraps a live <see cref="MudSession"/> for one world tab. Engine lines land on
/// the session loop (background); we enqueue them and drain to the
/// <see cref="ScrollbackBuffer"/> on a UI-thread timer, so the renderer sees at
/// most one update per frame instead of one per line (firehose-safe).
/// </summary>
public sealed class WorldViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly Rgb EchoColour = new(0x60, 0xC0, 0xF0);   // cyan-ish for local echo
    private static readonly Rgb SystemColour = new(0xF0, 0xC0, 0x40); // amber for system notices

    private readonly MudSession _session;
    private readonly LuaScriptHost _scriptHost;
    private readonly ConcurrentQueue<Line> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly List<Line> _drainBuffer = new(256);

    public string Title { get; }
    public ScrollbackBuffer Scrollback { get; } = new();
    public RelayCommand SubmitCommand { get; }

    private string _input = "";
    public string Input { get => _input; set => SetField(ref _input, value); }

    public WorldViewModel(WorldProfile profile)
    {
        Title = profile.Name;
        _session = new MudSession(profile);
        _session.LineReady += line => _pending.Enqueue(line);
        _session.StateChanged += s => _pending.Enqueue(Line.FromText($"[{s}]", SystemColour));
        _session.MsspReceived += mssp =>
        {
            string name = mssp.TryGetValue("NAME", out var n) ? n : Title;
            string players = mssp.TryGetValue("PLAYERS", out var pc) ? $" - {pc} players online" : "";
            AppendSystem($"server: {name}{players}");
        };

        // scripting: trigger/alias/timer script callbacks run on the session loop
        _scriptHost = new LuaScriptHost(new SessionWorldApi(_session));
        _session.ScriptDispatcher = (fn, wildcards) => _scriptHost.CallFunction(fn, wildcards.ToArray());
        _session.ScriptExecutor = code => _scriptHost.Execute(code);

        SubmitCommand = new RelayCommand(Submit);

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public Task ConnectAsync() => _session.ConnectAsync();

    public void AppendSystem(string text) => _pending.Enqueue(Line.FromText("* " + text, SystemColour));

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        _drainBuffer.Clear();
        while (_pending.TryDequeue(out Line? line))
            _drainBuffer.Add(line);
        Scrollback.AddRange(_drainBuffer);
    }

    private void Submit()
    {
        string text = Input ?? "";
        Input = "";

        if (text == "mipstart") { _pending.Enqueue(Line.FromText("> " + text, EchoColour)); _session.StartMip(); return; }

        // "/..." is a local Lua console — runs on the session loop, not sent to the MUD.
        // e.g.  /world.AddAlias("greet", "hi *", "say hello %1")
        if (text.StartsWith('/') && text.Length > 1)
        {
            _pending.Enqueue(Line.FromText(text, EchoColour));
            _session.RunScript(text[1..]);
            return;
        }

        _pending.Enqueue(Line.FromText("> " + text, EchoColour));   // local echo
        _session.Submit(text);
    }

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Stop();
        await _session.DisposeAsync();
    }
}

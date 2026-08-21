using System.Globalization;
using System.Text.RegularExpressions;

namespace Scrye.Core.Automation;

/// <summary>One step of a sequence: a command to send (with a repeat count) or a wait.</summary>
public sealed record SequenceStep
{
    /// <summary>"send" (straight to the MUD), "client" (through the client's command
    /// pipeline, so plugin and profile aliases see it) or "wait".</summary>
    public string Kind { get; init; } = "send";
    public string Text { get; init; } = "";     // command (send/client)
    public int Count { get; init; } = 1;         // repeats (send/client)
    public double Seconds { get; init; }         // delay (wait)

    public static SequenceStep Send(string text, int count = 1) =>
        new() { Kind = "send", Text = text, Count = Math.Max(1, count) };
    /// <summary>A step written <c>&gt;cs pause</c>: run through the client pipeline rather
    /// than sent to the MUD, so it can be a plugin command.</summary>
    public static SequenceStep Client(string text, int count = 1) =>
        new() { Kind = "client", Text = text, Count = Math.Max(1, count) };
    public static SequenceStep Wait(double seconds) =>
        new() { Kind = "wait", Seconds = Math.Max(0, seconds) };
}

/// <summary>
/// A named, ordered sequence of commands — first-class travel/action macros that
/// replace giant one-line aliases (roadmap #14). Between commands the runner waits
/// for the room prompt (<see cref="PromptGated"/>) so it never floods, with a safety
/// timeout; or, ungated, paces by a fixed delay.
/// </summary>
public sealed record SequenceDef
{
    public string Name { get; init; } = "";
    public IReadOnlyList<SequenceStep> Steps { get; init; } = Array.Empty<SequenceStep>();

    /// <summary>Wait for a prompt between commands (the usual speedwalk behaviour).</summary>
    public bool PromptGated { get; init; } = true;
    /// <summary>Safety: advance anyway if no prompt arrives within this long (prompt-gated).</summary>
    public double StepTimeoutSeconds { get; init; } = 2.0;
    /// <summary>Pacing between commands when NOT prompt-gated.</summary>
    public double StepDelaySeconds { get; init; } = 0.5;
}

/// <summary>The persisted, editable form of a named sequence: the raw source text
/// (parsed with <see cref="SequenceParser"/>) plus pacing options. Stored in a
/// profile layer and merged through the cascade like triggers/aliases, then
/// <see cref="ToDef"/>'d into a runnable <see cref="SequenceDef"/> at load time.</summary>
public sealed record SequenceSpec
{
    public string Name { get; init; } = "";
    /// <summary>Compact source, e.g. <c>enter; north x3; wait 2; west</c> (see <see cref="SequenceParser"/>).</summary>
    public string Source { get; init; } = "";
    public bool PromptGated { get; init; } = true;
    public double StepTimeoutSeconds { get; init; } = 2.0;
    public double StepDelaySeconds { get; init; } = 0.5;

    public SequenceDef ToDef() => SequenceParser.Parse(Name, Source, PromptGated) with
    {
        StepTimeoutSeconds = StepTimeoutSeconds <= 0 ? 2.0 : StepTimeoutSeconds,
        StepDelaySeconds = StepDelaySeconds < 0 ? 0.5 : StepDelaySeconds,
    };
}

public enum SequenceState { Idle, Waiting, WaitingForPrompt, Paused, Finished, Stopped }

/// <summary>A snapshot of a running sequence for the status strip.</summary>
public readonly record struct SequenceStatus(SequenceState State, string Name, int Sent, int Total, string Command)
{
    /// <summary>True while a sequence is mid-run (running or paused) — show the strip.</summary>
    public bool Active => State is SequenceState.Waiting or SequenceState.WaitingForPrompt or SequenceState.Paused;
}

/// <summary>
/// Runs one sequence at a time and holds a registry of named ones. Deterministic and
/// single-threaded (driven on the session loop): <see cref="Tick"/> advances waits and
/// the prompt-timeout, <see cref="OnPrompt"/> advances a prompt-gated step. Commands are
/// emitted via <see cref="Send"/>; progress via <see cref="StatusChanged"/>. The plan is
/// flattened (a repeat of N becomes N sends) so the step machine stays tiny.
/// </summary>
public sealed class SequenceEngine
{
    private readonly Dictionary<string, SequenceDef> _defs = new(StringComparer.OrdinalIgnoreCase);

    private SequenceDef? _active;
    private string _name = "";
    private List<SequenceStep> _plan = new();
    private int _cursor;
    private int _sent;
    private double _timer;
    private double _target;
    private string _command = "";
    private SequenceState _state = SequenceState.Idle;
    private SequenceState _resumeTo = SequenceState.Waiting;

    /// <summary>A command the sequence wants sent to the MUD.</summary>
    public event Action<string>? Send;
    /// <summary>A <c>&gt;</c>-prefixed step: run this through the client's command pipeline.
    /// When nothing subscribes, such a step falls back to <see cref="Send"/> -- a host with no
    /// pipeline (the CLI harness) still runs the sequence rather than silently dropping steps.</summary>
    public event Action<string>? SendClient;
    /// <summary>Progress/state changed.</summary>
    public event Action<SequenceStatus>? StatusChanged;

    public SequenceState State => _state;
    public IReadOnlyCollection<string> Names => _defs.Keys;

    public void Register(SequenceDef def) => _defs[def.Name] = def;

    /// <summary>Drop all registered named sequences (for a live profile reload).
    /// Does not stop a sequence that is currently running.</summary>
    public void ClearRegistry() => _defs.Clear();

    /// <summary>Run a registered sequence by name. Returns false if unknown.</summary>
    public bool Run(string name)
    {
        if (!_defs.TryGetValue(name, out SequenceDef? def)) return false;
        Start(def);
        return true;
    }

    /// <summary>Run a one-off sequence (e.g. a parsed <c>.walk</c>).</summary>
    public void RunAdHoc(SequenceDef def) => Start(def);

    public void OnPrompt()
    {
        if (_state == SequenceState.WaitingForPrompt) { _timer = 0; Step(); }
    }

    public void Tick(double dtSeconds)
    {
        if (_state is SequenceState.Waiting or SequenceState.WaitingForPrompt)
        {
            _timer += dtSeconds;
            if (_timer >= _target) { _timer = 0; Step(); }
        }
    }

    public void Pause()
    {
        if (_state is SequenceState.Waiting or SequenceState.WaitingForPrompt)
        {
            _resumeTo = _state;
            _state = SequenceState.Paused;
            EmitStatus();
        }
    }

    public void Resume()
    {
        if (_state == SequenceState.Paused)
        {
            _state = _resumeTo;
            _timer = 0;
            EmitStatus();
        }
    }

    public void Stop()
    {
        if (_active is null) return;
        _active = null;
        _state = SequenceState.Stopped;
        _command = "";
        EmitStatus();
    }

    // ---- internals -----------------------------------------------------------

    private void Start(SequenceDef def)
    {
        _active = def;
        _name = def.Name;
        _plan = Flatten(def);
        _cursor = 0; _sent = 0; _timer = 0; _command = "";
        if (_plan.Count == 0) { Finish(SequenceState.Finished); return; }
        Step();
    }

    private static List<SequenceStep> Flatten(SequenceDef def)
    {
        var plan = new List<SequenceStep>();
        foreach (SequenceStep s in def.Steps)
        {
            if (s.Kind == "wait") plan.Add(s);
            else for (int i = 0; i < Math.Max(1, s.Count); i++)
                plan.Add(s.Kind == "client" ? SequenceStep.Client(s.Text) : SequenceStep.Send(s.Text));
        }
        return plan;
    }

    /// <summary>Perform the action at the cursor, then arm the gate before the next one.</summary>
    private void Step()
    {
        if (_active is null) return;
        if (_cursor >= _plan.Count) { Finish(SequenceState.Finished); return; }

        SequenceStep step = _plan[_cursor];
        _cursor++;

        if (step.Kind == "wait")
        {
            _command = $"(wait {step.Seconds:0.#}s)";
            _timer = 0; _target = step.Seconds;
            _state = SequenceState.Waiting;
            EmitStatus();
            if (step.Seconds <= 0) Step();   // zero-wait: fall straight through
            return;
        }

        // send
        _command = step.Text;
        _sent++;
        if (step.Kind == "client" && SendClient is not null) SendClient.Invoke(step.Text);
        else Send?.Invoke(step.Text);

        if (_cursor >= _plan.Count) { Finish(SequenceState.Finished); return; }   // nothing after the last send

        _timer = 0;
        if (_active.PromptGated) { _state = SequenceState.WaitingForPrompt; _target = _active.StepTimeoutSeconds; }
        else { _state = SequenceState.Waiting; _target = _active.StepDelaySeconds; }
        EmitStatus();
    }

    private void Finish(SequenceState state)
    {
        _active = null;
        _state = state;
        _command = "";
        EmitStatus();
    }

    private int TotalSends()
    {
        int n = 0;
        foreach (SequenceStep s in _plan) if (s.Kind != "wait") n++;
        return n;
    }

    private void EmitStatus() =>
        StatusChanged?.Invoke(new SequenceStatus(_state, _name, _sent, TotalSends(), _command));
}

/// <summary>
/// Parses a compact sequence script into a <see cref="SequenceDef"/>. Steps are
/// separated by <c>;</c> or newlines. Grammar per step: <c>north</c>, <c>north x28</c>
/// or <c>north*28</c> (repeat), <c>wait 2</c> / <c>pause 1.5</c> (delay in seconds).
///
/// <para>A step written <c>&gt;cs pause</c> runs through the client's command pipeline --
/// plugin aliases, then profile aliases, then the MUD if nothing claimed it -- rather than
/// going straight to the wire, so a sequence can drive a plugin:
/// <c>&gt;cs pause; open cask; get all; wait 2; &gt;cs resume</c>. Unprefixed steps are
/// untouched, which is what keeps every speedwalk written before this behaving exactly as it
/// did: a lone <c>n</c> is still a lone <c>n</c> on the wire and never meets an alias.
/// <c>&gt;&gt;</c> is a literal '&gt;' for a MUD that wants one.</para>
/// </summary>
public static class SequenceParser
{
    private static readonly Regex WaitRe = new(@"^(?:wait|pause)\s+([0-9]*\.?[0-9]+)$", RegexOptions.IgnoreCase);
    private static readonly Regex RepeatRe = new(@"^(.*?)\s*[x*]\s*(\d+)$", RegexOptions.IgnoreCase);

    public static SequenceDef Parse(string name, string text, bool promptGated = true)
    {
        var steps = new List<SequenceStep>();
        foreach (string raw in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string tok = raw.Trim();
            if (tok.Length == 0) continue;

            bool client = false;
            if (tok.StartsWith(">>", StringComparison.Ordinal))
            {
                tok = tok[1..];                       // ">>look" -> ">look" sent to the MUD
            }
            else if (tok[0] == '>')
            {
                client = true;
                tok = tok[1..].Trim();
                if (tok.Length == 0) continue;        // a bare ">" asked for no command
            }

            // "wait 2" is a delay only when unprefixed. ">wait 2" asks the client to run a
            // command spelled "wait 2", which a plugin may well define, so it is not ours to
            // reinterpret as a pause.
            if (!client)
            {
                Match w = WaitRe.Match(tok);
                if (w.Success)
                {
                    steps.Add(SequenceStep.Wait(double.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture)));
                    continue;
                }
            }

            Match r = RepeatRe.Match(tok);
            if (r.Success && r.Groups[1].Value.Trim().Length > 0)
            {
                string body = r.Groups[1].Value.Trim();
                int n = int.Parse(r.Groups[2].Value);
                steps.Add(client ? SequenceStep.Client(body, n) : SequenceStep.Send(body, n));
                continue;
            }

            steps.Add(client ? SequenceStep.Client(tok) : SequenceStep.Send(tok));
        }
        return new SequenceDef { Name = name, Steps = steps, PromptGated = promptGated };
    }
}

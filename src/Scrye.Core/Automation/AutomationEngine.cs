namespace Scrye.Core.Automation;

/// <summary>
/// The per-world automation engine: triggers (on incoming lines), aliases (on
/// user input), timers (on an interval tick), and variables. Runs on the session
/// loop, so processing is single-threaded and deterministically ordered — no
/// locks, no re-entrancy. Rules are held by name (the stable id); adding a rule
/// with an existing name replaces it (supports the profile cascade's override).
/// </summary>
public sealed class AutomationEngine
{
    private sealed class Trig { public TriggerDef Def = null!; public CompiledPattern Pattern = null!; public bool Enabled; }
    private sealed class Als { public AliasDef Def = null!; public CompiledPattern Pattern = null!; public bool Enabled; }
    private sealed class Tmr { public TimerDef Def = null!; public bool Enabled; public double Elapsed; }

    private readonly List<Trig> _triggers = new();
    private readonly List<Als> _aliases = new();
    private readonly List<Tmr> _timers = new();
    private readonly VariableStore _vars;

    public AutomationEngine(VariableStore vars) => _vars = vars;

    public VariableStore Variables => _vars;
    public int TriggerCount => _triggers.Count;
    public int AliasCount => _aliases.Count;
    public int TimerCount => _timers.Count;

    /// <summary>Raised whenever a rule fires, with a summary of what it did.
    /// The session wires this to the event bus (trigger timeline / debugger).
    /// Optional — null in headless/unit contexts.</summary>
    public Action<AutomationHit>? Hit { get; set; }

    // ---- registration ----------------------------------------------------

    public void AddTrigger(TriggerDef def)
    {
        RemoveTrigger(def.Name);
        _triggers.Add(new Trig { Def = def, Pattern = Compile(def.Pattern, def.IsRegex, def.IgnoreCase), Enabled = def.Enabled });
        _triggers.Sort((a, b) => a.Def.Sequence.CompareTo(b.Def.Sequence));
    }

    public void AddAlias(AliasDef def)
    {
        RemoveAlias(def.Name);
        _aliases.Add(new Als { Def = def, Pattern = Compile(def.Pattern, def.IsRegex, def.IgnoreCase), Enabled = def.Enabled });
        _aliases.Sort((a, b) => a.Def.Sequence.CompareTo(b.Def.Sequence));
    }

    public void AddTimer(TimerDef def)
    {
        RemoveTimer(def.Name);
        _timers.Add(new Tmr { Def = def, Enabled = def.Enabled });
    }

    public bool RemoveTrigger(string name) => !string.IsNullOrEmpty(name) && _triggers.RemoveAll(t => t.Def.Name == name) > 0;
    public bool RemoveAlias(string name) => !string.IsNullOrEmpty(name) && _aliases.RemoveAll(a => a.Def.Name == name) > 0;
    public bool RemoveTimer(string name) => !string.IsNullOrEmpty(name) && _timers.RemoveAll(t => t.Def.Name == name) > 0;

    public bool EnableTrigger(string name, bool enabled) => SetEnabled(_triggers.Find(t => t.Def.Name == name), enabled, x => x.Enabled = enabled);
    public bool EnableAlias(string name, bool enabled) => SetEnabled(_aliases.Find(a => a.Def.Name == name), enabled, x => x.Enabled = enabled);
    public bool EnableTimer(string name, bool enabled) => SetEnabled(_timers.Find(t => t.Def.Name == name), enabled, x => x.Enabled = enabled);

    public void EnableTriggerGroup(string group, bool enabled) { foreach (Trig t in _triggers) if (t.Def.Group == group) t.Enabled = enabled; }
    public void EnableAliasGroup(string group, bool enabled) { foreach (Als a in _aliases) if (a.Def.Group == group) a.Enabled = enabled; }
    public void EnableTimerGroup(string group, bool enabled) { foreach (Tmr t in _timers) if (t.Def.Group == group) t.Enabled = enabled; }

    private static bool SetEnabled<T>(T? item, bool enabled, Action<T> apply) where T : class
    {
        if (item is null) return false;
        apply(item);
        return true;
    }

    // ---- processing ------------------------------------------------------

    /// <summary>Run an incoming line through the triggers, firing matches in order.</summary>
    public void ProcessLine(string line, IWorldActions ctx)
    {
        for (int i = 0; i < _triggers.Count; i++)
        {
            Trig t = _triggers[i];
            if (!t.Enabled) continue;

            MatchResult? m = t.Pattern.Match(line);
            if (m is null) continue;

            string action = Fire(t.Def.SendTo, t.Def.Send, t.Def.Variable, t.Def.Script, m, ctx);
            Hit?.Invoke(new AutomationHit(AutomationHitKind.Trigger, t.Def.Name, t.Def.Group, line, action));

            if (t.Def.OneShot) { _triggers.RemoveAt(i); i--; }
            if (!t.Def.KeepEvaluating) break;
        }
    }

    /// <summary>Dry-run: evaluate a line against the triggers exactly as
    /// <see cref="ProcessLine"/> would (order, enable state, keep-evaluating/break),
    /// but with NO side effects — nothing is sent, no variable changes, no one-shot
    /// is consumed. Returns the hits that would have fired. Powers the debugger's
    /// "what would this line do?" simulation.</summary>
    public IReadOnlyList<AutomationHit> Simulate(string line)
    {
        var hits = new List<AutomationHit>();
        for (int i = 0; i < _triggers.Count; i++)
        {
            Trig t = _triggers[i];
            if (!t.Enabled) continue;

            MatchResult? m = t.Pattern.Match(line);
            if (m is null) continue;

            hits.Add(new AutomationHit(AutomationHitKind.Trigger, t.Def.Name, t.Def.Group, line,
                Describe(t.Def.SendTo, t.Def.Send, t.Def.Variable, t.Def.Script, m)));

            if (!t.Def.KeepEvaluating) break;
        }
        return hits;
    }

    /// <summary>Run user input through the aliases. Returns true if an alias
    /// handled it (the raw input should NOT be sent); false if it should pass through.</summary>
    public bool ProcessInput(string input, IWorldActions ctx)
    {
        bool consumed = false;
        for (int i = 0; i < _aliases.Count; i++)
        {
            Als a = _aliases[i];
            if (!a.Enabled) continue;

            MatchResult? m = a.Pattern.Match(input);
            if (m is null) continue;

            string action = Fire(a.Def.SendTo, a.Def.Send, a.Def.Variable, a.Def.Script, m, ctx);
            Hit?.Invoke(new AutomationHit(AutomationHitKind.Alias, a.Def.Name, a.Def.Group, input, action));
            consumed = true;

            if (a.Def.OneShot) { _aliases.RemoveAt(i); i--; }
            if (!a.Def.KeepEvaluating) break;
        }
        return consumed;
    }

    /// <summary>Advance timers by <paramref name="dtSeconds"/>, firing any that come due.</summary>
    public void Tick(double dtSeconds, IWorldActions ctx)
    {
        for (int i = 0; i < _timers.Count; i++)
        {
            Tmr t = _timers[i];
            if (!t.Enabled) continue;

            t.Elapsed += dtSeconds;
            if (t.Elapsed < t.Def.IntervalSeconds) continue;

            t.Elapsed -= t.Def.IntervalSeconds;
            string action = Fire(t.Def.SendTo, t.Def.Send, t.Def.Variable, t.Def.Script, null, ctx);
            Hit?.Invoke(new AutomationHit(AutomationHitKind.Timer, t.Def.Name, t.Def.Group, "", action));

            if (t.Def.OneShot) { _timers.RemoveAt(i); i--; }
        }
    }

    // ---- firing ----------------------------------------------------------

    /// <summary>Execute a rule's action and return a human-readable summary of what it did.</summary>
    private string Fire(SendTo sendTo, string? send, string? variable, string? script, MatchResult? m, IWorldActions ctx)
    {
        string text = Template.Expand(send, m, _vars);

        switch (sendTo)
        {
            case SendTo.World when text.Length > 0: ctx.Send(text); break;
            case SendTo.Output: ctx.Echo(text); break;
            case SendTo.Command: ctx.Echo(text); break;
            case SendTo.Variable when !string.IsNullOrEmpty(variable): ctx.SetVariable(variable!, text); break;
            case SendTo.Script: break; // handled below
        }

        if (!string.IsNullOrEmpty(script))
            ctx.CallScript(script!, m?.Wildcards ?? Array.Empty<string>());

        return Describe(sendTo, send, variable, script, m);
    }

    /// <summary>Build the same summary <see cref="Fire"/> returns, but WITHOUT
    /// performing the action or mutating anything. Used by <see cref="Simulate"/>.</summary>
    private string Describe(SendTo sendTo, string? send, string? variable, string? script, MatchResult? m)
    {
        string text = Template.Expand(send, m, _vars);
        string primary = sendTo switch
        {
            SendTo.World when text.Length > 0 => $"send: {text}",
            SendTo.World => "send: (empty)",
            SendTo.Output => $"echo: {text}",
            SendTo.Command => $"command: {text}",
            SendTo.Variable when !string.IsNullOrEmpty(variable) => $"var {variable}={text}",
            SendTo.Variable => "var: (no target)",
            SendTo.Script => "",
            _ => "",
        };

        if (!string.IsNullOrEmpty(script))
        {
            string call = $"script: {script}";
            return primary.Length == 0 ? call : $"{primary}; {call}";
        }
        return primary;
    }

    private static CompiledPattern Compile(string pattern, bool isRegex, bool ignoreCase) =>
        new(pattern, isRegex, ignoreCase);
}

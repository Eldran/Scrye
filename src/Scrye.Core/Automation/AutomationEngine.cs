using Scrye.Core.Text;

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

    /// <summary>Triggers flagged <see cref="TriggerDef.Notify"/>, with their live enabled
    /// state. Exposed so the user can audit what will raise a notification — which matters
    /// far more once notifications leave the desktop and reach a phone.</summary>
    public IReadOnlyList<(TriggerDef Def, bool Enabled)> NotifyingTriggers
    {
        get
        {
            var list = new List<(TriggerDef, bool)>();
            foreach (Trig t in _triggers)
                if (t.Def.Notify) list.Add((t.Def, t.Enabled));
            return list;
        }
    }
    /// <summary>Every trigger with its live enabled state and its Notify flag. The companion
    /// panel needs the ones that are NOT notifying too — you cannot switch a notification on
    /// from a list that only shows what is already on.</summary>
    public IReadOnlyList<(TriggerDef Def, bool Enabled)> AllTriggers
    {
        get
        {
            var list = new List<(TriggerDef, bool)>();
            foreach (Trig t in _triggers) list.Add((t.Def, t.Enabled));
            return list;
        }
    }

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

    /// <summary>Drop all triggers/aliases/timers (used when live-reloading a profile's rule set).</summary>
    public void ClearTriggers() => _triggers.Clear();
    public void ClearAliases() => _aliases.Clear();
    public void ClearTimers() => _timers.Clear();

    /// <summary>Flip a trigger's Notify flag on the live rule set, so a change made in the
    /// companion panel takes effect without a reconnect. Matched by reference first (the panel
    /// hands back the very def it was given) and by name only as a fallback, so two triggers
    /// sharing a name cannot swap places. Returns false when the trigger is no longer loaded.
    ///
    /// <para><see cref="TriggerDef"/> is an immutable record, so this replaces the def rather
    /// than mutating it — the compiled pattern is untouched, since Notify does not affect
    /// matching.</para></summary>
    public bool SetTriggerNotify(TriggerDef def, bool notify)
    {
        Trig? t = _triggers.Find(x => ReferenceEquals(x.Def, def));
        if (t is null && !string.IsNullOrWhiteSpace(def.Name))
            t = _triggers.Find(x => x.Def.Name == def.Name);
        if (t is null) return false;
        t.Def = t.Def with { Notify = notify };
        return true;
    }

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

            string action = Fire(t.Def.SendTo, t.Def.Send, t.Def.Variable, t.Def.Script, m, ctx,
                                 t.Def.CapturePane, t.Def.Gag, t.Def.Notify, t.Def.Sound);
            ApplyHighlight(t.Def, m, line, ctx);
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
                Describe(t.Def.SendTo, t.Def.Send, t.Def.Variable, t.Def.Script, m,
                         t.Def.CapturePane, t.Def.Gag, t.Def.Notify, t.Def.Sound)));

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
    /// <summary>
    /// Stop every timer firing without touching any timer's own Enabled flag, so the state a
    /// user configured survives the suspension and comes back with it. Set by the idle guard when
    /// it decides nobody is at the keyboard; cleared when someone proves otherwise.
    /// </summary>
    public bool TimersSuspended { get; set; }

    public void Tick(double dtSeconds, IWorldActions ctx)
    {
        if (TimersSuspended) return;

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

    /// <summary>Execute a rule's action and return a human-readable summary of what it did.
    /// <paramref name="capturePane"/>/<paramref name="gag"/> only apply to triggers
    /// (they act on the line being processed).</summary>
    private string Fire(SendTo sendTo, string? send, string? variable, string? script, MatchResult? m,
                        IWorldActions ctx, string? capturePane = null, bool gag = false,
                        bool notify = false, string? sound = null)
    {
        string text = Template.Expand(send, m, _vars);

        if (!string.IsNullOrWhiteSpace(capturePane)) ctx.Capture(capturePane!.Trim());
        if (gag) ctx.GagLine();
        if (notify) ctx.Notify();
        if (!string.IsNullOrWhiteSpace(sound)) ctx.PlaySound(sound!.Trim());

        switch (sendTo)
        {
            // Multi-line send: each non-empty line is its own command (MUSHclient-style).
            case SendTo.World when text.Length > 0: ForEachLine(text, ctx.Send); break;
            case SendTo.Output: ForEachLine(text, ctx.Echo); break;
            case SendTo.Command: ForEachLine(text, ctx.Echo); break;
            case SendTo.Variable when !string.IsNullOrEmpty(variable): ctx.SetVariable(variable!, text); break;
            case SendTo.Script: break; // handled below
        }

        if (!string.IsNullOrEmpty(script))
            ctx.CallScript(script!, m?.Wildcards ?? Array.Empty<string>());

        return Describe(sendTo, send, variable, script, m, capturePane, gag, notify, sound);
    }

    /// <summary>Apply a trigger's highlight (if any) to the line being processed, via
    /// <see cref="IWorldActions.Highlight"/>. Whole-line highlights span the full text;
    /// otherwise only the matched range is recoloured.</summary>
    private static void ApplyHighlight(TriggerDef def, MatchResult m, string line, IWorldActions ctx)
    {
        bool hasFore = Rgb.TryParseHex(def.HighlightFore, out Rgb fore);
        bool hasBack = Rgb.TryParseHex(def.HighlightBack, out Rgb back);
        if (!hasFore && !hasBack) return;

        int start = def.HighlightWholeLine ? 0 : m.Index;
        int length = def.HighlightWholeLine ? line.Length : m.Length;
        if (length <= 0) return;
        ctx.Highlight(hasFore ? fore : null, hasBack ? back : null, start, length);
    }

    /// <summary>Run <paramref name="action"/> for each non-empty line of an expanded
    /// send template — a multi-line Send fires one command per line, in order.</summary>
    public static void ForEachLine(string text, Action<string> action)
    {
        if (!text.Contains('\n')) { if (text.Length > 0) action(text); return; }
        foreach (string raw in text.Split('\n'))
        {
            string cmd = raw.TrimEnd('\r');
            if (cmd.Length > 0) action(cmd);
        }
    }

    /// <summary>First line of a possibly multi-line send, with a "+n more" suffix — for
    /// event-log summaries.</summary>
    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        if (nl < 0) return text;
        int extra = 0;
        foreach (string raw in text.Split('\n')) if (raw.TrimEnd('\r').Length > 0) extra++;
        return $"{text[..nl].TrimEnd('\r')} (+{Math.Max(0, extra - 1)} more)";
    }

    /// <summary>Build the same summary <see cref="Fire"/> returns, but WITHOUT
    /// performing the action or mutating anything. Used by <see cref="Simulate"/>.</summary>
    private string Describe(SendTo sendTo, string? send, string? variable, string? script, MatchResult? m,
                            string? capturePane = null, bool gag = false,
                            bool notify = false, string? sound = null)
    {
        string text = Template.Expand(send, m, _vars);
        string primary = sendTo switch
        {
            SendTo.World when text.Length > 0 => $"send: {FirstLine(text)}",
            SendTo.World => "send: (empty)",
            SendTo.Output => $"echo: {FirstLine(text)}",
            SendTo.Command => $"command: {FirstLine(text)}",
            SendTo.Variable when !string.IsNullOrEmpty(variable) => $"var {variable}={text}",
            SendTo.Variable => "var: (no target)",
            SendTo.Script => "",
            _ => "",
        };

        var parts = new List<string>(4);
        if (primary.Length > 0) parts.Add(primary);
        if (!string.IsNullOrWhiteSpace(capturePane)) parts.Add($"capture: {capturePane!.Trim()}");
        if (gag) parts.Add("gag");
        if (notify) parts.Add("notify");
        if (!string.IsNullOrWhiteSpace(sound)) parts.Add($"sound: {sound!.Trim()}");
        if (!string.IsNullOrEmpty(script)) parts.Add($"script: {script}");
        return string.Join("; ", parts);
    }

    private static CompiledPattern Compile(string pattern, bool isRegex, bool ignoreCase) =>
        new(pattern, isRegex, ignoreCase);
}

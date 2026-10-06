using System.Globalization;
using System.Text;

namespace Scrye.Core.Automation;

/// <summary>
/// A trigger, alias or timer written out as plugin Lua - "Copy as Lua" in the rule editors
/// (a user missing MUSHclient's copy-a-trigger-into-a-plugin, 6 Oct 2026: try a rule quickly in
/// the editor, then move it into a plugin with the syntax already handled).
///
/// <para>What maps straight across does: the pattern and its flags, <c>lines</c>, and a
/// send to the MUD as <c>send =</c>, which the host expands exactly as a profile rule's
/// (<c>%1</c>, <c>%0</c>, <c>%&lt;name&gt;</c>, <c>${var}</c>). Every other target and effect
/// - echo, variable, capture pane, sound, notify - becomes a <c>run</c> function using the
/// wildcards it is handed. What a plugin rule cannot do (gag, highlight, group, sequence,
/// one-shot, a disabled rule...) is said in a comment at the top rather than dropped
/// silently, so the snippet never pretends to be more than it is.</para>
/// </summary>
public static class RuleLua
{
    public static string Trigger(TriggerDef t)
    {
        var notes = new List<string>();
        if (!t.Enabled) notes.Add("it is switched off in the profile - a plugin rule is always on");
        if (t.Gag) notes.Add("it gags the line - a plugin does that in scrye.onLine (return false)");
        if (t.HighlightFore is not null || t.HighlightBack is not null)
            notes.Add("it highlights the line - a plugin rule cannot recolour output");
        if (t.KeepEvaluating) notes.Add("'keep evaluating' - plugin triggers all run anyway");
        if (t.OneShot) notes.Add("one-shot - a plugin rule cannot remove itself");
        if (t.RepeatOnLine) notes.Add("'repeat on line' - a plugin rule fires once per line");
        if (t.Sequence != 100) notes.Add($"sequence {t.Sequence} - plugin triggers run in the order they are added");

        var sb = new StringBuilder();
        Header(sb, "trigger", t.Name, t.Group, notes);
        sb.Append("scrye.addTrigger{\n");
        Pattern(sb, t.Pattern, t.IsRegex, t.IgnoreCase);
        if (t.Lines > 1) sb.Append("  lines = ").Append(t.Lines.ToString(CultureInfo.InvariantCulture)).Append(",\n");

        var body = new List<string>();
        string? send = Target(t.SendTo, t.Send, t.Variable, t.Script, body, wildcards: true);
        if (t.CapturePane is not null) body.Add($"scrye.capture({Str(t.CapturePane)}, table.concat({{...}}, \" \"))  -- the profile captured the whole line");
        if (t.Sound is not null) body.Add($"scrye.sound({Str(t.Sound)})");
        if (t.Notify) body.Add($"scrye.notify({Str(Display(t.Name, t.Pattern) + " matched")})");
        if (send is not null) sb.Append("  send = ").Append(Str(send)).Append(",\n");
        Run(sb, body);
        sb.Append("}\n");
        return sb.ToString();
    }

    public static string Alias(AliasDef a)
    {
        var notes = new List<string>();
        if (!a.Enabled) notes.Add("it is switched off in the profile - a plugin rule is always on");
        if (a.KeepEvaluating) notes.Add("'keep evaluating' - a plugin alias consumes the line");
        if (a.OneShot) notes.Add("one-shot - a plugin rule cannot remove itself");
        if (a.Sequence != 100) notes.Add($"sequence {a.Sequence} - plugin aliases run in the order they are added");

        var sb = new StringBuilder();
        Header(sb, "alias", a.Name, a.Group, notes);
        sb.Append("scrye.addAlias{\n");
        Pattern(sb, a.Pattern, a.IsRegex, a.IgnoreCase);
        var body = new List<string>();
        string? send = Target(a.SendTo, a.Send, a.Variable, a.Script, body, wildcards: true);
        if (send is not null) sb.Append("  send = ").Append(Str(send)).Append(",\n");
        Run(sb, body);
        sb.Append("}\n");
        return sb.ToString();
    }

    public static string Timer(TimerDef t)
    {
        var notes = new List<string>();
        if (!t.Enabled) notes.Add("it is switched off in the profile - this starts it when the plugin loads");
        var sb = new StringBuilder();
        Header(sb, "timer", t.Name, t.Group, notes);
        var body = new List<string>();
        string? send = Target(t.SendTo, t.Send, t.Variable, t.Script, body, wildcards: false);
        if (send is not null)
        {
            int at = 0;   // the sends first, in their order, before anything else the body does
            foreach (string line in send.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) body.Insert(at++, $"scrye.send({Expr(line, wildcards: false)})");
        }
        string secs = t.IntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        sb.Append(t.OneShot ? "scrye.after(" : "scrye.every(").Append(secs).Append(", function()\n");
        foreach (string line in body) sb.Append("  ").Append(line).Append('\n');
        sb.Append("end)\n");
        return sb.ToString();
    }

    // ---- pieces ---------------------------------------------------------------------------

    private static void Header(StringBuilder sb, string kind, string name, string? group, List<string> notes)
    {
        sb.Append("-- ").Append(kind);
        if (!string.IsNullOrWhiteSpace(name)) sb.Append(" \"").Append(OneLine(name)).Append('"');
        if (!string.IsNullOrWhiteSpace(group)) sb.Append(" (group ").Append(OneLine(group!)).Append(')');
        sb.Append(", copied from Scrye's rule editor\n");
        foreach (string n in notes) sb.Append("-- note: ").Append(n).Append('\n');
    }

    private static void Pattern(StringBuilder sb, string pattern, bool regex, bool ignoreCase)
    {
        sb.Append("  pattern = ").Append(Str(pattern)).Append(",\n");
        if (regex) sb.Append("  regex = true,\n");
        if (!ignoreCase) sb.Append("  ignoreCase = false,\n");
    }

    /// <summary>The send to the MUD as the rule's <c>send =</c> (returned), or the other
    /// targets as lines of a run body.</summary>
    private static string? Target(SendTo to, string? send, string? variable, string? script,
                                  List<string> body, bool wildcards)
    {
        switch (to)
        {
            case SendTo.World:
                return string.IsNullOrEmpty(send) ? null : send;
            case SendTo.Output:
            case SendTo.Command:
                if (!string.IsNullOrEmpty(send)) body.Add($"scrye.print({Expr(send, wildcards)})");
                return null;
            case SendTo.Variable:
                if (!string.IsNullOrEmpty(variable))
                    body.Add($"scrye.setVariable({Str(variable)}, {Expr(send ?? "", wildcards)})");
                return null;
            case SendTo.Client:
                if (!string.IsNullOrEmpty(send))
                    body.Add($"-- ran through Scrye's own commands (aliases first): {OneLine(send)}");
                return null;
            case SendTo.Script:
            default:
                if (!string.IsNullOrEmpty(script))
                    body.Add($"-- called the script function '{OneLine(script)}': put its code here");
                return null;
        }
    }

    private static void Run(StringBuilder sb, List<string> body)
    {
        if (body.Count == 0) return;
        sb.Append("  run = function(...)\n");
        foreach (string line in body) sb.Append("    ").Append(line).Append('\n');
        sb.Append("  end,\n");
    }

    /// <summary>A send template as a Lua expression, for a run body: <c>%1</c>..<c>%9</c> are
    /// the wildcards it is handed, <c>${var}</c> a variable, <c>%%</c> a percent. <c>%0</c> and
    /// <c>%&lt;name&gt;</c> are not passed to a run function, so they stay as text with a
    /// marker a reader will see.</summary>
    public static string Expr(string template, bool wildcards)
    {
        var parts = new List<string>();
        var lit = new StringBuilder();
        void Flush() { if (lit.Length > 0) { parts.Add(Str(lit.ToString())); lit.Clear(); } }
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '%' && i + 1 < template.Length)
            {
                char n = template[i + 1];
                if (n == '%') { lit.Append('%'); i++; continue; }
                if (wildcards && n >= '1' && n <= '9')
                {
                    Flush();
                    parts.Add($"(select({n}, ...) or \"\")");
                    i++;
                    continue;
                }
            }
            else if (c == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                int end = template.IndexOf('}', i + 2);
                if (end > i + 2)
                {
                    Flush();
                    parts.Add($"(scrye.getVariable({Str(template.Substring(i + 2, end - i - 2))}) or \"\")");
                    i = end;
                    continue;
                }
            }
            lit.Append(c);
        }
        Flush();
        return parts.Count == 0 ? "\"\"" : string.Join(" .. ", parts);
    }

    /// <summary>A Lua string literal. A long bracket ([[...]], with as many '=' as it takes)
    /// keeps a regex readable - no doubled backslashes - and a quoted string is the fallback
    /// for text a long bracket cannot carry (a leading newline, which Lua drops, or a carriage
    /// return or other control character).</summary>
    public static string Str(string s)
    {
        bool longOk = !(s.Length > 0 && s[0] == '\n')
                      && !s.Any(ch => ch < ' ' && ch != '\n' && ch != '\t');
        if (longOk)
        {
            // the closer must not occur inside - nor be completed by a ']' the text ends with
            int level = 0;
            while ((s + "]").Contains("]" + new string('=', level) + "]", StringComparison.Ordinal)) level++;
            string eq = new('=', level);
            return "[" + eq + "[" + s + "]" + eq + "]";
        }
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append('\\').Append(((int)ch).ToString(CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private static string Display(string name, string pattern) =>
        string.IsNullOrWhiteSpace(name) ? OneLine(pattern) : OneLine(name);
}

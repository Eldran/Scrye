using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Scrye.Core.Automation;

/// <summary>One thing the import could not carry over, or carried over with a caveat.</summary>
/// <param name="Kind">"trigger", "alias", "timer", "macro", "variable".</param>
/// <param name="Name">What it was called, or its pattern when it had no name.</param>
/// <param name="Reason">Said in the terms of the person who wrote the rule, not the parser's.</param>
public sealed record ImportNote(string Kind, string Name, string Reason);

/// <summary>
/// Reads a MUSHclient world file (or an exported plugin) and turns its triggers, aliases,
/// timers, macros and variables into Scrye's own rule records.
///
/// <para>The two models line up almost field for field, which is why this is a parser and not
/// a translator: <c>match</c>/<c>regexp</c>/<c>sequence</c>/<c>group</c>/<c>keep_evaluating</c>
/// mean the same thing in both, MUSHclient's non-regex wildcards are the same <c>*</c>-captures
/// and <c>?</c>-singles that <see cref="CompiledPattern"/> already compiles, and <c>%1</c>..<c>%9</c>
/// in send text needs no rewriting at all.</para>
///
/// <para>What it will NOT do is guess. A rule whose action is a script function, a multi-line
/// trigger, a timer that fires at a time of day rather than on an interval — each is left out
/// and listed in <see cref="Skipped"/> with the reason, so the import is a thing you read
/// before you keep it rather than a pile of rules that quietly do less than they used to.</para>
/// </summary>
public sealed class MushclientImport
{
    public List<TriggerDef> Triggers { get; } = new();
    public List<AliasDef> Aliases { get; } = new();
    public List<TimerDef> Timers { get; } = new();
    public List<MacroDef> Macros { get; } = new();
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>Rules that did not come across at all.</summary>
    public List<ImportNote> Skipped { get; } = new();
    /// <summary>Rules that came across, but where something is worth looking at.</summary>
    public List<ImportNote> Warnings { get; } = new();

    public int Count => Triggers.Count + Aliases.Count + Timers.Count + Macros.Count + Variables.Count;

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _group;

    private MushclientImport(string group) => _group = group;

    /// <summary>
    /// Parse <paramref name="xml"/>. <paramref name="group"/> is the group given to any rule
    /// the file did not put in one — name it after the file, and every imported rule is one
    /// collapsed header in Settings, which is also how you delete the lot if you change your
    /// mind.
    /// </summary>
    /// <exception cref="System.Xml.XmlException">The file is not well-formed XML.</exception>
    public static MushclientImport Parse(string xml, string group = "imported")
    {
        var import = new MushclientImport(string.IsNullOrWhiteSpace(group) ? "imported" : group.Trim());
        XDocument doc = XDocument.Parse(xml, LoadOptions.None);

        foreach (XElement e in doc.Descendants("trigger")) import.ReadTrigger(e);
        foreach (XElement e in doc.Descendants("alias")) import.ReadAlias(e);
        foreach (XElement e in doc.Descendants("timer")) import.ReadTimer(e);
        foreach (XElement e in doc.Descendants("macro")) import.ReadMacro(e);
        foreach (XElement e in doc.Descendants("variable")) import.ReadVariable(e);
        return import;
    }

    // ---- attributes ---------------------------------------------------------

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    /// <summary>MUSHclient writes "y"/"n". An ABSENT attribute means the box was not ticked --
    /// it writes the ones that are on, so absence is a real answer and not a missing one.</summary>
    private static bool Flag(XElement e, string name) =>
        string.Equals(Attr(e, name), "y", StringComparison.OrdinalIgnoreCase);

    private static bool Flag(XElement e, string name, bool whenAbsent) =>
        e.Attribute(name) is null ? whenAbsent : Flag(e, name);

    private static int Int(XElement e, string name, int fallback)
    {
        string s = Attr(e, name);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    /// <summary>The action text: a <c>&lt;send&gt;</c> child in a world file, a <c>send="…"</c>
    /// attribute in some exports, and the element's own text as a last resort.</summary>
    private static string SendText(XElement e)
    {
        XElement? send = e.Element("send");
        if (send is not null) return send.Value;
        if (e.Attribute("send") is XAttribute a) return a.Value;
        string own = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));
        return own.Trim().Length == 0 ? "" : own;
    }

    /// <summary>A name Scrye can use as an identity. MUSHclient rules may be unnamed, and
    /// Scrye merges rules across profile layers BY NAME, so two blank names would be one
    /// rule. Falls back to the group plus a number, and never returns a duplicate.</summary>
    private string UniqueName(string wanted, string kind)
    {
        string baseName = wanted.Trim();
        if (baseName.Length == 0) baseName = $"{_group}-{kind}";
        string name = baseName;
        for (int n = 2; !_names.Add(name); n++) name = $"{baseName}-{n}";
        return name;
    }

    private string GroupOf(XElement e)
    {
        string g = Attr(e, "group").Trim();
        return g.Length == 0 ? _group : g;
    }

    private string Label(XElement e)
    {
        string n = Attr(e, "name").Trim();
        return n.Length > 0 ? n : Attr(e, "match");
    }

    // ---- send_to ------------------------------------------------------------

    /// <summary>MUSHclient's send_to numbers (SetTriggerOption docs). Anything without a Scrye
    /// equivalent returns null and the caller skips the rule saying so.</summary>
    private static (SendTo? To, string? Why) Destination(int sendTo) => sendTo switch
    {
        0 => (SendTo.World, null),
        1 => (SendTo.Command, null),
        2 => (SendTo.Output, null),
        8 => (SendTo.World, null),               // command queue: Scrye has one queue
        9 => (SendTo.Variable, null),
        12 or 13 or 14 => (SendTo.Script, null), // script engine (13 unqueued, 14 after omit)
        3 => (null, "sends to the status line, which Scrye does not have"),
        4 or 5 or 7 => (null, "sends to the notepad, which Scrye does not have"),
        6 => (null, "writes to the log file, which no Scrye rule can do"),
        10 => (null, "re-parses its output as a command; Scrye does not re-feed rule output"),
        11 => (null, "sends a speedwalk -- use a Scrye sequence (.walk) instead"),
        _ => (null, $"has an unknown send_to value ({sendTo})"),
    };

    // ---- colour -------------------------------------------------------------

    /// <summary>MUSHclient stores a colour as a Windows COLORREF: one decimal holding
    /// 0x00BBGGRR, so the bytes are the reverse of the "#RRGGBB" a person writes.
    ///
    /// <para>Reversed rather than assumed: getting it backwards turns a blue highlight orange,
    /// which is why the report prints both the number it read and the colour it made of it --
    /// one glance against MUSHclient's own swatch settles it.</para></summary>
    public static string? ColourRef(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
            return null;
        int r = v & 0xFF, g = (v >> 8) & 0xFF, b = (v >> 16) & 0xFF;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    // ---- triggers -----------------------------------------------------------

    private void ReadTrigger(XElement e)
    {
        string label = Label(e);

        if (Int(e, "lines_to_match", 1) > 1 || Flag(e, "multi_line"))
        {
            Skipped.Add(new ImportNote("trigger", label,
                "matches across several lines; Scrye matches one line at a time"));
            return;
        }

        int sendTo = Int(e, "send_to", 0);
        (SendTo? to, string? why) = Destination(sendTo);
        if (to is null)
        {
            Skipped.Add(new ImportNote("trigger", label, why!));
            return;
        }

        string script = Attr(e, "script").Trim();
        string send = SendText(e);
        bool gag = Flag(e, "omit_from_output") || sendTo == 14;

        if (to == SendTo.Script || script.Length > 0)
        {
            // The XML only ever names the function; its body lives in the plugin's <script>
            // block. Bringing the rule without it would give a trigger that fires and does
            // nothing, which is worse than not having it.
            Skipped.Add(new ImportNote("trigger", label,
                script.Length > 0
                    ? $"calls the script function '{script}', which has to be ported by hand"
                    : "sends to the script engine, which has to be ported by hand"));
            return;
        }

        // Worked out BEFORE the does-nothing check below, because recolouring a line IS the
        // whole point of a colour trigger: it has no send text, no gag and no sound, and an
        // earlier version of this dropped every one of them as doing nothing.
        (string? fore, string? back) = Highlight(e);

        if (send.Length == 0 && !gag && fore is null && back is null && Attr(e, "sound").Length == 0)
        {
            Skipped.Add(new ImportNote("trigger", label,
                "does nothing: no send text, no gag, no colour, no sound"));
            return;
        }

        if (Flag(e, "expand_variables"))
            Warnings.Add(new ImportNote("trigger", label,
                "used @variables; rewrite them as ${name} and check the variables came across"));
        if (e.Attribute("ignore_case") is null && Flag(e, "regexp"))
            Warnings.Add(new ImportNote("trigger", label, "had no ignore_case setting; imported case-SENSITIVE"));

        Triggers.Add(new TriggerDef
        {
            Name = UniqueName(Attr(e, "name"), "trigger"),
            Pattern = Attr(e, "match"),
            IsRegex = Flag(e, "regexp"),
            IgnoreCase = Flag(e, "ignore_case"),
            Enabled = Flag(e, "enabled", whenAbsent: true),
            KeepEvaluating = Flag(e, "keep_evaluating"),
            OneShot = Flag(e, "one_shot"),
            Temporary = Flag(e, "temporary"),
            Sequence = Int(e, "sequence", 100),
            Group = GroupOf(e),
            SendTo = to.Value,
            Send = send.Length > 0 ? send : null,
            Variable = sendTo == 9 ? NullIfEmpty(Attr(e, "variable")) : null,
            Gag = gag,
            Sound = NullIfEmpty(Attr(e, "sound")),
            HighlightFore = fore,
            HighlightBack = back,
            // A MUSHclient colour trigger recolours the text it MATCHED, not the whole line.
            HighlightWholeLine = false,
            Source = "mushclient",
        });
    }

    /// <summary>The "other" foreground/background of a colour trigger. The 16 named custom
    /// colours are a palette Scrye does not have, so those are reported instead of guessed at.</summary>
    private (string? Fore, string? Back) Highlight(XElement e)
    {
        string type = Attr(e, "colour_change_type");
        bool wantsText = type is "" or "0" or "1";   // both / text
        bool wantsBack = type is "" or "0" or "2";   // both / back

        string? fore = wantsText ? ColourRef(Attr(e, "other_text_colour")) : null;
        string? back = wantsBack ? ColourRef(Attr(e, "other_back_colour")) : null;

        string custom = Attr(e, "custom_colour");
        if (fore is null && back is null && custom.Length > 0 && custom != "0")
            Warnings.Add(new ImportNote("trigger", Label(e),
                $"recoloured using custom colour {custom}; pick a colour for it in Settings"));

        return (fore, back);
    }

    // ---- aliases ------------------------------------------------------------

    private void ReadAlias(XElement e)
    {
        string label = Label(e);
        int sendTo = Int(e, "send_to", 0);
        (SendTo? to, string? why) = Destination(sendTo);
        if (to is null)
        {
            Skipped.Add(new ImportNote("alias", label, why!));
            return;
        }

        string script = Attr(e, "script").Trim();
        if (to == SendTo.Script || script.Length > 0)
        {
            Skipped.Add(new ImportNote("alias", label,
                script.Length > 0
                    ? $"calls the script function '{script}', which has to be ported by hand"
                    : "sends to the script engine, which has to be ported by hand"));
            return;
        }

        string send = SendText(e);
        if (send.Length == 0)
        {
            Skipped.Add(new ImportNote("alias", label, "has nothing to send"));
            return;
        }

        if (Flag(e, "expand_variables"))
            Warnings.Add(new ImportNote("alias", label,
                "used @variables; rewrite them as ${name} and check the variables came across"));

        Aliases.Add(new AliasDef
        {
            Name = UniqueName(Attr(e, "name"), "alias"),
            Pattern = Attr(e, "match"),
            IsRegex = Flag(e, "regexp"),
            IgnoreCase = Flag(e, "ignore_case"),
            Enabled = Flag(e, "enabled", whenAbsent: true),
            KeepEvaluating = Flag(e, "keep_evaluating"),
            OneShot = Flag(e, "one_shot"),
            Temporary = Flag(e, "temporary"),
            Sequence = Int(e, "sequence", 100),
            Group = GroupOf(e),
            SendTo = to.Value,
            Send = send,
            Variable = sendTo == 9 ? NullIfEmpty(Attr(e, "variable")) : null,
            Source = "mushclient",
        });
    }

    // ---- timers -------------------------------------------------------------

    private void ReadTimer(XElement e)
    {
        string label = Label(e);
        if (Flag(e, "at_time"))
        {
            Skipped.Add(new ImportNote("timer", label,
                "fires at a time of day; Scrye timers only repeat on an interval"));
            return;
        }

        string script = Attr(e, "script").Trim();
        if (script.Length > 0)
        {
            Skipped.Add(new ImportNote("timer", label,
                $"calls the script function '{script}', which has to be ported by hand"));
            return;
        }

        double seconds = Int(e, "hour", 0) * 3600
                       + Int(e, "minute", 0) * 60
                       + Double(e, "second");
        if (seconds <= 0)
        {
            Skipped.Add(new ImportNote("timer", label, "has no interval"));
            return;
        }

        string send = SendText(e);
        if (send.Length == 0)
        {
            Skipped.Add(new ImportNote("timer", label, "has nothing to send"));
            return;
        }

        (SendTo? to, string? why) = Destination(Int(e, "send_to", 0));
        if (to is null) { Skipped.Add(new ImportNote("timer", label, why!)); return; }

        Timers.Add(new TimerDef
        {
            Name = UniqueName(Attr(e, "name"), "timer"),
            IntervalSeconds = seconds,
            Enabled = Flag(e, "enabled", whenAbsent: true),
            OneShot = Flag(e, "one_shot"),
            Group = GroupOf(e),
            SendTo = to.Value,
            Send = send,
            Source = "mushclient",
        });
    }

    private static double Double(XElement e, string name)
    {
        string s = Attr(e, name);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    // ---- macros -------------------------------------------------------------

    private void ReadMacro(XElement e)
    {
        // MUSHclient keeps macros as numbered keypad/function entries. Only the ones that
        // name a key AND have something to send can become a Scrye gesture binding.
        string key = Attr(e, "key").Trim();
        if (key.Length == 0) key = Attr(e, "name").Trim();
        string send = SendText(e);

        if (key.Length == 0 || send.Length == 0)
        {
            Skipped.Add(new ImportNote("macro", key.Length > 0 ? key : "(unnamed)",
                "has no key or nothing to send"));
            return;
        }

        Macros.Add(new MacroDef { Key = key, Send = send, Source = "mushclient" });
    }

    // ---- variables ----------------------------------------------------------

    private void ReadVariable(XElement e)
    {
        string name = Attr(e, "name").Trim();
        if (name.Length == 0) return;
        Variables[name] = e.Value;
    }

    private static string? NullIfEmpty(string s) => s.Trim().Length == 0 ? null : s;

    /// <summary>"#2133FF (from 16724769)" — the colour beside the number it was read from, so
    /// the BGR reading in <see cref="ColourRef"/> can be checked at a glance rather than
    /// taken on trust. The number is recomputed from the hex, which is exact: the conversion
    /// only reorders bytes.</summary>
    private static string Swatch(string? hex)
    {
        if (hex is null || hex.Length != 7) return "-";
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return $"{hex} (from {(b << 16) | (g << 8) | r})";
    }

    // ---- the report ---------------------------------------------------------

    /// <summary>What was found, what was left behind and why — the thing you read before
    /// deciding to keep any of it.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.Append($"triggers {Triggers.Count}, aliases {Aliases.Count}, timers {Timers.Count}");
        if (Macros.Count > 0) sb.Append($", macros {Macros.Count}");
        if (Variables.Count > 0) sb.Append($", variables {Variables.Count}");
        sb.AppendLine();

        int coloured = Triggers.Count(t => t.HighlightFore is not null || t.HighlightBack is not null);
        if (coloured > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"-- colours ({coloured}) — check one against MUSHclient before keeping these --");
            foreach (TriggerDef t in Triggers.Where(t => t.HighlightFore is not null || t.HighlightBack is not null).Take(10))
                sb.AppendLine($"  {t.Name}: text {Swatch(t.HighlightFore)}  back {Swatch(t.HighlightBack)}");
        }

        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"-- imported, but look at these ({Warnings.Count}) --");
            foreach (ImportNote n in Warnings) sb.AppendLine($"  {n.Kind} {n.Name}: {n.Reason}");
        }

        if (Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"-- not imported ({Skipped.Count}) --");
            foreach (ImportNote n in Skipped) sb.AppendLine($"  {n.Kind} {n.Name}: {n.Reason}");
        }

        return sb.ToString().TrimEnd();
    }
}

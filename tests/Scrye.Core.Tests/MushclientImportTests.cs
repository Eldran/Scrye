using System.Linq;
using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Bringing hand-written MUSHclient triggers and aliases across. The two models line up
/// almost field for field, so most of what is pinned here is the other half of the job:
/// what must NOT come across silently, and what must be flagged rather than guessed at.
/// </summary>
public class MushclientImportTests
{
    private const string Sample = """
<?xml version="1.0" encoding="iso-8859-1"?>
<!DOCTYPE muclient>
<muclient>
<world site="3k.org" port="3000" name="3Scapes" />
<triggers>
  <trigger enabled="y" group="combat" match="^You are hungry\.$" regexp="y" send_to="0" sequence="100" ignore_case="y">
   <send>eat bread</send>
  </trigger>
  <trigger enabled="y" match="* tells you *" send_to="0" sequence="50" ignore_case="y" name="autoreply">
   <send>tell %1 busy right now</send>
  </trigger>
  <trigger enabled="y" match="^\[chat\]" regexp="y" omit_from_output="y" send_to="0" sequence="100" ignore_case="y" name="gagchat" />
  <trigger enabled="y" match="^Someone shouts" regexp="y" other_text_colour="16724769" other_back_colour="0"
           colour_change_type="1" send_to="0" sequence="100" ignore_case="y" name="shoutcolour" />
  <trigger enabled="y" match="^You lose (\d+) hp" regexp="y" send_to="9" variable="lasthit" sequence="100" ignore_case="y" name="trackhit">
   <send>%1</send>
  </trigger>
  <trigger enabled="y" match="^The bell tolls" regexp="y" script="OnBell" sequence="100" name="bell" />
  <trigger enabled="y" match="^A long tale" regexp="y" lines_to_match="3" multi_line="y" send_to="0" name="tale">
   <send>listen</send>
  </trigger>
  <trigger enabled="y" match="^Note this" regexp="y" send_to="4" name="tonotepad">
   <send>whatever</send>
  </trigger>
  <trigger enabled="y" match="^Enemy spotted" regexp="y" send_to="0" sound="alert.wav" keep_evaluating="y" one_shot="y" name="spotted">
   <send>flee</send>
  </trigger>
  <trigger enabled="y" match="^Hp: @hp" send_to="0" expand_variables="y" name="expandy">
   <send>score</send>
  </trigger>
  <trigger enabled="y" match="^Case matters here" regexp="y" send_to="0" name="nocase">
   <send>ok</send>
  </trigger>
</triggers>
<aliases>
  <alias match="kk *" enabled="y" send_to="0" sequence="100" ignore_case="y">
   <send>kill %1</send>
  </alias>
  <alias name="gd" match="^gd$" regexp="y" enabled="y" send_to="0" sequence="100" ignore_case="y" group="looting">
   <send>get all from corpse</send>
  </alias>
  <alias name="scriptone" match="^tank (.*?)$" regexp="y" script="Tank" enabled="y" sequence="100" />
  <alias name="sw" match="^sw$" regexp="y" enabled="y" send_to="11" sequence="100">
   <send>4n2e</send>
  </alias>
  <alias match="rest" enabled="y" send_to="0" sequence="100" ignore_case="y">
   <send>sit
rest</send>
  </alias>
</aliases>
<timers enabled="y">
  <timer second="30" enabled="y" send_to="0" name="keepalive">
   <send>look</send>
  </timer>
  <timer minute="5" second="0" enabled="y" send_to="0" name="save">
   <send>save</send>
  </timer>
  <timer hour="8" minute="30" at_time="y" enabled="y" send_to="0" name="morning">
   <send>wake</send>
  </timer>
</timers>
<macros>
  <macro key="F1" >
   <send>north</send>
  </macro>
</macros>
<variables>
  <variable name="hp">100</variable>
  <variable name="target">orc</variable>
</variables>
</muclient>
""";

    private static readonly MushclientImport Imp = MushclientImport.Parse(Sample, "mush");

    private static TriggerDef? T(string n) => Imp.Triggers.FirstOrDefault(t => t.Name == n);
    private static AliasDef? A(string n) => Imp.Aliases.FirstOrDefault(a => a.Name == n);
    private static TimerDef? M(string n) => Imp.Timers.FirstOrDefault(t => t.Name == n);
    private static bool SkippedFor(string fragment) =>
        Imp.Skipped.Any(s => s.Reason.Contains(fragment, System.StringComparison.OrdinalIgnoreCase));

    // ---- what comes across --------------------------------------------------

    [Fact]
    public void A_regex_trigger_keeps_its_send_flag_and_group()
    {
        TriggerDef t = Imp.Triggers.First(x => x.Pattern.Contains("hungry"));
        Assert.Equal("eat bread", t.Send);
        Assert.True(t.IsRegex);
        Assert.Equal("combat", t.Group);
    }

    [Fact]
    public void A_wildcard_trigger_stays_a_wildcard_and_keeps_its_captures()
    {
        // MUSHclient's non-regex "*" captures are the same ones CompiledPattern compiles,
        // and %1 needs no rewriting -- so this is deliberately NOT converted to a regex.
        Assert.False(T("autoreply")!.IsRegex);
        Assert.Equal("tell %1 busy right now", T("autoreply")!.Send);
        Assert.Equal(50, T("autoreply")!.Sequence);
    }

    [Fact]
    public void Omit_from_output_becomes_a_gag()
    {
        Assert.True(T("gagchat")!.Gag);
        Assert.Null(T("gagchat")!.Send);
    }

    [Fact]
    public void Send_to_variable_keeps_its_variable_name()
    {
        Assert.Equal(SendTo.Variable, T("trackhit")!.SendTo);
        Assert.Equal("lasthit", T("trackhit")!.Variable);
    }

    [Fact]
    public void Sound_and_the_evaluation_flags_carry_over()
    {
        Assert.Equal("alert.wav", T("spotted")!.Sound);
        Assert.True(T("spotted")!.KeepEvaluating);
        Assert.True(T("spotted")!.OneShot);
    }

    [Fact]
    public void An_alias_keeps_its_own_group_and_an_ungrouped_one_lands_in_the_import_group()
    {
        Assert.Equal("looting", A("gd")!.Group);
        Assert.Equal("mush", Imp.Aliases.First(a => a.Pattern == "kk *").Group);
    }

    [Fact]
    public void A_multi_line_send_body_survives() =>
        Assert.Contains("\n", Imp.Aliases.First(a => a.Pattern == "rest").Send!);

    [Fact]
    public void Interval_timers_come_across_in_seconds()
    {
        Assert.Equal(30, M("keepalive")!.IntervalSeconds);
        Assert.Equal(300, M("save")!.IntervalSeconds);   // minute="5"
    }

    [Fact]
    public void Macros_and_variables_come_across()
    {
        Assert.Equal("F1", Imp.Macros.Single().Key);
        Assert.Equal("north", Imp.Macros.Single().Send);
        Assert.Equal("100", Imp.Variables["hp"]);
        Assert.Equal("orc", Imp.Variables["target"]);
    }

    // ---- colours ------------------------------------------------------------

    [Fact]
    public void A_colour_only_trigger_is_kept()
    {
        // It has no send text, no gag and no sound: recolouring the line IS what it does.
        // An earlier version dropped every one of these as "does nothing".
        Assert.NotNull(T("shoutcolour"));
        Assert.Equal("#2133FF", T("shoutcolour")!.HighlightFore);
    }

    [Theory]
    [InlineData("255", "#FF0000")]        // COLORREF 0x0000FF -> pure RED
    [InlineData("16711680", "#0000FF")]   // COLORREF 0xFF0000 -> pure BLUE
    [InlineData("16724769", "#2133FF")]
    public void A_colour_is_read_as_a_Windows_COLORREF(string raw, string hex) =>
        // 0x00BBGGRR, so the bytes are the reverse of "#RRGGBB". Reading them the other way
        // silently turns every blue highlight orange, which is why it is pinned here.
        Assert.Equal(hex, MushclientImport.ColourRef(raw));

    [Fact]
    public void Colour_change_type_text_only_leaves_the_background_alone() =>
        Assert.Null(T("shoutcolour")!.HighlightBack);

    [Fact]
    public void A_colour_trigger_recolours_the_match_not_the_whole_line() =>
        Assert.False(T("shoutcolour")!.HighlightWholeLine);

    // ---- what must not come across silently ---------------------------------

    [Fact]
    public void A_script_rule_is_skipped_and_names_the_function()
    {
        // The XML only ever names the function; the body lives in the plugin's <script>
        // block. A trigger that fires and does nothing is worse than not having it.
        Assert.Null(T("bell"));
        Assert.Null(A("scriptone"));
        Assert.True(SkippedFor("script function 'OnBell'"));
        Assert.True(SkippedFor("script function 'Tank'"));
    }

    [Fact]
    public void A_multi_line_trigger_is_skipped()
    {
        Assert.Null(T("tale"));
        Assert.True(SkippedFor("one line at a time"));
    }

    [Fact]
    public void Destinations_Scrye_does_not_have_are_skipped()
    {
        Assert.Null(T("tonotepad"));
        Assert.True(SkippedFor("notepad"));
        Assert.Null(A("sw"));
        Assert.True(SkippedFor("speedwalk"));       // points at sequences instead
        Assert.Null(M("morning"));
        Assert.True(SkippedFor("time of day"));
    }

    // ---- flagged rather than guessed at -------------------------------------

    [Fact]
    public void Expand_variables_is_imported_but_flagged() =>
        Assert.Contains(Imp.Warnings, w => w.Name == "expandy" && w.Reason.Contains("@variables"));

    [Fact]
    public void A_regex_trigger_with_no_ignore_case_is_imported_case_sensitive_and_said_so()
    {
        // Absence is a real answer in this format -- MUSHclient writes the flags that are on.
        // Silently loosening it would make patterns over-match; silently keeping it could stop
        // them matching at all. So: keep it, and say so.
        Assert.False(T("nocase")!.IgnoreCase);
        Assert.Contains(Imp.Warnings, w => w.Name == "nocase" && w.Reason.Contains("case-SENSITIVE"));
    }

    // ---- identity -----------------------------------------------------------

    [Fact]
    public void Every_imported_rule_has_a_unique_name()
    {
        // Scrye merges rules across profile layers BY NAME, so two blank names would collapse
        // into one rule -- and MUSHclient rules are often unnamed.
        Assert.All(Imp.Aliases, a => Assert.NotEqual("", a.Name));
        Assert.Equal(Imp.Aliases.Count, Imp.Aliases.Select(a => a.Name).Distinct().Count());
        Assert.Equal(Imp.Triggers.Count, Imp.Triggers.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void Two_rules_sharing_a_name_are_kept_apart()
    {
        MushclientImport dup = MushclientImport.Parse(
            "<muclient><aliases>" +
            "<alias name='same' match='a' enabled='y' send_to='0'><send>1</send></alias>" +
            "<alias name='same' match='b' enabled='y' send_to='0'><send>2</send></alias>" +
            "</aliases></muclient>", "dup");
        Assert.Equal(2, dup.Aliases.Count);
        Assert.NotEqual(dup.Aliases[0].Name, dup.Aliases[1].Name);
    }

    [Fact]
    public void An_empty_world_file_imports_nothing_quietly()
    {
        MushclientImport empty = MushclientImport.Parse("<muclient></muclient>", "x");
        Assert.Equal(0, empty.Count);
        Assert.Empty(empty.Skipped);
    }
}

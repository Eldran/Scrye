using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// <c>${name}</c> and <c>${!name}</c> inside trigger and alias PATTERNS (6 Nov 2026): the
/// pattern follows the variable, so "attack whatever ${targ} is" is one trigger instead of
/// one per target.
/// </summary>
public class PatternVariableTests
{
    private sealed class Recorder : IWorldActions
    {
        private readonly VariableStore _vars;
        public Recorder(VariableStore vars) => _vars = vars;
        public List<string> Sends { get; } = new();
        public List<string> Echoes { get; } = new();
        public void Send(string text) => Sends.Add(text);
        public void Echo(string text) => Echoes.Add(text);
        public string? GetVariable(string name) => _vars.Get(name);
        public void SetVariable(string name, string value) => _vars.Set(name, value);
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    private static (AutomationEngine engine, Recorder rec, VariableStore vars) NewEngine()
    {
        var vars = new VariableStore();
        return (new AutomationEngine(vars), new Recorder(vars), vars);
    }

    [Fact]
    public void A_trigger_pattern_follows_its_variable()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "hunt", Pattern = "${targ} arrives.", Send = "kill ${targ}" });

        vars.Set("targ", "Bob");
        engine.ProcessLine("Jim arrives.", rec);
        engine.ProcessLine("Bob arrives.", rec);
        Assert.Equal(new[] { "kill Bob" }, rec.Sends);

        vars.Set("targ", "Jim");
        engine.ProcessLine("Bob arrives.", rec);
        engine.ProcessLine("Jim arrives.", rec);
        Assert.Equal(new[] { "kill Bob", "kill Jim" }, rec.Sends);
    }

    [Fact]
    public void An_unset_or_empty_variable_matches_nothing_not_everything()
    {
        // The unanchored regex is the case that matters: with ${targ} read as "", the pattern
        // " arrives" would fire on everyone who arrives.
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "hunt", Pattern = "${targ} arrives", IsRegex = true, Send = "kill" });
        engine.ProcessLine("Bob arrives", rec);
        vars.Set("targ", "");
        engine.ProcessLine("Bob arrives", rec);
        Assert.Empty(rec.Sends);

        vars.Set("targ", "Bob");
        engine.ProcessLine("Bob arrives", rec);
        Assert.Single(rec.Sends);

        vars.Delete("targ");
        engine.ProcessLine("Bob arrives", rec);
        Assert.Single(rec.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_value_is_matched_literally(bool regex)
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = regex ? "^${targ} arrives$" : "${targ} arrives",
                                           IsRegex = regex, Send = "hit" });
        vars.Set("targ", "a.b*(c)?");
        engine.ProcessLine("axbbbb(c) arrives", rec);
        engine.ProcessLine("ab arrives", rec);
        Assert.Empty(rec.Sends);
        engine.ProcessLine("a.b*(c)? arrives", rec);
        Assert.Single(rec.Sends);
    }

    [Fact]
    public void A_variable_does_not_shift_the_wildcard_numbers()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "${targ} hits * for *.", Send = "say %1/%2" });
        vars.Set("targ", "The (big) troll");
        engine.ProcessLine("The (big) troll hits you for 12.", rec);
        Assert.Equal(new[] { "say you/12" }, rec.Sends);
    }

    [Theory]
    [InlineData(false, "${!friends} pokes you.")]
    [InlineData(true, @"^${!friends} pokes you\.$")]
    public void A_bang_variable_is_a_list_of_alternatives(bool regex, string pattern)
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "heal", Pattern = pattern, IsRegex = regex, Send = "heal %0" });
        vars.Set("friends", "Eldran|Bjorn");
        engine.ProcessLine("Eldran pokes you.", rec);
        engine.ProcessLine("Mallory pokes you.", rec);
        engine.ProcessLine("Bjorn pokes you.", rec);
        // the alternation is grouped: "Eldran|Bjorn pokes you." is not "Eldran" OR "Bjorn pokes you."
        engine.ProcessLine("Eldran is here", rec);
        Assert.Equal(new[] { "heal Eldran pokes you.", "heal Bjorn pokes you." }, rec.Sends);
    }

    [Fact]
    public void A_list_can_be_captured_as_a_wildcard()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "heal", Pattern = @"^(${!friends}) pokes you\.$", IsRegex = true, Send = "heal %1" });
        vars.Set("friends", "Eldran|Bjorn");
        engine.ProcessLine("Bjorn pokes you.", rec);
        Assert.Equal(new[] { "heal Bjorn" }, rec.Sends);
    }

    [Fact]
    public void A_bad_regex_value_is_reported_once_and_the_rule_recovers()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "heal", Pattern = "${!friends} pokes you.", Send = "heal" });
        vars.Set("friends", "Eldran|(");
        engine.Simulate("Eldran pokes you.");          // the dry run must not use up the report
        engine.ProcessLine("Eldran pokes you.", rec);
        engine.ProcessLine("Eldran pokes you.", rec);
        Assert.Empty(rec.Sends);
        Assert.Single(rec.Echoes);
        Assert.Contains("'heal'", rec.Echoes[0]);

        vars.Set("friends", "Eldran");
        engine.ProcessLine("Eldran pokes you.", rec);
        Assert.Single(rec.Sends);
        Assert.Single(rec.Echoes);
    }

    [Fact]
    public void A_broken_regex_is_still_refused_when_the_rule_is_added()
    {
        var (engine, _, _) = NewEngine();
        Assert.ThrowsAny<ArgumentException>(() =>
            engine.AddTrigger(new TriggerDef { Name = "bad", Pattern = "(${targ}", IsRegex = true, Send = "x" }));
    }

    [Fact]
    public void A_variable_set_by_one_trigger_is_used_by_another()
    {
        var (engine, rec, _) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "pick", Pattern = "You target *.", SendTo = SendTo.Variable, Variable = "targ", Send = "%1" });
        engine.AddTrigger(new TriggerDef { Name = "hunt", Pattern = "${targ} arrives.", Send = "kill %0" });
        engine.ProcessLine("Bob arrives.", rec);
        engine.ProcessLine("You target Bob.", rec);
        engine.ProcessLine("Bob arrives.", rec);
        Assert.Equal(new[] { "kill Bob arrives." }, rec.Sends);
    }

    [Fact]
    public void An_alias_pattern_follows_its_variable()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddAlias(new AliasDef { Name = "r", Pattern = "rr ${tank}", Send = "rescue ${tank}" });
        vars.Set("tank", "Bjorn");
        Assert.False(engine.ProcessInput("rr Eldran", rec));
        Assert.True(engine.ProcessInput("rr Bjorn", rec));
        Assert.Equal(new[] { "rescue Bjorn" }, rec.Sends);
    }

    [Fact]
    public void Ignore_case_covers_the_value_too()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "${targ} arrives.", IgnoreCase = true, Send = "x" });
        vars.Set("targ", "bob");
        engine.ProcessLine("BOB arrives.", rec);
        Assert.Single(rec.Sends);
    }

    [Fact]
    public void A_multi_line_trigger_can_use_a_variable()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "${targ} arrives.\n${targ} attacks you!", Lines = 2, Send = "flee" });
        vars.Set("targ", "Bob");
        engine.ProcessLine("Bob arrives.", rec);
        engine.ProcessLine("Bob attacks you!", rec);
        Assert.Equal(new[] { "flee" }, rec.Sends);
    }

    [Theory]
    [InlineData(@"\${targ}", true, "${targ} here")]   // escaped in a regex: the text itself
    [InlineData("^cost: $5", true, "cost: $5")]        // not a reference (well, not a sensible regex either)
    [InlineData("${2x} costs", false, "${2x} costs")] // not a name: literal in a wildcard
    public void Things_that_are_not_references_are_left_alone(string pattern, bool regex, string line)
    {
        Assert.Empty(PatternVariables.Names(pattern, regex));
        var p = new RulePattern(pattern, regex, ignoreCase: false);
        bool matched = p.Current(new VariableStore()).Match(line) is not null;
        Assert.Equal(new CompiledPattern(pattern, regex, false).Match(line) is not null, matched);
    }

    [Fact]
    public void An_escaped_reference_stays_text_beside_a_real_one()
    {
        var (engine, rec, vars) = NewEngine();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = @"^\${targ} is ${targ}$", IsRegex = true, Send = "x" });
        vars.Set("targ", "Bob");
        engine.ProcessLine("Bob is Bob", rec);
        Assert.Empty(rec.Sends);
        engine.ProcessLine("${targ} is Bob", rec);
        Assert.Single(rec.Sends);
    }

    [Fact]
    public void Names_are_listed_once_and_may_carry_dots()
    {
        Assert.Equal(new[] { "targ", "mxp.hp", "friends" },
                     PatternVariables.Names("${targ} ${mxp.hp} ${!friends} ${targ}", isRegex: false));
    }

    [Fact]
    public void Setting_the_same_value_again_does_not_move_the_store_version()
    {
        var vars = new VariableStore();
        vars.Set("hp", "100");
        long v = vars.Version;
        vars.Set("hp", "100");
        Assert.Equal(v, vars.Version);
        vars.Set("hp", "99");
        Assert.NotEqual(v, vars.Version);
        long w = vars.Version;
        Assert.False(vars.Delete("nope"));
        Assert.Equal(w, vars.Version);
    }

    [Fact]
    public void Mushclient_at_variables_import_as_scrye_variables()
    {
        const string xml = """
            <muclient>
            <triggers>
              <trigger enabled="y" match="^@!friends pokes you, @who$" regexp="y" send_to="0" expand_variables="y" name="poke">
               <send>heal @who; mail me@@home</send>
              </trigger>
              <trigger enabled="y" match="@who leaves" send_to="0" name="plain">
               <send>say @who</send>
              </trigger>
            </triggers>
            <aliases>
              <alias match="rr @tank" enabled="y" send_to="0" expand_variables="y" name="rr">
               <send>rescue @tank</send>
              </alias>
            </aliases>
            </muclient>
            """;
        var imp = MushclientImport.Parse(xml, "mush");
        TriggerDef poke = imp.Triggers.Single(t => t.Name == "poke");
        Assert.Equal("^${!friends} pokes you, ${who}$", poke.Pattern);
        Assert.Equal("heal ${who}; mail me@home", poke.Send);
        TriggerDef plain = imp.Triggers.Single(t => t.Name == "plain");   // no expand_variables: untouched
        Assert.Equal("@who leaves", plain.Pattern);
        Assert.Equal("say @who", plain.Send);
        AliasDef rr = imp.Aliases.Single(a => a.Name == "rr");
        Assert.Equal("rr ${tank}", rr.Pattern);
        Assert.Equal("rescue ${tank}", rr.Send);
    }

    [Fact]
    public void Copy_as_lua_says_the_pattern_variables_do_not_come_along()
    {
        string lua = RuleLua.Trigger(new TriggerDef { Name = "hunt", Pattern = "${targ} arrives.", Send = "kill ${targ}" });
        Assert.Contains("uses ${targ}", lua);
        string plain = RuleLua.Trigger(new TriggerDef { Name = "hunt", Pattern = "* arrives.", Send = "kill %1" });
        Assert.DoesNotContain("does not follow", plain);
    }
}

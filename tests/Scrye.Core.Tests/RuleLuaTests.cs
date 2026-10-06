using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// "Copy as Lua" (6 Oct 2026): a rule from the editor written out as plugin code, with the
/// quoting handled - what maps across does, and what cannot is said in a comment.
/// </summary>
public class RuleLuaTests
{
    [Fact]
    public void A_regex_trigger_sending_to_the_MUD_keeps_its_pattern_readable()
    {
        string lua = RuleLua.Trigger(new TriggerDef
        {
            Name = "hunger", Pattern = @"^You are (\w+) hungry\.$", IsRegex = true, Send = "eat %1",
        });
        Assert.Contains("-- trigger \"hunger\"", lua);
        Assert.Contains("scrye.addTrigger{", lua);
        Assert.Contains(@"pattern = [[^You are (\w+) hungry\.$]],", lua);   // no doubled backslashes
        Assert.Contains("regex = true,", lua);
        Assert.Contains("send = [[eat %1]],", lua);                          // the host expands it
        Assert.DoesNotContain("run =", lua);
        Assert.DoesNotContain("ignoreCase", lua);                            // the default
    }

    [Fact]
    public void Other_targets_become_a_run_function_with_the_wildcards()
    {
        string lua = RuleLua.Trigger(new TriggerDef
        {
            Pattern = "* tells you: *", SendTo = SendTo.Output, Send = "TELL from %1: %2 (${mood})",
            IgnoreCase = false, Sound = "beep", CapturePane = "Chats",
        });
        Assert.Contains("ignoreCase = false,", lua);
        Assert.Contains("run = function(...)", lua);
        Assert.Contains("scrye.print([[TELL from ]] .. (select(1, ...) or \"\") .. [[: ]] .. (select(2, ...) or \"\")"
                        + " .. [[ (]] .. (scrye.getVariable([[mood]]) or \"\") .. [[)]])", lua);
        Assert.Contains("scrye.sound([[beep]])", lua);
        Assert.Contains("scrye.capture([[Chats]]", lua);
    }

    [Fact]
    public void A_variable_target_sets_the_variable()
    {
        string lua = RuleLua.Alias(new AliasDef { Pattern = "tgt *", SendTo = SendTo.Variable, Variable = "target", Send = "%1" });
        Assert.Contains("scrye.addAlias{", lua);
        Assert.Contains("scrye.setVariable([[target]], (select(1, ...) or \"\"))", lua);
    }

    [Fact]
    public void What_a_plugin_rule_cannot_do_is_said_not_dropped()
    {
        string lua = RuleLua.Trigger(new TriggerDef
        {
            Pattern = "x", Enabled = false, Gag = true, HighlightFore = "#FF0000", OneShot = true,
            RepeatOnLine = true, KeepEvaluating = true, Sequence = 50, Group = "combat",
        });
        Assert.Contains("(group combat)", lua);
        foreach (string word in new[] { "switched off", "gags", "highlights", "keep evaluating", "one-shot",
                                        "repeat on line", "sequence 50" })
            Assert.Contains(word, lua);
    }

    [Fact]
    public void A_multi_line_trigger_keeps_its_lines()
    {
        string lua = RuleLua.Trigger(new TriggerDef { Pattern = "a\nb", Lines = 3, Send = "x" });
        Assert.Contains("lines = 3,", lua);
    }

    [Fact]
    public void A_timer_becomes_every_or_after_sending_each_line()
    {
        string every = RuleLua.Timer(new TimerDef { Name = "keepalive", IntervalSeconds = 300, Send = "l\nscore ${who}" });
        Assert.Contains("scrye.every(300, function()", every);
        Assert.Contains("scrye.send([[l]])", every);
        Assert.Contains("scrye.send([[score ]] .. (scrye.getVariable([[who]]) or \"\"))", every);
        Assert.True(every.IndexOf("[[l]]", StringComparison.Ordinal) < every.IndexOf("[[score ]]", StringComparison.Ordinal),
                    "the lines go out in order");
        string once = RuleLua.Timer(new TimerDef { IntervalSeconds = 2.5, OneShot = true, Send = "x" });
        Assert.Contains("scrye.after(2.5, function()", once);
    }

    [Theory]
    [InlineData("plain", "[[plain]]")]
    [InlineData("has ]] inside", "[=[has ]] inside]=]")]
    [InlineData("ends with ]", "[=[ends with ]]=]")]
    [InlineData("\nleading newline", "\"\\nleading newline\"")]
    [InlineData("cr\rhere \"q\" \\", "\"cr\\rhere \\\"q\\\" \\\\\"")]
    public void Strings_are_quoted_so_Lua_reads_them_back_exactly(string text, string lua)
    {
        Assert.Equal(lua, RuleLua.Str(text));
    }

    [Fact]
    public void A_percent_sign_and_unknown_markers_stay_text()
    {
        Assert.Equal("[[100% sure %0]]", RuleLua.Expr("100%% sure %0", wildcards: true));
        Assert.Equal("[[say %1]]", RuleLua.Expr("say %1", wildcards: false));
    }
}

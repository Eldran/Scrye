using System.Collections.Generic;
using System.Reflection;
using System.Threading.Channels;
using Scrye.Core.Automation;
using Scrye.Core.Model;
using Scrye.Core.Session;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// One typed line, several commands:
/// <c>vtrade refine smithy transfer all;vtrade refine smelter transfer all;vtrade refine all fill</c>.
///
/// <para>Pinned here: the split rule itself, and the thing that actually matters — that
/// each part takes its own trip through the alias pipeline, and that a send which did NOT
/// come from a person is never split. Drives <c>HandleInput</c> and reads the mailbox
/// directly, the way <c>MipReloginTests</c> does, so no socket is needed.</para>
/// </summary>
public class CommandSeparatorTests
{
    // ---- the rule ----------------------------------------------------------

    [Theory]
    [InlineData("north")]
    [InlineData("")]
    [InlineData("say nothing to see here")]
    public void No_separator_means_no_change_at_all(string text) =>
        Assert.Null(CommandSeparator.Split(text));

    [Fact]
    public void Splits_on_semicolon() =>
        Assert.Equal(
            new[]
            {
                "vtrade refine smithy transfer all",
                "vtrade refine smelter transfer all",
                "vtrade refine all fill",
            },
            CommandSeparator.Split(
                "vtrade refine smithy transfer all;vtrade refine smelter transfer all;vtrade refine all fill")!);

    [Fact]
    public void Parts_are_trimmed() =>
        Assert.Equal(new[] { "n", "s", "e" }, CommandSeparator.Split("n; s ;  e")!);

    [Fact]
    public void A_trailing_separator_is_not_a_blank_command() =>
        Assert.Equal(new[] { "n" }, CommandSeparator.Split("n;")!);

    [Fact]
    public void Doubled_separator_is_a_literal_semicolon() =>
        Assert.Equal(new[] { "say I went there; it was fun" },
            CommandSeparator.Split("say I went there;; it was fun")!);

    [Fact]
    public void A_literal_and_a_separator_can_share_a_line() =>
        Assert.Equal(new[] { "say a;b", "north" }, CommandSeparator.Split("say a;;b;north")!);

    [Fact]
    public void Nothing_but_separators_asks_for_nothing() =>
        Assert.Empty(CommandSeparator.Split(";")!);

    // ---- the session -------------------------------------------------------

    private static MudSession NewSession() =>
        new(new WorldProfile { Name = "t", Host = "localhost", Port = 1 });

    /// <summary>What <c>Submit</c> would reach once the loop picked the message up. The loop
    /// only runs after a connect, so call the handler and read the outbox by hand.</summary>
    private static List<string> Typed(MudSession s, string text, bool split = true)
    {
        typeof(MudSession).GetMethod("HandleInput", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(s, new object[] { text, split });
        return Outbox(s);
    }

    private static List<string> Outbox(MudSession s)
    {
        var mailbox = (Channel<SessionMessage>)typeof(MudSession)
            .GetField("_mailbox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
        List<string> sent = new();
        while (mailbox.Reader.TryRead(out SessionMessage? msg))
            if (msg is SessionMessage.SendText t) sent.Add(t.Text);
        return sent;
    }

    [Fact]
    public void A_typed_line_becomes_several_commands() =>
        Assert.Equal(new[] { "one", "two", "three" }, Typed(NewSession(), "one;two;three"));

    [Fact]
    public void A_line_without_a_separator_is_one_command_verbatim() =>
        Assert.Equal(new[] { "say a b c" }, Typed(NewSession(), "say a b c"));

    [Fact]
    public void Each_part_goes_through_the_aliases_on_its_own()
    {
        MudSession s = NewSession();
        s.Automation.AddAlias(new AliasDef { Name = "gg", Pattern = "gg", Send = "get all" });
        Assert.Equal(new[] { "n", "get all", "s" }, Typed(s, "n;gg;s"));
    }

    [Fact]
    public void An_alias_matches_a_part_it_would_not_match_inside_the_whole_line()
    {
        MudSession s = NewSession();
        s.Automation.AddAlias(new AliasDef { Name = "gg", Pattern = "gg", Send = "get all" });
        // Without the split this whole string is one non-matching command; with it, the
        // middle part is exactly "gg" and fires.
        Assert.Contains("get all", Typed(s, "look;gg;wield axe"));
    }

    [Fact]
    public void A_plugin_send_is_never_split()
    {
        // IWorldActions.Send is what triggers, timers and plugins use. A plugin saying
        // something with a semicolon in it must not quietly become two commands.
        MudSession s = NewSession();
        ((IWorldActions)s).Send("say a;b");
        Assert.Equal(new[] { "say a;b" }, Outbox(s));
    }

    [Fact]
    public void The_escape_survives_the_whole_pipeline() =>
        // ";;" is unescaped once, on the way in — what reaches the MUD holds one ";".
        Assert.Equal(new[] { "say hi; bye" }, Typed(NewSession(), "say hi;; bye"));

    // ---- what the MUD authored ---------------------------------------------

    [Fact]
    public void An_MXP_command_link_is_never_split() =>
        // MudSession.SubmitLiteral, which WorldViewModel.HandleCommandLink uses. "look;quit"
        // in a <SEND> is the MUD's text, not a person asking for two commands.
        Assert.Equal(new[] { "look;quit" }, Typed(NewSession(), "look;quit", split: false));

    [Fact]
    public void An_MXP_command_link_cannot_reach_a_local_alias_through_a_separator()
    {
        // The escalation this closes: every extra command a link fans out into is another
        // chance to match an alias, and an alias can be SendTo.Script.
        MudSession s = NewSession();
        s.Automation.AddAlias(new AliasDef { Name = "gg", Pattern = "gg", Send = "get all" });
        Assert.DoesNotContain("get all", Typed(s, "look;gg", split: false));
    }
}

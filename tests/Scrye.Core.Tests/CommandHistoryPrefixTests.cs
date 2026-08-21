using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Recall filtered by what you have already typed: `vtrade `, then Ctrl+Up or Alt+Up, cycles
/// only the vtrade commands. Plain Up/Down is the whole history and stays that way — it was
/// briefly the filtered walk, and in use that turned out to answer a narrower question than
/// the key is asking.
///
/// <para>Everything here drives the history through <see cref="Box"/>, which does what the
/// command line does: whatever a step hands back is what is in the box on the next one. That
/// is the contract, not a convenience — an edit ends a walk, and the history works that out by
/// comparing the text it is handed against what it last gave you. Two bare
/// <c>Previous("")</c> calls are not "press Up twice"; they are "press Up, clear the box,
/// press Up".</para>
/// </summary>
public class CommandHistoryPrefixTests
{
    /// <summary>The command line, as far as <see cref="CommandHistory"/> can tell.</summary>
    private sealed class Box
    {
        public readonly CommandHistory History;
        public string Text;
        public Box(CommandHistory history, string text = "") { History = history; Text = text; }
        public string? Up(string? prefix = null)
        {
            string? r = History.Previous(Text, prefix);
            if (r is not null) Text = r;
            return r;
        }
        public string? Down()
        {
            string? r = History.Next(Text);
            if (r is not null) Text = r;
            return r;
        }
        public void Type(string t) => Text = t;
    }

    private static CommandHistory Seeded()
    {
        var h = new CommandHistory();
        foreach (string c in new[]
        {
            "vhelp", "vtrade goods iron", "look", "vtrade goods mead", "score",
            "vtrade goods iron",             // a NON-consecutive repeat: Add does not collapse these
            "vbuild list", "north",
        }) h.Add(c);
        return h;
    }

    [Fact]
    public void Plain_up_walks_the_whole_history_and_keeps_walking()
    {
        // The reported break: Up from an empty box gave the newest command and then stopped
        // dead. The box's TextChanged for the recalled text was being read as the user
        // typing, which re-anchored the walk on the command it had just recalled — a view of
        // one entry, stepped forever.
        var b = new Box(Seeded());
        Assert.Equal("north", b.Up());
        Assert.Equal("vbuild list", b.Up());
        Assert.Equal("vtrade goods iron", b.Up());
        Assert.Equal("score", b.Up());
    }

    [Fact]
    public void Plain_up_ignores_what_you_have_typed()
    {
        // The filtered walk is a separate gesture. Up on its own is the whole history, which
        // is what the key means in every shell and every other client.
        var b = new Box(Seeded(), "vtrade ");
        Assert.Equal("north", b.Up());
        Assert.Equal("vbuild list", b.Up());
    }

    [Fact]
    public void A_prefix_limits_the_walk_to_what_starts_with_it()
    {
        var b = new Box(Seeded(), "vtrade ");
        Assert.Equal("vtrade goods iron", b.Up("vtrade "));
        Assert.Equal("vtrade goods mead", b.Up("vtrade "));
    }

    [Fact]
    public void The_filter_is_anchored_when_the_walk_begins()
    {
        // After the first Up the box holds the matched command. Re-deriving the prefix from
        // THAT would collapse the cycle to one entry, so the anchor has to outlive the box.
        var b = new Box(Seeded(), "vtrade ");
        Assert.Equal("vtrade goods iron", b.Up("vtrade "));
        Assert.Equal("vtrade goods mead", b.Up("a different prefix entirely"));
    }

    [Fact]
    public void A_walk_never_shows_the_same_command_twice()
    {
        // "vtrade goods iron" was run twice with other commands in between, which Add's
        // consecutive-only dedupe does not touch. Filtered, that is the tedious case.
        var b = new Box(Seeded(), "vtrade ");
        b.Up("vtrade ");
        Assert.Equal(2, b.History.MatchCount);
    }

    [Fact]
    public void Down_walks_back_and_ends_at_what_you_had_typed()
    {
        var b = new Box(Seeded(), "vtrade ");
        b.Up("vtrade ");
        b.Up("vtrade ");
        Assert.Equal("vtrade goods iron", b.Down());
        Assert.Equal("vtrade ", b.Down());
        Assert.Null(b.Down());
    }

    [Fact]
    public void A_prefix_nothing_matches_recalls_nothing_and_leaves_no_dead_walk()
    {
        var b = new Box(Seeded(), "zzz");
        Assert.Null(b.Up("zzz"));    // the box is left alone
        Assert.Equal("north", b.Up());
    }

    [Fact]
    public void Editing_re_anchors_the_next_walk()
    {
        // No Resync() call and no event: the history sees that the box no longer holds what
        // it handed back, which is the only signal that cannot arrive at the wrong moment.
        var b = new Box(Seeded(), "vtrade ");
        Assert.Equal("vtrade goods iron", b.Up("vtrade "));
        b.Type("vbuild");
        Assert.Equal("vbuild list", b.Up("vbuild"));
    }

    [Fact]
    public void Editing_ends_an_unfiltered_walk_too()
    {
        var b = new Box(Seeded());
        b.Up(); b.Up();
        b.Type("half typed");
        Assert.Equal("north", b.Up());      // back at the newest, on a fresh walk
    }

    [Fact]
    public void Down_after_an_edit_does_nothing_rather_than_resuming()
    {
        var b = new Box(Seeded());
        b.Up(); b.Up();
        b.Type("half typed");
        Assert.Null(b.Down());
    }

    [Fact]
    public void Submitting_ends_the_walk()
    {
        var b = new Box(Seeded(), "vtrade ");
        b.Up("vtrade ");
        b.History.Add("vtrade goods furs");
        b.Type("");
        Assert.Equal("vtrade goods furs", b.Up());
    }

    [Fact]
    public void The_prefix_match_ignores_case() =>
        Assert.Equal("vtrade goods iron", new Box(Seeded(), "VTR").Up("VTR"));

    // ---- the inline suggestion (ghost text reads this) ----

    [Fact]
    public void Suggest_returns_the_newest_longer_match()
    {
        CommandHistory h = Seeded();
        Assert.Equal("vtrade goods iron", h.Suggest("vtr"));
        Assert.Null(h.Suggest("vtrade goods iron"));   // nothing left to suggest
        Assert.Null(h.Suggest(""));
        Assert.Null(h.Suggest("zzz"));
    }

    [Fact]
    public void Suggest_does_not_disturb_a_walk_in_progress()
    {
        var b = new Box(Seeded(), "vtrade ");
        b.Up("vtrade ");
        b.History.Suggest("vb");
        Assert.Equal("vtrade goods mead", b.Up("vtrade "));
    }
}

using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Recall filtered by what you have already typed: `vtrade ` then Up cycles only the vtrade
/// commands. MUSHclient puts this on Alt+Up because plain Up is spoken for; with a prefix in
/// the box there is no other sensible reading of Up, so it goes on plain Up here and Ctrl+Up
/// keeps the unfiltered walk.
/// </summary>
public class CommandHistoryPrefixTests
{
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
    public void No_prefix_is_the_whole_history_newest_first()
    {
        CommandHistory h = Seeded();
        Assert.Equal("north", h.Previous(""));
        Assert.Equal("vbuild list", h.Previous(""));
    }

    [Fact]
    public void A_prefix_limits_the_walk_to_what_starts_with_it()
    {
        CommandHistory h = Seeded();
        Assert.Equal("vtrade goods iron", h.Previous("vtrade ", "vtrade "));
        Assert.Equal("vtrade goods mead", h.Previous("", "vtrade "));
    }

    [Fact]
    public void The_filter_is_anchored_when_the_walk_begins()
    {
        // After the first Up the box holds the matched command. Re-deriving the prefix from
        // THAT would collapse the cycle to one entry, so the anchor has to outlive the box.
        CommandHistory h = Seeded();
        Assert.Equal("vtrade goods iron", h.Previous("vtrade ", "vtrade "));
        Assert.Equal("vtrade goods mead", h.Previous("", "vtrade goods iron"));
    }

    [Fact]
    public void A_walk_never_shows_the_same_command_twice()
    {
        // "vtrade goods iron" was run twice with other commands in between, which Add's
        // consecutive-only dedupe does not touch. Filtered, that is the tedious case.
        CommandHistory h = Seeded();
        h.Previous("vtrade ", "vtrade ");
        Assert.Equal(2, h.MatchCount);
    }

    [Fact]
    public void Down_walks_back_and_ends_at_what_you_had_typed()
    {
        CommandHistory h = Seeded();
        h.Previous("vtrade ", "vtrade ");
        h.Previous("", "vtrade ");
        Assert.Equal("vtrade goods iron", h.Next());
        Assert.Equal("vtrade ", h.Next());
        Assert.Null(h.Next());
    }

    [Fact]
    public void A_prefix_nothing_matches_recalls_nothing_and_leaves_no_dead_walk()
    {
        CommandHistory h = Seeded();
        Assert.Null(h.Previous("zzz", "zzz"));   // the box is left alone
        Assert.Equal("north", h.Previous("", ""));
    }

    [Fact]
    public void Editing_re_anchors_the_next_walk()
    {
        CommandHistory h = Seeded();
        Assert.Equal("vtrade goods iron", h.Previous("vtrade ", "vtrade "));
        h.Resync();                              // the input box changed under the user's hands
        Assert.Equal("vbuild list", h.Previous("vbuild", "vbuild"));
    }

    [Fact]
    public void Submitting_ends_the_walk()
    {
        CommandHistory h = Seeded();
        h.Previous("vtrade ", "vtrade ");
        h.Add("vtrade goods furs");
        Assert.Equal("vtrade goods furs", h.Previous("", ""));
    }

    [Fact]
    public void The_prefix_match_ignores_case() =>
        Assert.Equal("vtrade goods iron", Seeded().Previous("VTR", "VTR"));

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
        CommandHistory h = Seeded();
        h.Previous("vtrade ", "vtrade ");
        h.Suggest("vb");
        Assert.Equal("vtrade goods mead", h.Previous("", "vtrade "));
    }
}

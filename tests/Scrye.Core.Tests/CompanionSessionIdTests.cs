using Scrye.Companion.Protocol;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Session identity (companion design §4). A client remembers this id between runs, so the
/// properties that matter are: stable for the same profile, distinct for different
/// characters, and containing no separator that could fake a deeper chain.
/// </summary>
public class CompanionSessionIdTests
{
    [Fact]
    public void SameProfile_ProducesSameId() =>
        Assert.Equal(
            CompanionSessionId.FromProfile("3Scapes", null, "Eldran"),
            CompanionSessionId.FromProfile("3Scapes", null, "Eldran"));

    [Fact]
    public void DifferentCharactersOnOneMud_Differ() =>
        Assert.NotEqual(
            CompanionSessionId.FromProfile("3Scapes", null, "Eldran"),
            CompanionSessionId.FromProfile("3Scapes", null, "Vikar"));

    [Fact]
    public void IdIsReadable()
    {
        Assert.Equal("3scapes/eldran", CompanionSessionId.FromProfile("3Scapes", null, "Eldran"));
        Assert.Equal("3scapes/main/eldran", CompanionSessionId.FromProfile("3Scapes", "Main", "Eldran"));
        Assert.Equal("3scapes", CompanionSessionId.FromProfile("3Scapes", null, null));
    }

    [Fact]
    public void EmptyLayersAreSkipped_NotLeftAsBlankSegments() =>
        Assert.Equal("3scapes/eldran", CompanionSessionId.FromProfile("3Scapes", "   ", "Eldran"));

    [Fact]
    public void AllEmpty_FallsBackToAnEphemeralId()
    {
        string id = CompanionSessionId.FromProfile("", null, null);
        Assert.StartsWith("quick-", id);
    }

    [Fact]
    public void EphemeralIdsAreUnique() =>
        Assert.NotEqual(CompanionSessionId.NewEphemeral(), CompanionSessionId.NewEphemeral());

    [Theory]
    [InlineData("Three Scapes", "three-scapes")]
    [InlineData("MUD (test)", "mud-test")]
    [InlineData("  Padded  ", "padded")]
    [InlineData("dots.and_underscores", "dots-and-underscores")]
    // Non-ASCII collapses rather than passing through: a leading one is dropped entirely
    // (no leading dash), interior ones become a single separator, trailing ones are trimmed.
    [InlineData("Ünïcodé", "n-cod")]
    [InlineData("!!!", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SlugIsAsciiSafe(string? input, string expected) =>
        Assert.Equal(expected, CompanionSessionId.Slug(input));

    [Fact]
    public void SlugCannotSmuggleASeparator()
    {
        // A character name containing '/' must not be able to fake a deeper profile chain.
        string id = CompanionSessionId.FromProfile("3Scapes", null, "evil/name");
        Assert.Equal("3scapes/evil-name", id);
        Assert.Equal(2, id.Split('/').Length);
    }

    [Fact]
    public void SlugHasNoLeadingOrTrailingDashes()
    {
        Assert.Equal("abc", CompanionSessionId.Slug("---abc---"));
        Assert.Equal("a-b", CompanionSessionId.Slug("  a   b  "));
    }
}

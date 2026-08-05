using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The plugin colour markup (<see cref="Markup"/>). The behaviour that matters most here is the
/// failure behaviour: markup is written by hand in Lua and mixed with text that came off the MUD,
/// so malformed input must render literally rather than swallowing the line.
/// </summary>
public class MarkupTests
{
    private static readonly Rgb Base = new(0xE8, 0xDF, 0xFF);

    // a stand-in scheme; only these names resolve, everything else is "unknown"
    private static Rgb? Resolve(string name) => name.ToLowerInvariant() switch
    {
        "accent" => new Rgb(0xFF, 0x2E, 0x88),
        "success" => new Rgb(0xB6, 0xFF, 0x3C),
        "error" => new Rgb(0xFF, 0x3B, 0x5C),
        "dim" => new Rgb(0x9A, 0x7F, 0xC7),
        _ => null,
    };

    private static IReadOnlyList<StyledRun> Parse(string s) => Markup.Parse(s, Resolve, Base);

    private static string Shape(string s) =>
        string.Join(" | ", Parse(s).Select(r =>
            $"{r.Fore.ToHex()}{(r.Flags == RunFlags.None ? "" : "+" + r.Flags)}:{r.Text}"));

    [Theory]
    [InlineData("hello", "#E8DFFF:hello")]
    [InlineData("@{accent}hi@{}", "#FF2E88:hi")]
    [InlineData("a@{error}b@{}c", "#E8DFFF:a | #FF3B5C:b | #E8DFFF:c")]
    [InlineData("@{#21E6FF}cyan@{}", "#21E6FF:cyan")]
    [InlineData("tail @{success}green", "#E8DFFF:tail  | #B6FF3C:green")]
    public void ColoursRuns(string input, string expected) => Assert.Equal(expected, Shape(input));

    [Fact]
    public void StylesNestAndPopBackToTheEnclosingOne() =>
        Assert.Equal("#FF2E88:A | #FF3B5C:B | #FF2E88:C | #E8DFFF:D",
            Shape("@{accent}A@{error}B@{}C@{}D"));

    [Fact]
    public void ExtraClosesFallBackToTheBaseStyleRatherThanThrowing() =>
        Assert.Equal("#E8DFFF:x", Shape("@{}@{}x"));

    // Malformed markup must never eat text: every one of these renders literally.
    [Theory]
    [InlineData("user@@host", "user@host")]
    [InlineData("5 @ 6", "5 @ 6")]
    [InlineData("a@{accent b", "a@{accent b")]
    [InlineData("@x", "@x")]
    [InlineData("cost@", "cost@")]
    public void MalformedMarkupRendersLiterally(string input, string plain) =>
        Assert.Equal(plain, string.Concat(Parse(input).Select(r => r.Text)));

    [Fact]
    public void UnknownColourKeepsTheCurrentOneInsteadOfRenderingInvisibly() =>
        Assert.Equal("#E8DFFF:x", Shape("@{gren}x@{}"));

    [Theory]
    [InlineData("@{accent,bold}A@{}", "#FF2E88+Bold:A")]
    [InlineData("@{,underline}A@{}", "#E8DFFF+Underline:A")]
    [InlineData("@{accent,wiggle}A@{}", "#FF2E88:A")]           // unknown flag ignored
    public void AppliesFlags(string input, string expected) => Assert.Equal(expected, Shape(input));

    [Fact]
    public void FlagsAreInheritedByNestedRuns() =>
        Assert.Equal("#E8DFFF+Bold:A | #FF3B5C+Bold:B", Shape("@{,bold}A@{error}B@{}@{}"));

    [Fact]
    public void ParsesABackgroundAfterASlash()
    {
        StyledRun run = Assert.Single(Parse("@{#FF2E88/#0B0420}A@{}"));
        Assert.Equal("#FF2E88", run.Fore.ToHex());
        Assert.Equal("#0B0420", run.Back.ToHex());
    }

    [Fact]
    public void MergesAdjacentRunsThatShareAStyle() =>
        Assert.Equal("#FF2E88:ab", Shape("@{accent}a@{}@{accent}b@{}"));

    // Strip backs the sinks that cannot show colour (scrye.log, scrye.notify). If it ever
    // disagreed with the parser, a log file would not match what the user saw on screen.
    [Theory]
    [InlineData("@{accent}[build]@{} @{success}OK@{} 10 iron")]
    [InlineData("user@@host")]
    [InlineData("a@{accent b")]
    [InlineData("plain")]
    [InlineData("")]
    public void StripAlwaysAgreesWithTheParsedPlainText(string input) =>
        Assert.Equal(string.Concat(Parse(input).Select(r => r.Text)), Markup.Strip(input));

    [Fact]
    public void EscapingUntrustedTextNeutralisesInjectedMarkup()
    {
        // what a plugin does before embedding MUD text: text:gsub("@", "@@")
        const string hostile = "@{error}not a colour@{}";
        string escaped = hostile.Replace("@", "@@");
        StyledRun run = Assert.Single(Parse(escaped));
        Assert.Equal(hostile, run.Text);            // rendered as characters
        Assert.Equal(Base, run.Fore);               // and it did not change the colour
    }

    [Fact]
    public void EmptyInputProducesNoRunsButToLineStillHasOne()
    {
        Assert.Empty(Markup.Parse("", Resolve, Base));
        Assert.Single(Markup.ToLine("", Resolve, Base).Runs);
    }

    [Fact]
    public void ToLinePlainTextMatchesStrip() =>
        Assert.Equal(Markup.Strip("@{accent}[build]@{} ok"),
                     Markup.ToLine("@{accent}[build]@{} ok", Resolve, Base).PlainText);

    [Fact]
    public void HasMarkupIsTrueOnlyWhenThereIsSomethingToParse()
    {
        Assert.False(Markup.HasMarkup("no markup here"));
        Assert.False(Markup.HasMarkup(""));
        Assert.False(Markup.HasMarkup(null));
        Assert.True(Markup.HasMarkup("a@{accent}b"));
    }

    // ---- clickable runs -------------------------------------------------------
    // click= carries a whole command, so it is taken verbatim to the closing brace. That is the
    // only part of the grammar where commas and spaces are content rather than separators.

    private static LinkInfo? LinkOf(string s) => Parse(s).SingleOrDefault(r => r.Link is not null).Link;

    [Theory]
    [InlineData("@{click=look}Look@{}", "look")]
    [InlineData("@{accent,click=score}Score@{}", "score")]
    [InlineData("@{click=vbuild start warehouse}W@{}", "vbuild start warehouse")]
    [InlineData("@{click=say hello, friend}Hi@{}", "say hello, friend")]
    [InlineData("@{accent,bold,click=north}N@{}", "north")]
    public void ParsesAClickCommandVerbatim(string input, string expected) =>
        Assert.Equal(expected, LinkOf(input)?.Action);

    [Fact]
    public void CarriesALongCommandPastTheStyleSpecLengthGuard()
    {
        const string cmd = "vtrade dispatch sell 65 bread uppsala escort 5";
        Assert.Equal(cmd, LinkOf($"@{{success,click={cmd}}}65 Bread>Uppsala@{{}}")?.Action);
    }

    [Fact]
    public void PromptVariantSetsPromptRatherThanRunning()
    {
        LinkInfo? link = LinkOf("@{prompt=vbuild start dock}Dock@{}");
        Assert.Equal("vbuild start dock", link?.Action);
        Assert.True(link?.Prompt);
        Assert.False(link?.IsUrl);
    }

    [Theory]
    [InlineData("@{accent}plain@{}")]
    [InlineData("@{accent,click=}x@{}")]     // empty command is not a link
    public void LeavesRunsUnlinkedWhenThereIsNoCommand(string input) =>
        Assert.All(Parse(input), r => Assert.Null(r.Link));

    [Fact]
    public void AdjacentLinksAreNotMergedIntoOneRun()
    {
        IReadOnlyList<StyledRun> runs = Parse("@{click=a}A@{}@{click=b}B@{}plain");
        Assert.Equal(3, runs.Count);
        Assert.Equal("a", runs[0].Link?.Action);
        Assert.Equal("b", runs[1].Link?.Action);
        Assert.Null(runs[2].Link);
    }

    [Fact]
    public void TheCommandNeverLeaksIntoTheVisibleText()
    {
        const string input = "@{click=vbuild start warehouse}Warehouse@{}";
        Assert.Equal("Warehouse", string.Concat(Parse(input).Select(r => r.Text)));
        Assert.Equal("Warehouse", Markup.Strip(input));
    }
}

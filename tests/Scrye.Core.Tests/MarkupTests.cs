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

    // ---- rclick= (API 1.16): a second, right-button action on the same run ----
    // Emitted BEFORE click= by plugins (a pre-1.16 host reads it as an unknown flag),
    // but the parser accepts either order.

    [Theory]
    [InlineData("@{accent,rclick=atrade floorset bread,click=atrade exempt bread}Bread@{}",
                "atrade exempt bread", "atrade floorset bread")]
    [InlineData("@{accent,click=atrade exempt bread,rclick=atrade floorset bread}Bread@{}",
                "atrade exempt bread", "atrade floorset bread")]
    [InlineData("@{rclick=mapg shift s}s@{}", "", "mapg shift s")]
    public void ParsesARightClickActionBesideTheClick(string input, string click, string? rclick)
    {
        LinkInfo? l = LinkOf(input);
        Assert.NotNull(l);
        Assert.Equal(click, l!.Action);
        Assert.Equal(rclick, l.RightAction);
    }

    [Fact]
    public void TheLastVerbStillTakesItsCommandVerbatimToTheBrace()
    {
        LinkInfo? l = LinkOf("@{rclick=atrade floorset x,click=say hello, friend}Hi@{}");
        Assert.Equal("say hello, friend", l?.Action);
        Assert.Equal("atrade floorset x", l?.RightAction);
    }

    [Fact]
    public void RclickNeverLeaksIntoTheStyleOrThePlainText()
    {
        StyledRun run = Assert.Single(Parse("@{accent,rclick=atrade floorset bread,click=x}Bread@{}"));
        Assert.Equal("Bread", run.Text);
        Assert.Equal("#FF2E88", run.Fore.ToHex());   // 'rclick=...' did not eat the colour
    }

    [Fact]
    public void ARunWithoutRclickHasNoRightAction() =>
        Assert.Null(LinkOf("@{click=look}Look@{}")?.RightAction);

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

    // ---- menu= (API 1.19): a whole context menu on the right button ----
    // Entries 'Label|command' separated by ';'; '-' a separator, a bare label a caption.
    // Emitted BEFORE rclick=/click= by plugins, value kept comma-free, so a pre-1.19 host
    // reads it as unknown flags and falls back to the rclick= that follows.

    [Fact]
    public void ParsesAMenuBesideTheOtherVerbs()
    {
        LinkInfo? l = LinkOf(
            "@{accent,menu=Hold|atrade exempt bread;Floor 500|atrade floorset bread;-;About,"
            + "rclick=atrade floorset bread,click=atrade exempt bread}Bread@{}");
        Assert.NotNull(l);
        Assert.Equal("atrade exempt bread", l!.Action);
        Assert.Equal("atrade floorset bread", l.RightAction);      // the pre-1.19 fallback rides along
        Assert.NotNull(l.Menu);
        Assert.Equal(4, l.Menu!.Count);
        Assert.Equal(new Scrye.Core.Plugins.MenuEntry("Hold", "atrade exempt bread"), l.Menu[0]);
        Assert.Equal(new Scrye.Core.Plugins.MenuEntry("Floor 500", "atrade floorset bread"), l.Menu[1]);
        Assert.True(l.Menu[2].IsSeparator);
        Assert.Equal(new Scrye.Core.Plugins.MenuEntry("About", null), l.Menu[3]);   // caption: no command
    }

    [Fact]
    public void AMenuAloneMakesTheRunALink()
    {
        // no click=, no rclick= - the run is right-button-only, like an rclick=-only run
        LinkInfo? l = LinkOf("@{menu=Walk there|mapg go 42;Details|mapg room 42}Inn@{}");
        Assert.NotNull(l);
        Assert.Equal("", l!.Action);
        Assert.Null(l.RightAction);
        Assert.Equal(2, l.Menu!.Count);
        Assert.Equal("mapg go 42", l.Menu[0].Command);
    }

    [Fact]
    public void MenuCommandsKeepTheirLaterBars()
    {
        // the label splits at the FIRST '|'; anything after belongs to the command verbatim
        LinkInfo? l = LinkOf("@{menu=Odd|say a|b}X@{}");
        Assert.Equal("say a|b", Assert.Single(l!.Menu!).Command);
    }

    [Fact]
    public void AnEmptyOrJunkMenuYieldsNoMenu()
    {
        Assert.Null(LinkOf("@{menu=,click=x}X@{}")!.Menu);          // empty value: no menu
        Assert.Null(LinkOf("@{menu=;;;,click=x}X@{}")!.Menu);       // only empty entries
        Assert.Null(LinkOf("@{menu=}X@{}"));                        // menu= alone and empty: no link at all
    }

    [Fact]
    public void MenuNeverLeaksIntoTheStyleOrThePlainText()
    {
        const string input = "@{accent,menu=Hold|atrade exempt bread;About}Bread@{}";
        StyledRun run = Assert.Single(Parse(input));
        Assert.Equal("Bread", run.Text);
        Assert.Equal("Bread", Markup.Strip(input));
    }

    [Fact]
    public void ALongMenuSpecStillParses()
    {
        // the 1.19 cap raise to 512: a four-entry menu with real commands blows past 256
        string spec = "menu=Dispatch best|vtrade dispatch sell 310 bread lodbrok's hold escort 5"
            + ";Dispatch half|vtrade dispatch sell 155 bread lodbrok's hold escort 5"
            + ";Set the floor to five hundred units|atrade floorset bread"
            + ";Clear the floor entirely|atrade floor bread 0"
            + ";Hold this good out of trading|atrade exempt bread";
        Assert.True(spec.Length > 256);
        LinkInfo? l = LinkOf("@{" + spec + "}Bread@{}");
        Assert.Equal(5, l!.Menu!.Count);
    }
}

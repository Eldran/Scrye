using System.Text.RegularExpressions;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Review fixes in Core that guard the plugin runtimes: a manifest <c>entry</c> is confined to
/// the plugin's folder, and plugin rule patterns can carry a match timeout.
/// </summary>
public sealed class ReviewPluginSafetyTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "scrye-review-plugin", "myplugin");

    private static PluginDescriptor With(string entry) =>
        new(new PluginManifest { Id = "p", Name = "p", Entry = entry }, Folder);

    [Theory]
    [InlineData("main.lua")]
    [InlineData("src/main.lua")]
    [InlineData("./src/../main.lua")]
    public void EntryInsideTheFolderResolvesUnderIt(string entry)
    {
        string path = With(entry).EntryPath;
        Assert.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, path);
    }

    [Theory]
    [InlineData("../other/main.lua")]
    [InlineData("../myplugin-evil/main.lua")]   // sibling sharing the folder name as a prefix
    [InlineData("..")]
    [InlineData("")]
    public void EntryEscapingTheFolderIsRefused(string entry)
    {
        Assert.Throws<InvalidOperationException>(() => _ = With(entry).EntryPath);
    }

    [Fact]
    public void AbsoluteEntryIsRefused()
    {
        string outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere", "main.lua"));
        Assert.Throws<InvalidOperationException>(() => _ = With(outside).EntryPath);
    }

    [Fact]
    public void PluginPatternTimesOutInsteadOfHanging()
    {
        // Classic catastrophic backtracking: (a+)+$ against a long run of a's and a mismatch.
        var p = new CompiledPattern("^(a+)+$", isRegex: true, ignoreCase: false,
                                    matchTimeout: TimeSpan.FromMilliseconds(50));
        Assert.Throws<RegexMatchTimeoutException>(() => p.Match(new string('a', 40) + "!"));
    }

    [Fact]
    public void PatternWithoutTimeoutStillMatchesNormally()
    {
        var p = new CompiledPattern("You see *.", isRegex: false, ignoreCase: true);
        Assert.Equal("a dragon", p.Match("you see a dragon.")!.Group(1));
        var q = new CompiledPattern("hp (\\d+)", isRegex: true, ignoreCase: false,
                                    matchTimeout: CompiledPattern.PluginMatchTimeout);
        Assert.Equal("42", q.Match("hp 42")!.Group(1));
    }
}

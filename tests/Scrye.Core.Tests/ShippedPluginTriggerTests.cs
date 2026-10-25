using System.Text.RegularExpressions;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The shipped plugins' output triggers, read straight from their main.lua. A trigger that
/// ends at <c>$</c> must allow trailing whitespace: some guilds pad their lines (a Changeling's
/// "There is no mutant here." arrives with a run of spaces after it), and an exact end left
/// 3S Chaos Sea re-sending 'kill mutant' into an empty room forever, its room-clear signal
/// never matching (Joakim, 25 Oct 2026). The Lua harnesses cannot see this - they fire
/// triggers by name, not through the regex - so it is checked here, on the real patterns.
/// </summary>
public class ShippedPluginTriggerTests
{
    private static string PluginsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrye.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Scrye.App", "plugins");
    }

    /// <summary>Every scrye.addTrigger's pattern in a Lua source, long-bracket or quoted.</summary>
    internal static IEnumerable<string> TriggerPatterns(string lua)
    {
        int at = 0;
        while ((at = lua.IndexOf("addTrigger", at, StringComparison.Ordinal)) >= 0)
        {
            at += "addTrigger".Length;
            Match m = Regex.Match(lua[at..Math.Min(lua.Length, at + 400)],
                @"pattern\s*=\s*(?:\[\[(?<long>.*?)\]\]|""(?<quoted>(?:[^""\\]|\\.)*)"")", RegexOptions.Singleline);
            if (!m.Success) continue;
            yield return m.Groups["long"].Success
                ? m.Groups["long"].Value
                : m.Groups["quoted"].Value.Replace(@"\\", @"\");   // a Lua "..." string escapes its backslashes
        }
    }

    [Fact]
    public void Every_end_anchored_output_trigger_allows_trailing_whitespace()
    {
        var offenders = new List<string>();
        int checkedCount = 0;
        foreach (string file in Directory.GetFiles(PluginsDir(), "main.lua", SearchOption.AllDirectories))
        {
            string plugin = Path.GetFileName(Path.GetDirectoryName(file)!);
            foreach (string pattern in TriggerPatterns(File.ReadAllText(file)))
            {
                checkedCount++;
                if (!pattern.EndsWith('$') || pattern.EndsWith(@"\$")) continue;     // not anchored at the end
                if (pattern.EndsWith(@"\s*$") || pattern.EndsWith(@"\s+$")) continue;
                offenders.Add($"{plugin}: {pattern}");
            }
        }
        Assert.True(checkedCount > 20, $"only {checkedCount} trigger patterns found - is the parser still finding them?");
        Assert.True(offenders.Count == 0,
            "end-anchored triggers that a padded line never matches:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("There is no mutant here.", "mutant")]
    [InlineData("There is no mutant here.                                                   ", "mutant")]
    [InlineData("There is no small varmint here.  ", "small varmint")]
    public void The_room_is_clear_line_matches_padded_and_captures_the_name_alone(string line, string name)
    {
        // the pattern exactly as 3S Chaos Sea ships it
        string pattern = TriggerPatterns(File.ReadAllText(Path.Combine(PluginsDir(), "3s-chaossea", "main.lua")))
            .Single(p => p.StartsWith("^There is no "));
        Match m = Regex.Match(line, pattern);
        Assert.True(m.Success, $"'{pattern}' did not match '{line}'");
        Assert.Equal(name, m.Groups[1].Value);
    }

    [Theory]
    [InlineData("=M= Small Varmint", "Small Varmint")]
    [InlineData("=M= Small Varmint      ", "Small Varmint")]
    public void A_room_marker_captures_the_mob_without_its_padding(string line, string name)
    {
        // a padded marker line's spaces would otherwise become part of the name, and an
        // exclusion by name would quietly stop matching
        string pattern = TriggerPatterns(File.ReadAllText(Path.Combine(PluginsDir(), "3s-chaossea", "main.lua")))
            .First(p => p.Contains("=M="));
        Match m = Regex.Match(line, pattern);
        Assert.True(m.Success);
        Assert.Equal(name, m.Groups[1].Value);
    }
}

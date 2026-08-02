using Scrye.Core.Automation;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The companion privilege rule (design §7.3). Its whole job is to stop a paired device that
/// only holds "send commands" from reaching the Lua console — <c>WorldViewModel.Submit</c>
/// dispatches <c>/…</c> to <c>MudSession.RunScript</c>, so without this gate "may send
/// commands" would silently mean "may run arbitrary script on the session loop".
/// </summary>
public class CommandPrivilegeTests
{
    private static readonly CommandOrigin Phone = CommandOrigin.Companion();                    // default: no scripting
    private static readonly CommandOrigin TrustedPhone = CommandOrigin.Companion(mayRunScripts: true);

    [Theory]
    [InlineData("/world.AddAlias(\"x\", \"*\", \"y\")")]
    [InlineData("/print('hi')")]
    [InlineData("/a")]
    public void ScriptConsoleIsRecognised(string text) =>
        Assert.True(CommandPrivilege.IsScriptConsole(text));

    [Theory]
    [InlineData("/")]              // nothing to run — goes to the MUD as text
    [InlineData(" /foo")]          // console never claims it; MUD input
    [InlineData("north")]
    [InlineData(".walk n;n;e")]
    [InlineData("mipstart")]
    [InlineData("")]
    [InlineData(null)]
    public void NonScriptTextIsNotPrivileged(string? text) =>
        Assert.False(CommandPrivilege.IsScriptConsole(text));

    [Fact]
    public void LocalOriginMayDoAnything()
    {
        Assert.True(CommandOrigin.Local.MayRunScripts);
        Assert.True(CommandPrivilege.IsPermitted("/world.Send('x')", CommandOrigin.Local));
    }

    [Fact]
    public void CompanionDefaultsToNoScripting()
    {
        // The default matters more than the check: a device granted "send commands"
        // must not pick up scripting implicitly.
        Assert.False(CommandOrigin.Companion().MayRunScripts);
        Assert.False(CommandPrivilege.IsPermitted("/world.Send('x')", Phone));
    }

    [Fact]
    public void CompanionMayScriptOnceGranted() =>
        Assert.True(CommandPrivilege.IsPermitted("/world.Send('x')", TrustedPhone));

    [Theory]
    [InlineData("north")]
    [InlineData("say hello")]
    [InlineData("/")]
    public void OrdinaryInputIsAlwaysPermitted(string text) =>
        Assert.True(CommandPrivilege.IsPermitted(text, Phone));

    [Theory]
    [InlineData(".walk north;north;east")]
    [InlineData(".seq market-run")]
    [InlineData(".stop")]
    [InlineData(".log")]
    public void ClientCommandsAreNotGated(string text)
    {
        // Sequences are command lists this desktop already authored, not arbitrary code.
        // Firing a walk route from a phone is a feature, not a privilege escalation.
        Assert.False(CommandPrivilege.IsScriptConsole(text));
        Assert.True(CommandPrivilege.IsPermitted(text, Phone));
    }

    [Fact]
    public void OriginIsAValue_SoItCannotBeMutatedAfterTheDecision()
    {
        var a = CommandOrigin.Companion(mayRunScripts: false);
        var b = CommandOrigin.Companion(mayRunScripts: false);
        Assert.Equal(a, b);
        Assert.NotEqual(a, CommandOrigin.Companion(mayRunScripts: true));
        Assert.Equal(CommandSource.Companion, a.Source);
    }
}

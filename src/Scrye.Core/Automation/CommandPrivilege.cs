namespace Scrye.Core.Automation;

/// <summary>
/// Decides whether a command's text needs elevated privilege. Lives here, in a UI-free and
/// dependency-free assembly, precisely because it is security-relevant: the rule is small,
/// exactly testable, and has exactly one definition that every entry point shares
/// (companion design §7.3).
/// </summary>
public static class CommandPrivilege
{
    /// <summary>
    /// True when <paramref name="text"/> is a local Lua console command — a leading '/'
    /// with something after it, which <c>MudSession.RunScript</c> executes on the session
    /// loop. This is the only privileged prefix.
    ///
    /// <para>A bare <c>"/"</c> is not one: there is nothing to run, and it is sent to the
    /// MUD as ordinary text. Neither is <c>" /foo"</c> — leading whitespace means the
    /// console never claimed it, so it also goes to the MUD verbatim. Both are safe
    /// outcomes; the classification only has to avoid the opposite mistake of treating
    /// something as ordinary text when the pipeline would in fact execute it.</para>
    /// </summary>
    public static bool IsScriptConsole(string? text) =>
        text is not null && text.Length > 1 && text[0] == '/';

    /// <summary>Whether <paramref name="origin"/> may submit <paramref name="text"/>.
    /// Everything that is not the script console is permitted from any origin: client "."
    /// commands are pre-authored sequences rather than arbitrary code, and plain text is
    /// just MUD input.</summary>
    public static bool IsPermitted(string? text, CommandOrigin origin) =>
        !IsScriptConsole(text) || origin.MayRunScripts;
}

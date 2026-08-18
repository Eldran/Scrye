using System.Collections.Generic;
using System.Text;

namespace Scrye.Core.Automation;

/// <summary>
/// Splits one typed line into the several commands it stands for:
/// <c>vtrade refine smithy transfer all;vtrade refine smelter transfer all;vtrade refine all fill</c>
/// is three commands, not one.
///
/// <para>Lives beside <see cref="CommandPrivilege"/> and for the same reason: the rule is
/// small, exactly testable, and wants exactly one definition. It is applied at
/// <c>MudSession.HandleInput</c>, which is reached only by things a person did — typing, a
/// macro key, a companion device. What a trigger, timer or plugin sends goes out through
/// <c>IWorldActions.Send</c> and is never split, so a plugin sending a <c>say</c> with a
/// semicolon in it cannot be turned into two commands behind its back.</para>
///
/// <para>Two prefixes never reach here at all, which is what makes ';' safe to claim: the
/// Lua console (<c>/...</c>), where ';' separates Lua statements, and the client's own '.'
/// commands (<c>.walk n;n;e</c>), where the sequence parser already owns ';'. Both are
/// handled in <c>WorldViewModel.SubmitText</c> before the session sees them.</para>
/// </summary>
public static class CommandSeparator
{
    /// <summary>The separator. Doubled (<c>;;</c>) it means a literal semicolon, which is how
    /// you still type <c>say I went there;; it was fun</c> as one command.</summary>
    public const char Separator = ';';

    /// <summary>
    /// The commands <paramref name="text"/> stands for, or <c>null</c> when it holds no
    /// separator at all — the overwhelmingly common case, and the one where nothing about
    /// the existing behaviour should change, not even trimming.
    ///
    /// <para>Parts are trimmed and empty ones dropped, so a trailing <c>;</c> is harmless
    /// rather than a blank line sent to the MUD. A line that is nothing but separators
    /// therefore yields an empty list: it asked for no commands, and gets none.</para>
    /// </summary>
    public static IReadOnlyList<string>? Split(string? text)
    {
        if (text is null || text.IndexOf(Separator) < 0) return null;

        List<string> parts = new();
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != Separator)
            {
                sb.Append(c);
            }
            else if (i + 1 < text.Length && text[i + 1] == Separator)
            {
                sb.Append(Separator);   // ";;" -> one literal ";"
                i++;
            }
            else
            {
                Flush(parts, sb);
            }
        }
        Flush(parts, sb);
        return parts;
    }

    private static void Flush(List<string> parts, StringBuilder sb)
    {
        string s = sb.ToString().Trim();
        sb.Clear();
        if (s.Length > 0) parts.Add(s);
    }
}

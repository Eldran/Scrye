using System.Text.RegularExpressions;

namespace Scrye.Core.Session;

/// <summary>
/// A small pure state machine that answers login prompts after connect: feed it each
/// incoming line (including telnet-GA-flushed prompt fragments) and it returns the
/// text to send — the username on a name/login prompt, then the password on a
/// password prompt — or null. It sends each at most once and gives up quietly after
/// <see cref="MaxLines"/> non-empty lines, so a MUD with an unrecognised login flow
/// just behaves as if auto-login were off. Pure and session-loop-agnostic → CLI-testable.
/// </summary>
public sealed class AutoLogin
{
    /// <summary>Non-empty lines examined before giving up.</summary>
    public const int MaxLines = 60;

    private static readonly Regex NamePrompt = new(
        @"\b(name|login|account|character)\b.*[:?]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PasswordPrompt = new(
        @"\bpassword\b.*[:?]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _username;
    private readonly string? _password;
    private int _fed;

    public bool SentUsername { get; private set; }
    public bool SentPassword { get; private set; }

    /// <summary>True once there's nothing left to do (both sent, or gave up).</summary>
    public bool Done =>
        (SentUsername && (SentPassword || _password is null)) || _fed >= MaxLines;

    public AutoLogin(string username, string? password)
    {
        _username = username;
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>Examine one incoming line; returns the reply to send, or null.
    /// <paramref name="isPassword"/> is set when the reply is the password, so the
    /// caller can keep it out of echoes and event logs.</summary>
    public string? Feed(string text, out bool isPassword)
    {
        isPassword = false;
        if (Done) return null;
        string t = text.Trim();
        if (t.Length == 0) return null;
        _fed++;

        if (!SentUsername)
        {
            if (NamePrompt.IsMatch(t))
            {
                SentUsername = true;
                return _username;
            }
            return null;
        }

        if (_password is not null && !SentPassword && PasswordPrompt.IsMatch(t))
        {
            SentPassword = true;
            isPassword = true;
            return _password;
        }
        return null;
    }
}

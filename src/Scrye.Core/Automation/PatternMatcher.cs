using System.Text;
using System.Text.RegularExpressions;

namespace Scrye.Core.Automation;

/// <summary>The captured groups of a successful match.</summary>
public sealed class MatchResult
{
    private readonly Match _match;
    internal MatchResult(Match match) => _match = match;

    public string Whole => _match.Value;

    /// <summary>Character offset of the whole match within the input line.</summary>
    public int Index => _match.Index;

    /// <summary>Character length of the whole match.</summary>
    public int Length => _match.Length;

    /// <summary>Group by index: 0 = whole match, 1..n = wildcards.</summary>
    public string Group(int index) =>
        index >= 0 && index < _match.Groups.Count && _match.Groups[index].Success
            ? _match.Groups[index].Value : "";

    public string? Named(string name)
    {
        Group g = _match.Groups[name];
        return g.Success ? g.Value : null;
    }

    /// <summary>Numbered wildcards (groups 1..n) as a list.</summary>
    public IReadOnlyList<string> Wildcards
    {
        get
        {
            var list = new List<string>(Math.Max(0, _match.Groups.Count - 1));
            for (int i = 1; i < _match.Groups.Count; i++)
                list.Add(_match.Groups[i].Value);
            return list;
        }
    }
}

/// <summary>A compiled trigger/alias pattern. Wildcard patterns (<c>*</c> capturing,
/// <c>?</c> single char) are anchored to the whole line; regex patterns are used
/// as-is (match anywhere). Backed by .NET <see cref="Regex"/>.</summary>
public sealed class CompiledPattern
{
    private readonly Regex _regex;
    private readonly bool _never;

    /// <summary>The match limit plugin runtimes compile their rules with (see the
    /// <c>matchTimeout</c> constructor parameter).</summary>
    public static readonly TimeSpan PluginMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <param name="multiLine">Matching over several lines joined with <c>\n</c> (a trigger's
    /// <see cref="TriggerDef.Lines"/> &gt; 1): <c>^</c> and <c>$</c> then match at every line's start
    /// and end, as they do in MUSHclient's multi-line triggers, so a wildcard pattern typed on two
    /// lines matches two consecutive lines wherever they sit in the window.</param>
    /// <param name="matchTimeout">Per-match time limit. Null (the default) keeps the process-wide
    /// default, which is what the user's own triggers/aliases use. Plugin runtimes pass a short
    /// limit so a catastrophically backtracking plugin regex throws
    /// <see cref="RegexMatchTimeoutException"/> instead of freezing the session loop.</param>
    public CompiledPattern(string pattern, bool isRegex, bool ignoreCase, bool multiLine = false,
                           TimeSpan? matchTimeout = null)
    {
        RegexOptions opts = RegexOptions.CultureInvariant;
        if (ignoreCase) opts |= RegexOptions.IgnoreCase;
        if (multiLine) opts |= RegexOptions.Multiline;
        // a pattern typed in a text box on Windows may carry \r\n; the lines it matches do not
        pattern = pattern.Replace("\r\n", "\n");
        // An EMPTY pattern matches nothing, ever. Left to the regex engine it would do the
        // opposite: "" as a regex matches every line and every command, and "" as a wildcard
        // matches an empty command - so an alias saved with its pattern box still blank (the
        // editor seeds a new row with a name and no pattern) fired on everything you typed,
        // which reads from the outside as "the alias name triggers it". A rule with no
        // pattern has nothing to say yet; it stays quiet until it has one.
        _never = pattern.Length == 0;
        string source = isRegex ? pattern : WildcardToRegex(pattern);
        _regex = matchTimeout is { } timeout ? new Regex(source, opts, timeout) : new Regex(source, opts);
    }

    public MatchResult? Match(string input)
    {
        if (_never) return null;
        Match m = _regex.Match(input);
        return m.Success ? new MatchResult(m) : null;
    }

    /// <summary>
    /// Match over a window of lines joined with <c>\n</c>, the last being the newest, and only
    /// count a match that reaches into that newest line. Without that rule a block would fire on
    /// its last line and then again on every line after it while it stayed in the window.
    /// Every match in the window is tried, so a block that ends on the newest line is found even
    /// when an earlier one also matches.
    /// </summary>
    public MatchResult? MatchWindow(string joined, int newestStart)
    {
        if (_never) return null;
        for (Match m = _regex.Match(joined); m.Success; m = m.NextMatch())
        {
            int end = m.Index + m.Length;
            bool reaches = end > newestStart
                           || (end == newestStart && newestStart == joined.Length && m.Length > 0);
            if (reaches) return new MatchResult(m);
            if (m.Length == 0 && m.Index >= joined.Length) break;
        }
        return null;
    }

    private static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 4);
        sb.Append('^');
        foreach (char c in pattern)
        {
            if (c == '*') sb.Append("(.*?)");
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return sb.ToString();
    }
}

/// <summary>Expands a send template: <c>%0</c> whole match, <c>%1</c>..<c>%9</c>
/// numbered wildcards, <c>%&lt;name&gt;</c> named groups, <c>${var}</c> variables,
/// <c>%%</c> a literal percent.</summary>
public static class Template
{
    public static string Expand(string? template, MatchResult? match, VariableStore vars)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";

        var sb = new StringBuilder(template.Length);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];

            if (c == '%' && i + 1 < template.Length)
            {
                char n = template[i + 1];
                if (n >= '0' && n <= '9') { sb.Append(match?.Group(n - '0') ?? ""); i++; continue; }
                if (n == '%') { sb.Append('%'); i++; continue; }
                if (n == '<')
                {
                    int end = template.IndexOf('>', i + 2);
                    if (end > 0)
                    {
                        string name = template.Substring(i + 2, end - (i + 2));
                        sb.Append(match?.Named(name) ?? "");
                        i = end;
                        continue;
                    }
                }
            }
            else if (c == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                int end = template.IndexOf('}', i + 2);
                if (end > 0)
                {
                    string name = template.Substring(i + 2, end - (i + 2));
                    sb.Append(vars.Get(name) ?? "");
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
        }
        return sb.ToString();
    }
}

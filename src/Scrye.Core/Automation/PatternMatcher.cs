using System.Text;
using System.Text.RegularExpressions;

namespace Scrye.Core.Automation;

/// <summary>The captured groups of a successful match.</summary>
public sealed class MatchResult
{
    private readonly Match _match;
    internal MatchResult(Match match) => _match = match;

    public string Whole => _match.Value;

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

    public CompiledPattern(string pattern, bool isRegex, bool ignoreCase)
    {
        RegexOptions opts = RegexOptions.CultureInvariant;
        if (ignoreCase) opts |= RegexOptions.IgnoreCase;
        _regex = new Regex(isRegex ? pattern : WildcardToRegex(pattern), opts);
    }

    public MatchResult? Match(string input)
    {
        Match m = _regex.Match(input);
        return m.Success ? new MatchResult(m) : null;
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

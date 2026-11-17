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

    /// <summary>The match limit the user's own triggers and aliases compile with. They had none,
    /// so one catastrophically backtracking pattern - easily imported from MUSHclient - froze
    /// the session loop for good (release review, 21 Oct 2026). More generous than the plugin
    /// limit: a hand-written pattern on a long line is allowed to be slow, just not endless.</summary>
    public static readonly TimeSpan UserMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <param name="multiLine">Matching over several lines joined with <c>\n</c> (a trigger's
    /// <see cref="TriggerDef.Lines"/> &gt; 1): <c>^</c> and <c>$</c> then match at every line's start
    /// and end, as they do in MUSHclient's multi-line triggers, so a wildcard pattern typed on two
    /// lines matches two consecutive lines wherever they sit in the window.</param>
    /// <param name="matchTimeout">Per-match time limit. Null (the default) keeps the process-wide
    /// default (none). The automation engine passes <see cref="UserMatchTimeout"/> and plugin
    /// runtimes <see cref="PluginMatchTimeout"/>, so a catastrophically backtracking regex throws
    /// <see cref="RegexMatchTimeoutException"/> instead of freezing the session loop.</param>
    public CompiledPattern(string pattern, bool isRegex, bool ignoreCase, bool multiLine = false,
                           TimeSpan? matchTimeout = null)
        : this(SourceOf(pattern, isRegex), ignoreCase, multiLine, matchTimeout) { }

    /// <param name="source">The finished regex, or null for a pattern that never matches.</param>
    private CompiledPattern(string? source, bool ignoreCase, bool multiLine, TimeSpan? matchTimeout)
    {
        RegexOptions opts = RegexOptions.CultureInvariant;
        if (ignoreCase) opts |= RegexOptions.IgnoreCase;
        if (multiLine) opts |= RegexOptions.Multiline;
        _never = source is null;
        source ??= "(?!)";
        _regex = matchTimeout is { } timeout ? new Regex(source, opts, timeout) : new Regex(source, opts);
    }

    private static string? SourceOf(string pattern, bool isRegex)
    {
        // a pattern typed in a text box on Windows may carry \r\n; the lines it matches do not
        pattern = pattern.Replace("\r\n", "\n");
        // An EMPTY pattern matches nothing, ever. Left to the regex engine it would do the
        // opposite: "" as a regex matches every line and every command, and "" as a wildcard
        // matches an empty command - so an alias saved with its pattern box still blank (the
        // editor seeds a new row with a name and no pattern) fired on everything you typed,
        // which reads from the outside as "the alias name triggers it". A rule with no
        // pattern has nothing to say yet; it stays quiet until it has one.
        if (pattern.Length == 0) return null;
        return isRegex ? pattern : WildcardToRegex(pattern);
    }

    /// <summary>A pattern that matches nothing.</summary>
    public static CompiledPattern Never { get; } = new((string?)null, false, false, null);

    /// <summary>Compile a pattern whose <c>${name}</c> / <c>${!name}</c> references are filled
    /// in by <paramref name="fragment"/> (see <see cref="PatternVariables"/>). When any
    /// reference has no value the result is <see cref="Never"/>-like: it matches nothing.
    /// Throws <see cref="ArgumentException"/> when the filled-in regex is not valid.</summary>
    public static CompiledPattern WithVariables(string pattern, bool isRegex, bool ignoreCase,
                                                Func<string, bool, string?> fragment,
                                                bool multiLine = false, TimeSpan? matchTimeout = null)
        => new(PatternVariables.ToRegex(pattern, isRegex, fragment), ignoreCase, multiLine, matchTimeout);

    public MatchResult? Match(string input)
    {
        if (_never) return null;
        Match m = _regex.Match(input);
        return m.Success ? new MatchResult(m) : null;
    }

    /// <summary>Every match in <paramref name="input"/>, left to right, at most
    /// <paramref name="max"/> (a trigger's "repeat on same line"). An empty match counts only
    /// when it is the first: a pattern that can match nothing (<c>x*</c>) would otherwise
    /// "match" between every two characters.</summary>
    public IReadOnlyList<MatchResult> Matches(string input, int max)
    {
        var all = new List<MatchResult>();
        if (_never || max <= 0) return all;
        for (Match m = _regex.Match(input); m.Success && all.Count < max; m = m.NextMatch())
        {
            if (m.Length == 0 && all.Count > 0) continue;
            all.Add(new MatchResult(m));
            if (m.Length == 0) break;
        }
        return all;
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
        foreach (char c in pattern) AppendWildcard(sb, c);
        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>One character of a wildcard pattern, as regex: <c>*</c> a lazy capture,
    /// <c>?</c> any one character, everything else literal.</summary>
    internal static void AppendWildcard(StringBuilder sb, char c)
    {
        if (c == '*') sb.Append("(.*?)");
        else if (c == '?') sb.Append('.');
        else sb.Append(Regex.Escape(c.ToString()));
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

/// <summary>
/// Variables inside a trigger or alias PATTERN. <c>${name}</c> stands for the variable's
/// current value, matched literally (a name with a dot or a star in it is still that name);
/// <c>${!name}</c> takes the value as a piece of regex, wrapped in <c>(?:...)</c> so a list
/// such as <c>Eldran|Bjorn</c> is one alternative. Same meaning in wildcard and regex patterns.
///
/// <para>A reference to a variable that is unset or empty makes the whole pattern match
/// nothing. The other reading - the reference turns into nothing - is the dangerous one: an
/// unanchored <c>${targ} arrives</c> would fire for anyone who arrives the moment the target
/// is cleared.</para>
///
/// <para>Names start with a letter or <c>_</c> and go on with letters, digits, <c>_</c> and
/// <c>.</c> (so <c>${mxp.hp}</c> works). Anything else after a <c>$</c> - a regex <c>$</c>
/// anchor, a quantifier like <c>${2}</c>, an escaped <c>\$</c> in a regex - is left alone.</para>
/// </summary>
public static class PatternVariables
{
    /// <summary>The variables a pattern refers to, each once, in order of first use.</summary>
    public static IReadOnlyList<string> Names(string? pattern, bool isRegex)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(pattern)) return names;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (isRegex && pattern[i] == '\\') { i++; continue; }
            if (TryToken(pattern, i, out string name, out _, out int end))
            {
                if (!names.Contains(name)) names.Add(name);
                i = end;
            }
        }
        return names;
    }

    public static bool Uses(string? pattern, bool isRegex) => Names(pattern, isRegex).Count > 0;

    /// <summary>The regex a pattern compiles to with its references filled in by
    /// <paramref name="fragment"/>(name, raw), or null - match nothing - when the pattern is
    /// empty or any reference comes back null.</summary>
    public static string? ToRegex(string pattern, bool isRegex, Func<string, bool, string?> fragment)
    {
        pattern = pattern.Replace("\r\n", "\n");
        if (pattern.Length == 0) return null;
        var sb = new StringBuilder(pattern.Length + 16);
        if (!isRegex) sb.Append('^');
        for (int i = 0; i < pattern.Length; i++)
        {
            if (TryToken(pattern, i, out string name, out bool raw, out int end))
            {
                string? f = fragment(name, raw);
                if (f is null) return null;
                sb.Append(f);
                i = end;
                continue;
            }
            char c = pattern[i];
            if (!isRegex) { CompiledPattern.AppendWildcard(sb, c); continue; }
            sb.Append(c);
            if (c == '\\' && i + 1 < pattern.Length) sb.Append(pattern[++i]);   // \$ stays \$
        }
        if (!isRegex) sb.Append('$');
        return sb.ToString();
    }

    /// <summary>Fill references from live variables: unset or empty is null (no match),
    /// <c>${x}</c> the value escaped, <c>${!x}</c> the value as regex.</summary>
    public static Func<string, bool, string?> From(VariableStore vars) => (name, raw) =>
    {
        string? v = vars.Get(name);
        if (string.IsNullOrEmpty(v)) return null;
        return raw ? "(?:" + v + ")" : Regex.Escape(v);
    };

    /// <summary>Any value at all - for checking a pattern without the variables (the rule
    /// editor's test box, the up-front syntax check).</summary>
    public static readonly Func<string, bool, string?> AnyValue = (_, _) => "(?:.+?)";

    private static bool TryToken(string p, int i, out string name, out bool raw, out int end)
    {
        name = ""; raw = false; end = i;
        if (p[i] != '$' || i + 2 >= p.Length || p[i + 1] != '{') return false;
        int j = i + 2;
        if (p[j] == '!') { raw = true; j++; }
        int start = j;
        if (j >= p.Length || !(char.IsAsciiLetter(p[j]) || p[j] == '_')) return false;
        while (j < p.Length && (char.IsAsciiLetterOrDigit(p[j]) || p[j] == '_' || p[j] == '.')) j++;
        if (j >= p.Length || p[j] != '}') return false;
        name = p[start..j];
        end = j;
        return true;
    }
}

/// <summary>
/// A user rule's pattern as the automation engine holds it. Without variables it is compiled
/// once. With them it is recompiled when one of ITS variables changes value - checked against
/// <see cref="VariableStore.Version"/> first, so a store that did not move costs one compare.
/// </summary>
public sealed class RulePattern
{
    private readonly string _pattern;
    private readonly bool _isRegex, _ignoreCase, _multiLine;
    private readonly string[] _names;
    private readonly string?[] _values;
    private long _version = -1;
    private CompiledPattern _compiled;

    /// <exception cref="ArgumentException">The pattern is not a valid regex - whatever its
    /// variables hold - so it is refused when the rule is added, as before.</exception>
    public RulePattern(string pattern, bool isRegex, bool ignoreCase, bool multiLine = false)
    {
        _pattern = pattern ?? "";
        _isRegex = isRegex; _ignoreCase = ignoreCase; _multiLine = multiLine;
        _names = PatternVariables.Names(_pattern, isRegex).ToArray();
        _values = new string?[_names.Length];
        if (_names.Length == 0)
        {
            _compiled = new CompiledPattern(_pattern, isRegex, ignoreCase, multiLine, CompiledPattern.UserMatchTimeout);
            return;
        }
        CompiledPattern.WithVariables(_pattern, isRegex, ignoreCase, PatternVariables.AnyValue, multiLine);
        _compiled = CompiledPattern.Never;   // every value unknown until the first Current
    }

    public IReadOnlyList<string> Variables => _names;

    private string? _error;

    /// <summary>Why the last recompile failed - a <c>${!name}</c> value that is not valid
    /// regex - handed out once and then cleared, so it is reported once per bad value.</summary>
    public string? TakeError()
    {
        string? e = _error;
        _error = null;
        return e;
    }

    /// <summary>The pattern as it stands with the variables' values right now. When a
    /// <c>${!name}</c> value makes the regex invalid it matches nothing until that value
    /// changes, and <see cref="TakeError"/> says why.</summary>
    public CompiledPattern Current(VariableStore vars)
    {
        if (_names.Length == 0 || vars.Version == _version) return _compiled;
        _version = vars.Version;

        bool changed = false;
        for (int i = 0; i < _names.Length; i++)
        {
            string? v = vars.Get(_names[i]);
            if (string.Equals(v, _values[i], StringComparison.Ordinal)) continue;
            _values[i] = v;
            changed = true;
        }
        if (!changed) return _compiled;

        _error = null;
        try
        {
            _compiled = CompiledPattern.WithVariables(_pattern, _isRegex, _ignoreCase, PatternVariables.From(vars),
                                                      _multiLine, CompiledPattern.UserMatchTimeout);
        }
        catch (ArgumentException ex)
        {
            _compiled = CompiledPattern.Never;
            _error = ex.Message;
        }
        return _compiled;
    }
}

namespace Scrye.Core.Text;

/// <summary>
/// The inline colour markup plugins use anywhere the plugin API takes a display string
/// (<c>scrye.print</c>, <c>scrye.capture</c>, and any state a <c>text</c>/<c>table</c> widget
/// is bound to). It exists because <see cref="StyledRun"/> can already express per-run colour —
/// the engine renders MUD ANSI with it — but the plugin API had no way to say so, which is why
/// the ported MUSHclient plugins lost every <c>ColourNote</c> when they came across.
///
/// <para><b>Grammar.</b> <c>@{spec}</c> opens a style, <c>@{}</c> closes the innermost one, and
/// <c>@@</c> is a literal '@'. A spec is a colour, or a colour followed by comma-separated flags,
/// or flags alone (<c>@{,bold}</c> keeps the current colour). A colour is a
/// <see cref="Scrye.Core.Plugins.ThemeToken"/> name or a <c>#RRGGBB</c> literal; a background may
/// follow after '/' (<c>@{#FF2E88/#0B0420}</c>). Flags: <c>bold</c>, <c>underline</c>,
/// <c>italic</c>, <c>inverse</c>.</para>
///
/// <para><b>It never throws and never eats text.</b> Malformed markup renders literally rather
/// than vanishing: an unterminated <c>@{</c>, a stray <c>@</c>, or an unmatched <c>@{}</c> all
/// pass through as characters. An unknown colour name keeps the current colour (matching
/// ThemeToken's rule that unknown names fall back rather than render invisibly) — so a typo
/// costs you the colour, not the line.</para>
///
/// <para>Styles nest on a stack, so a caller can wrap a substring that already carries its own
/// colours without having to know what they were.</para>
/// </summary>
public static class Markup
{
    /// <summary>Longest theme-token name; guards the token scan against pathological input.</summary>
    private const int MaxSpecLength = 64;

    /// <summary>True if <paramref name="text"/> contains anything the parser would act on.
    /// Lets callers skip the parse (and the allocation) for the overwhelmingly common
    /// plain-string case.</summary>
    public static bool HasMarkup(string? text) => !string.IsNullOrEmpty(text) && text.IndexOf('@') >= 0;

    /// <summary>
    /// The text with all markup removed, for sinks that cannot show colour at all
    /// (<c>scrye.log</c>, <c>scrye.notify</c>, the mobile companion, trigger matching).
    /// Without this a plugin that colours a line would leak "@{accent}" into its log file.
    /// </summary>
    public static string Strip(string? text)
    {
        if (!HasMarkup(text)) return text ?? "";
        var sb = new System.Text.StringBuilder(text!.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (TryReadToken(text, i, out _, out int next)) { i = next; continue; }
            if (IsEscapedAt(text, i)) { sb.Append('@'); i += 2; continue; }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse <paramref name="text"/> into styled runs.
    /// </summary>
    /// <param name="text">The markup string. Null or empty yields an empty run list.</param>
    /// <param name="resolve">
    /// Resolves a colour word to a concrete colour: given a theme-token name (never a '#'
    /// literal — those are parsed here) it returns the scheme's colour, or null if the name is
    /// unknown. Hosts pass their theme lookup; pass <c>_ => null</c> for hex-only parsing.
    /// </param>
    /// <param name="baseFore">Colour for text outside any <c>@{...}</c>.</param>
    /// <param name="baseBack">Background for text outside any <c>@{...}</c>.</param>
    public static IReadOnlyList<StyledRun> Parse(
        string? text, Func<string, Rgb?>? resolve, Rgb? baseFore = null, Rgb? baseBack = null)
    {
        var runs = new List<StyledRun>();
        if (string.IsNullOrEmpty(text)) return runs;

        Style base_ = new(baseFore ?? Rgb.DefaultFore, baseBack ?? Rgb.DefaultBack, RunFlags.None);
        if (!HasMarkup(text))
        {
            runs.Add(new StyledRun(text, base_.Fore, base_.Back, base_.Flags));
            return runs;
        }

        var stack = new Stack<Style>();
        Style cur = base_;
        var buf = new System.Text.StringBuilder(text.Length);

        void Flush()
        {
            if (buf.Length == 0) return;
            // merge into the previous run when the style is identical, so "@{a}x@{}@{a}y@{}"
            // is one run rather than two — keeps run lists short for the renderer
            if (runs.Count > 0)
            {
                StyledRun p = runs[^1];
                if (p.Fore == cur.Fore && p.Back == cur.Back && p.Flags == cur.Flags && p.Link is null)
                {
                    runs[^1] = p with { Text = p.Text + buf };
                    buf.Clear();
                    return;
                }
            }
            runs.Add(new StyledRun(buf.ToString(), cur.Fore, cur.Back, cur.Flags));
            buf.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            if (IsEscapedAt(text, i)) { buf.Append('@'); i += 2; continue; }

            if (TryReadToken(text, i, out string spec, out int next))
            {
                Flush();
                if (spec.Length == 0)
                {
                    // "@{}" — close the innermost style; at the outermost level, reset to base
                    cur = stack.Count > 0 ? stack.Pop() : base_;
                }
                else
                {
                    stack.Push(cur);
                    cur = Apply(cur, spec, resolve);
                }
                i = next;
                continue;
            }

            buf.Append(text[i]);
            i++;
        }
        Flush();
        return runs;
    }

    /// <summary>Convenience: <see cref="Parse"/> wrapped in a <see cref="Line"/>.</summary>
    public static Line ToLine(
        string? text, Func<string, Rgb?>? resolve, Rgb? baseFore = null, Rgb? baseBack = null)
    {
        IReadOnlyList<StyledRun> runs = Parse(text, resolve, baseFore, baseBack);
        if (runs.Count == 0)
            runs = new[] { new StyledRun("", baseFore ?? Rgb.DefaultFore, baseBack ?? Rgb.DefaultBack, RunFlags.None) };
        return new Line(runs, isPrompt: false, DateTimeOffset.UtcNow);
    }

    // ---- internals ---------------------------------------------------------

    private readonly record struct Style(Rgb Fore, Rgb Back, RunFlags Flags);

    /// <summary>True when position <paramref name="i"/> starts an escaped literal "@@".</summary>
    private static bool IsEscapedAt(string s, int i) => s[i] == '@' && i + 1 < s.Length && s[i + 1] == '@';

    /// <summary>
    /// Read "@{...}" at <paramref name="i"/>. On success <paramref name="spec"/> is the text
    /// between the braces (possibly empty) and <paramref name="next"/> is the index just past
    /// the '}'. Anything malformed — no '{', no closing '}', or an over-long spec — is not a
    /// token, so the caller emits the characters literally.
    /// </summary>
    private static bool TryReadToken(string s, int i, out string spec, out int next)
    {
        spec = ""; next = i;
        if (s[i] != '@' || i + 1 >= s.Length || s[i + 1] != '{') return false;
        int close = s.IndexOf('}', i + 2);
        if (close < 0 || close - (i + 2) > MaxSpecLength) return false;
        spec = s[(i + 2)..close];
        next = close + 1;
        return true;
    }

    /// <summary>Apply one spec ("colour", "colour,flag,...", ",flag", "fg/bg") to the current style.</summary>
    private static Style Apply(Style cur, string spec, Func<string, Rgb?>? resolve)
    {
        Style s = cur;
        string[] parts = spec.Split(',');

        string colour = parts[0].Trim();
        if (colour.Length > 0)
        {
            int slash = colour.IndexOf('/');
            string fore = slash >= 0 ? colour[..slash].Trim() : colour;
            string back = slash >= 0 ? colour[(slash + 1)..].Trim() : "";
            if (fore.Length > 0 && TryColour(fore, resolve, out Rgb f)) s = s with { Fore = f };
            if (back.Length > 0 && TryColour(back, resolve, out Rgb b)) s = s with { Back = b };
        }

        for (int i = 1; i < parts.Length; i++)
        {
            RunFlags add = parts[i].Trim().ToLowerInvariant() switch
            {
                "bold" or "b"      => RunFlags.Bold,
                "underline" or "u" => RunFlags.Underline,
                "italic" or "i"    => RunFlags.Italic,
                "inverse" => RunFlags.Inverse,
                _ => RunFlags.None,          // unknown flag: ignored, like an unknown colour
            };
            s = s with { Flags = s.Flags | add };
        }
        return s;
    }

    /// <summary>A '#RRGGBB' literal, else whatever <paramref name="resolve"/> makes of the name.</summary>
    private static bool TryColour(string word, Func<string, Rgb?>? resolve, out Rgb rgb)
    {
        if (word.Length > 0 && word[0] == '#') return Rgb.TryParseHex(word, out rgb);
        Rgb? r = resolve?.Invoke(word);
        rgb = r ?? default;
        return r is not null;
    }
}

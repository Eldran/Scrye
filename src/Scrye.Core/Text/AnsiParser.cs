using System.Text;

namespace Scrye.Core.Text;

/// <summary>
/// Incremental ANSI + MXP parser. Feed decoded characters; it emits a <see cref="Line"/>
/// via <see cref="LineCompleted"/> on each newline (and on demand for prompts).
/// Handles SGR colour: reset, bold/underline/italic/blink/inverse, the 16 standard
/// colours, bright colours (90-97/100-107), xterm-256 (38;5;n / 48;5;n) and
/// truecolour (38;2;r;g;b / 48;2;r;g;b). Unknown escape sequences are swallowed.
///
/// MXP (core subset, active only when <see cref="MxpEnabled"/> is set by telnet
/// negotiation of option 91): line-security modes via <c>ESC[#z</c>; formatting tags
/// B/I/U/S/EM/STRONG, COLOR/C (name or #hex, push/pop), FONT (pass-through pop),
/// BR/SBR; secure-only link tags SEND (command, PROMPT= puts it in the input box)
/// and A (URL) whose runs carry a <see cref="LinkInfo"/>; VERSION/SUPPORT replies via
/// <see cref="MxpResponse"/>; entities (&amp;amp; &amp;lt; &amp;gt; &amp;quot; &amp;apos;
/// &amp;nbsp; &amp;#nn;). Unknown/unauthorised tags are stripped. A '&lt;' that never
/// closes within <see cref="MaxTagLength"/> chars (or hits a newline) is replayed as
/// literal text, so sloppy servers can't eat output.
/// </summary>
public sealed class AnsiParser
{
    private enum State { Normal, Esc, Csi, MxpTag, MxpEntity }

    private const int MaxTagLength = 512;
    private const int MaxEntityLength = 12;

    private readonly Func<DateTimeOffset> _clock;
    private State _state = State.Normal;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _text = new();
    private readonly List<StyledRun> _runs = new();

    private Rgb _fore = Rgb.DefaultFore;
    private Rgb _back = Rgb.DefaultBack;
    private RunFlags _flags = RunFlags.None;

    // ---- MXP state -----------------------------------------------------------
    private readonly StringBuilder _tag = new();
    private char _tagQuote;                        // active quote char inside a tag, or '\0'
    private readonly StringBuilder _entity = new();
    private readonly Stack<(Rgb fore, Rgb back)> _mxpColorStack = new();
    private LinkInfo? _mxpLink;                    // active <SEND>/<A> link, applied to runs
    private readonly StringBuilder _mxpLinkText = new();   // text inside the link (for &text; / defaults)
    private string? _mxpLinkRawAction;             // raw href/command (may contain &text;)
    private bool _mxpLinkIsUrl, _mxpLinkPrompt;
    private string? _mxpLinkHint;
    private int _mxpMode;                          // 0 open, 1 secure, 2 locked (this line)
    private int _mxpDefaultMode;                   // what mode resets to at each newline (5/6/7 locks)
    private bool _mxpTempSecure;                   // ESC[4z: next tag only

    public AnsiParser(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Raised for every completed line.</summary>
    public event Action<Line>? LineCompleted;

    /// <summary>Turn MXP tag interpretation on (set when telnet option 91 negotiates).
    /// Off (default) = byte-for-byte identical behaviour to the plain ANSI parser.</summary>
    public bool MxpEnabled { get; set; }

    /// <summary>A protocol reply the client must send to the server verbatim
    /// (responses to &lt;VERSION&gt; / &lt;SUPPORT&gt;). Includes trailing newline.</summary>
    public event Action<string>? MxpResponse;

    /// <summary>Client name/version used in the VERSION reply.</summary>
    public string ClientName { get; set; } = "Scrye";
    public string ClientVersion { get; set; } = "0.9";

    public void Feed(ReadOnlySpan<char> chars)
    {
        foreach (char c in chars)
            FeedChar(c);
    }

    /// <summary>Emit whatever text is buffered as a (prompt) line, e.g. on telnet GA/EOR.
    /// No-op if nothing is pending.</summary>
    public void FlushAsPrompt()
    {
        if (_text.Length > 0 || _runs.Count > 0)
            EmitLine(isPrompt: true);
    }

    private void FeedChar(char c)
    {
        switch (_state)
        {
            case State.Normal:
                if (c == '\x1b') _state = State.Esc;
                else if (c == '\n') EmitLine(isPrompt: false);
                else if (c == '\r') { /* ignore bare CR */ }
                else if (MxpEnabled && _mxpMode != 2 && c == '<') { _tag.Clear(); _tagQuote = '\0'; _state = State.MxpTag; }
                else if (MxpEnabled && _mxpMode != 2 && c == '&') { _entity.Clear(); _state = State.MxpEntity; }
                else AppendText(c);
                break;

            case State.Esc:
                if (c == '[') { _state = State.Csi; _params.Clear(); }
                else _state = State.Normal;   // other escapes: ignore the introducer
                break;

            case State.Csi:
                if ((c >= '0' && c <= '9') || c == ';')
                    _params.Append(c);
                else
                {
                    if (c == 'm') ApplySgr(_params.ToString());
                    else if (c == 'z' && MxpEnabled) ApplyMxpMode(_params.ToString());
                    // any other final byte (H, J, K, ...) is consumed and ignored for now
                    _state = State.Normal;
                }
                break;

            case State.MxpTag:
                if (_tagQuote != '\0')
                {
                    _tag.Append(c);
                    if (c == _tagQuote) _tagQuote = '\0';
                }
                else if (c == '"' || c == '\'') { _tag.Append(c); _tagQuote = c; }
                else if (c == '>')
                {
                    _state = State.Normal;
                    HandleMxpTag(_tag.ToString());
                }
                else if (c == '\n' || _tag.Length >= MaxTagLength)
                {
                    // not a real tag — replay as literal text so output is never eaten
                    AppendText('<');
                    foreach (char t in _tag.ToString()) AppendText(t);
                    _state = State.Normal;
                    if (c == '\n') EmitLine(isPrompt: false);
                    else AppendText(c);
                }
                else _tag.Append(c);
                break;

            case State.MxpEntity:
                if (c == ';')
                {
                    _state = State.Normal;
                    AppendEntity(_entity.ToString());
                }
                else if (!IsEntityChar(c) || _entity.Length >= MaxEntityLength)
                {
                    // not an entity — replay literally
                    AppendText('&');
                    foreach (char t in _entity.ToString()) AppendText(t);
                    _state = State.Normal;
                    FeedChar(c);
                }
                else _entity.Append(c);
                break;
        }
    }

    private static bool IsEntityChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '#';

    private void AppendText(char c)
    {
        _text.Append(c);
        if (_mxpLink is not null) _mxpLinkText.Append(c);
    }

    private void FlushRun()
    {
        if (_text.Length == 0) return;
        _runs.Add(new StyledRun(_text.ToString(), _fore, _back, _flags, _mxpLink));
        _text.Clear();
    }

    private void EmitLine(bool isPrompt)
    {
        if (_mxpLink is not null) CloseLink();   // a link never spans lines
        FlushRun();
        var line = new Line(_runs.ToArray(), isPrompt, _clock());
        _runs.Clear();
        LineCompleted?.Invoke(line);
        _mxpMode = _mxpDefaultMode;              // per-line security modes reset at newline
        _mxpTempSecure = false;
    }

    // ---- MXP: modes ----------------------------------------------------------

    private void ApplyMxpMode(string paramText)
    {
        int.TryParse(paramText.Length == 0 ? "0" : paramText, out int code);
        switch (code)
        {
            case 0: _mxpMode = 0; break;                    // open line
            case 1: _mxpMode = 1; break;                    // secure line
            case 2: _mxpMode = 2; break;                    // locked line
            case 3:                                          // reset: close open tags
                if (_mxpLink is not null) CloseLink();
                if (_mxpColorStack.Count > 0) { FlushRun(); (_fore, _back) = _mxpColorStack.ToArray()[^1]; _mxpColorStack.Clear(); }
                break;
            case 4: _mxpTempSecure = true; break;           // secure for the NEXT tag only
            case 5: _mxpDefaultMode = 0; _mxpMode = 0; break;   // lock open
            case 6: _mxpDefaultMode = 1; _mxpMode = 1; break;   // lock secure
            case 7: _mxpDefaultMode = 2; _mxpMode = 2; break;   // lock locked
        }
    }

    private bool InSecureMode()
    {
        if (_mxpTempSecure) return true;
        return _mxpMode == 1;
    }

    // ---- MXP: tags -----------------------------------------------------------

    private void HandleMxpTag(string content)
    {
        bool secure = InSecureMode();
        _mxpTempSecure = false;   // consumed by this tag either way

        content = content.Trim();
        if (content.Length == 0) return;

        bool closing = content.StartsWith('/');
        if (closing) content = content[1..].Trim();

        (string name, List<(string key, string val)> attrs) = ParseTag(content);
        name = name.ToUpperInvariant();

        if (closing) { HandleClose(name); return; }

        switch (name)
        {
            // ---- open-category formatting (allowed in open + secure modes) ----
            case "B": case "BOLD": case "STRONG": SetFlag(RunFlags.Bold, true); break;
            case "I": case "ITALIC": case "EM": SetFlag(RunFlags.Italic, true); break;
            case "U": case "UNDERLINE": SetFlag(RunFlags.Underline, true); break;
            case "S": case "STRIKEOUT": case "STRIKE": break;   // no strike flag — strip
            case "C": case "COLOR": case "COLOUR": PushColor(attrs); break;
            case "FONT": _mxpColorStack.Push((_fore, _back)); ApplyFontColor(attrs); break;   // pop on </FONT>
            case "BR": EmitLine(isPrompt: false); break;
            case "SBR": AppendText(' '); break;
            case "HR": break;                                   // strip

            // ---- secure-only tags --------------------------------------------
            case "SEND": if (secure) OpenLink(attrs, isUrl: false); break;
            case "A": if (secure) OpenLink(attrs, isUrl: true); break;
            case "VERSION":
                if (secure) MxpResponse?.Invoke($"\x1b[1z<VERSION MXP=1.0 CLIENT={ClientName} VERSION={ClientVersion}>\r\n");
                break;
            case "SUPPORT":
                if (secure) MxpResponse?.Invoke("\x1b[1z<SUPPORTS +send +a +b +bold +strong +i +italic +em +u +underline +s +color +c +font +br +sbr +version +support>\r\n");
                break;

            // everything else (custom elements, IMAGE, SOUND, GAUGE, ...): strip
        }
    }

    private void HandleClose(string name)
    {
        switch (name)
        {
            case "B": case "BOLD": case "STRONG": SetFlag(RunFlags.Bold, false); break;
            case "I": case "ITALIC": case "EM": SetFlag(RunFlags.Italic, false); break;
            case "U": case "UNDERLINE": SetFlag(RunFlags.Underline, false); break;
            case "S": case "STRIKEOUT": case "STRIKE": break;
            case "C": case "COLOR": case "COLOUR": case "FONT": PopColor(); break;
            case "SEND": case "A": if (_mxpLink is not null) CloseLink(); break;
        }
    }

    private void SetFlag(RunFlags flag, bool on)
    {
        FlushRun();
        if (on) _flags |= flag; else _flags &= ~flag;
    }

    // ---- MXP: colour ---------------------------------------------------------

    private void PushColor(List<(string key, string val)> attrs)
    {
        FlushRun();
        _mxpColorStack.Push((_fore, _back));
        // <COLOR fore [back]> or <COLOR FORE=x BACK=y>
        int positional = 0;
        foreach ((string key, string val) in attrs)
        {
            string k = key.ToUpperInvariant();
            if (k == "FORE" || (key.Length == 0 && positional == 0 && val.Length > 0))
            { if (TryParseColor(val, out Rgb c)) _fore = c; if (key.Length == 0) positional++; }
            else if (k == "BACK" || (key.Length == 0 && positional == 1))
            { if (TryParseColor(val, out Rgb c)) _back = c; if (key.Length == 0) positional++; }
        }
    }

    private void ApplyFontColor(List<(string key, string val)> attrs)
    {
        FlushRun();
        foreach ((string key, string val) in attrs)
        {
            string k = key.ToUpperInvariant();
            if (k == "COLOR" || k == "FORE") { if (TryParseColor(val, out Rgb c)) _fore = c; }
            else if (k == "BACK") { if (TryParseColor(val, out Rgb c)) _back = c; }
            // FACE / SIZE: monospace terminal — ignored
        }
    }

    private void PopColor()
    {
        if (_mxpColorStack.Count == 0) return;
        FlushRun();
        (_fore, _back) = _mxpColorStack.Pop();
    }

    private static bool TryParseColor(string value, out Rgb color)
    {
        color = default;
        string v = value.Trim().Trim('"', '\'');
        if (v.Length == 0) return false;
        if (v[0] == '#' && v.Length == 7 &&
            int.TryParse(v[1..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
        {
            color = new Rgb((byte)(hex >> 16), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));
            return true;
        }
        switch (v.ToLowerInvariant())
        {
            case "black": color = new Rgb(0, 0, 0); return true;
            case "red": color = new Rgb(0xCD, 0x00, 0x00); return true;
            case "green": color = new Rgb(0x00, 0xCD, 0x00); return true;
            case "yellow": color = new Rgb(0xCD, 0xCD, 0x00); return true;
            case "blue": color = new Rgb(0x1E, 0x90, 0xFF); return true;
            case "magenta": color = new Rgb(0xCD, 0x00, 0xCD); return true;
            case "cyan": color = new Rgb(0x00, 0xCD, 0xCD); return true;
            case "white": color = new Rgb(0xE5, 0xE5, 0xE5); return true;
            case "gray": case "grey": case "silver": color = new Rgb(0xC0, 0xC0, 0xC0); return true;
            case "maroon": color = new Rgb(0x80, 0x00, 0x00); return true;
            case "olive": color = new Rgb(0x80, 0x80, 0x00); return true;
            case "navy": color = new Rgb(0x00, 0x00, 0x80); return true;
            case "purple": color = new Rgb(0x80, 0x00, 0x80); return true;
            case "teal": color = new Rgb(0x00, 0x80, 0x80); return true;
            case "lime": color = new Rgb(0x00, 0xFF, 0x00); return true;
            case "aqua": color = new Rgb(0x00, 0xFF, 0xFF); return true;
            case "fuchsia": color = new Rgb(0xFF, 0x00, 0xFF); return true;
            case "orange": color = new Rgb(0xFF, 0xA5, 0x00); return true;
            default: return false;
        }
    }

    // ---- MXP: links ----------------------------------------------------------

    private void OpenLink(List<(string key, string val)> attrs, bool isUrl)
    {
        if (_mxpLink is not null) CloseLink();   // no nesting
        FlushRun();

        _mxpLinkRawAction = null;
        _mxpLinkIsUrl = isUrl;
        _mxpLinkPrompt = false;
        _mxpLinkHint = null;
        int positional = 0;
        foreach ((string key, string val) in attrs)
        {
            string k = key.ToUpperInvariant();
            if (k == "HREF" || (key.Length == 0 && positional == 0 && val.Length > 0))
            { _mxpLinkRawAction = Unquote(val); if (key.Length == 0) positional++; }
            else if (k == "HINT" || (key.Length == 0 && positional == 1))
            { _mxpLinkHint = Unquote(val); if (key.Length == 0) positional++; }
            else if (k == "PROMPT") _mxpLinkPrompt = true;
            // EXPIRE / other attrs: ignored in this pass
        }

        _mxpLinkText.Clear();
        // Placeholder LinkInfo — runs inside the link reference this instance; the real
        // action is patched in CloseLink() (so "&text;"/empty href can use the link text).
        _mxpLink = new LinkInfo("", _mxpLinkIsUrl, _mxpLinkPrompt, _mxpLinkHint);
    }

    private void CloseLink()
    {
        FlushRun();   // runs so far carry the placeholder link instance
        LinkInfo placeholder = _mxpLink!;
        _mxpLink = null;

        string text = _mxpLinkText.ToString();
        string action = string.IsNullOrEmpty(_mxpLinkRawAction) ? text : _mxpLinkRawAction!;
        action = action.Replace("&text;", text, StringComparison.OrdinalIgnoreCase);
        var resolved = placeholder with { Action = action };

        // patch already-flushed runs that reference the placeholder
        for (int i = 0; i < _runs.Count; i++)
            if (ReferenceEquals(_runs[i].Link, placeholder))
                _runs[i] = _runs[i] with { Link = resolved };
        _mxpLinkText.Clear();
        _mxpLinkRawAction = null;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) return s[1..^1];
        return s;
    }

    // ---- MXP: tag/attr + entity parsing --------------------------------------

    /// <summary>Splits "NAME attr attr=val attr='v v'" into the name + (key, value) pairs.
    /// Positional (keyless) attributes come back with an empty key, in order.</summary>
    private static (string name, List<(string key, string val)> attrs) ParseTag(string content)
    {
        var attrs = new List<(string, string)>();
        int i = 0;
        string name = ReadToken(content, ref i, out _);
        while (i < content.Length)
        {
            while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
            if (i >= content.Length) break;
            string tok = ReadToken(content, ref i, out bool quoted);
            if (tok.Length == 0) continue;
            if (!quoted && i < content.Length && content[i] == '=')
            {
                i++;   // skip '='
                string val = ReadToken(content, ref i, out _);
                attrs.Add((tok, val));
            }
            else if (!quoted && tok.Contains('='))
            {
                int eq = tok.IndexOf('=');
                attrs.Add((tok[..eq], tok[(eq + 1)..]));
            }
            else attrs.Add(("", tok));
        }
        return (name, attrs);
    }

    private static string ReadToken(string s, ref int i, out bool quoted)
    {
        quoted = false;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) return "";
        if (s[i] == '"' || s[i] == '\'')
        {
            char q = s[i++];
            quoted = true;
            int start = i;
            while (i < s.Length && s[i] != q) i++;
            string inner = s[start..i];
            if (i < s.Length) i++;   // closing quote
            return inner;
        }
        int begin = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=') i++;
        return s[begin..i];
    }

    private void AppendEntity(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "amp": AppendText('&'); break;
            case "lt": AppendText('<'); break;
            case "gt": AppendText('>'); break;
            case "quot": AppendText('"'); break;
            case "apos": AppendText('\''); break;
            case "nbsp": AppendText(' '); break;
            default:
                if (name.Length > 1 && name[0] == '#' && int.TryParse(name[1..], out int code) &&
                    code > 0 && code <= 0x10FFFF)
                {
                    foreach (char c in char.ConvertFromUtf32(code)) AppendText(c);
                }
                else
                {
                    // unknown entity — replay literally
                    AppendText('&');
                    foreach (char c in name) AppendText(c);
                    AppendText(';');
                }
                break;
        }
    }

    // ---- SGR (unchanged) -----------------------------------------------------

    private void ApplySgr(string paramText)
    {
        FlushRun();   // style change starts a new run

        // empty "\x1b[m" means reset
        int[] codes = ParseCodes(paramText);
        for (int i = 0; i < codes.Length; i++)
        {
            int code = codes[i];
            switch (code)
            {
                case 0: _fore = Rgb.DefaultFore; _back = Rgb.DefaultBack; _flags = RunFlags.None; break;
                case 1: _flags |= RunFlags.Bold; break;
                case 3: _flags |= RunFlags.Italic; break;
                case 4: _flags |= RunFlags.Underline; break;
                case 5: _flags |= RunFlags.Blink; break;
                case 7: _flags |= RunFlags.Inverse; break;
                case 22: _flags &= ~RunFlags.Bold; break;
                case 23: _flags &= ~RunFlags.Italic; break;
                case 24: _flags &= ~RunFlags.Underline; break;
                case 25: _flags &= ~RunFlags.Blink; break;
                case 27: _flags &= ~RunFlags.Inverse; break;
                case >= 30 and <= 37: _fore = Rgb.Ansi16(code - 30, (_flags & RunFlags.Bold) != 0); break;
                case 38: _fore = ReadExtended(codes, ref i) ?? _fore; break;
                case 39: _fore = Rgb.DefaultFore; break;
                case >= 40 and <= 47: _back = Rgb.Ansi16(code - 40, false); break;
                case 48: _back = ReadExtended(codes, ref i) ?? _back; break;
                case 49: _back = Rgb.DefaultBack; break;
                case >= 90 and <= 97: _fore = Rgb.Ansi16(code - 90, true); break;
                case >= 100 and <= 107: _back = Rgb.Ansi16(code - 100, true); break;
            }
        }
    }

    /// <summary>Consumes a 38/48 extended-colour subsequence: 5;n (256) or 2;r;g;b (truecolour).</summary>
    private static Rgb? ReadExtended(int[] codes, ref int i)
    {
        if (i + 1 >= codes.Length) return null;
        int mode = codes[i + 1];
        if (mode == 5 && i + 2 < codes.Length)
        {
            int n = codes[i + 2];
            i += 2;
            return Rgb.Xterm256(Math.Clamp(n, 0, 255));
        }
        if (mode == 2 && i + 4 < codes.Length)
        {
            byte r = (byte)Math.Clamp(codes[i + 2], 0, 255);
            byte g = (byte)Math.Clamp(codes[i + 3], 0, 255);
            byte b = (byte)Math.Clamp(codes[i + 4], 0, 255);
            i += 4;
            return new Rgb(r, g, b);
        }
        return null;
    }

    private static int[] ParseCodes(string paramText)
    {
        if (paramText.Length == 0) return new[] { 0 };
        string[] parts = paramText.Split(';');
        var codes = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            codes[i] = int.TryParse(parts[i], out int v) ? v : 0;
        return codes;
    }
}

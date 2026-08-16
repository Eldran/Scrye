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
    private const int MaxEntityLength = 32;

    // Custom <!ELEMENT>/<!ENTITY> definitions are a small template language driven by a
    // remote server, so every dimension is bounded. These are generous for real MUDs and
    // cheap insurance against a hostile or broken one.
    private const int MaxDefinitions = 256;    // elements, and entities, each
    private const int MaxExpandDepth = 4;      // an element whose body uses another element
    private const int MaxExpandLength = 4096;  // total expanded text from one tag

    /// <summary>A custom element defined by <c>&lt;!ELEMENT name '&lt;send ...&gt;' ATT='a b'&gt;</c>.</summary>
    private sealed record MxpElement(string Definition, IReadOnlyList<string> Attributes, bool Open, bool Empty);

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
    private string? _mxpLinkExpire;                // EXPIRE group name ("" = the unnamed group)

    // <VAR>, <DEST>, <GAUGE> and custom <!ELEMENT>/<!ENTITY> state
    private string? _mxpVarName;                   // open <VAR name>: collect text until </VAR>
    private readonly StringBuilder _mxpVarText = new();
    private string? _mxpDest;                      // open <DEST pane>: lines route there
    private string? _mxpDestThisLine;              // a DEST opened on this line, even if already closed
    private readonly Dictionary<string, string> _mxpEntities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MxpElement> _mxpElements = new(StringComparer.OrdinalIgnoreCase);
    private int _mxpExpandDepth;                   // guards recursive element/entity expansion

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

    /// <summary>A server-set MXP variable: <c>&lt;VAR name&gt;value&lt;/VAR&gt;</c>. Secure-only.
    /// The host decides where these land; they are deliberately NOT written straight into the
    /// user's own variables, so a MUD cannot redefine one their aliases depend on.</summary>
    public event Action<string, string>? MxpVariable;

    /// <summary>A server-set gauge: name, value, max (0 when absent) and caption.</summary>
    public event Action<string, double, double, string>? MxpGauge;

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
        // <VAR name>value</VAR>: the value is the tag's text, so tee it while one is open.
        if (_mxpVarName is not null && _mxpVarText.Length < MaxExpandLength) _mxpVarText.Append(c);
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
        var line = new Line(_runs.ToArray(), isPrompt, _clock(), _mxpDest ?? _mxpDestThisLine);
        _mxpDestThisLine = null;   // the latch covers one line; an open <DEST> carries itself
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

        // <!ELEMENT ...> / <!ENTITY ...> definitions. Secure-only: a definition is a
        // standing instruction to reinterpret later text, which is exactly the authority
        // an open line must not have.
        if (content.StartsWith('!'))
        {
            if (secure) HandleDefinition(content[1..].TrimStart());
            return;
        }

        (string name, List<(string key, string val)> attrs) = ParseTag(content);
        name = name.ToUpperInvariant();

        if (closing)
        {
            // a custom element's closing tag ends whatever its definition opened
            if (_mxpElements.ContainsKey(name)) { ExpandElement(name, null, closing: true); return; }
            HandleClose(name);
            return;
        }

        // a custom element expands to its definition, then falls through the normal path
        if (_mxpElements.ContainsKey(name)) { ExpandElement(name, attrs, closing: false); return; }

        switch (name)
        {
            // ---- open-category formatting (allowed in open + secure modes) ----
            case "B": case "BOLD": case "STRONG": SetFlag(RunFlags.Bold, true); break;
            case "I": case "ITALIC": case "EM": SetFlag(RunFlags.Italic, true); break;
            case "U": case "UNDERLINE": SetFlag(RunFlags.Underline, true); break;
            case "S": case "STRIKEOUT": case "STRIKE": SetFlag(RunFlags.Strikeout, true); break;
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
                // Advertise only what is actually implemented. This used to claim +s while
                // <S> was stripped, so a server could use strikeout to mean "destroyed" and
                // the meaning would vanish silently.
                if (secure) MxpResponse?.Invoke(
                    "\x1b[1z<SUPPORTS +send +a +b +bold +strong +i +italic +em +u +underline +s +strikeout"
                    + " +color +c +font +br +sbr +hr +var +dest +gauge +version +support>\r\n");
                break;

            case "VAR":
                // Open a capture: the value is the tag's TEXT, closed by </VAR>.
                if (secure) { _mxpVarName = FirstValue(attrs); _mxpVarText.Clear(); }
                break;

            case "DEST":
                // Route the enclosed lines to a named capture pane -- the same panes triggers
                // and plugins write to, so nothing new has to be built to display them.
                // Latch it for the current line as well: <DEST chat>text</DEST> closes before
                // the newline arrives, so the line would otherwise emit with no destination.
                if (secure) { _mxpDest = FirstValue(attrs); _mxpDestThisLine ??= _mxpDest; }
                break;

            case "GAUGE": if (secure) HandleGauge(attrs); break;

            // IMAGE / SOUND / FRAME and anything unrecognised: stripped on purpose.
        }
    }

    private void HandleClose(string name)
    {
        switch (name)
        {
            case "B": case "BOLD": case "STRONG": SetFlag(RunFlags.Bold, false); break;
            case "I": case "ITALIC": case "EM": SetFlag(RunFlags.Italic, false); break;
            case "U": case "UNDERLINE": SetFlag(RunFlags.Underline, false); break;
            case "S": case "STRIKEOUT": case "STRIKE": SetFlag(RunFlags.Strikeout, false); break;
            case "C": case "COLOR": case "COLOUR": case "FONT": PopColor(); break;
            case "SEND": case "A": if (_mxpLink is not null) CloseLink(); break;
            case "VAR":
                if (_mxpVarName is not null)
                {
                    MxpVariable?.Invoke(_mxpVarName, _mxpVarText.ToString());
                    _mxpVarName = null; _mxpVarText.Clear();
                }
                break;
            case "DEST": _mxpDest = null; break;
        }
    }

    /// <summary>
    /// <c>&lt;!ELEMENT name '&lt;send href="..."&gt;' ATT='a b' OPEN EMPTY&gt;</c> and
    /// <c>&lt;!ENTITY name "value"&gt;</c>.
    ///
    /// <para>This is the one place a server gets to define new vocabulary, so it is bounded on
    /// every axis (<see cref="MaxDefinitions"/>, <see cref="MaxExpandDepth"/>,
    /// <see cref="MaxExpandLength"/>) and refuses to redefine a built-in tag — otherwise a MUD
    /// could redefine B or SEND and change what every later line means.</para>
    /// </summary>
    private void HandleDefinition(string content)
    {
        bool isEntity = content.StartsWith("ENTITY", StringComparison.OrdinalIgnoreCase);
        bool isElement = content.StartsWith("ELEMENT", StringComparison.OrdinalIgnoreCase)
                      || content.StartsWith("EL", StringComparison.OrdinalIgnoreCase);
        if (!isEntity && !isElement) return;

        int i = 0;
        ReadToken(content, ref i, out _);                       // the ELEMENT/ENTITY keyword
        string name = ReadToken(content, ref i, out _);
        if (name.Length == 0 || IsBuiltInTag(name)) return;

        if (isEntity)
        {
            if (_mxpEntities.Count >= MaxDefinitions && !_mxpEntities.ContainsKey(name)) return;
            string val = ReadToken(content, ref i, out _);
            if (val.Equals("DELETE", StringComparison.OrdinalIgnoreCase)) { _mxpEntities.Remove(name); return; }
            _mxpEntities[name] = Unquote(val);
            return;
        }

        if (_mxpElements.Count >= MaxDefinitions && !_mxpElements.ContainsKey(name)) return;
        string definition = ReadToken(content, ref i, out bool quoted);
        if (!quoted && definition.Length == 0) return;

        var atts = new List<string>();
        bool open = false, empty = false;
        while (i < content.Length)
        {
            string tok = ReadToken(content, ref i, out _);
            if (tok.Length == 0) break;
            if (tok.Equals("OPEN", StringComparison.OrdinalIgnoreCase)) { open = true; continue; }
            if (tok.Equals("EMPTY", StringComparison.OrdinalIgnoreCase)) { empty = true; continue; }
            if (tok.Equals("DELETE", StringComparison.OrdinalIgnoreCase)) { _mxpElements.Remove(name); return; }
            if (tok.Equals("ATT", StringComparison.OrdinalIgnoreCase))
            {
                if (i < content.Length && content[i] == '=') i++;
                string list = ReadToken(content, ref i, out _);
                foreach (string a in Unquote(list).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // "name=default" -- only the name matters for substitution
                    int eq = a.IndexOf('=');
                    atts.Add(eq > 0 ? a[..eq] : a);
                    if (atts.Count >= 32) break;
                }
            }
        }
        _mxpElements[name] = new MxpElement(Unquote(definition), atts, open, empty);
    }

    /// <summary>Replay a custom element's definition through the tag handler, substituting
    /// its attributes. Depth-bounded, because a definition may itself use another element.</summary>
    private void ExpandElement(string name, List<(string key, string val)>? attrs, bool closing)
    {
        if (!_mxpElements.TryGetValue(name, out MxpElement? el)) return;
        if (_mxpExpandDepth >= MaxExpandDepth) return;

        if (closing)
        {
            // close whatever the definition opened, in reverse
            _mxpExpandDepth++;
            foreach (string tag in TagsIn(el.Definition).Reverse())
                if (!tag.StartsWith('/')) HandleClose(TagNameOf(tag).ToUpperInvariant());
            _mxpExpandDepth--;
            return;
        }

        string body = el.Definition;
        // positional and named attribute substitution: &name; inside the definition
        if (attrs is not null && el.Attributes.Count > 0)
        {
            int positional = 0;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string val) in attrs)
            {
                if (key.Length > 0) values[key] = Unquote(val);
                else if (positional < el.Attributes.Count) values[el.Attributes[positional++]] = Unquote(val);
            }
            foreach (string att in el.Attributes)
            {
                string v = values.TryGetValue(att, out string? got) ? got : "";
                body = body.Replace("&" + att + ";", v, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (body.Length > MaxExpandLength) return;

        // The definition is markup: run each tag in it through the normal handler. An element
        // defined as OPEN is usable on an open line; otherwise its tags need secure, which
        // they have, because a definition can only be made on a secure line.
        _mxpExpandDepth++;
        bool savedTemp = _mxpTempSecure;
        foreach (string tag in TagsIn(body))
        {
            _mxpTempSecure = true;          // the definition itself was trusted when defined
            HandleMxpTag(tag);
        }
        _mxpTempSecure = savedTemp;
        _mxpExpandDepth--;
    }

    private static IEnumerable<string> TagsIn(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int lt = s.IndexOf('<', i);
            if (lt < 0) yield break;
            int gt = s.IndexOf('>', lt + 1);
            if (gt < 0) yield break;
            yield return s[(lt + 1)..gt];
            i = gt + 1;
        }
    }

    private static string TagNameOf(string tag)
    {
        int i = 0;
        return ReadToken(tag.TrimStart('/'), ref i, out _);
    }

    private static bool IsBuiltInTag(string name) => name.ToUpperInvariant() switch
    {
        "B" or "BOLD" or "STRONG" or "I" or "ITALIC" or "EM" or "U" or "UNDERLINE"
        or "S" or "STRIKEOUT" or "STRIKE" or "C" or "COLOR" or "COLOUR" or "FONT"
        or "BR" or "SBR" or "HR" or "SEND" or "A" or "VERSION" or "SUPPORT"
        or "VAR" or "DEST" or "GAUGE" => true,
        _ => false,
    };

    /// <summary>First attribute value, named or positional — the shape <c>&lt;VAR hp&gt;</c> and
    /// <c>&lt;DEST chat&gt;</c> both use.</summary>
    private static string? FirstValue(List<(string key, string val)> attrs)
    {
        foreach ((string key, string val) in attrs)
            if (val.Length > 0) return Unquote(val);
        return null;
    }

    /// <summary>&lt;GAUGE value max caption&gt; — positional or named. Reported through
    /// <see cref="MxpGauge"/>; the host decides where to put it.</summary>
    private void HandleGauge(List<(string key, string val)> attrs)
    {
        string name = "", caption = ""; double value = 0, max = 0;
        int positional = 0;
        foreach ((string key, string val) in attrs)
        {
            string k = key.ToUpperInvariant();
            string v = Unquote(val);
            if (k == "MAX") { double.TryParse(v, System.Globalization.NumberStyles.Any,
                                              System.Globalization.CultureInfo.InvariantCulture, out max); }
            else if (k == "CAPTION") caption = v;
            else if (k == "VALUE") { name = v; }
            else if (key.Length == 0)
            {
                switch (positional++)
                {
                    case 0: name = v; break;
                    case 1: double.TryParse(v, System.Globalization.NumberStyles.Any,
                                            System.Globalization.CultureInfo.InvariantCulture, out max); break;
                    case 2: caption = v; break;
                }
            }
        }
        if (name.Length == 0) return;
        // The first argument is an entity NAME in the spec: resolve it if we know it, so
        // "<GAUGE hp max=maxhp>" works after "<!ENTITY hp 100>".
        value = ResolveNumber(name);
        if (max == 0 && _mxpEntities.Count > 0) max = ResolveNumber("max" + name);
        MxpGauge?.Invoke(name, value, max, caption);
    }

    private double ResolveNumber(string nameOrLiteral)
    {
        if (double.TryParse(nameOrLiteral, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double direct)) return direct;
        if (_mxpEntities.TryGetValue(nameOrLiteral, out string? ent)
            && double.TryParse(ent, System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)) return v;
        return 0;
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

            // Valueless flags arrive from ParseTag as ("", "PROMPT") — the same shape as a
            // positional value. Match them by name FIRST, or `<SEND href="say " PROMPT>`
            // takes "PROMPT" as its positional href and the real command is silently lost.
            if (key.Length == 0 && val.Equals("PROMPT", StringComparison.OrdinalIgnoreCase))
            { _mxpLinkPrompt = true; continue; }
            if (key.Length == 0 && val.Equals("EXPIRE", StringComparison.OrdinalIgnoreCase))
            { _mxpLinkExpire = ""; continue; }                       // bare EXPIRE = the unnamed group

            if (k == "HREF" || (key.Length == 0 && positional == 0 && val.Length > 0))
            { _mxpLinkRawAction = Unquote(val); if (key.Length == 0) positional++; }
            else if (k == "HINT" || (key.Length == 0 && positional == 1))
            { _mxpLinkHint = Unquote(val); if (key.Length == 0) positional++; }
            else if (k == "PROMPT") _mxpLinkPrompt = true;
            else if (k == "EXPIRE") _mxpLinkExpire = Unquote(val);
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
        // Deliberately does NOT trim. ParseTag has already stripped the quotes from a quoted
        // value, so trimming here would eat whitespace the server meant: href="say " is a
        // prompt prefix, and "say" without the space is a different command.
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
                else if (_mxpEntities.TryGetValue(name, out string? custom))
                {
                    // a server-defined <!ENTITY>. Its value is text, not markup: expanding it
                    // through the tag handler would let an entity smuggle a <SEND> onto a line
                    // the server did not mark secure.
                    if (custom.Length <= MaxExpandLength)
                        foreach (char c in custom) AppendText(c);
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

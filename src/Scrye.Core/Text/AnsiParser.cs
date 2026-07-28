using System.Text;

namespace Scrye.Core.Text;

/// <summary>
/// Incremental ANSI parser. Feed decoded characters; it emits a <see cref="Line"/>
/// via <see cref="LineCompleted"/> on each newline (and on demand for prompts).
/// Handles SGR colour: reset, bold/underline/italic/blink/inverse, the 16 standard
/// colours, bright colours (90-97/100-107), xterm-256 (38;5;n / 48;5;n) and
/// truecolour (38;2;r;g;b / 48;2;r;g;b). Unknown escape sequences are swallowed.
/// Deliberately small — the MXP layer will wrap this later.
/// </summary>
public sealed class AnsiParser
{
    private enum State { Normal, Esc, Csi }

    private readonly Func<DateTimeOffset> _clock;
    private State _state = State.Normal;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _text = new();
    private readonly List<StyledRun> _runs = new();

    private Rgb _fore = Rgb.DefaultFore;
    private Rgb _back = Rgb.DefaultBack;
    private RunFlags _flags = RunFlags.None;

    public AnsiParser(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Raised for every completed line.</summary>
    public event Action<Line>? LineCompleted;

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
                else _text.Append(c);
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
                    // any other final byte (H, J, K, ...) is consumed and ignored for now
                    _state = State.Normal;
                }
                break;
        }
    }

    private void FlushRun()
    {
        if (_text.Length == 0) return;
        _runs.Add(new StyledRun(_text.ToString(), _fore, _back, _flags));
        _text.Clear();
    }

    private void EmitLine(bool isPrompt)
    {
        FlushRun();
        var line = new Line(_runs.ToArray(), isPrompt, _clock());
        _runs.Clear();
        LineCompleted?.Invoke(line);
    }

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

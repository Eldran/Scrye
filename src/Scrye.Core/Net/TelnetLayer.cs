using System.Text;

namespace Scrye.Core.Net;

/// <summary>
/// Telnet IAC state machine with real option handling. Separates in-band data
/// from telnet control, negotiates the options Scrye supports, and surfaces
/// out-of-band protocols as events. Responses to send back to the server are
/// raised via <see cref="SendData"/> (raw bytes, no newline). Unsupported
/// options are politely refused (WILL->DONT, DO->WONT).
///
/// Supported: ECHO (input masking), SGA, TTYPE/MTTS (terminal type), NAWS
/// (window size), CHARSET (UTF-8), MSSP (server status), GMCP (out-of-band JSON).
/// MCCP (compression) is intentionally refused for now — see the worklog.
/// </summary>
public sealed class TelnetLayer
{
    // commands
    private const byte IAC = 255, SE = 240, SB = 250, WILL = 251, WONT = 252, DO = 253, DONT = 254;
    // options
    private const byte GA = 249, EOR = 239;   // prompt markers (Go-Ahead / End-Of-Record)
    private const byte OPT_ECHO = 1, OPT_SGA = 3, OPT_TTYPE = 24, OPT_NAWS = 31,
                       OPT_CHARSET = 42, OPT_MSSP = 70, OPT_MCCP2 = 86, OPT_GMCP = 201;
    // sub-negotiation markers
    private const byte TTYPE_IS = 0, TTYPE_SEND = 1;
    private const byte CHARSET_REQUEST = 1, CHARSET_ACCEPTED = 2, CHARSET_REJECTED = 3;
    private const byte MSSP_VAR = 1, MSSP_VAL = 2;

    private enum P { Data, Iac, Will, Wont, Do, Dont, Sb, SbData, SbIac }

    private P _state = P.Data;
    private byte _sbOption;
    private readonly List<byte> _sb = new();
    private int _ttypeIndex;

    /// <summary>Bytes to send to the server (negotiation replies, subnegotiations). Raw — no newline.</summary>
    public event Action<byte[]>? SendData;
    /// <summary>An out-of-band GMCP message: (package, json-or-empty).</summary>
    public event Action<string, string>? GmcpReceived;
    /// <summary>Parsed MSSP server-status variables.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? MsspReceived;
    /// <summary>Server ECHO state changed. true = server echoes (client should mask local input).</summary>
    public event Action<bool>? ServerEchoChanged;
    /// <summary>A prompt marker (IAC GA / IAC EOR) — flush any buffered prompt line.</summary>
    public event Action? GoAhead;

    /// <summary>Supplies the current terminal size for NAWS.</summary>
    public Func<(int cols, int rows)>? WindowSize { get; set; }

    public string ClientName { get; set; } = "Scrye";
    public string TerminalType { get; set; } = "XTERM-256COLOR";
    /// <summary>MTTS bitmask: ANSI(1) | UTF-8(4) | 256-colour(8) | truecolour(256) = 269.</summary>
    public int MttsBitmask { get; set; } = 269;

    /// <summary>Process a chunk of raw bytes; returns the in-band data (IAC stripped).
    /// Any replies are raised via <see cref="SendData"/>.</summary>
    public byte[] Process(ReadOnlySpan<byte> input)
    {
        var data = new List<byte>(input.Length);
        foreach (byte b in input)
            Step(b, data);
        return data.ToArray();
    }

    /// <summary>Send an out-of-band GMCP message to the server.</summary>
    public void SendGmcp(string package, string json)
    {
        string msg = string.IsNullOrEmpty(json) ? package : package + " " + json;
        SendSub(OPT_GMCP, Encoding.UTF8.GetBytes(msg));
    }

    /// <summary>Re-send the current window size (call on terminal resize).</summary>
    public void SendWindowSize() => SendNaws();

    private void Step(byte b, List<byte> data)
    {
        switch (_state)
        {
            case P.Data:
                if (b == IAC) _state = P.Iac;
                else data.Add(b);
                break;

            case P.Iac:
                switch (b)
                {
                    case IAC: data.Add(IAC); _state = P.Data; break;   // escaped 0xFF
                    case WILL: _state = P.Will; break;
                    case WONT: _state = P.Wont; break;
                    case DO: _state = P.Do; break;
                    case DONT: _state = P.Dont; break;
                    case SB: _state = P.Sb; break;
                    case GA: case EOR: GoAhead?.Invoke(); _state = P.Data; break;
                    default: _state = P.Data; break;                    // NOP/other: ignore
                }
                break;

            case P.Will: OnWill(b); _state = P.Data; break;
            case P.Wont: OnWont(b); _state = P.Data; break;
            case P.Do: OnDo(b); _state = P.Data; break;
            case P.Dont: _state = P.Data; break;                        // ack only; no reply (avoid loops)

            case P.Sb: _sbOption = b; _sb.Clear(); _state = P.SbData; break;
            case P.SbData:
                if (b == IAC) _state = P.SbIac;
                else _sb.Add(b);
                break;
            case P.SbIac:
                if (b == SE) { OnSubnegotiation(_sbOption, _sb); _state = P.Data; }
                else { _sb.Add(b); _state = P.SbData; }                 // IAC IAC inside SB -> literal
                break;
        }
    }

    // ---- server offers to enable an option on ITS side (WILL) ----------------
    private void OnWill(byte option)
    {
        switch (option)
        {
            case OPT_ECHO: SendCmd(DO, option); ServerEchoChanged?.Invoke(true); break;
            case OPT_SGA: SendCmd(DO, option); break;
            case OPT_MSSP: SendCmd(DO, option); break;
            case OPT_GMCP: SendCmd(DO, option); break;
            case OPT_CHARSET: SendCmd(DO, option); break;
            case OPT_MCCP2: SendCmd(DONT, option); break;               // deferred: refuse compression
            default: SendCmd(DONT, option); break;
        }
    }

    private void OnWont(byte option)
    {
        if (option == OPT_ECHO) ServerEchoChanged?.Invoke(false);
        // no reply for WONT (avoids negotiation loops)
    }

    // ---- server asks US to enable an option on OUR side (DO) ------------------
    private void OnDo(byte option)
    {
        switch (option)
        {
            case OPT_TTYPE: SendCmd(WILL, option); break;
            case OPT_SGA: SendCmd(WILL, option); break;
            case OPT_CHARSET: SendCmd(WILL, option); break;
            case OPT_NAWS: SendCmd(WILL, option); SendNaws(); break;
            default: SendCmd(WONT, option); break;
        }
    }

    private void OnSubnegotiation(byte option, List<byte> payload)
    {
        switch (option)
        {
            case OPT_TTYPE:
                if (payload.Count >= 1 && payload[0] == TTYPE_SEND) SendTerminalType();
                break;
            case OPT_CHARSET:
                HandleCharset(payload);
                break;
            case OPT_MSSP:
                MsspReceived?.Invoke(ParseMssp(payload));
                break;
            case OPT_GMCP:
                HandleGmcp(payload);
                break;
        }
    }

    // ---- protocol handlers ---------------------------------------------------

    private void SendTerminalType()
    {
        string name = _ttypeIndex switch
        {
            0 => ClientName,
            1 => TerminalType,
            _ => "MTTS " + MttsBitmask,
        };
        var payload = new List<byte> { TTYPE_IS };
        payload.AddRange(Encoding.ASCII.GetBytes(name));
        SendSub(OPT_TTYPE, payload.ToArray());
        if (_ttypeIndex < 2) _ttypeIndex++;
    }

    private void SendNaws()
    {
        (int cols, int rows) = WindowSize?.Invoke() ?? (80, 24);
        cols = Math.Clamp(cols, 1, 65535);
        rows = Math.Clamp(rows, 1, 65535);
        byte[] payload =
        {
            (byte)(cols >> 8), (byte)(cols & 0xFF),
            (byte)(rows >> 8), (byte)(rows & 0xFF),
        };
        SendSub(OPT_NAWS, payload);
    }

    private void HandleCharset(List<byte> payload)
    {
        // REQUEST <sep> name<sep>name...
        if (payload.Count < 2 || payload[0] != CHARSET_REQUEST) return;
        char sep = (char)payload[1];
        string rest = Encoding.ASCII.GetString(payload.ToArray(), 2, payload.Count - 2);
        string? utf8 = rest.Split(sep).FirstOrDefault(s => s.Trim().Equals("UTF-8", StringComparison.OrdinalIgnoreCase));

        if (utf8 is not null)
        {
            var reply = new List<byte> { CHARSET_ACCEPTED };
            reply.AddRange(Encoding.ASCII.GetBytes(utf8.Trim()));
            SendSub(OPT_CHARSET, reply.ToArray());
        }
        else
        {
            SendSub(OPT_CHARSET, new[] { CHARSET_REJECTED });
        }
    }

    private static Dictionary<string, string> ParseMssp(List<byte> payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < payload.Count)
        {
            if (payload[i] != MSSP_VAR) { i++; continue; }
            i++;
            var name = new StringBuilder();
            while (i < payload.Count && payload[i] != MSSP_VAL) name.Append((char)payload[i++]);
            if (i < payload.Count) i++; // skip VAL
            var val = new StringBuilder();
            while (i < payload.Count && payload[i] != MSSP_VAR) val.Append((char)payload[i++]);
            if (name.Length > 0) result[name.ToString()] = val.ToString();
        }
        return result;
    }

    private void HandleGmcp(List<byte> payload)
    {
        string text = Encoding.UTF8.GetString(payload.ToArray());
        int space = text.IndexOf(' ');
        if (space < 0) GmcpReceived?.Invoke(text, "");
        else GmcpReceived?.Invoke(text[..space], text[(space + 1)..]);
    }

    // ---- low-level send ------------------------------------------------------

    private void SendCmd(byte command, byte option) => SendData?.Invoke(new[] { IAC, command, option });

    private void SendSub(byte option, byte[] payload)
    {
        var buf = new List<byte>(payload.Length + 5) { IAC, SB, option };
        foreach (byte b in payload)
        {
            buf.Add(b);
            if (b == IAC) buf.Add(IAC);   // escape IAC in payload
        }
        buf.Add(IAC);
        buf.Add(SE);
        SendData?.Invoke(buf.ToArray());
    }
}

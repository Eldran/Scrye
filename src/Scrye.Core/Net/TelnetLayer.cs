namespace Scrye.Core.Net;

/// <summary>
/// Minimal telnet IAC state machine. Separates in-band data bytes from telnet
/// control sequences. For the skeleton it politely REFUSES every option
/// (WILL→DONT, DO→WONT) and swallows subnegotiations — enough to keep servers
/// happy and get a clean text stream. Real option handlers (MCCP, GMCP, MSSP,
/// MTTS, CHARSET, NAWS…) plug in here later via an ITelnetOption registry.
/// </summary>
public sealed class TelnetLayer
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;

    private enum State { Data, Iac, Will, Wont, Do, Dont, Sb, SbIac }

    private State _state = State.Data;

    /// <summary>
    /// Processes a chunk of raw bytes. Returns the in-band data bytes (IAC stripped).
    /// Any bytes that must be sent back to the server (negotiation replies) are
    /// written to <paramref name="response"/>.
    /// </summary>
    public byte[] Process(ReadOnlySpan<byte> input, out byte[] response)
    {
        var data = new List<byte>(input.Length);
        var reply = new List<byte>(0);

        foreach (byte b in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == IAC) _state = State.Iac;
                    else data.Add(b);
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case IAC: data.Add(IAC); _state = State.Data; break; // escaped 0xFF
                        case WILL: _state = State.Will; break;
                        case WONT: _state = State.Wont; break;
                        case DO: _state = State.Do; break;
                        case DONT: _state = State.Dont; break;
                        case SB: _state = State.Sb; break;
                        default: _state = State.Data; break;                  // NOP/GA/etc: ignore
                    }
                    break;

                case State.Will:  reply.Add(IAC); reply.Add(DONT); reply.Add(b); _state = State.Data; break;
                case State.Do:    reply.Add(IAC); reply.Add(WONT); reply.Add(b); _state = State.Data; break;
                case State.Wont:  _state = State.Data; break;   // no reply needed
                case State.Dont:  _state = State.Data; break;

                case State.Sb:
                    if (b == IAC) _state = State.SbIac;         // watch for IAC SE
                    // else: swallow subnegotiation payload
                    break;

                case State.SbIac:
                    _state = b == SE ? State.Data : State.Sb;   // IAC SE ends it; IAC IAC stays in SB
                    break;
            }
        }

        response = reply.Count == 0 ? Array.Empty<byte>() : reply.ToArray();
        return data.ToArray();
    }
}

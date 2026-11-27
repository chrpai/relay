namespace TelnetRelay;

/// <summary>
/// The server side of telnet, for a caller the relay found to be a telnet
/// client: offers echo, suppress-go-ahead and binary mode, answers the
/// client's option negotiation, removes telnet commands from what the client
/// sends, and doubles $FF in what is sent to it. The BBS behind it sees and
/// sends plain bytes.
/// </summary>
public sealed class TelnetFilter
{
    public const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    public const byte BINARY = 0, ECHO = 1, SGA = 3;

    private readonly bool[] _we = new bool[256];    // options we have on
    private readonly bool[] _they = new bool[256];  // options the client has on
    private int _state;                             // 0 data, 1 after IAC, 2 option verb, 3 in SB, 4 IAC in SB
    private byte _verb;
    private bool _afterCr;

    private static bool WeMay(byte o) => o is BINARY or ECHO or SGA;
    private static bool TheyMay(byte o) => o is BINARY or SGA;

    /// <summary>What to send the client first: we echo, no go-ahead, binary both ways.</summary>
    public byte[] Offer()
    {
        _we[ECHO] = _we[SGA] = _we[BINARY] = true;
        _they[SGA] = _they[BINARY] = true;
        return new byte[]
        {
            IAC, WILL, ECHO, IAC, WILL, SGA, IAC, DO, SGA,
            IAC, WILL, BINARY, IAC, DO, BINARY,
        };
    }

    /// <summary>
    /// Bytes from the client: data goes to <paramref name="data"/>, answers to
    /// its negotiation to <paramref name="reply"/>.
    /// </summary>
    public void FromClient(ReadOnlySpan<byte> input, List<byte> data, List<byte> reply)
    {
        foreach (byte b in input)
        {
            switch (_state)
            {
                case 0:
                    if (b == IAC) { _state = 1; break; }
                    if (_afterCr && (b == 0 || b == 10)) { _afterCr = false; break; } // CR NUL, CR LF: just CR
                    _afterCr = b == 13;
                    data.Add(b);
                    break;
                case 1:
                    _state = 0;
                    if (b == IAC) { _afterCr = false; data.Add(IAC); }
                    else if (b is DO or DONT or WILL or WONT) { _verb = b; _state = 2; }
                    else if (b == SB) _state = 3;
                    break;                                  // other commands (NOP, AYT...) are dropped
                case 2:
                    _state = 0;
                    Negotiate(_verb, b, reply);
                    break;
                case 3:
                    if (b == IAC) _state = 4;
                    break;
                case 4:
                    _state = b == SE ? 0 : 3;
                    break;
            }
        }
    }

    private void Negotiate(byte verb, byte opt, List<byte> reply)
    {
        switch (verb)
        {
            case DO:
                if (WeMay(opt)) { if (!_we[opt]) { _we[opt] = true; reply.AddRange(new[] { IAC, WILL, opt }); } }
                else reply.AddRange(new[] { IAC, WONT, opt });
                break;
            case DONT:
                if (_we[opt]) { _we[opt] = false; reply.AddRange(new[] { IAC, WONT, opt }); }
                break;
            case WILL:
                if (TheyMay(opt)) { if (!_they[opt]) { _they[opt] = true; reply.AddRange(new[] { IAC, DO, opt }); } }
                else reply.AddRange(new[] { IAC, DONT, opt });
                break;
            case WONT:
                if (_they[opt]) { _they[opt] = false; reply.AddRange(new[] { IAC, DONT, opt }); }
                break;
        }
    }

    /// <summary>Data for the client, with $FF doubled.</summary>
    public static void ToClient(ReadOnlySpan<byte> input, List<byte> output)
    {
        foreach (byte b in input)
        {
            output.Add(b);
            if (b == IAC) output.Add(IAC);
        }
    }
}

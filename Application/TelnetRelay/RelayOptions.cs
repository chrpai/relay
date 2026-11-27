namespace TelnetRelay;

/// <summary>
/// Settings read from the "Relay" section of appsettings.json.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>Port callers connect to (the public side).</summary>
    public int CallerPort { get; set; } = 23;

    /// <summary>Port the BBS dials out to and waits on.</summary>
    public int BbsPort { get; set; } = 6400;

    /// <summary>
    /// How long a caller waits for the BBS to come back when it is not
    /// connected and not in a call, for example while it redials after the
    /// previous caller hung up. After this the caller gets the Offline message.
    /// </summary>
    public int ReconnectGraceSeconds { get; set; } = 5;

    /// <summary>
    /// Line sent to the BBS when a caller is handed to it. The BBS treats it
    /// like a modem's CONNECT result code.
    /// </summary>
    public string BbsConnectLine { get; set; } = "CONNECT\r\n";

    /// <summary>
    /// How long to watch a new caller for telnet negotiation before joining
    /// them to the BBS. Telnet clients (SyncTERM, PuTTY, Windows telnet) send
    /// IAC negotiation as soon as they connect; raw TCP clients (most C64
    /// Wi-Fi modems) send nothing. When this is above 0, the BBS gets
    /// "CONNECT TELNET" or "CONNECT RAW" instead of plain "CONNECT", so it
    /// can turn telnet handling on or off for that call. 0 turns detection off.
    /// </summary>
    public int TelnetDetectMilliseconds { get; set; } = 0;

    /// <summary>
    /// Modem link only. A caller is answered only once the BBS has sent no
    /// modem commands for this long, so a caller never sees the BBS's reset
    /// and init commands (ATZ, ATE0V0...) that it sends after each call.
    /// </summary>
    public int AnswerQuietMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Modem link only. After a call the BBS resets and sets up its modem
    /// again; a caller who arrives before that is held until it is done. If
    /// the BBS sends no commands at all, the caller is answered after this
    /// many seconds anyway.
    /// </summary>
    public int ReinitWaitSeconds { get; set; } = 30;

    /// <summary>
    /// Modem link only, for troubleshooting: logs every chunk of call data
    /// in hex, both ways (what the caller sent, what the BBS got after
    /// telnet handling, and what the BBS sent). Busy; leave it off normally.
    /// </summary>
    public bool TraceData { get; set; } = false;

    /// <summary>
    /// Modem link only. Time between raising carrier and sending CONNECT.
    /// Image BBS refreshes its carrier flag only now and then, and checks it
    /// right after CONNECT; with no gap it still reads "no carrier" and sends
    /// the modem an "at", which the caller sees.
    /// </summary>
    public int CarrierToConnectMilliseconds { get; set; } = 500;

    public RelayMessages Messages { get; set; } = new();
}

/// <summary>
/// Text sent to callers. Each is sent as-is, one byte per character
/// (Latin-1), so \r\n line endings and \u00XX escapes for PETSCII or CP437
/// codes work. An empty string sends nothing.
/// </summary>
public sealed class RelayMessages
{
    /// <summary>Sent to the caller just before they are joined to the BBS.</summary>
    public string Connected { get; set; } = "Connecting...\r\n";

    /// <summary>Sent when the BBS is on a call with someone else.</summary>
    public string Busy { get; set; } = "The BBS is busy with another caller. Please try again soon.\r\n";

    /// <summary>Sent when the BBS is not connected to the relay.</summary>
    public string Offline { get; set; } = "The BBS is offline right now. Please try again later.\r\n";
}

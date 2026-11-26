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

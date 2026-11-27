using System.Net.Sockets;
using System.Text;

namespace TelnetRelay;

/// <summary>
/// A BBS that connected with the modem hello ($FF $02), such as the Image BBS
/// Ultimate Net driver. The relay plays its modem: it answers the BBS's AT
/// commands, rings it when a caller arrives, and dials out for ATD.
///
/// Link format, both ways: bytes, with $FF doubled. Escapes:
///   relay -> BBS: $FF $01 carrier on, $FF $00 carrier off
///   BBS -> relay: $FF $02 hello, $FF $00 DTR dropped (hang up)
///
/// Modem: numeric or word results (V0/V1), echo (E), quiet (Q), S0 (S0 > 0
/// answers at once: carrier, then CONNECT; S0=0 sends RING and waits for
/// ATA), ATA, ATD[T|P]host[:port], ATH, ATZ, ATI. Other commands answer OK.
/// The stored profile is E0 V0 S0=1, which is what Image sets up anyway.
/// When a caller hangs up the carrier drops; NO CARRIER is only sent for
/// calls the BBS dialed.
///
/// Like a real modem, which can't answer while the BBS is giving it commands,
/// a caller is only answered once the BBS has been quiet for
/// AnswerQuietMilliseconds; after a call, also only once the BBS has reset
/// and set up its modem again (or ReinitWaitSeconds have passed). Otherwise
/// a caller who rings back at once would see those commands as text.
/// </summary>
public sealed class ModemLink
{
    private enum State { Command, Ringing, Online, Dialing }

    private const int RingAnswerSeconds = 30;
    private const int DialSeconds = 30;
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly Socket _bbs;
    private readonly RelayOptions _options;
    private readonly Action<RelayLogLevel, string> _log;
    private readonly Action<ModemLink> _available;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _lock = new();

    private State _state = State.Command;
    private bool _reserved;
    private bool _dead;

    // Hayes settings
    private bool _echo, _verbose, _quiet;
    private int _s0;
    private readonly StringBuilder _line = new();

    // the call
    private Socket? _peer;
    private TelnetFilter? _telnet;
    private TaskCompletionSource<bool>? _answer;
    private CancellationTokenSource? _dial;
    private bool _hungUpHere;
    private bool _dialedOut;

    // modem setup by the BBS (Environment.TickCount64 times)
    private long _lastCommandAt = long.MinValue / 2;
    private long _callEndedAt;
    private bool _awaitSetup;                               // a call ended; no AT command since

    public string Who { get; }

    public ModemLink(Socket bbs, string who, RelayOptions options,
        Action<RelayLogLevel, string> log, Action<ModemLink> available)
    {
        _bbs = bbs;
        Who = who;
        _options = options;
        _log = log;
        _available = available;
        Reset();
    }

    private void Reset()
    {
        _echo = false;
        _verbose = false;
        _quiet = false;
        _s0 = 1;
    }

    /// <summary>Claims the modem for a caller if it is idle.</summary>
    public bool TryReserve()
    {
        lock (_lock)
        {
            if (_dead || _reserved || _state != State.Command) return false;
            _reserved = true;
            return true;
        }
    }

    // ---- the link -----------------------------------------------------------

    /// <summary>Reads the BBS until it goes away.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[1024];
        bool esc = false;
        var toPeer = new List<byte>();
        try
        {
            while (true)
            {
                int n = await _bbs.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (n == 0) break;
                toPeer.Clear();
                for (int i = 0; i < n; i++)
                {
                    byte b = buffer[i];
                    if (esc)
                    {
                        esc = false;
                        if (b != 0xff)
                        {
                            await FlushToPeerAsync(toPeer, ct);
                            if (b == 0x00) HangUp();
                            continue;
                        }
                    }
                    else if (b == 0xff)
                    {
                        esc = true;
                        continue;
                    }

                    State state;
                    lock (_lock) state = _state;
                    if (state == State.Online)
                        toPeer.Add(b);
                    else if (state == State.Dialing)
                        CancelDial();                       // a key aborts the dial
                    else
                        await CommandByteAsync(b, ct);
                }
                await FlushToPeerAsync(toPeer, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            lock (_lock) _dead = true;
            _answer?.TrySetResult(false);
            CancelDial();
            CloseQuietly(_peer);
            CloseQuietly(_bbs);
        }
    }

    private async Task FlushToPeerAsync(List<byte> data, CancellationToken ct)
    {
        if (data.Count == 0) return;
        Trace("BBS -> caller", data.ToArray());
        Socket? peer;
        TelnetFilter? telnet;
        lock (_lock) { peer = _peer; telnet = _telnet; }
        if (peer != null)
        {
            var output = data;
            if (telnet != null)
            {
                output = new List<byte>(data.Count + 8);
                TelnetFilter.ToClient(data.ToArray(), output);
            }
            try { await SendAllAsync(peer, output.ToArray(), ct); }
            catch (SocketException) { CloseQuietly(peer); }
            catch (ObjectDisposedException) { }
        }
        data.Clear();
    }

    private void CancelDial()
    {
        lock (_lock) _dial?.Cancel();
    }

    private void HangUp()
    {
        Socket? peer;
        lock (_lock)
        {
            if (_state == State.Command) return;
            _hungUpHere = true;
            peer = _peer;
            _answer?.TrySetResult(false);
            _dial?.Cancel();
        }
        CloseQuietly(peer);                                 // ends the call
    }

    // ---- sending to the BBS ---------------------------------------------------

    private async Task SendBbsAsync(byte[] raw, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try { await SendAllAsync(_bbs, raw, ct); }
        catch (SocketException) { CloseQuietly(_bbs); }
        catch (ObjectDisposedException) { }
        finally { _send.Release(); }
    }

    private Task SendDataAsync(ReadOnlySpan<byte> data, CancellationToken ct)
    {
        var raw = new List<byte>(data.Length + 4);
        foreach (byte b in data)
        {
            raw.Add(b);
            if (b == 0xff) raw.Add(0xff);
        }
        return SendBbsAsync(raw.ToArray(), ct);
    }

    private Task CarrierAsync(bool on, CancellationToken ct) =>
        SendBbsAsync(new byte[] { 0xff, (byte)(on ? 1 : 0) }, ct);

    private static readonly string[] Words = { "OK", "CONNECT", "RING", "NO CARRIER", "ERROR" };

    private Task ResultAsync(int code, CancellationToken ct)
    {
        if (_quiet) return Task.CompletedTask;
        string text = _verbose ? "\r\n" + Words[code] + "\r\n" : code + "\r";
        return SendDataAsync(Latin1.GetBytes(text), ct);
    }

    private Task TextAsync(string text, CancellationToken ct) =>
        SendDataAsync(Latin1.GetBytes(text), ct);

    // ---- Hayes commands ---------------------------------------------------------

    private async Task CommandByteAsync(byte b, CancellationToken ct)
    {
        lock (_lock) _lastCommandAt = Environment.TickCount64;
        if (_echo) await SendDataAsync(new[] { b }, ct);
        if (b is >= 0xc1 and <= 0xda) b -= 0x80;           // PETSCII shifted letters
        char c = char.ToUpperInvariant((char)b);
        if (c == '\r')
        {
            string line = _line.ToString();
            _line.Clear();
            await LineAsync(line, ct);
        }
        else if (b is 8 or 0x14)                            // BS, PETSCII DEL
        {
            if (_line.Length > 0) _line.Length--;
        }
        else if (b >= 32 && _line.Length < 80)
            _line.Append(c);
    }

    private async Task LineAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (!line.StartsWith("AT")) return;
        lock (_lock) _awaitSetup = false;
        int i = 2;
        while (i < line.Length)
        {
            char c = line[i++];
            switch (c)
            {
                case 'D':
                    await DialAsync(line.Substring(i), ct);
                    return;
                case 'A':
                    await AnswerCommandAsync(ct);
                    return;
                case 'Z':
                    Reset();
                    break;
                case 'E': _echo = Digit(line, ref i) != 0; break;
                case 'V': _verbose = Digit(line, ref i) != 0; break;
                case 'Q': _quiet = Digit(line, ref i) != 0; break;
                case 'S':
                {
                    int reg = Number(line, ref i);
                    if (i < line.Length && line[i] == '=')
                    {
                        i++;
                        int value = Number(line, ref i);
                        if (reg == 0) _s0 = value;
                    }
                    else if (i < line.Length && line[i] == '?')
                    {
                        i++;
                        if (reg == 0) await TextAsync(_s0 + "\r", ct);
                    }
                    break;
                }
                case 'I':
                    Digit(line, ref i);
                    await TextAsync("\r\nTELNET RELAY MODEM\r\n", ct);
                    break;
                case '*':                                   // the C64 driver's own commands
                    i = line.Length;
                    break;
                case '&': case '\\': case '%':
                    if (i < line.Length) i++;
                    Number(line, ref i);
                    break;
                default:                                    // H, L, M, X, B, ... : OK
                    Number(line, ref i);
                    break;
            }
        }
        await ResultAsync(0, ct);
    }

    private static int Digit(string s, ref int i)
    {
        if (i < s.Length && char.IsDigit(s[i])) return s[i++] - '0';
        return 0;
    }

    private static int Number(string s, ref int i)
    {
        int v = 0;
        while (i < s.Length && char.IsDigit(s[i])) v = v * 10 + (s[i++] - '0');
        return v;
    }

    private async Task AnswerCommandAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool>? answer;
        lock (_lock) answer = _state == State.Ringing ? _answer : null;
        if (answer != null) answer.TrySetResult(true);
        else await ResultAsync(3, ct);                      // nobody to answer
    }

    // ---- calls in -----------------------------------------------------------------

    /// <summary>
    /// A caller for this (reserved) modem: ring, answer, and run the call
    /// until either side hangs up. False if the BBS didn't answer.
    /// </summary>
    public async Task<bool> CallAsync(Socket caller, bool telnet, ReadOnlyMemory<byte> early, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_dead) { _reserved = false; return false; }
            _state = State.Ringing;
            _answer = answer;
            _hungUpHere = false;
            _dialedOut = false;
        }

        await WaitForSetupAsync(ct);

        // Image takes any result code at idle for a call, so with S0 > 0
        // there is no RING: carrier, then CONNECT, as a modem that has
        // answered. S0=0 rings and waits for ATA.
        bool answered;
        if (_s0 > 0) answered = true;
        else
        {
            await ResultAsync(2, ct);                       // RING
            var timeout = Task.Delay(TimeSpan.FromSeconds(RingAnswerSeconds), ct);
            answered = await Task.WhenAny(answer.Task, timeout) == answer.Task && answer.Task.Result;
        }

        if (!answered)
        {
            lock (_lock) { _state = State.Command; _answer = null; _reserved = false; }
            if (!_dead) _available(this);
            return false;
        }

        await SendTextAsync(caller, _options.Messages.Connected, ct);
        TelnetFilter? filter = null;
        if (telnet)
        {
            filter = new TelnetFilter();
            await SendAllAsync(caller, filter.Offer(), ct);
        }
        await StartOnlineAsync(caller, filter, ct);

        if (early.Length > 0)
            await FromPeerAsync(caller, filter, early.Span.ToArray(), ct);
        await PumpPeerAsync(caller, filter, ct);
        return true;
    }

    /// <summary>
    /// Holds a new caller while the BBS is still giving its modem commands
    /// (after a call it resets and sets the modem up again).
    /// </summary>
    private async Task WaitForSetupAsync(CancellationToken ct)
    {
        bool logged = false;
        while (true)
        {
            long now = Environment.TickCount64;
            bool setup, dead;
            long last, ended;
            lock (_lock) { setup = _awaitSetup; last = _lastCommandAt; ended = _callEndedAt; dead = _dead; }
            if (dead) return;
            bool setupDone = !setup || now - ended >= _options.ReinitWaitSeconds * 1000L;
            bool quiet = now - last >= _options.AnswerQuietMilliseconds;
            if (setupDone && quiet) return;
            if (!logged)
            {
                _log(RelayLogLevel.Information, $"Caller waits while BBS {Who} sets up its modem.");
                logged = true;
            }
            await Task.Delay(50, ct);
        }
    }

    private async Task StartOnlineAsync(Socket peer, TelnetFilter? filter, CancellationToken ct)
    {
        lock (_lock)
        {
            _peer = peer;
            _telnet = filter;
            _answer = null;
            _state = State.Online;
        }
        await CarrierAsync(true, ct);                       // carrier first, as a modem's DCD
        if (_options.CarrierToConnectMilliseconds > 0)      // so Image sees it before CONNECT
            await Task.Delay(_options.CarrierToConnectMilliseconds, ct);
        await ResultAsync(1, ct);                           // CONNECT
    }

    // peer -> BBS until the peer goes away or the BBS hangs up
    private async Task PumpPeerAsync(Socket peer, TelnetFilter? filter, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                int n = await peer.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (n == 0) break;
                await FromPeerAsync(peer, filter, buffer.AsSpan(0, n).ToArray(), ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            bool byBbs, dialed;
            lock (_lock)
            {
                byBbs = _hungUpHere;
                dialed = _dialedOut;
                _peer = null;
                _telnet = null;
                _state = State.Command;
                _reserved = false;
                _awaitSetup = true;
                _callEndedAt = Environment.TickCount64;
            }
            CloseQuietly(peer);
            if (!_dead && !ct.IsCancellationRequested)
            {
                await CarrierAsync(false, ct);
                // NO CARRIER only for calls out: after a caller, the BBS goes
                // by the carrier, and a stray result could look like a call
                if (dialed && !byBbs) await ResultAsync(3, ct);
                _available(this);
            }
        }
    }

    private async Task FromPeerAsync(Socket peer, TelnetFilter? filter, byte[] bytes, CancellationToken ct)
    {
        Trace("caller -> relay", bytes);
        if (filter == null)
        {
            await SendDataAsync(bytes, ct);
            return;
        }
        var data = new List<byte>(bytes.Length);
        var reply = new List<byte>();
        filter.FromClient(bytes, data, reply);
        Trace("relay -> BBS (telnet removed)", data.ToArray());
        if (reply.Count > 0) await SendAllAsync(peer, reply.ToArray(), ct);
        if (data.Count > 0) await SendDataAsync(data.ToArray(), ct);
    }

    // ---- calls out ------------------------------------------------------------------

    private async Task DialAsync(string rest, CancellationToken ct)
    {
        string target = rest.Trim().TrimStart('T', 'P').Trim().Trim('"').Trim();
        string host = target;
        int port = 23;
        int colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target.AsSpan(colon + 1), out int p) && p is > 0 and < 65536)
        {
            host = target.Substring(0, colon);
            port = p;
        }
        host = host.ToLowerInvariant();

        CancellationTokenSource dial;
        lock (_lock)
        {
            if (_reserved || _state != State.Command || host.Length == 0) dial = null!;
            else
            {
                _reserved = true;
                _state = State.Dialing;
                _hungUpHere = false;
                _dialedOut = true;
                _dial = dial = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }
        }
        if (dial == null)
        {
            await ResultAsync(3, ct);
            return;
        }

        _log(RelayLogLevel.Information, $"BBS {Who} dials {host}:{port}.");
        _ = Task.Run(async () =>
        {
            Socket? peer = null;
            try
            {
                dial.CancelAfter(TimeSpan.FromSeconds(DialSeconds));
                peer = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await peer.ConnectAsync(host, port, dial.Token);
                peer.NoDelay = true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                CloseQuietly(peer);
                peer = null;
                _log(RelayLogLevel.Information, $"BBS {Who}: dialing {host}:{port} failed ({ex.Message}).");
            }
            lock (_lock) _dial = null;
            dial.Dispose();

            if (peer == null)
            {
                lock (_lock) { _state = State.Command; _reserved = false; }
                if (!_dead)
                {
                    await ResultAsync(3, ct);
                    _available(this);
                }
                return;
            }
            await StartOnlineAsync(peer, null, ct);
            await PumpPeerAsync(peer, null, ct);
            _log(RelayLogLevel.Information, $"BBS {Who}: call to {host}:{port} ended.");
        }, CancellationToken.None);
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>TraceData: one log entry per chunk, hex and printable text.</summary>
    private void Trace(string direction, byte[] bytes)
    {
        if (!_options.TraceData || bytes.Length == 0) return;
        const int Max = 256;
        int n = Math.Min(bytes.Length, Max);
        var hex = new StringBuilder(n * 3);
        var text = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            byte b = bytes[i];
            hex.Append(b.ToString("X2")).Append(' ');
            text.Append(b is >= 32 and < 127 ? (char)b : '.');
        }
        string more = bytes.Length > Max ? $" (+{bytes.Length - Max} more)" : "";
        _log(RelayLogLevel.Information, $"{Who} {direction}, {bytes.Length} bytes{more}: {hex}| {text}");
    }

    private static async Task SendAllAsync(Socket socket, byte[] bytes, CancellationToken ct)
    {
        int sent = 0;
        while (sent < bytes.Length)
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, ct);
    }

    private static Task SendTextAsync(Socket socket, string text, CancellationToken ct) =>
        string.IsNullOrEmpty(text) ? Task.CompletedTask : SendAllAsync(socket, Latin1.GetBytes(text), ct);

    private static void CloseQuietly(Socket? socket)
    {
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Dispose(); } catch { }
    }
}

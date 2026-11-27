using System.Net;
using System.Net.Sockets;
using System.Text;
using TelnetRelay;

// Scenario tests for RelayEngine. Each test starts a relay on free ports,
// plays the BBS and the callers with plain sockets, and checks what each sees.

var tests = new (string Name, Func<Task> Run)[]
{
    ("Caller gets Offline when no BBS is connected", Offline),
    ("Caller is joined to a waiting BBS", Joined),
    ("Bytes pass both ways untouched, including 0xFF", BinaryClean),
    ("Second caller gets Busy during a call", Busy),
    ("Busy message survives a caller that sends telnet negotiation first", BusyWithNegotiation),
    ("Caller hangup closes the BBS connection", CallerHangsUp),
    ("BBS hangup closes the caller connection", BbsHangsUp),
    ("Caller waits for a BBS that reconnects within the grace period", GracePeriod),
    ("A dead idle BBS connection is not given a caller", DeadIdleBbs),
    ("A new BBS connection replaces an idle one", Replaced),
    ("Next caller is served after the BBS redials", RedialAfterCall),
    ("Telnet detection: a telnet caller gives CONNECT TELNET, negotiation passed on", DetectTelnet),
    ("Telnet detection: a silent caller gives CONNECT RAW", DetectRawSilent),
    ("Telnet detection: a raw caller's first keystroke is passed on", DetectRawTyping),
    ("Modem link: AT commands get numeric results, V1 gives words", ModemCommands),
    ("Modem link: a caller arrives, S0=1 answers at once (carrier, CONNECT)", ModemAnswer),
    ("Modem link: data both ways, $FF escaped on the link", ModemData),
    ("Modem link: caller hangs up: carrier off, no result", ModemCallerHangsUp),
    ("Modem link: DTR drop hangs up the caller", ModemDtrDrop),
    ("Modem link: S0=0 rings and waits for ATA", ModemManualAnswer),
    ("Modem link: busy during a call, next caller after it", ModemBusyThenNext),
    ("Modem link: telnet caller handled by the relay", ModemTelnetCaller),
    ("Modem link: ATD dials out", ModemDial),
    ("Modem link: ATD to nowhere answers NO CARRIER", ModemDialFails),
    ("Modem link: link gone mid-call hangs up the caller", ModemLinkGone),
    ("Modem link: a caller after a call waits for the BBS's modem setup", ModemWaitsForSetup),
    ("Modem link: a caller isn't answered while the BBS sends commands", ModemWaitsForQuiet),
    ("Modem link: a BBS that never sets up again still gets the caller", ModemSetupTimeout),
    ("Modem link: AT commands from the BBS during a call hang up the caller", ModemStrayCommands),
    ("Modem link: a caller typing AT (echoed by the BBS) stays on", ModemEchoedAt),
    ("Modem link: escape (pause, ESC ESC ESC with S2=27, pause) then ATH0 hangs up", ModemEscapeHangup),
    ("Modem link: escape then ATO goes back online", ModemEscapeOnline),
    ("Modem link: +++ inside data is just data", ModemPlusInData),
    ("Modem link: TraceData with TraceFile writes the trace to a file", ModemTraceFile),
    ("Telnet filter: in binary mode CR LF and CR NUL from the client stay data", TelnetBinaryCr),
    ("Mux: the hello is answered, nodes capped at 4", MuxHello),
    ("Mux: an unsupported version gets ERROR and is closed", MuxBadVersion),
    ("Mux: a caller gets Busy while no node is ready", MuxBusyNoNode),
    ("Mux: a ready node gets CALL, data goes both ways in frames, 0xFF is not doubled", MuxCallData),
    ("Mux: callers go to the lowest ready node, Busy when none, a node needs READY again after a call", MuxLowestFree),
    ("Mux: two calls at once stay separate", MuxSeparate),
    ("Mux: BBS HANGUP closes the caller after what it sent is delivered", MuxBbsHangup),
    ("Mux: DOWN ends the call and takes the node out", MuxDown),
    ("Mux: a telnet caller is handled by the relay, CALL says so", MuxTelnet),
    ("Mux: a raw caller's first bytes reach the BBS after CALL", MuxRawEarly),
    ("Mux: PING is answered with PONG", MuxPingPong),
    ("Mux: a frame for a node that does not exist gets ERROR and a close", MuxBadNode),
    ("Mux: an unknown frame type gets ERROR and a close", MuxBadType),
    ("Mux: the BBS connection dropping hangs up the callers", MuxLinkGone),
    ("Mux: a new mux connection replaces the old one", MuxReplaced),
    ("Mux: frames split across TCP segments are put together", MuxSplitFrames),
    ("Mux: a caller who is not taking output is hung up without holding up another node", MuxSlowCaller),
    ("Mux: the relay pings a silent BBS and drops one that does not answer", MuxKeepAlive),
    ("Mux: a caller waiting for the BBS gets a node when it connects and says READY", MuxGrace),
    ("Mux: plain BBS and modem link still work beside the new hello", MuxOthersUnchanged),
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine(failed == 0 ? $"\nAll {tests.Length} tests passed." : $"\n{failed} of {tests.Length} tests failed.");
return failed == 0 ? 0 : 1;

// ---- Scenarios ---------------------------------------------------------------

async Task Offline()
{
    await using var relay = await TestRelay.StartAsync(grace: 1);
    using var caller = await relay.CallAsync();
    Expect(await ReadAllAsync(caller), relay.Options.Messages.Offline, "caller text");
}

async Task Joined()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await relay.BbsAsync();
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(caller, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
    Expect(await ReadExactAsync(bbs, "CONNECT\r\n".Length), "CONNECT\r\n", "BBS text");
}

async Task BinaryClean()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await relay.BbsAsync();
    using var caller = await relay.CallAsync();
    await ReadExactAsync(caller, relay.Options.Messages.Connected.Length);
    await ReadExactAsync(bbs, "CONNECT\r\n".Length);

    var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    await caller.GetStream().WriteAsync(all);
    var atBbs = await ReadBytesAsync(bbs, 256);
    if (!atBbs.SequenceEqual(all)) throw new Exception("caller to BBS bytes changed");

    await bbs.GetStream().WriteAsync(all);
    var atCaller = await ReadBytesAsync(caller, 256);
    if (!atCaller.SequenceEqual(all)) throw new Exception("BBS to caller bytes changed");
}

async Task Busy()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await relay.BbsAsync();
    using var first = await relay.CallAsync();
    await ReadExactAsync(first, relay.Options.Messages.Connected.Length);
    using var second = await relay.CallAsync();
    Expect(await ReadAllAsync(second), relay.Options.Messages.Busy, "second caller text");
}

async Task BusyWithNegotiation()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await relay.BbsAsync();
    using var first = await relay.CallAsync();
    await ReadExactAsync(first, relay.Options.Messages.Connected.Length);
    using var second = await relay.CallAsync();
    // IAC DO SUPPRESS-GO-AHEAD, IAC WILL TERMINAL-TYPE, the way SyncTERM opens.
    await second.GetStream().WriteAsync(new byte[] { 255, 253, 3, 255, 251, 24 });
    Expect(await ReadAllAsync(second), relay.Options.Messages.Busy, "second caller text");
}

async Task CallerHangsUp()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await relay.BbsAsync();
    var caller = await relay.CallAsync();
    await ReadExactAsync(bbs, "CONNECT\r\n".Length);
    caller.Dispose();
    await ExpectClosedAsync(bbs, "BBS");
}

async Task BbsHangsUp()
{
    await using var relay = await TestRelay.StartAsync();
    var bbs = await relay.BbsAsync();
    using var caller = await relay.CallAsync();
    await ReadExactAsync(caller, relay.Options.Messages.Connected.Length);
    await ReadExactAsync(bbs, "CONNECT\r\n".Length);
    bbs.Dispose();
    await ExpectClosedAsync(caller, "caller");
}

async Task GracePeriod()
{
    await using var relay = await TestRelay.StartAsync(grace: 3);
    using var caller = await relay.CallAsync();
    await Task.Delay(500);
    using var bbs = await relay.BbsAsync();
    Expect(await ReadExactAsync(caller, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
    Expect(await ReadExactAsync(bbs, "CONNECT\r\n".Length), "CONNECT\r\n", "BBS text");
}

async Task DeadIdleBbs()
{
    await using var relay = await TestRelay.StartAsync(grace: 1);
    var bbs = await relay.BbsAsync();
    bbs.Dispose();
    await Task.Delay(200);
    using var caller = await relay.CallAsync();
    Expect(await ReadAllAsync(caller), relay.Options.Messages.Offline, "caller text");
}

async Task Replaced()
{
    await using var relay = await TestRelay.StartAsync();
    using var oldBbs = await relay.BbsAsync();
    using var newBbs = await relay.BbsAsync();
    await ExpectClosedAsync(oldBbs, "old BBS");
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(newBbs, "CONNECT\r\n".Length), "CONNECT\r\n", "new BBS text");
}

async Task RedialAfterCall()
{
    await using var relay = await TestRelay.StartAsync(grace: 3);
    var bbs = await relay.BbsAsync();
    var caller = await relay.CallAsync();
    await ReadExactAsync(bbs, "CONNECT\r\n".Length);
    caller.Dispose();
    await ExpectClosedAsync(bbs, "BBS");
    bbs.Dispose();

    // The next caller arrives before the BBS has redialed, then it redials.
    using var next = await relay.CallAsync();
    await Task.Delay(300);
    using var bbs2 = await relay.BbsAsync();
    Expect(await ReadExactAsync(next, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "next caller text");
}

async Task DetectTelnet()
{
    await using var relay = await TestRelay.StartAsync(detectMs: 500);
    using var bbs = await relay.BbsAsync();
    using var caller = await relay.CallAsync();
    var negotiation = new byte[] { 255, 253, 3, 255, 251, 24 }; // IAC DO SGA, IAC WILL TTYPE
    await caller.GetStream().WriteAsync(negotiation);
    Expect(await ReadExactAsync(bbs, "CONNECT TELNET\r\n".Length), "CONNECT TELNET\r\n", "BBS text");
    var passed = await ReadBytesAsync(bbs, negotiation.Length);
    if (!passed.SequenceEqual(negotiation)) throw new Exception("negotiation bytes were not passed on");
}

async Task DetectRawSilent()
{
    await using var relay = await TestRelay.StartAsync(detectMs: 300);
    using var bbs = await relay.BbsAsync();
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(caller, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
    Expect(await ReadExactAsync(bbs, "CONNECT RAW\r\n".Length), "CONNECT RAW\r\n", "BBS text");
    await bbs.GetStream().WriteAsync(new byte[] { 65 });
    Expect(await ReadExactAsync(caller, 1), "A", "caller text after connect");
}

async Task DetectRawTyping()
{
    await using var relay = await TestRelay.StartAsync(detectMs: 1000);
    using var bbs = await relay.BbsAsync();
    using var caller = await relay.CallAsync();
    await caller.GetStream().WriteAsync(new byte[] { 13 });
    Expect(await ReadExactAsync(bbs, "CONNECT RAW\r\n".Length + 1), "CONNECT RAW\r\n\r", "BBS text");
}

async Task<TcpClient> Modem(TestRelay relay)
{
    var c = new TcpClient();
    await c.ConnectAsync(IPAddress.Loopback, relay.Options.BbsPort);
    await c.GetStream().WriteAsync(new byte[] { 0xff, 0x02 });
    await Task.Delay(RelayEngine.HelloMilliseconds + 200);
    return c;
}

static Task Send(TcpClient c, string text) => c.GetStream().WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

async Task ModemCommands()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await Modem(relay);
    await Send(bbs, "ATZ\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "ATZ");
    await Send(bbs, "ate0v1\r");                              // lower case, as Image sends it
    Expect(await ReadExactAsync(bbs, 6), "\r\nOK\r\n", "V1");
    await Send(bbs, "ATV0S2=27S7=25\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "V0");
    await Send(bbs, "AT*Rrelay.lan:6400\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "AT*R ignored");
    await Send(bbs, "hello\r");                               // not a command: no answer
    await Send(bbs, "AT\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "AT");
}

async Task<(TestRelay relay, TcpClient bbs, TcpClient caller)> ModemCall(int detectMs = 0)
{
    var relay = await TestRelay.StartAsync(detectMs: detectMs);
    var bbs = await Modem(relay);
    var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "carrier, CONNECT");
    Expect(await ReadExactAsync(caller, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
    return (relay, bbs, caller);
}

async Task ModemAnswer()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
}

async Task ModemData()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
    await caller.GetStream().WriteAsync(new byte[] { 65, 0xff, 66 });
    var got = await ReadBytesAsync(bbs, 4);
    if (!got.SequenceEqual(new byte[] { 65, 0xff, 0xff, 66 })) throw new Exception("caller to BBS: " + Convert.ToHexString(got));
    await bbs.GetStream().WriteAsync(new byte[] { 67, 0xff, 0xff, 68 });
    got = await ReadBytesAsync(caller, 3);
    if (!got.SequenceEqual(new byte[] { 67, 0xff, 68 })) throw new Exception("BBS to caller: " + Convert.ToHexString(got));
}

async Task ModemCallerHangsUp()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs;
    caller.Dispose();
    Expect(await ReadExactAsync(bbs, 2), "\xff\x00", "carrier off");
    await Send(bbs, "AT\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "no NO CARRIER after a caller");
}

async Task ModemDtrDrop()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
    await bbs.GetStream().WriteAsync(new byte[] { 0xff, 0x00 });
    await ExpectClosedAsync(caller, "caller");
    Expect(await ReadExactAsync(bbs, 2), "\xff\x00", "carrier off");
    await Send(bbs, "AT\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "no NO CARRIER after our own hang-up");
}

async Task ModemManualAnswer()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await Modem(relay);
    await Send(bbs, "ATS0=0\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "S0=0");
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 2), "2\r", "RING");
    await Task.Delay(300);
    await Send(bbs, "ATA\r");
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "CONNECT after ATA");
    Expect(await ReadExactAsync(caller, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
}

async Task ModemBusyThenNext()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs;
    using (var second = await relay.CallAsync())
        Expect(await ReadAllAsync(second), relay.Options.Messages.Busy, "second caller");
    caller.Dispose();
    Expect(await ReadExactAsync(bbs, 2), "\xff\x00", "first call over");
    await Send(bbs, "ATZ\r");                                // the BBS sets its modem up again
    Expect(await ReadExactAsync(bbs, 2), "0\r", "ATZ");
    await Send(bbs, "ATS0=1\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "S0=1");
    using var next = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "next caller");
}

async Task ModemWaitsForSetup()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs;
    caller.Dispose();
    Expect(await ReadExactAsync(bbs, 2), "\xff\x00", "first call over");
    using var next = await relay.CallAsync();                 // rings back at once
    await Send(bbs, "AT\r");                                 // Image's bare AT during logoff
    Expect(await ReadExactAsync(bbs, 2), "0\r", "AT");
    await Task.Delay(700);                                    // quiet, but setup not done
    if (bbs.Available > 0) throw new Exception("answered before the BBS set its modem up");
    await Send(bbs, "ATZ\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "ATZ");
    await Send(bbs, "ATE0V0\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "E0V0");
    await Task.Delay(400);
    if (bbs.Available > 0) throw new Exception("answered in the middle of the setup");
    await Send(bbs, "ATS0=1S2=27S7=25\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "init");
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "answered after the setup");
    Expect(await ReadExactAsync(next, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
    await Send(bbs, "X");
    Expect(await ReadExactAsync(next, 1), "X", "only BBS data reaches the caller, no commands");
}

async Task ModemWaitsForQuiet()
{
    await using var relay = await TestRelay.StartAsync(quietMs: 600);
    using var bbs = await Modem(relay);
    await Send(bbs, "ATZ\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "ATZ");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "answered once quiet");
    if (sw.ElapsedMilliseconds < 400) throw new Exception($"answered after {sw.ElapsedMilliseconds} ms, during the commands");
}

async Task ModemStrayCommands()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
    await Task.Delay(300);
    await Send(bbs, "ate0v0h0x1m0b0\r");                     // Image retrying its setup
    await Send(bbs, "at\r");
    await ExpectClosedAsync(caller, "caller");
    Expect(await ReadExactAsync(bbs, 2), "\xff\x00", "carrier off");
    await Send(bbs, "ate0v0h0x1m0b0\r");                     // and the modem answers again
    Expect(await ReadExactAsync(bbs, 2), "0\r", "OK in command mode");
}

async Task ModemEchoedAt()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
    for (int i = 0; i < 3; i++)
    {
        await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("at\r"));
        Expect(await ReadExactAsync(bbs, 3), "at\r", "caller's typing");
        await Send(bbs, "at\r");                             // the BBS echoes it
        Expect(await ReadExactAsync(caller, 3), "at\r", "echo");
    }
    await Send(bbs, "still here");
    Expect(await ReadExactAsync(caller, 10), "still here", "call still up");
}

async Task ModemEscapeHangup()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await Modem(relay);
    await Send(bbs, "ATS0=1S2=27\r");                         // as Image's init
    Expect(await ReadExactAsync(bbs, 2), "0\r", "init");
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "carrier, CONNECT");
    await Task.Delay(300);                                    // Image: &,22,15
    foreach (var _ in Enumerable.Range(0, 3))
    {
        await Send(bbs, "\x1b");                              // ESC, &,22,5 apart
        await Task.Delay(80);
    }
    await Task.Delay(250);
    Expect(await ReadExactAsync(bbs, 2), "0\r", "OK: command mode");
    await Send(bbs, "+++");                                   // Image sends these too
    await Task.Delay(170);
    await Send(bbs, "ath0\r");
    await ExpectClosedAsync(caller, "caller");
    var got = await ReadExactAsync(bbs, 4);
    if (got != "0\r\xff\x00" && got != "\xff\x00" + "0\r") throw new Exception("after ATH0: " + Convert.ToHexString(Encoding.Latin1.GetBytes(got)));
}

async Task ModemEscapeOnline()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
    await Task.Delay(300);
    await Send(bbs, "+++");
    Expect(await ReadExactAsync(caller, 3), "+++", "the escape reaches the caller, as with a modem");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "OK: command mode");
    await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("lost"));
    await Task.Delay(100);
    await Send(bbs, "ATO\r");
    Expect(await ReadExactAsync(bbs, 2), "1\r", "CONNECT: online again");
    await Send(bbs, "back");
    Expect(await ReadExactAsync(caller, 4), "back", "data flows again");
    await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("hi"));
    Expect(await ReadExactAsync(bbs, 2), "hi", "caller's data, without what was sent in command mode");
}

async Task ModemPlusInData()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var b = bbs; using var c = caller;
    await Send(bbs, "a+++b");                                 // no pause before
    Expect(await ReadExactAsync(caller, 5), "a+++b", "data");
    await Task.Delay(400);
    await Send(bbs, "x");
    Expect(await ReadExactAsync(caller, 1), "x", "still online");
    if (bbs.Available > 0) throw new Exception("the relay answered: " + bbs.Available + " bytes");
}

Task TelnetBinaryCr()
{
    var f = new TelnetFilter();
    f.Offer();                                                // asks for binary both ways
    var data = new List<byte>(); var reply = new List<byte>();
    f.FromClient(new byte[] { 255, 251, 0 }, data, reply);    // IAC WILL BINARY
    f.FromClient(new byte[] { 13, 10, 13, 0, 13, 13, 65 }, data, reply);
    if (!data.SequenceEqual(new byte[] { 13, 10, 13, 0, 13, 13, 65 }))
        throw new Exception("binary mode changed the data: " + Convert.ToHexString(data.ToArray()));
    var text = new TelnetFilter();                            // a client in text mode
    data.Clear();
    text.FromClient(new byte[] { 13, 10, 65, 13, 0 }, data, reply);
    if (!data.SequenceEqual(new byte[] { 13, 65, 13 }))
        throw new Exception("text mode: " + Convert.ToHexString(data.ToArray()));
    return Task.CompletedTask;
}

async Task ModemTraceFile()
{
    string file = Path.Combine(Path.GetTempPath(), $"relay-trace-{Guid.NewGuid():N}", "trace.log");
    await using var relay = await TestRelay.StartAsync(trace: file);
    using var bbs = await Modem(relay);
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "carrier, CONNECT");
    await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("Hi"));
    Expect(await ReadExactAsync(bbs, 2), "Hi", "data");
    await Task.Delay(200);
    string log = File.ReadAllText(file);
    if (!log.Contains("caller -> relay, 2 bytes: 48 69 | Hi")) throw new Exception("trace file: " + log);
    Directory.Delete(Path.GetDirectoryName(file)!, true);
}

async Task ModemSetupTimeout()
{
    await using var relay = await TestRelay.StartAsync(reinitSeconds: 1);
    using var bbs = await Modem(relay);
    using (var first = await relay.CallAsync())
        Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "first caller");
    Expect(await ReadExactAsync(bbs, 2), "\xff\x00", "first call over");
    using var next = await relay.CallAsync();                 // the BBS sends nothing
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "answered after ReinitWaitSeconds");
}

async Task ModemTelnetCaller()
{
    await using var relay = await TestRelay.StartAsync(detectMs: 300);
    using var bbs = await Modem(relay);
    using var caller = await relay.CallAsync();
    await caller.GetStream().WriteAsync(new byte[] { 255, 251, 24, 255, 253, 3 });     // WILL TTYPE, DO SGA
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "carrier, CONNECT");
    Expect(await ReadExactAsync(caller, relay.Options.Messages.Connected.Length), relay.Options.Messages.Connected, "caller text");
    var offer = await ReadBytesAsync(caller, 15);
    if (offer[0] != 255 || offer[1] != 251 || offer[2] != 1) throw new Exception("no telnet offer: " + Convert.ToHexString(offer));
    var refuse = await ReadBytesAsync(caller, 3);
    if (!refuse.SequenceEqual(new byte[] { 255, 254, 24 })) throw new Exception("TTYPE not refused: " + Convert.ToHexString(refuse));
    await caller.GetStream().WriteAsync(new byte[] { 65, 255, 255, 66, 13, 0, 67 });   // A, $FF, B, CR NUL, C
    var got = await ReadBytesAsync(bbs, 6);
    if (!got.SequenceEqual(new byte[] { 65, 0xff, 0xff, 66, 13, 67 })) throw new Exception("caller to BBS: " + Convert.ToHexString(got));
    await bbs.GetStream().WriteAsync(new byte[] { 68, 0xff, 0xff, 69 });
    got = await ReadBytesAsync(caller, 4);
    if (!got.SequenceEqual(new byte[] { 68, 0xff, 0xff, 69 })) throw new Exception("BBS to caller: " + Convert.ToHexString(got));
}

async Task ModemDial()
{
    var remote = new TcpListener(IPAddress.Loopback, 0);
    remote.Start();
    int port = ((IPEndPoint)remote.LocalEndpoint).Port;
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await Modem(relay);
    await Send(bbs, $"ATDT127.0.0.1:{port}\r");
    using var far = await remote.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "CONNECT");
    await Send(bbs, "hi");
    Expect(await ReadExactAsync(far, 2), "hi", "to the far end");
    await far.GetStream().WriteAsync(new byte[] { 0xff, 33 });
    var got = await ReadBytesAsync(bbs, 3);
    if (!got.SequenceEqual(new byte[] { 0xff, 0xff, 33 })) throw new Exception("from far end: " + Convert.ToHexString(got));
    using (var busy = await relay.CallAsync())
        Expect(await ReadAllAsync(busy), relay.Options.Messages.Busy, "caller while dialed out");
    far.Dispose();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x00" + "3\r", "far end hung up");
    remote.Stop();
}

async Task ModemDialFails()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await Modem(relay);
    await Send(bbs, $"ATDT127.0.0.1:{port}\r");
    Expect(await ReadExactAsync(bbs, 2), "3\r", "NO CARRIER");
    using var caller = await relay.CallAsync();
    Expect(await ReadExactAsync(bbs, 4), "\xff\x01" + "1\r", "callers still come after");
}

async Task ModemLinkGone()
{
    var (relay, bbs, caller) = await ModemCall();
    await using var _ = relay; using var c = caller;
    bbs.Dispose();
    await ExpectClosedAsync(caller, "caller");
}


// ---- Mux ---------------------------------------------------------------------

async Task<MuxBbs> MuxConnect(TestRelay relay, int nodes = 2, params int[] ready)
{
    var bbs = await MuxBbs.ConnectAsync(relay, nodes: (byte)nodes);
    foreach (int n in ready) await bbs.ReadyAsync(n);
    return bbs;
}

async Task<string> CallerHello(TcpClient caller, TestRelay relay) =>
    await ReadExactAsync(caller, relay.Options.Messages.Connected.Length);

async Task MuxHello()
{
    await using var relay = await TestRelay.StartAsync();
    using (var bbs = await MuxBbs.ConnectAsync(relay, nodes: 3))
        if (!bbs.Hello.SequenceEqual(new byte[] { 0xFF, 0x03, 1, 3 })) throw new Exception("hello answer " + Convert.ToHexString(bbs.Hello));
    using (var bbs = await MuxBbs.ConnectAsync(relay, nodes: 9))
        if (!bbs.Hello.SequenceEqual(new byte[] { 0xFF, 0x03, 1, 4 })) throw new Exception("hello answer for 9 nodes " + Convert.ToHexString(bbs.Hello));
}

async Task MuxBadVersion()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxBbs.ConnectAsync(relay, version: 2, readHello: false);
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 15 || f.Payload.Length != 1 || f.Payload[0] != 3) throw new Exception($"expected ERROR 3, got type {f.Type} {Convert.ToHexString(f.Payload)}");
    await ExpectClosedAsync(bbs.Tcp, "BBS");
}

async Task MuxBusyNoNode()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 2);                       // connected, no node ready
    using var caller = await relay.CallAsync();
    Expect(await ReadAllAsync(caller), relay.Options.Messages.Busy, "caller text");
}

async Task MuxCallData()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 2, 1);
    using var caller = await relay.CallAsync();
    var call = await bbs.ReadFrameAsync();
    if (call.Type != 1 || call.Node != 1) throw new Exception($"expected CALL for node 1, got type {call.Type} node {call.Node}");
    if (call.Payload[0] != 0) throw new Exception("flags should be 0 for a raw caller");
    Expect(Encoding.Latin1.GetString(call.Payload, 1, call.Payload.Length - 1), "127.0.0.1", "caller address");
    Expect(await CallerHello(caller, relay), relay.Options.Messages.Connected, "caller text");

    await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("hi"));
    var d = await bbs.ReadFrameAsync();
    if (d.Type != 0 || d.Node != 1 || Encoding.Latin1.GetString(d.Payload) != "hi") throw new Exception("caller data frame wrong");

    await bbs.SendAsync(0, 1, Encoding.Latin1.GetBytes("yo"));
    Expect(await ReadExactAsync(caller, 2), "yo", "text to caller");

    // every byte value, both ways; 300 bytes need two frames from the caller
    var all = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
    await caller.GetStream().WriteAsync(all);
    var got = await bbs.ReadDataAsync(1, 300);
    if (!got.SequenceEqual(all)) throw new Exception("caller to BBS bytes changed");
    await bbs.SendAsync(0, 1, all.AsSpan(0, 255).ToArray());
    await bbs.SendAsync(0, 1, all.AsSpan(255).ToArray());
    var back = await ReadBytesAsync(caller, 300);
    if (!back.SequenceEqual(all)) throw new Exception("BBS to caller bytes changed (0xFF must not be doubled for a raw caller)");
}

async Task MuxLowestFree()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 2, 1, 2);
    using var a = await relay.CallAsync();
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 1 || f.Node != 1) throw new Exception($"first caller should get node 1, got node {f.Node}");
    using var b = await relay.CallAsync();
    f = await bbs.ReadFrameAsync();
    if (f.Type != 1 || f.Node != 2) throw new Exception($"second caller should get node 2, got node {f.Node}");
    using var c = await relay.CallAsync();
    Expect(await ReadAllAsync(c), relay.Options.Messages.Busy, "third caller text");

    a.Dispose();                                                      // node 1 caller hangs up
    f = await bbs.ReadFrameAsync();
    if (f.Type != 2 || f.Node != 1) throw new Exception($"expected HANGUP for node 1, got type {f.Type} node {f.Node}");
    using var d = await relay.CallAsync();                            // node 1 is down until READY
    Expect(await ReadAllAsync(d), relay.Options.Messages.Busy, "caller text before READY");
    await bbs.ReadyAsync(1);
    using var e = await relay.CallAsync();
    f = await bbs.ReadFrameAsync();
    if (f.Type != 1 || f.Node != 1) throw new Exception($"after READY the caller should get node 1, got node {f.Node}");
}

async Task MuxSeparate()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 2, 1, 2);
    using var a = await relay.CallAsync();
    await bbs.ReadFrameAsync();
    using var b = await relay.CallAsync();
    await bbs.ReadFrameAsync();
    await CallerHello(a, relay);
    await CallerHello(b, relay);

    await bbs.SendAsync(0, 1, Encoding.Latin1.GetBytes("one"));
    await bbs.SendAsync(0, 2, Encoding.Latin1.GetBytes("two"));
    Expect(await ReadExactAsync(a, 3), "one", "node 1 caller");
    Expect(await ReadExactAsync(b, 3), "two", "node 2 caller");

    await b.GetStream().WriteAsync(Encoding.Latin1.GetBytes("from b"));
    await a.GetStream().WriteAsync(Encoding.Latin1.GetBytes("from a"));
    var got = new Dictionary<int, string>();
    while (got.Values.Sum(v => v.Length) < 12)
    {
        var f = await bbs.ReadFrameAsync();
        if (f.Type == 0) got[f.Node] = got.GetValueOrDefault(f.Node, "") + Encoding.Latin1.GetString(f.Payload);
    }
    Expect(got[1], "from a", "node 1 data");
    Expect(got[2], "from b", "node 2 data");
}

async Task MuxBbsHangup()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 1, 1);
    using var caller = await relay.CallAsync();
    await bbs.ReadFrameAsync();
    await CallerHello(caller, relay);
    await bbs.SendAsync(0, 1, Encoding.Latin1.GetBytes("goodbye"));
    await bbs.SendAsync(2, 1, Array.Empty<byte>());
    Expect(await ReadAllAsync(caller), "goodbye", "what the caller got before the close");
    // the node is down until READY: a new caller is busy
    using var next = await relay.CallAsync();
    Expect(await ReadAllAsync(next), relay.Options.Messages.Busy, "next caller text");
}

async Task MuxDown()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 1, 1);
    using var caller = await relay.CallAsync();
    await bbs.ReadFrameAsync();
    await CallerHello(caller, relay);
    await bbs.SendAsync(4, 1, Array.Empty<byte>());
    await ExpectClosedAsync(caller, "caller");
    await bbs.ReadyAsync(1);                                          // back in service
    using var again = await relay.CallAsync();
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 1) throw new Exception("a READY after DOWN should take calls again");
}

async Task MuxTelnet()
{
    await using var relay = await TestRelay.StartAsync(detectMs: 400);
    using var bbs = await MuxConnect(relay, 1, 1);
    using var caller = await relay.CallAsync();
    await caller.GetStream().WriteAsync(new byte[] { 255, 253, 3 });  // IAC DO SGA: a telnet client
    var call = await bbs.ReadFrameAsync();
    if (call.Type != 1 || call.Payload[0] != 1) throw new Exception("CALL flags should say telnet");
    // the relay's offer comes first, then answers; none of it reaches the BBS
    await Task.Delay(300);
    await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("a"));
    var d = await bbs.ReadFrameAsync();
    if (d.Type != 0 || !d.Payload.SequenceEqual(new byte[] { (byte)'a' })) throw new Exception("BBS should see just the typed byte, got " + Convert.ToHexString(d.Payload));
    // 0xFF toward a telnet caller is doubled by the relay
    await bbs.SendAsync(0, 1, new byte[] { 1, 255, 2 });
    var seen = new List<byte>();
    var buffer = new byte[256];
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        int n = await caller.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        seen.AddRange(buffer.AsSpan(0, n).ToArray());
        if (seen.Count >= 4 && seen.Skip(seen.Count - 4).SequenceEqual(new byte[] { 1, 255, 255, 2 })) return;
    }
    throw new Exception("expected 01 FF FF 02 at the end, got " + Convert.ToHexString(seen.ToArray()));
}

async Task MuxRawEarly()
{
    await using var relay = await TestRelay.StartAsync(detectMs: 400);
    using var bbs = await MuxConnect(relay, 1, 1);
    using var caller = await relay.CallAsync();
    await caller.GetStream().WriteAsync(Encoding.Latin1.GetBytes("x"));
    var call = await bbs.ReadFrameAsync();
    if (call.Type != 1 || call.Payload[0] != 0) throw new Exception("CALL flags should be 0 for a raw caller");
    var d = await bbs.ReadFrameAsync();
    if (d.Type != 0 || Encoding.Latin1.GetString(d.Payload) != "x") throw new Exception("the first keystroke should follow CALL");
}

async Task MuxPingPong()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 1);
    await bbs.SendAsync(5, 0, new byte[] { 7, 8 });
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 6 || !f.Payload.SequenceEqual(new byte[] { 7, 8 })) throw new Exception("expected PONG with the same payload");
}

async Task MuxBadNode()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 2);
    await bbs.SendAsync(3, 3, Array.Empty<byte>());                   // READY for node 3 of 2
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 15 || f.Payload[0] != 2) throw new Exception("expected ERROR 2");
    await ExpectClosedAsync(bbs.Tcp, "BBS");
}

async Task MuxBadType()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 2);
    await bbs.SendAsync(9, 1, Array.Empty<byte>());
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 15 || f.Payload[0] != 1) throw new Exception("expected ERROR 1");
    await ExpectClosedAsync(bbs.Tcp, "BBS");
}

async Task MuxLinkGone()
{
    await using var relay = await TestRelay.StartAsync();
    var bbs = await MuxConnect(relay, 1, 1);
    using var caller = await relay.CallAsync();
    await bbs.ReadFrameAsync();
    await CallerHello(caller, relay);
    bbs.Dispose();
    await ExpectClosedAsync(caller, "caller");
}

async Task MuxReplaced()
{
    await using var relay = await TestRelay.StartAsync();
    var first = await MuxConnect(relay, 1, 1);
    using var second = await MuxConnect(relay, 1, 1);
    await ExpectClosedAsync(first.Tcp, "first mux");
    using var caller = await relay.CallAsync();
    var f = await second.ReadFrameAsync();
    if (f.Type != 1) throw new Exception("the new connection should get the call");
}

async Task MuxSplitFrames()
{
    await using var relay = await TestRelay.StartAsync();
    using var bbs = await MuxConnect(relay, 1, 1);
    using var caller = await relay.CallAsync();
    await bbs.ReadFrameAsync();
    await CallerHello(caller, relay);
    var frame = new byte[] { 0x01, 5, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e' };
    foreach (var b in frame)                                          // one byte at a time
    {
        await bbs.Tcp.GetStream().WriteAsync(new[] { b });
        await Task.Delay(30);
    }
    Expect(await ReadExactAsync(caller, 5), "abcde", "text to caller");
}

async Task MuxSlowCaller()
{
    await using var relay = await TestRelay.StartAsync(muxBuffer: 8192);
    using var bbs = await MuxConnect(relay, 2, 1, 2);
    using var slow = await relay.CallAsync();                         // node 1: never reads
    await bbs.ReadFrameAsync();
    using var fast = await relay.CallAsync();                         // node 2: reads
    await bbs.ReadFrameAsync();
    await CallerHello(fast, relay);

    var chunk = new byte[255];
    Array.Fill(chunk, (byte)'x');
    // flood node 1 until the relay gives up on that caller (it sends HANGUP)
    var hangup = Task.Run(async () =>
    {
        while (true)
        {
            var f = await bbs.ReadFrameAsync(30000);
            if (f.Type == 2 && f.Node == 1) return;
        }
    });
    int sent = 0;
    while (!hangup.IsCompleted && sent < 200_000)
    {
        await bbs.SendAsync(0, 1, chunk);
        sent++;
        if (sent == 2000)
        {
            await bbs.SendAsync(0, 2, Encoding.Latin1.GetBytes("ping"));
            Expect(await ReadExactAsync(fast, 4), "ping", "the other node's caller, while node 1 is backed up");
        }
    }
    await hangup.WaitAsync(TimeSpan.FromSeconds(30));
    await bbs.SendAsync(0, 2, Encoding.Latin1.GetBytes("pong"));
    Expect(await ReadExactAsync(fast, 4), "pong", "the other node's caller afterwards");
}

async Task MuxKeepAlive()
{
    await using var relay = await TestRelay.StartAsync(muxPing: 1, muxPong: 1);
    using var bbs = await MuxConnect(relay, 1);
    var f = await bbs.ReadFrameAsync(4000);
    if (f.Type != 5 || f.Node != 0) throw new Exception("expected a PING after a quiet second");
    await bbs.SendAsync(6, 0, f.Payload);                             // answer it: stays up
    f = await bbs.ReadFrameAsync(4000);
    if (f.Type != 5) throw new Exception("expected another PING");
    await ExpectClosedAsync(bbs.Tcp, "BBS");                          // this one is not answered
}

async Task MuxGrace()
{
    await using var relay = await TestRelay.StartAsync(grace: 4);
    using var caller = await relay.CallAsync();                       // no BBS yet: waits
    await Task.Delay(300);
    using var bbs = await MuxConnect(relay, 1, 1);
    var f = await bbs.ReadFrameAsync();
    if (f.Type != 1 || f.Node != 1) throw new Exception("the waiting caller should get node 1");
    Expect(await CallerHello(caller, relay), relay.Options.Messages.Connected, "caller text");
}

async Task MuxOthersUnchanged()
{
    await using var relay = await TestRelay.StartAsync();
    // a plain BBS (no hello) still gets CONNECT
    using (var plain = await relay.BbsAsync())
    using (var caller = await relay.CallAsync())
        Expect(await ReadExactAsync(plain, "CONNECT\r\n".Length), "CONNECT\r\n", "plain BBS text");
    await Task.Delay(300);
    // a modem link (hello FF 02) still answers AT
    using var modem = await Modem(relay);
    await Send(modem, "AT\r");
    Expect(await ReadExactAsync(modem, 2), "OK".Substring(0, 0) + "0\r", "modem answer");
}

// ---- Helpers -----------------------------------------------------------------

static void Expect(string actual, string expected, string what)
{
    if (actual != expected)
        throw new Exception($"{what}: expected \"{Escape(expected)}\" but got \"{Escape(actual)}\"");
}

static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

static async Task<byte[]> ReadBytesAsync(TcpClient client, int count)
{
    var buffer = new byte[count];
    await client.GetStream().ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    return buffer;
}

static async Task<string> ReadExactAsync(TcpClient client, int count) =>
    Encoding.Latin1.GetString(await ReadBytesAsync(client, count));

static async Task<string> ReadAllAsync(TcpClient client)
{
    using var ms = new MemoryStream();
    await client.GetStream().CopyToAsync(ms).WaitAsync(TimeSpan.FromSeconds(10));
    return Encoding.Latin1.GetString(ms.ToArray());
}

static async Task ExpectClosedAsync(TcpClient client, string who)
{
    var buffer = new byte[64];
    try
    {
        while (true)
        {
            int n = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (n == 0) return;
        }
    }
    catch (IOException) { return; } // reset also counts as closed
    catch (TimeoutException) { throw new Exception($"{who} connection was not closed"); }
}


/// <summary>A BBS that speaks the mux protocol.</summary>
sealed class MuxBbs : IDisposable
{
    public TcpClient Tcp { get; }
    public byte[] Hello { get; private set; } = Array.Empty<byte>();
    private readonly NetworkStream _s;

    private MuxBbs(TcpClient tcp) { Tcp = tcp; _s = tcp.GetStream(); }

    public static async Task<MuxBbs> ConnectAsync(TestRelay relay, byte version = 1, byte nodes = 2, bool readHello = true)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, relay.Options.BbsPort);
        var bbs = new MuxBbs(tcp);
        await bbs._s.WriteAsync(new byte[] { 0xFF, 0x03, version, nodes });
        if (readHello)
        {
            var hello = new byte[4];
            await bbs._s.ReadExactlyAsync(hello).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            bbs.Hello = hello;
        }
        return bbs;
    }

    public async Task SendAsync(int type, int node, byte[] payload)
    {
        var buf = new byte[2 + payload.Length];
        buf[0] = (byte)((type << 4) | node);
        buf[1] = (byte)payload.Length;
        payload.CopyTo(buf, 2);
        await _s.WriteAsync(buf);
    }

    public async Task ReadyAsync(int node)
    {
        await SendAsync(3, node, Array.Empty<byte>());
        await Task.Delay(100);                                        // let the relay register it
    }

    public async Task<(int Type, int Node, byte[] Payload)> ReadFrameAsync(int timeoutMs = 5000)
    {
        var header = new byte[2];
        await _s.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        var payload = new byte[header[1]];
        if (payload.Length > 0)
            await _s.ReadExactlyAsync(payload).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        return (header[0] >> 4, header[0] & 0x0F, payload);
    }

    /// <summary>Collects DATA frames for one node until <paramref name="count"/> bytes have arrived.</summary>
    public async Task<byte[]> ReadDataAsync(int node, int count)
    {
        var got = new List<byte>();
        while (got.Count < count)
        {
            var f = await ReadFrameAsync();
            if (f.Type == 0 && f.Node == node) got.AddRange(f.Payload);
        }
        return got.ToArray();
    }

    public void Dispose() => Tcp.Dispose();
}

sealed class TestRelay : IAsyncDisposable
{
    public RelayOptions Options { get; }
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    private TestRelay(RelayOptions options)
    {
        Options = options;
        var engine = new RelayEngine(options, (level, msg) =>
        {
            if (Environment.GetEnvironmentVariable("RELAY_TEST_LOG") == "1")
                Console.WriteLine($"      [{level}] {msg}");
        });
        _run = engine.RunAsync(_cts.Token);
        Started = engine.Started;
    }

    private Task Started { get; }

    public static async Task<TestRelay> StartAsync(int grace = 2, int detectMs = 0, int quietMs = 200, int reinitSeconds = 30, string? trace = null,
        int muxBuffer = 16384, int muxPing = 60, int muxPong = 10)
    {
        var relay = new TestRelay(new RelayOptions
        {
            CallerPort = FreePort(),
            BbsPort = FreePort(),
            ReconnectGraceSeconds = grace,
            TelnetDetectMilliseconds = detectMs,
            AnswerQuietMilliseconds = quietMs,
            ReinitWaitSeconds = reinitSeconds,
            CarrierToConnectMilliseconds = 100,
            TraceData = trace != null,
            TraceFile = trace ?? "",
            MuxNodeBufferBytes = muxBuffer,
            MuxPingSeconds = muxPing,
            MuxPongSeconds = muxPong,
        });
        var first = await Task.WhenAny(relay.Started, relay._run).WaitAsync(TimeSpan.FromSeconds(5));
        if (first == relay._run) await relay._run; // surfaces a startup error
        return relay;
    }

    public async Task<TcpClient> BbsAsync()
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, Options.BbsPort);
        await Task.Delay(100); // let the relay register it
        return c;
    }

    public async Task<TcpClient> CallAsync()
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, Options.CallerPort);
        return c;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}

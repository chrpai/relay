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
    await Task.Delay(400);
    if (bbs.Available > 0) throw new Exception("answered before the BBS set its modem up");
    await Send(bbs, "ATZ\r");
    Expect(await ReadExactAsync(bbs, 2), "0\r", "ATZ");
    await Send(bbs, "ATE0V0S0=1\r");
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

    public static async Task<TestRelay> StartAsync(int grace = 2, int detectMs = 0, int quietMs = 200, int reinitSeconds = 30)
    {
        var relay = new TestRelay(new RelayOptions
        {
            CallerPort = FreePort(),
            BbsPort = FreePort(),
            ReconnectGraceSeconds = grace,
            TelnetDetectMilliseconds = detectMs,
            AnswerQuietMilliseconds = quietMs,
            ReinitWaitSeconds = reinitSeconds,
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

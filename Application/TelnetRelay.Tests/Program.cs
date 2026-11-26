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

    public static async Task<TestRelay> StartAsync(int grace = 2)
    {
        var relay = new TestRelay(new RelayOptions
        {
            CallerPort = FreePort(),
            BbsPort = FreePort(),
            ReconnectGraceSeconds = grace,
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

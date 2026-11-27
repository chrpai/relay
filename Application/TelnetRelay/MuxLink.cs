using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace TelnetRelay;

/// <summary>A node that has been set aside for a caller (see <see cref="MuxLink.TryReserve"/>).</summary>
public sealed record MuxReservation(MuxLink Link, int Node);

/// <summary>
/// A BBS that connected with the mux hello ($FF $03 version nodes): one TCP
/// connection carries every node's caller, in frames. The protocol is in the
/// UltimateBoard repository, docs/mux-protocol.md.
///
///   frame = ctl len payload   ctl = type (high nibble) + node (low nibble)
///
/// The relay does the telnet work for each caller, so the BBS only sees bytes.
/// A caller that is slow to read never holds up another node: output for a
/// caller is queued per node and the queue has a limit.
/// </summary>
public sealed class MuxLink
{
    public const byte Version = 1;
    public const int MaxNodes = 4;                    // version 1 allows up to 4 nodes

    public const byte Data = 0, Call = 1, Hangup = 2, Ready = 3, Down = 4, Ping = 5, Pong = 6, Error = 15;
    public const byte ErrBadFrame = 1, ErrUnknownNode = 2, ErrVersion = 3;

    private enum NodeState { Down, Ready, Reserved, InCall }

    // one call on a node; a node's next call gets a new one, so a call that is
    // still winding down can never touch it
    private sealed class CallState
    {
        public Socket Caller = null!;
        public TelnetFilter? Filter;
        public Channel<byte[]> Out = null!;           // bytes on their way to the caller
        public long Pending;                          // bytes in Out
        public CancellationTokenSource End = null!;   // cancelled to end the call
        public bool EndedByBbs;                       // the BBS ended it (or the link died): no HANGUP to send
    }

    private sealed class Node
    {
        public NodeState State;
        public CallState? Current;
    }

    private readonly Socket _bbs;
    private readonly RelayOptions _options;
    private readonly Action<RelayLogLevel, string> _log;
    private readonly Action<MuxLink> _available;
    private readonly Node[] _nodes;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private CancellationTokenSource? _stop;
    private bool _dead;
    private long _lastRx;
    private long _pingSentAt;

    public string Who { get; }
    public int Nodes { get; }

    public MuxLink(Socket bbs, string who, int nodes, RelayOptions options,
        Action<RelayLogLevel, string> log, Action<MuxLink> available)
    {
        _bbs = bbs;
        Who = who;
        Nodes = Math.Clamp(nodes, 1, MaxNodes);
        _options = options;
        _log = log;
        _available = available;
        _nodes = new Node[Nodes + 1];
        for (int i = 0; i < _nodes.Length; i++) _nodes[i] = new Node();
    }

    // ---- the connection ---------------------------------------------------

    /// <summary>Answers the hello and runs the frame loop until the BBS goes away.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = cts;
        _lastRx = Environment.TickCount64;
        Task? keep = null;
        try
        {
            await SendBufferAsync(new byte[] { 0xFF, 0x03, Version, (byte)Nodes }, cts.Token);
            keep = KeepAliveAsync(cts.Token);
            await ReadLoopAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            cts.Cancel();
            if (keep != null) { try { await keep; } catch { } }
            Die();
        }
    }

    /// <summary>Closes the connection (a newer BBS connection took its place).</summary>
    public void Close()
    {
        try { _stop?.Cancel(); } catch (ObjectDisposedException) { }
        Die();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        int have = 0;
        while (true)
        {
            int n = await _bbs.ReceiveAsync(buffer.AsMemory(have), SocketFlags.None, ct);
            if (n == 0) return;
            have += n;

            int pos = 0;
            while (have - pos >= 2 && have - pos >= 2 + buffer[pos + 1])
            {
                byte ctl = buffer[pos];
                int len = buffer[pos + 1];
                var payload = buffer.AsSpan(pos + 2, len).ToArray();
                pos += 2 + len;
                _lastRx = Environment.TickCount64;
                Interlocked.Exchange(ref _pingSentAt, 0);
                if (!await HandleFrameAsync((byte)(ctl >> 4), ctl & 0x0F, payload, ct)) return;
            }
            if (pos > 0)
            {
                Buffer.BlockCopy(buffer, pos, buffer, 0, have - pos);
                have -= pos;
            }
        }
    }

    private async Task<bool> HandleFrameAsync(byte type, int node, byte[] payload, CancellationToken ct)
    {
        switch (type)
        {
            case Ping:
                await SendFrameAsync(Pong, node, payload, ct);
                return true;
            case Pong:
                return true;
            case Data:
            case Hangup:
            case Ready:
            case Down:
                if (node < 1 || node > Nodes)
                {
                    _log(RelayLogLevel.Warning, $"BBS {Who} sent a frame for node {node} (it has {Nodes}); closing.");
                    await TrySendErrorAsync(ErrUnknownNode);
                    return false;
                }
                break;
            default:
                _log(RelayLogLevel.Warning, $"BBS {Who} sent a frame of type {type}; closing.");
                await TrySendErrorAsync(ErrBadFrame);
                return false;
        }

        var n = _nodes[node];
        bool becameReady = false;
        switch (type)
        {
            case Data:
                FromBbs(n, node, payload);
                break;
            case Hangup:
                lock (_lock)
                {
                    var c = n.Current;
                    if (n.State != NodeState.InCall || c == null) break;
                    c.EndedByBbs = true;
                    c.Out.Writer.TryComplete();           // what is queued still goes out first
                    c.End.CancelAfter(TimeSpan.FromSeconds(10));
                }
                _log(RelayLogLevel.Information, $"BBS {Who} hung up node {node}.");
                break;
            case Ready:
                lock (_lock)
                {
                    if (n.State == NodeState.Down) { n.State = NodeState.Ready; becameReady = true; }
                }
                break;
            case Down:
                lock (_lock)
                {
                    if (n.State == NodeState.InCall && n.Current != null)
                    {
                        n.Current.EndedByBbs = true;
                        n.Current.End.Cancel();
                    }
                    n.State = NodeState.Down;
                }
                _log(RelayLogLevel.Information, $"BBS {Who} took node {node} out of service.");
                break;
        }
        if (becameReady)
        {
            _log(RelayLogLevel.Information, $"BBS {Who} node {node} is ready.");
            _available(this);
        }
        return true;
    }

    private void FromBbs(Node n, int node, byte[] payload)
    {
        CallState? c;
        lock (_lock)
        {
            c = n.Current;
            if (n.State != NodeState.InCall || c == null || c.EndedByBbs) return;   // no caller to send to
        }
        byte[] bytes = payload;
        if (c.Filter != null)
        {
            var doubled = new List<byte>(payload.Length + 8);
            TelnetFilter.ToClient(payload, doubled);
            bytes = doubled.ToArray();
        }
        if (!Enqueue(c, bytes))
        {
            _log(RelayLogLevel.Warning,
                $"Node {node}: the caller is not taking output ({_options.MuxNodeBufferBytes:N0} bytes queued); hung up.");
            c.End.Cancel();                                         // we end it, so the BBS gets HANGUP
        }
    }

    /// <summary>Queues bytes for a caller. False if the queue is over its limit.</summary>
    private bool Enqueue(CallState c, byte[] bytes)
    {
        if (Interlocked.Add(ref c.Pending, bytes.Length) > _options.MuxNodeBufferBytes) return false;
        c.Out.Writer.TryWrite(bytes);
        return true;
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        long ping = Math.Max(1, _options.MuxPingSeconds) * 1000L;
        long pong = Math.Max(1, _options.MuxPongSeconds) * 1000L;
        int step = (int)Math.Clamp(Math.Min(ping, pong) / 5, 20, 1000);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(step, ct);
            long now = Environment.TickCount64;
            long sent = Interlocked.Read(ref _pingSentAt);
            if (sent != 0)
            {
                if (now - sent >= pong)
                {
                    _log(RelayLogLevel.Warning, $"BBS {Who} did not answer a ping; closing.");
                    _stop?.Cancel();
                    return;
                }
            }
            else if (now - Interlocked.Read(ref _lastRx) >= ping)
            {
                Interlocked.Exchange(ref _pingSentAt, now);
                await SendFrameAsync(Ping, 0, Array.Empty<byte>(), ct);
            }
        }
    }

    /// <summary>The link is over: end every call without telling the BBS, close the socket.</summary>
    private void Die()
    {
        lock (_lock)
        {
            if (_dead) return;
            _dead = true;
            foreach (var n in _nodes)
            {
                if (n.State == NodeState.InCall && n.Current != null)
                {
                    n.Current.EndedByBbs = true;               // no HANGUP: nobody to receive it
                    n.Current.End.Cancel();
                }
                n.State = NodeState.Down;
            }
        }
        try { _bbs.Shutdown(SocketShutdown.Both); } catch { }
        _bbs.Dispose();
    }

    // ---- nodes and callers -------------------------------------------------

    /// <summary>The lowest numbered ready node, now reserved for a caller; null if none.</summary>
    public MuxReservation? TryReserve()
    {
        lock (_lock)
        {
            if (_dead) return null;
            for (int i = 1; i <= Nodes; i++)
            {
                if (_nodes[i].State == NodeState.Ready)
                {
                    _nodes[i].State = NodeState.Reserved;
                    return new MuxReservation(this, i);
                }
            }
        }
        return null;
    }

    /// <summary>Gives a reserved node back unused (the caller went away before the call began).</summary>
    public void Release(int node)
    {
        bool ready = false;
        lock (_lock)
        {
            if (!_dead && _nodes[node].State == NodeState.Reserved) { _nodes[node].State = NodeState.Ready; ready = true; }
        }
        if (ready) _available(this);
    }

    /// <summary>
    /// Runs a call on a reserved node until either side hangs up. False if
    /// the node was no longer available (the BBS took it down, or the link
    /// died): the caller has been sent nothing.
    /// </summary>
    public async Task<bool> CallAsync(int node, Socket caller, string who, bool telnet,
        ReadOnlyMemory<byte> early, CancellationToken ct)
    {
        var n = _nodes[node];
        var c = new CallState
        {
            Caller = caller,
            Filter = telnet ? new TelnetFilter() : null,
            Out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true }),
            End = CancellationTokenSource.CreateLinkedTokenSource(ct),
        };
        lock (_lock)
        {
            if (_dead || n.State != NodeState.Reserved) return false;
            n.State = NodeState.InCall;
            n.Current = c;
        }

        Task? writer = null;
        bool served = false;                                    // false: the caller has been sent nothing and is not ours to close
        try
        {
            var address = Encoding.Latin1.GetBytes(who);
            var payload = new byte[1 + Math.Min(address.Length, 100)];
            payload[0] = (byte)(telnet ? 1 : 0);
            Array.Copy(address, 0, payload, 1, payload.Length - 1);
            try
            {
                await SendFrameAsync(Call, node, payload, ct);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                c.EndedByBbs = true;                            // the link is gone: no HANGUP to send
                return false;                                   // and the caller got nothing
            }

            served = true;
            _log(RelayLogLevel.Information, $"Caller {who} joined node {node}{(telnet ? " (telnet)" : "")}.");
            if (!string.IsNullOrEmpty(_options.Messages.Connected))
                c.Out.Writer.TryWrite(Encoding.Latin1.GetBytes(_options.Messages.Connected));
            if (c.Filter != null) c.Out.Writer.TryWrite(c.Filter.Offer());

            writer = Task.Run(() => WriteToCallerAsync(c));

            var started = DateTime.UtcNow;
            try
            {
                if (early.Length > 0) await FromCallerAsync(c, node, early.ToArray(), c.End.Token);
                var buffer = new byte[1024];
                while (true)
                {
                    int r = await caller.ReceiveAsync(buffer, SocketFlags.None, c.End.Token);
                    if (r == 0) break;
                    await FromCallerAsync(c, node, buffer.AsSpan(0, r).ToArray(), c.End.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }

            _log(RelayLogLevel.Information,
                $"Call from {who} on node {node} ended after {DateTime.UtcNow - started:hh\\:mm\\:ss}.");
            return true;
        }
        finally
        {
            c.End.Cancel();
            if (writer != null) { try { await writer; } catch { } }

            bool tell;
            lock (_lock)
            {
                tell = !c.EndedByBbs && !_dead && n.Current == c && n.State == NodeState.InCall;
                if (n.Current == c)
                {
                    n.Current = null;
                    if (n.State == NodeState.InCall) n.State = NodeState.Down;   // until the BBS says READY
                }
            }
            if (tell && !ct.IsCancellationRequested)
            {
                try { await SendFrameAsync(Hangup, node, Array.Empty<byte>(), CancellationToken.None); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
            if (served)
            {
                try { caller.Shutdown(SocketShutdown.Both); } catch { }
                caller.Dispose();
            }
        }
    }

    // queue -> caller, one writer per call so sends to the caller never overlap
    private async Task WriteToCallerAsync(CallState c)
    {
        try
        {
            await foreach (var bytes in c.Out.Reader.ReadAllAsync(c.End.Token))
            {
                int sent = 0;
                while (sent < bytes.Length)
                    sent += await c.Caller.SendAsync(bytes.AsMemory(sent), SocketFlags.None, c.End.Token);
                Interlocked.Add(ref c.Pending, -bytes.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { c.End.Cancel(); }             // the caller is gone
        catch (ObjectDisposedException) { c.End.Cancel(); }
        finally
        {
            bool byBbs;
            lock (_lock) byBbs = c.EndedByBbs;
            if (byBbs) c.End.Cancel();                          // the BBS hung up and everything it sent is out
        }
    }

    private async Task FromCallerAsync(CallState c, int node, byte[] bytes, CancellationToken ct)
    {
        byte[] data = bytes;
        if (c.Filter != null)
        {
            var list = new List<byte>(bytes.Length);
            var reply = new List<byte>();
            c.Filter.FromClient(bytes, list, reply);
            if (reply.Count > 0) Enqueue(c, reply.ToArray());
            data = list.ToArray();
        }
        if (data.Length == 0) return;

        // frames of up to 255 bytes, sent in one write
        int frames = (data.Length + 254) / 255;
        var buffer = new byte[data.Length + 2 * frames];
        int o = 0;
        for (int i = 0; i < data.Length; i += 255)
        {
            int len = Math.Min(255, data.Length - i);
            buffer[o++] = (byte)((Data << 4) | node);
            buffer[o++] = (byte)len;
            Buffer.BlockCopy(data, i, buffer, o, len);
            o += len;
        }
        await SendBufferAsync(buffer, ct);
    }

    // ---- writing frames ----------------------------------------------------

    private Task SendFrameAsync(byte type, int node, byte[] payload, CancellationToken ct)
    {
        var buffer = new byte[2 + payload.Length];
        buffer[0] = (byte)((type << 4) | (node & 0x0F));
        buffer[1] = (byte)payload.Length;
        Buffer.BlockCopy(payload, 0, buffer, 2, payload.Length);
        return SendBufferAsync(buffer, ct);
    }

    private async Task SendBufferAsync(byte[] buffer, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            int sent = 0;
            while (sent < buffer.Length)
                sent += await _bbs.SendAsync(buffer.AsMemory(sent), SocketFlags.None, ct);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task TrySendErrorAsync(byte code)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendFrameAsync(Error, 0, new[] { code }, t.Token);
        }
        catch { }
    }

    /// <summary>Sends an ERROR frame on a connection that has not become a link (a bad hello).</summary>
    public static async Task RejectAsync(Socket bbs, byte code, CancellationToken ct)
    {
        try
        {
            await bbs.SendAsync(new byte[] { (byte)(Error << 4), 1, code }, SocketFlags.None, ct);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}

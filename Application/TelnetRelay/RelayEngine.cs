using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TelnetRelay;

public enum RelayLogLevel { Information, Warning, Error }

/// <summary>
/// A single-line relay. The BBS dials out to <see cref="RelayOptions.BbsPort"/>
/// and waits. When a caller connects to <see cref="RelayOptions.CallerPort"/>,
/// the relay sends the BBS a CONNECT line and copies bytes both ways until
/// either side hangs up. Callers who can't be joined get the Busy or Offline
/// message.
///
/// The engine has no dependencies outside the base class library, so it can be
/// hosted by the Windows service or by a test harness.
/// </summary>
public sealed class RelayEngine
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly RelayOptions _options;
    private readonly Action<RelayLogLevel, string> _log;

    private readonly object _gate = new();
    private Socket? _waitingBbs;                                  // BBS connected and idle
    private TaskCompletionSource<Socket>? _callerWaitingForBbs;   // caller inside the reconnect grace period
    private int _activeCalls;

    public RelayEngine(RelayOptions options, Action<RelayLogLevel, string> log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>Set once both ports are listening; handy for tests.</summary>
    public Task Started => _started.Task;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(CancellationToken ct)
    {
        using var bbsListener = Listen(_options.BbsPort);
        using var callerListener = Listen(_options.CallerPort);

        _log(RelayLogLevel.Information,
            $"Relay started. Callers on port {_options.CallerPort}, BBS on port {_options.BbsPort}.");
        _started.TrySetResult();

        try
        {
            await Task.WhenAll(
                AcceptLoopAsync(bbsListener, OnBbsAsync, ct),
                AcceptLoopAsync(callerListener, OnCallerAsync, ct));
        }
        finally
        {
            Socket? waiting;
            lock (_gate)
            {
                waiting = _waitingBbs;
                _waitingBbs = null;
            }
            if (waiting != null) Close(waiting);
            _log(RelayLogLevel.Information, "Relay stopped.");
        }
    }

    private static Socket Listen(int port)
    {
        Socket socket;
        if (Socket.OSSupportsIPv6)
        {
            // One dual-mode socket takes both IPv4 and IPv6 connections.
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            socket.DualMode = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        else
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        socket.Listen(16);
        return socket;
    }

    private async Task AcceptLoopAsync(Socket listener, Func<Socket, CancellationToken, Task> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log(RelayLogLevel.Warning, $"Accept failed: {ex.Message}");
                continue;
            }

            Tune(accepted);
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(accepted, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Close(accepted);
                }
                catch (Exception ex)
                {
                    _log(RelayLogLevel.Error, $"Unexpected error: {ex}");
                    Close(accepted);
                }
            }, CancellationToken.None);
        }
    }

    // ---- BBS side ---------------------------------------------------------

    private Task OnBbsAsync(Socket bbs, CancellationToken ct)
    {
        string who = Describe(bbs);
        TaskCompletionSource<Socket>? handTo = null;
        Socket? replaced = null;

        lock (_gate)
        {
            if (_callerWaitingForBbs != null)
            {
                handTo = _callerWaitingForBbs;
                _callerWaitingForBbs = null;
            }
            else
            {
                replaced = _waitingBbs;
                _waitingBbs = bbs;
            }
        }

        if (handTo != null)
        {
            _log(RelayLogLevel.Information, $"BBS connected from {who}; a caller was waiting.");
            handTo.TrySetResult(bbs);
        }
        else
        {
            _log(RelayLogLevel.Information, $"BBS connected from {who}; waiting for a caller.");
            if (replaced != null)
            {
                _log(RelayLogLevel.Warning, $"Dropped the previous idle BBS connection from {Describe(replaced)}.");
                Close(replaced);
            }
        }
        return Task.CompletedTask;
    }

    // ---- Caller side ------------------------------------------------------

    private async Task OnCallerAsync(Socket caller, CancellationToken ct)
    {
        string who = Describe(caller);
        Socket? bbs = null;
        bool busy = false;
        TaskCompletionSource<Socket>? wait = null;

        lock (_gate)
        {
            bbs = TakeWaitingBbs();
            if (bbs != null)
            {
                _activeCalls++;
            }
            else if (_activeCalls > 0 || _callerWaitingForBbs != null)
            {
                busy = true;
            }
            else
            {
                wait = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
                _callerWaitingForBbs = wait;
            }
        }

        if (busy)
        {
            _log(RelayLogLevel.Information, $"Caller {who} got the busy message.");
            await SendAndCloseAsync(caller, _options.Messages.Busy, ct);
            return;
        }

        if (wait != null)
        {
            // Not connected and not on a call: the BBS may be redialing after
            // the last caller, so give it a moment before calling it offline.
            await Task.WhenAny(wait.Task, Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.ReconnectGraceSeconds)), ct));

            bool timedOut = false;
            lock (_gate)
            {
                if (_callerWaitingForBbs == wait)
                {
                    _callerWaitingForBbs = null;
                    timedOut = true;
                }
            }

            if (timedOut)
            {
                _log(RelayLogLevel.Information, $"Caller {who} got the offline message.");
                await SendAndCloseAsync(caller, _options.Messages.Offline, ct);
                return;
            }

            // The BBS took our place in line before we gave up, so the result is set.
            bbs = await wait.Task;
            lock (_gate) _activeCalls++;
        }

        try
        {
            await RunCallAsync(caller, bbs!, who, ct);
        }
        finally
        {
            lock (_gate) _activeCalls--;
        }
    }

    /// <summary>Takes the idle BBS connection if it is still alive. Call under _gate.</summary>
    private Socket? TakeWaitingBbs()
    {
        var bbs = _waitingBbs;
        _waitingBbs = null;
        if (bbs == null) return null;
        if (IsAlive(bbs)) return bbs;

        _log(RelayLogLevel.Information, $"The idle BBS connection from {Describe(bbs)} had closed.");
        Close(bbs);
        return null;
    }

    private async Task RunCallAsync(Socket caller, Socket bbs, string who, CancellationToken ct)
    {
        _log(RelayLogLevel.Information, $"Caller {who} connected to the BBS.");
        var started = DateTime.UtcNow;
        try
        {
            await SendTextAsync(caller, _options.Messages.Connected, ct);
            await SendTextAsync(bbs, _options.BbsConnectLine, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var up = PumpAsync(caller, bbs, cts.Token);
            var down = PumpAsync(bbs, caller, cts.Token);

            var first = await Task.WhenAny(up, down);
            cts.Cancel();
            long toBbs = await up;
            long toCaller = await down;

            string side = first == up ? "caller" : "BBS";
            var length = DateTime.UtcNow - started;
            _log(RelayLogLevel.Information,
                $"Call from {who} ended by the {side} after {length:hh\\:mm\\:ss}. " +
                $"{toBbs:N0} bytes to the BBS, {toCaller:N0} bytes to the caller.");
        }
        catch (SocketException ex)
        {
            _log(RelayLogLevel.Information, $"Call from {who} ended: {ex.Message}");
        }
        finally
        {
            Close(caller);
            Close(bbs);
        }
    }

    private static async Task<long> PumpAsync(Socket from, Socket to, CancellationToken ct)
    {
        var buffer = new byte[4096];
        long total = 0;
        try
        {
            while (true)
            {
                int n = await from.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (n == 0) break;
                int sent = 0;
                while (sent < n)
                    sent += await to.SendAsync(buffer.AsMemory(sent, n - sent), SocketFlags.None, ct);
                total += n;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        return total;
    }

    // ---- Helpers ----------------------------------------------------------

    private static async Task SendTextAsync(Socket socket, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Latin1.GetBytes(text);
        int sent = 0;
        while (sent < bytes.Length)
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, ct);
    }

    /// <summary>
    /// Sends a message and closes politely. Telnet clients send option
    /// negotiation as soon as they connect; closing with those bytes unread
    /// makes Windows reset the connection, which can throw away the message
    /// before the caller sees it. So: send, half-close, drain briefly, close.
    /// </summary>
    private static async Task SendAndCloseAsync(Socket socket, string text, CancellationToken ct)
    {
        try
        {
            await SendTextAsync(socket, text, ct);
            socket.Shutdown(SocketShutdown.Send);

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drain.CancelAfter(TimeSpan.FromSeconds(2));
            var buffer = new byte[512];
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, drain.Token) > 0) { }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            Close(socket);
        }
    }

    private static void Tune(Socket socket)
    {
        socket.NoDelay = true; // keystrokes go out at once
        try
        {
            // Notice a peer that vanished without closing (power off, router
            // dropped the connection) within about 45 seconds. Also keeps the
            // idle BBS connection open through home routers.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
    }

    private static bool IsAlive(Socket socket)
    {
        try
        {
            // Readable with nothing to read means the peer closed.
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (SocketException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    private static string Describe(Socket socket)
    {
        try
        {
            if (socket.RemoteEndPoint is IPEndPoint ep)
            {
                var address = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                return address.ToString();
            }
        }
        catch (ObjectDisposedException) { }
        return "unknown";
    }

    private static void Close(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        socket.Dispose();
    }
}

using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using UnifiedPicoModule.Core;
using UnifiedPicoModule.Protocol;

namespace UnifiedPicoModule.Transport;

// All mutation is serialized by Gate; readers only see atomic, immutable SourceSnapshot instances.
// Network polling runs exclusively on a background worker and never blocks SDK initialization.
public abstract class UdpTrackingSource : ITrackingSource
{
    protected readonly object Gate = new();
    protected SourceSnapshot Current;
    protected volatile bool Ending;
    private readonly Action<string> log;
    private readonly ConcurrentQueue<string> logMessages = new();
    private int queuedLogs, drainingLogs;
    private readonly List<Socket> sockets = [];
    private Socket[] activeSockets = [];
    private readonly Dictionary<string, double> warningTimes = new(StringComparer.Ordinal);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool started;
    private ulong? eyeTimestamp, faceTimestamp;
    private double nextDiagnostic, rateStart;
    private long eyeCount, faceCount, auxiliaryCount;

    protected UdpTrackingSource(SourceKind source, Action<string> log)
    {
        Current = SourceSnapshot.Empty(source);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public SourceSnapshot Snapshot => Volatile.Read(ref Current);

    public void Start(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(Ending, this);
            if (started) return;
            started = true;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cancellation.Token;
            worker = Task.Run(() => Run(token));
        }
    }

    protected abstract void OpenSockets();
    protected abstract void Tick(double now);
    protected abstract void Receive(Socket socket, IPEndPoint sender, ReadOnlySpan<byte> bytes, double now);
    protected virtual void StopOwnedSession() { }

    protected Socket Bind(string address, int port)
    {
        ObjectDisposedException.ThrowIf(Ending, this);
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"ListenAddress must be an IPv4 literal: {address}");
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.ReceiveBufferSize = 64 * 1024;
            socket.SendTimeout = 100;
            socket.Bind(new IPEndPoint(ip, port));
            socket.Blocking = false;
            sockets.Add(socket);
            Volatile.Write(ref activeSockets, sockets.ToArray());
            Log($"{Current.Source}: listening {address}:{port} (exclusive UDP)");
            return socket;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new InvalidOperationException($"Cannot bind UDP {address}:{port}: {ex.Message}", ex);
        }
    }

    protected static IPAddress? ParsePeer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!IPAddress.TryParse(text, out var ip) || !IsUnicast(ip))
            throw new ArgumentException($"Peer IP must be an IPv4 unicast literal: {text}");
        return ip;
    }

    protected static bool IsUnicast(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !ip.Equals(IPAddress.Broadcast);
    }

    protected static bool Usable(DecodedPacket packet) =>
        packet.Eye is { } eye && (eye.CombinedValid || eye.Left.GazeValid || eye.Right.GazeValid ||
            eye.Left.OpennessValid || eye.Right.OpennessValid || eye.Left.PupilValid || eye.Right.PupilValid) ||
        packet.Face is { } face && Enum.GetValues<PicoShape>().Any(face.Has);

    protected void Publish(SourceSnapshot snapshot)
    {
        var previous = Snapshot;
        if (!Ending || snapshot.State is ConnectionState.Stopped or ConnectionState.Faulted)
            // The bounded teardown fallback is the only writer outside Gate. If it wins the
            // publication race, discard this in-flight worker update instead of reviving frames.
            Interlocked.CompareExchange(ref Current, snapshot, previous);
    }

    protected void Reset(InputProtocol? protocol, CapabilitySnapshot capabilities, ConnectionState state, string detail)
    {
        eyeTimestamp = faceTimestamp = null;
        eyeCount = faceCount = auxiliaryCount = 0;
        rateStart = MonotonicClock.Now;
        Publish(Current with { Generation = Current.Generation + 1, Protocol = protocol,
            Capabilities = capabilities, State = state, Eye = null, Face = null, Detail = detail });
        Log($"{Current.Source}: generation={Current.Generation}, protocol={protocol?.ToString() ?? "unlocked"}, {detail}");
    }

    protected bool Commit(DecodedPacket packet)
    {
        var eye = packet.Eye;
        var face = packet.Face;
        if (eye is not null && !Advance(eye.SourceTimestamp, ref eyeTimestamp)) eye = null;
        if (face is not null && !Advance(face.SourceTimestamp, ref faceTimestamp)) face = null;
        if (eye is null && face is null)
        {
            Drop("timestamp", "Duplicate/backward reliable source timestamp rejected");
            return false;
        }
        // Structurally valid new samples are published even when every field is invalid. The
        // consuming pipeline must see this invalidation rather than keep the previous good frame.
        Publish(Current with { Eye = eye ?? Current.Eye, EyeSequence = Current.EyeSequence + (eye is null ? 0 : 1),
            Face = face ?? Current.Face, FaceSequence = Current.FaceSequence + (face is null ? 0 : 1),
            State = ConnectionState.Connected, Detail = "Validated datagrams; validity is per field" });
        if (eye is not null) eyeCount++;
        if (face is not null) { if (packet.EyeAuxiliary) auxiliaryCount++; else faceCount++; }
        return true;
    }

    private static bool Advance(ulong? timestamp, ref ulong? previous)
    {
        if (timestamp is null or 0) return true;
        if (previous is not null && timestamp.Value <= previous.Value) return false;
        previous = timestamp;
        return true;
    }

    protected void Drop(string key, string reason)
    {
        Publish(Current with { DroppedPackets = Current.DroppedPackets + 1 });
        Warn(key, reason);
    }

    protected void Warn(string key, string reason)
    {
        var now = MonotonicClock.Now;
        if (warningTimes.TryGetValue(key, out var last) && now - last < 5) return;
        warningTimes[key] = now;
        Log($"{Current.Source}: {reason}");
    }

    protected void Log(string message)
    {
        // A host-provided logging delegate may be slow. Never execute it under Gate or on the
        // acquisition worker, and bound the pending queue rather than accumulate unlimited tasks.
        if (Interlocked.Increment(ref queuedLogs) > 32)
        {
            Interlocked.Decrement(ref queuedLogs);
            return;
        }
        logMessages.Enqueue(message);
        ScheduleLogs();
    }

    private void ScheduleLogs()
    {
        if (Interlocked.CompareExchange(ref drainingLogs, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static source => source.DrainLogs(), this, false);
    }

    private void DrainLogs()
    {
        while (logMessages.TryDequeue(out var message))
        {
            Interlocked.Decrement(ref queuedLogs);
            try { log(message); } catch { /* Logging cannot terminate acquisition. */ }
        }
        Interlocked.Exchange(ref drainingLogs, 0);
        if (!logMessages.IsEmpty) ScheduleLogs();
    }

    protected bool Send(Socket socket, ReadOnlySpan<byte> bytes, IPEndPoint peer)
    {
        try { return socket.SendTo(bytes, SocketFlags.None, peer) == bytes.Length; }
        catch (SocketException ex) { Warn("send", $"UDP send to {peer} failed: {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
        return false;
    }

    private void Diagnostic(double now)
    {
        if (now < nextDiagnostic) return;
        var elapsed = now - rateStart;
        if (elapsed > 0)
        {
            var eyeAge = Current.Eye is null ? "none" : $"{(now - Current.Eye.Arrival) * 1000:F0}ms";
            var faceAge = Current.Face is null ? "none" : $"{(now - Current.Face.Arrival) * 1000:F0}ms";
            Log($"{Current.Source}: state={Current.State} protocol={Current.Protocol} generation={Current.Generation} " +
                $"E={eyeCount / elapsed:F1}Hz F={faceCount / elapsed:F1}Hz A={auxiliaryCount / elapsed:F1}Hz eyeAge={eyeAge} morphologyAge={faceAge} " +
                $"eyeMorphValid={Current.Face?.EyeValid == true} faceValid={Current.Face?.FaceValid == true} " +
                $"modeKnown={Current.Capabilities.ModeKnown} independent={Current.Capabilities.IndependentPerEye} drops={Current.DroppedPackets}");
        }
        eyeCount = faceCount = auxiliaryCount = 0;
        rateStart = now;
        nextDiagnostic = now + 5;
    }

    private void Run(CancellationToken token)
    {
        try
        {
            lock (Gate)
            {
                if (Ending || token.IsCancellationRequested) return;
                rateStart = MonotonicClock.Now;
                OpenSockets();
            }
            var buffer = new byte[2048]; // Larger than every supported packet; truncated packets fail exact lengths.
            while (!token.IsCancellationRequested && !Ending)
            {
                lock (Gate)
                {
                    if (Ending || token.IsCancellationRequested) break;
                    var now = MonotonicClock.Now;
                    Tick(now);
                    Diagnostic(now);
                }
                var readable = new List<Socket>(sockets);
                Socket.Select(readable, null, null, 100_000); // Bounded cancellation/poll interval: 100ms.
                var remaining = 64; // Bounded bursts prevent a sender from starving cancellation/control.
                foreach (var socket in readable)
                {
                    do
                    {
                        if (token.IsCancellationRequested || Ending || remaining-- <= 0) break;
                        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                        int count;
                        try { count = socket.ReceiveFrom(buffer, SocketFlags.None, ref sender); }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            // Windows can surface an earlier UDP send's ICMP error on ReceiveFrom.
                            // A missing peer is a reconnect event, not a fatal acquisition failure.
                            lock (Gate) { if (!Ending) Warn("icmp", "UDP ICMP reset; acquisition remains active"); }
                            break;
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                        {
                            lock (Gate) { if (!Ending) Drop("oversize", "Oversized datagram rejected"); }
                            continue;
                        }
                        lock (Gate)
                        {
                            if (Ending || token.IsCancellationRequested) break;
                            Receive(socket, (IPEndPoint)sender, buffer.AsSpan(0, count), MonotonicClock.Now);
                        }
                    } while (socket.Available > 0);
                }
            }
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                if (!Ending && !token.IsCancellationRequested)
                {
                    Reset(null, new(), ConnectionState.Faulted, ex.Message);
                    Log($"{Current.Source}: acquisition fault: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            lock (Gate)
            {
                if (!Ending)
                {
                    Ending = true;
                    StopOwnedSession();
                    if (Current.State != ConnectionState.Faulted)
                        Reset(null, new(), ConnectionState.Stopped, "Source cancelled");
                }
                foreach (var socket in sockets) socket.Dispose();
                cancellation?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        // Cancel before acquiring the mutation lock. A slow OS interface enumeration must not
        // make teardown wait indefinitely; socket close also interrupts select/receive.
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        var pending = worker;
        if (Monitor.TryEnter(Gate, 200))
        {
            try
            {
                if (!Ending)
                {
                    Ending = true;
                    StopOwnedSession();
                    foreach (var socket in sockets) socket.Dispose();
                    Reset(null, new(), ConnectionState.Stopped, "Source disposed");
                }
            }
            finally { Monitor.Exit(Gate); }
        }
        else
        {
            Ending = true;
            foreach (var socket in Volatile.Read(ref activeSockets)) socket.Dispose();
            var current = Snapshot;
            Volatile.Write(ref Current, current with { Generation = current.Generation + 1,
                Protocol = null, Capabilities = new(), Eye = null, Face = null,
                State = ConnectionState.Stopped, Detail = "Disposed; busy worker cleanup deferred, STOP is best-effort" });
            Log($"{current.Source}: teardown lock busy; sockets closed, STOP could not be guaranteed");
        }
        if (pending is not null && Task.CurrentId != pending.Id)
            try { pending.Wait(1000); } catch (AggregateException) { }
    }
}

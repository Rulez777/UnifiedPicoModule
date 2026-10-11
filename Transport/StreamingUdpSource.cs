using System.Net;
using System.Net.Sockets;
using UnifiedPicoModule.Configuration;
using UnifiedPicoModule.Core;
using UnifiedPicoModule.Protocol;

namespace UnifiedPicoModule.Transport;

public sealed class StreamingUdpSource : UdpTrackingSource
{
    public const int HeaderPort = 29765;
    public const int BodyPort = 29763;
    private readonly string listenAddress;
    private readonly string? configuredIP;
    private readonly bool localOnly;
    private readonly InputProtocol? configuredProtocol;
    private readonly int timeoutMs;
    private readonly Header16Decoder header;
    private readonly BodyOnlyDecoder body = new();
    private Socket? headerSocket, bodySocket;
    private IPAddress? requiredIP;
    private IPEndPoint? peer;
    private readonly HashSet<IPAddress> localAddresses = [];
    private double lastData;

    public StreamingUdpSource(StreamingSettings settings, Action<string> log)
        : this(settings, log, null) { }

    // Integration hook for an optional strict header profile. Passing null does not disable the
    // 908-byte path: it then accepts >=908-byte frames with trackingType=2 and takes the body after
    // the 16-byte header (matching SAFT), ignoring trailing bytes. It never disables the exact
    // 892-byte headerless path.
    public StreamingUdpSource(StreamingSettings settings, Action<string> log, StreamingHeaderProfile? confirmedHeader)
        : base(SourceKind.Streaming, log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        listenAddress = settings.ListenAddress;
        configuredIP = settings.IP;
        localOnly = settings.LocalOnly;
        configuredProtocol = settings.Protocol;
        timeoutMs = Math.Clamp(settings.SessionTimeoutMs, 100, 60000);
        header = new(confirmedHeader);
    }

    protected override void OpenSockets()
    {
        if (configuredProtocol is not (null or InputProtocol.StreamingHeader16 or InputProtocol.StreamingBodyOnly))
            throw new ArgumentException("Streaming.Protocol must be StreamingHeader16 or StreamingBodyOnly");
        requiredIP = ParsePeer(configuredIP);
        RefreshLocalAddresses();
        if (localOnly && requiredIP is not null && !IsLocal(requiredIP))
            throw new ArgumentException("Streaming.IP is nonlocal while LocalOnly=true");
        if (configuredProtocol != InputProtocol.StreamingBodyOnly) headerSocket = Bind(listenAddress, HeaderPort);
        if (configuredProtocol != InputProtocol.StreamingHeader16) bodySocket = Bind(listenAddress, BodyPort);
        Log("Streaming: 908+ header layout checked from pike c026aa4; measured sender PICO Connect 10.6.6 sends 912 bytes (16+896), trailing bytes ignored");
    }

    protected override void Tick(double now)
    {
        if (peer is null) return;
        if ((now - lastData) * 1000 >= timeoutMs)
        {
            peer = null;
            Reset(null, new(), ConnectionState.Searching, "Streaming session timeout; caches and protocol/IP lock cleared", timeout: true);
            RefreshLocalAddresses();
        }
        else if (now - lastData > 0.25 && Current.State != ConnectionState.MissingFrames)
            Publish(Current with { State = ConnectionState.MissingFrames, Detail = "Streaming frames missing" });
    }

    protected override void Receive(Socket receiver, IPEndPoint sender, ReadOnlySpan<byte> bytes, double now)
    {
        if (!IsUnicast(sender.Address) || requiredIP is not null && !requiredIP.Equals(sender.Address) ||
            localOnly && !IsLocal(sender.Address) || peer is not null && !peer.Equals(sender))
        {
            Drop("peer", "Streaming datagram rejected: nonlocal or mismatched configured/locked endpoint");
            return;
        }
        var protocol = ReferenceEquals(receiver, headerSocket) ? InputProtocol.StreamingHeader16 : InputProtocol.StreamingBodyOnly;
        if (Current.Protocol is not null && Current.Protocol != protocol)
        {
            Drop("protocol", "Streaming protocol is locked to the first validated source until session timeout");
            return;
        }
        // Exact heartbeat content only. Before the first frame, acknowledge an allowed local or
        // explicitly configured sender without letting the control packet capture the source lock.
        // Some producers wait for POLO before emitting data; Auto still needs a valid tracking frame.
        if (BridgeControl.IsPing(bytes))
        {
            if (Send(receiver, BridgeControl.Polo, sender)) Publish(Current with { LastControl = now });
            return;
        }
        DecodedPacket? packet;
        if (protocol == InputProtocol.StreamingHeader16)
        {
            if (!header.TryDecode(bytes, now, out packet, out var reason))
            {
                Drop("header", reason);
                return;
            }
        }
        else if (!body.TryDecode(bytes, now, out packet))
        {
            Drop("body", "StreamingBodyOnly requires exactly 892 bytes");
            return;
        }
        if (peer is null)
        {
            if (!Usable(packet!))
            {
                Drop("invalid-discovery", "Invalid streaming fields cannot establish the source");
                return;
            }
            peer = new(sender.Address, sender.Port);
            Reset(protocol, new(), ConnectionState.Connected, $"Streaming endpoint/protocol locked: {peer}");
        }
        if (Commit(packet!)) lastData = now;
    }

    private bool IsLocal(IPAddress ip) => IPAddress.IsLoopback(ip) || localAddresses.Contains(ip);

    private void RefreshLocalAddresses()
    {
        localAddresses.Clear();
        foreach (var card in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (card.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                foreach (var address in card.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork) localAddresses.Add(address.Address);
    }
}

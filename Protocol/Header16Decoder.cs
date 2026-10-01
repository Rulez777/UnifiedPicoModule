using System.Buffers.Binary;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Protocol;

// StreamingHeaderProfile is an optional strict-mode check, not a prerequisite for receiving 29765.
// When it is null, a datagram is accepted whenever it is at least 908 bytes and TrackingType == 2,
// matching the observable behavior of SAFT (LegacyConnector) and pike (Updater + DataPacket): those
// receivers ignore the header fields and just take the body after the 16-byte header. Measured
// PICO Connect 10.6.6 (faceTrackingTransferProtocol=2) sends 912-byte datagrams (16-byte header +
// 896-byte body, i.e. 4 trailing bytes beyond the documented 892), so length is a lower bound and
// trailing bytes are ignored. In default mode startCode/subType/multiPack/currentIndex/version are
// not validated; the 29765 sender has not been accepted with a documented six-field profile.
public sealed record StreamingHeaderProfile(byte StartCode1, byte StartCode2, byte SubType,
    byte MultiPack, byte CurrentIndex, ushort Version);

public sealed record StreamingHeader(byte StartCode1, byte StartCode2, byte TrackingType, byte SubType,
    byte MultiPack, byte CurrentIndex, ushort Version, ulong Timestamp);

public sealed class Header16Decoder
{
    public const int PacketLength = 908;
    private readonly StreamingHeaderProfile? profile;

    public Header16Decoder(StreamingHeaderProfile? confirmedProfile = null) => profile = confirmedProfile;

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out StreamingHeader? header)
    {
        header = null;
        if (data.Length < PacketLength) return false;
        header = new(data[0], data[1], data[2], data[3], data[4], data[5],
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8, 8)));
        return true;
    }

    public bool TryDecode(ReadOnlySpan<byte> data, double arrival, out DecodedPacket? packet) =>
        TryDecode(data, arrival, out packet, out _);

    public bool TryDecode(ReadOnlySpan<byte> data, double arrival, out DecodedPacket? packet, out string reason)
    {
        packet = null;
        reason = "Expected at least 908 bytes (16-byte header + 892-byte body; trailing bytes ignored)";
        if (!TryReadHeader(data, out var header)) return false;
        reason = "TrackingType must be face (2)";
        if (header!.TrackingType != 2) return false;
        // Default mode (no profile): accept on exact length + trackingType=2 only, like SAFT/pike.
        // A configured profile turns on the strict six-field match below.
        if (profile is { } strict)
        {
            reason = "Unknown or mismatched StreamingHeader16 magic, subtype, version or fragment fields";
            if (header.StartCode1 != strict.StartCode1 || header.StartCode2 != strict.StartCode2 ||
                header.SubType != strict.SubType || header.MultiPack != strict.MultiPack ||
                header.CurrentIndex != strict.CurrentIndex || header.Version != strict.Version) return false;
        }
        // Header timestamp is transport metadata, not the eye clock. Body supplies the face clock.
        // Measured PICO Connect 10.6.6 never sets the 29765 validity floats to 1f, so this path
        // consumes shapes without them, exactly like SAFT (LegacyConnector) and pike (Updater).
        packet = new(InputProtocol.StreamingHeader16,
            Face: WireFields.Face(data, 16, 24, 312, arrival, true, validityFromFloats: false));
        reason = "";
        return true;
    }
}

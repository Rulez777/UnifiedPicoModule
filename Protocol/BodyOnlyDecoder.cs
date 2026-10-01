using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Protocol;

public sealed class BodyOnlyDecoder
{
    public const int PacketLength = 892;

    public bool TryDecode(ReadOnlySpan<byte> data, double arrival, out DecodedPacket? packet)
    {
        packet = null;
        if (data.Length != PacketLength) return false;
        // Measured header-based senders never set the validity floats to 1f; SAFT/pike consume the
        // shapes directly on both the header and headerless paths, so this path does the same.
        packet = new(InputProtocol.StreamingBodyOnly,
            Face: WireFields.Face(data, 0, 8, 296, arrival, true, validityFromFloats: false));
        return true;
    }
}

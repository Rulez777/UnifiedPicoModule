using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Protocol;

public sealed class DaemonLegacyDecoder
{
    public const int PacketLength = 536;

    public bool TryDecode(ReadOnlySpan<byte> data, double arrival, out DecodedPacket? packet)
    {
        packet = null;
        if (data.Length != PacketLength) return false;
        // 384-byte face + 152-byte eye prefix. The old "eye timestamp" at 380 is padding,
        // not a clock. Eye status starts at 384; vector/open/pupil at 432/468/476.
        packet = new(InputProtocol.DaemonLegacy536,
            WireFields.Eye(data, 384, 432, 468, 476, null, arrival),
            WireFields.Face(data, 0, 8, 296, arrival, true));
        return true;
    }
}

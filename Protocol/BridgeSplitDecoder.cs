using UnifiedPicoModule.Core;
using System.Buffers.Binary;

namespace UnifiedPicoModule.Protocol;

// BridgeSplit: 19-byte header (tag, channel mask, morphology validity, epoch, source timestamp),
// then the subscribed shapes; X appends the 16-byte EPI1 independent-eye extension.
public sealed class BridgeSplitDecoder
{
    public const int HeaderLength = 19;
    public const int EyePacketLength = 83;
    public const int EyeAuxiliaryPacketLength = 95;
    public const int FaceOnlyPacketLength = 151;
    public const int FacePacketLength = 227;
    public const int IndependentExtensionLength = 16;
    public static ReadOnlySpan<int> EyeAuxiliaryShapes => [0, 2, 3, 4, 11, 12, 16, 26, 28, 30, 31, 35, 36, 38, 41, 44, 45, 46, 47];
    public static ReadOnlySpan<int> FaceOnlyShapes => [1, 5, 6, 7, 8, 9, 10, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23,
        24, 25, 27, 29, 32, 33, 34, 37, 39, 40, 42, 43, 48, 49, 50, 51];

    public bool TryDecode(ReadOnlySpan<byte> data, double arrival, out DecodedPacket? packet)
    {
        packet = null;
        if (data.Length < HeaderLength || data[0] is not ((byte)'E' or (byte)'F' or (byte)'A' or (byte)'X')) return false;
        int mask = data[1], flags = data[2];
        var epoch = WireFields.Timestamp(data, 3);
        var timestamp = WireFields.Timestamp(data, 11);
        if (mask is < 1 or > 3 || (flags & ~mask) != 0 || epoch is null || timestamp is null) return false;
        if (data[0] == (byte)'E')
        {
            if (data.Length != EyePacketLength || (mask & 1) == 0 || flags != 0) return false;
            // Only the four defined state bits; reject unknown flags and non-finite payloads.
            for (var i = 19; i < 31; i += 4)
                if ((BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)) & ~0x906u) != 0) return false;
            for (var i = 31; i < data.Length; i += 4) if (!float.IsFinite(WireFields.Float(data, i))) return false;
            packet = new(InputProtocol.BridgeSplit, Eye: WireFields.Eye(data, 19, 31, 67, 75, timestamp, arrival),
                Epoch: epoch, ChannelMask: mask);
            return true;
        }
        bool independent = data[0] == (byte)'X';
        bool auxiliary = data[0] == (byte)'A' || independent && mask == 1;
        bool faceOnly = mask == 2;
        int extensionLength = independent ? IndependentExtensionLength : 0;
        if (independent && mask is not (1 or 3)) return false;
        if (auxiliary ? data.Length != EyeAuxiliaryPacketLength + extensionLength || mask != 1 :
            data.Length != (faceOnly ? FaceOnlyPacketLength : FacePacketLength) + extensionLength || (mask & 2) == 0) return false;
        var values = new float[52]; var present = new bool[52];
        int count = auxiliary ? EyeAuxiliaryShapes.Length : faceOnly ? FaceOnlyShapes.Length : 52;
        for (var i = 0; i < count; i++)
        {
            var value = WireFields.Float(data, HeaderLength + i * 4);
            if (!float.IsFinite(value)) return false;
            int index = auxiliary ? EyeAuxiliaryShapes[i] : faceOnly ? FaceOnlyShapes[i] : i;
            values[index] = value; present[index] = true;
        }
        IndependentEyeExpression? independentEyes = null;
        if (independent)
        {
            int offset = HeaderLength + count * 4;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)) != 1) return false;
            var rightInner = WireFields.Float(data, offset + 4);
            int leftState = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 8, 4));
            int rightState = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 12, 4));
            if (!float.IsFinite(rightInner) || leftState is < -1 or > 1 || rightState is < -1 or > 1) return false;
            independentEyes = new(rightInner, leftState, rightState);
        }
        packet = new(InputProtocol.BridgeSplit, Face: new(values, present, (flags & 1) != 0, (flags & 2) != 0,
            timestamp, arrival, independentEyes: independentEyes), Epoch: epoch, ChannelMask: mask, EyeAuxiliary: auxiliary);
        return true;
    }
}

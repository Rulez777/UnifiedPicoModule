using System.Buffers.Binary;
using System.Numerics;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Protocol;

public sealed record DecodedPacket(InputProtocol Protocol, RawEye? Eye = null, RawFace? Face = null,
    ulong? Epoch = null, int? ChannelMask = null, bool EyeAuxiliary = false);

// Explicit little-endian reads, never native struct marshalling or inferred extension lengths.
internal static class WireFields
{
    internal static float Float(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));

    internal static ulong? Timestamp(ReadOnlySpan<byte> data, int offset)
    {
        // The observed vendor service timestamps are positive signed 64-bit values. Zero and
        // negative sentinel values are not reliable clocks and must use arrival sequence instead.
        var value = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
        return value > 0 ? (ulong)value : null;
    }

    internal static RawFace Face(ReadOnlySpan<byte> data, int timestamp, int shapes, int validity,
        double arrival, bool keepExtra, bool validityFromFloats = true)
    {
        var values = new float[52];
        var present = new bool[52];
        for (var i = 0; i < values.Length; i++)
        {
            var value = Float(data, shapes + i * 4);
            present[i] = float.IsFinite(value);
            values[i] = present[i] ? value : 0;
        }
        float[]? extra = keepExtra ? new float[20] : null;
        if (extra is not null)
            for (var i = 0; i < extra.Length; i++)
            {
                var value = Float(data, shapes + (52 + i) * 4);
                // No extra-slot presence mask exists in the shared contract; NaN denotes absent.
                extra[i] = float.IsFinite(value) ? value : float.NaN;
            }
        // Some senders (measured: PICO Connect 10.6.6 on 29765) never set the validity floats to 1f.
        // SAFT/pike ignore them and consume the shapes directly, so the header16 path can opt out.
        var eyeValid = validityFromFloats ? Float(data, validity) == 1f : true;
        var faceValid = validityFromFloats ? Float(data, validity + 4) == 1f : true;
        return new(values, present, eyeValid, faceValid,
            Timestamp(data, timestamp), arrival, extra);
    }

    internal static RawEye Eye(ReadOnlySpan<byte> data, int status, int vectors, int openness,
        int pupil, ulong? timestamp, double arrival)
    {
        var left = OneEye(data, status, vectors, openness, pupil);
        var right = OneEye(data, status + 4, vectors + 12, openness + 4, pupil + 4);
        var combined = Vector(data, vectors + 24);
        var combinedStatus = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(status + 8, 4));
        var combinedValid = (combinedStatus & 2) != 0 && Finite(combined);
        return new(left, right, combinedValid ? combined : Vector3.Zero, combinedValid, timestamp, arrival);
    }

    private static NativeEye OneEye(ReadOnlySpan<byte> data, int statusOffset, int vectorOffset,
        int opennessOffset, int pupilOffset)
    {
        var status = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(statusOffset, 4));
        var vector = Vector(data, vectorOffset);
        var gazeValid = (status & 0x100) != 0 && Finite(vector);
        var openness = Float(data, opennessOffset);
        var openValid = (status & 4) != 0 && float.IsFinite(openness) && openness is >= 0 and <= 1;
        var pupil = Float(data, pupilOffset) / 10f;
        var pupilValid = (status & 0x800) != 0 && float.IsFinite(pupil) && pupil > 0;
        return new(gazeValid ? vector : Vector3.Zero, gazeValid, openValid ? openness : 0,
            openValid, pupilValid ? pupil : 0, pupilValid);
    }

    private static Vector3 Vector(ReadOnlySpan<byte> data, int offset) =>
        new(Float(data, offset), Float(data, offset + 4), Float(data, offset + 8));
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

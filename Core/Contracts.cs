using System.Diagnostics;
using System.Numerics;

namespace UnifiedPicoModule.Core;

public enum SourceKind { Bridge, Streaming }
public enum InputProtocol { BridgeSplit, DaemonLegacy536, StreamingHeader16, StreamingBodyOnly }
public enum ConnectionState { Searching, Connected, MissingFrames, Stopped, Faulted }
public enum EyePolicy { NativePreferred, LegacyNative, BlendshapeOnly }
public enum SourceSelection { Auto, Bridge, Streaming }

// These numeric identifiers describe the PICO wire format, not an SDK enum.
public enum PicoShape
{
    EyeLookDownL, NoseSneerL, EyeLookInL, BrowInnerUp, BrowDownR, MouthClose,
    MouthLowerDownR, JawOpen, MouthUpperUpR, MouthShrugUpper, MouthFunnel,
    EyeLookInR, EyeLookDownR, NoseSneerR, MouthRollUpper, JawRight, BrowDownL,
    MouthShrugLower, MouthRollLower, MouthSmileL, MouthPressL, MouthSmileR,
    MouthPressR, MouthDimpleR, MouthLeft, JawForward, EyeSquintL, MouthFrownL,
    EyeBlinkL, CheekSquintL, BrowOuterUpL, EyeLookUpL, JawLeft, MouthStretchL,
    MouthPucker, EyeLookUpR, BrowOuterUpR, CheekSquintR, EyeBlinkR, MouthUpperUpL,
    MouthFrownR, EyeSquintR, MouthStretchR, CheekPuff, EyeLookOutL, EyeLookOutR,
    EyeWideR, EyeWideL, MouthRight, MouthDimpleL, MouthLowerDownL, TongueOut
}

// Module-owned expression identifiers; translated by name only at the SDK boundary.
public enum ExpressionChannel
{
    EyeSquintLeft, EyeSquintRight, EyeWideLeft, EyeWideRight,
    BrowPinchLeft, BrowPinchRight, BrowLowererLeft, BrowLowererRight,
    BrowInnerUpLeft, BrowInnerUpRight, BrowOuterUpLeft, BrowOuterUpRight,
    CheekSquintLeft, CheekSquintRight, CheekPuffLeft, CheekPuffRight,
    JawOpen, JawLeft, JawRight, JawForward, MouthClosed,
    LipSuckUpperLeft, LipSuckUpperRight, LipSuckLowerLeft, LipSuckLowerRight,
    LipFunnelUpperLeft, LipFunnelUpperRight, LipFunnelLowerLeft, LipFunnelLowerRight,
    LipPuckerUpperLeft, LipPuckerUpperRight, LipPuckerLowerLeft, LipPuckerLowerRight,
    MouthUpperUpLeft, MouthUpperUpRight, MouthUpperDeepenLeft, MouthUpperDeepenRight,
    NoseSneerLeft, NoseSneerRight, MouthLowerDownLeft, MouthLowerDownRight,
    MouthUpperLeft, MouthUpperRight, MouthLowerLeft, MouthLowerRight,
    MouthCornerPullLeft, MouthCornerPullRight, MouthCornerSlantLeft, MouthCornerSlantRight,
    MouthFrownLeft, MouthFrownRight, MouthStretchLeft, MouthStretchRight,
    MouthDimpleLeft, MouthDimpleRight, MouthRaiserUpper, MouthRaiserLower,
    MouthPressLeft, MouthPressRight, TongueOut
}

public static class MonotonicClock
{
    public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    public static bool Fresh(double arrival, double now, int milliseconds) =>
        arrival > 0 && now >= arrival && (now - arrival) * 1000 <= milliseconds;
}

public sealed class RawFace
{
    private readonly float[] values;
    private readonly bool[] present;
    private readonly float[] extra;
    public ulong? SourceTimestamp { get; }
    public double Arrival { get; }
    public bool EyeValid { get; }
    public bool FaceValid { get; }
    public RawFace(float[] values, bool[] present, bool eyeValid, bool faceValid,
        ulong? sourceTimestamp, double arrival, float[]? extraVisemes = null)
    {
        if (values.Length != 52 || present.Length != 52) throw new ArgumentException("Expected 52 PICO shapes.");
        this.values = (float[])values.Clone(); this.present = (bool[])present.Clone();
        extra = extraVisemes is null ? [] : (float[])extraVisemes.Clone();
        EyeValid = eyeValid; FaceValid = faceValid; SourceTimestamp = sourceTimestamp; Arrival = arrival;
    }
    public float this[PicoShape shape] => values[(int)shape];
    public bool Has(PicoShape shape) => present[(int)shape] && float.IsFinite(this[shape]) &&
        (IsEyeShape(shape) ? EyeValid : FaceValid);
    public ReadOnlySpan<float> ExtraVisemes => extra;
    public RawFace WithValidity(bool eyeEnabled, bool faceEnabled) =>
        new(values, present, EyeValid && eyeEnabled, FaceValid && faceEnabled, SourceTimestamp, Arrival, extra);
    public static bool IsEyeShape(PicoShape shape) => shape is PicoShape.EyeLookDownL or PicoShape.EyeLookInL or
        PicoShape.BrowInnerUp or PicoShape.BrowDownR or PicoShape.EyeLookInR or PicoShape.EyeLookDownR or
        PicoShape.BrowDownL or PicoShape.EyeSquintL or PicoShape.EyeBlinkL or PicoShape.BrowOuterUpL or
        PicoShape.EyeLookUpL or PicoShape.EyeLookUpR or PicoShape.BrowOuterUpR or PicoShape.EyeBlinkR or
        PicoShape.EyeSquintR or PicoShape.EyeLookOutL or PicoShape.EyeLookOutR or PicoShape.EyeWideR or PicoShape.EyeWideL;
}

public sealed record NativeEye(Vector3 Gaze, bool GazeValid, float Openness, bool OpennessValid,
    float PupilMillimeters, bool PupilValid);
public sealed record RawEye(NativeEye Left, NativeEye Right, Vector3 CombinedGaze, bool CombinedValid,
    ulong? SourceTimestamp, double Arrival);
public sealed record CapabilitySnapshot(bool ModeKnown = false, bool Enhanced = false,
    bool IndependentPerEye = false, string Plugin = "off");
public sealed record SourceSnapshot(SourceKind Source, long Generation, InputProtocol? Protocol,
    ConnectionState State, CapabilitySnapshot Capabilities, RawEye? Eye, long EyeSequence,
    RawFace? Face, long FaceSequence, double LastControl, long DroppedPackets, string Detail)
{
    public static SourceSnapshot Empty(SourceKind kind) => new(kind, 0, null, ConnectionState.Searching,
        new(), null, 0, null, 0, 0, 0, "Waiting for validated input");
}
public interface ITrackingSource : IDisposable
{
    SourceSnapshot Snapshot { get; }
    void Start(CancellationToken cancellationToken);
}

public sealed class ExpressionFrame
{
    private readonly float[] values = new float[Enum.GetValues<ExpressionChannel>().Length];
    public float this[ExpressionChannel channel] { get => values[(int)channel]; set => values[(int)channel] = value; }
    public ExpressionFrame Clone()
    {
        var copy = new ExpressionFrame(); Array.Copy(values, copy.values, values.Length); return copy;
    }
    public void Clamp(float maximum = 1)
    {
        for (var i = 0; i < values.Length; i++) values[i] = float.IsFinite(values[i]) ? Math.Clamp(values[i], 0, maximum) : 0;
    }
    public static bool IsEye(ExpressionChannel channel) => (int)channel <= (int)ExpressionChannel.BrowOuterUpRight;
}
public sealed record ResolvedEye(float GazeX = 0, float GazeY = 0, float Openness = 1,
    float PupilMillimeters = 3.5f, string GazeOrigin = "neutral", bool RealPupil = false);
public sealed record TrackingOutput(ExpressionFrame Expressions, ResolvedEye Left, ResolvedEye Right);

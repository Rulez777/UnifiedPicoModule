using System.Numerics;
using UnifiedPicoModule.Configuration;
using UnifiedPicoModule.Core;
using UnifiedPicoModule.Processing;

namespace UnifiedPicoModule.Mapping;

public sealed class EyeResolver(ModuleConfig config)
{
    private enum GazeSource { NativeCombined, NativePerEye, Blendshape }

    private sealed class EyeState
    {
        public Vector2 Gaze;
        public float Openness = 1;
        public float Pupil;
        public double GazeTime, OpennessTime, PupilTime;
        public int GazeExpiryMs, OpennessExpiryMs;
        public string Origin = "neutral";
        public string OpennessOrigin = "neutral";
    }
    private EyeState left = new(), right = new();
    private readonly AdaptiveGazeFilter leftFilter = new(config.GazeSmoothing), rightFilter = new(config.GazeSmoothing);
    private RawFace? smoothingFace;
    private GazeSource? smoothingSource;
    private bool smoothingActive;
    public (double Left, double Right)? SmoothingWeights => smoothingActive ? (leftFilter.Weight, rightFilter.Weight) : null;
    public void Reset() { left = new(); right = new(); ResetSmoothing(); }
    public void ResetSmoothing()
    {
        leftFilter.Reset(); rightFilter.Reset(); smoothingFace = null; smoothingSource = null; smoothingActive = false;
    }

    public (ResolvedEye Left, ResolvedEye Right) Resolve(SourceSnapshot snapshot, double now)
    {
        var native = snapshot.Eye is { } e && MonotonicClock.Fresh(e.Arrival, now, config.Stale.EyeMs) ? e : null;
        var face = snapshot.Face is { } f && MonotonicClock.Fresh(f.Arrival, now, config.Stale.FaceMs) ? f : null;
        var gazeSource = SelectGazeSource(snapshot, config);
        var nativeOpenness = NativeOpennessPreferred(snapshot, config);
        var smooth = config.GazeSmoothing.Enabled && !config.DisableEyeTracking && gazeSource != GazeSource.Blendshape;
        if (smooth != smoothingActive || smoothingSource != gazeSource)
        { ResetSmoothing(); smoothingActive = smooth; smoothingSource = gazeSource; }
        if (smooth && face is not null && face.Has(PicoShape.EyeBlinkL) && face.Has(PicoShape.EyeBlinkR) &&
            face.SourceTimestamp is > 0 && (smoothingFace?.SourceTimestamp is not { } previous || face.SourceTimestamp > previous))
            smoothingFace = face;
        var combinedLid = smooth && gazeSource == GazeSource.NativeCombined && native is not null
            ? CombinedLid(native, now) : null;
        return (ResolveSide(left, leftFilter, native?.Left, native, face, true, gazeSource, nativeOpenness, combinedLid, now),
            ResolveSide(right, rightFilter, native?.Right, native, face, false, gazeSource, nativeOpenness, combinedLid, now));
    }

    private ResolvedEye ResolveSide(EyeState state, AdaptiveGazeFilter filter, NativeEye? eye, RawEye? frame, RawFace? face,
        bool isLeft, GazeSource gazeSource, bool nativeOpenness, double? combinedLid, double now)
    {
        if (gazeSource == GazeSource.Blendshape) DeriveGaze(state, face, isLeft);
        else if (frame is not null)
        {
            // Invalid samples leave this eye's last valid value and reception time unchanged.
            var gaze = NativeGaze(eye, frame, gazeSource);
            Vector2? xy = gaze is { } vector ? new(vector.X, vector.Y) : null;
            var observed = xy is not null;
            if (smoothingActive && frame.SourceTimestamp is > 0)
            {
                var lid = gazeSource == GazeSource.NativePerEye ? PerEyeLid(eye) : combinedLid;
                var settings = config.GazeSmoothing;
                var curveLow = gazeSource == GazeSource.NativePerEye ? settings.OpennessHigh : settings.BlinkLow;
                var curveHigh = gazeSource == GazeSource.NativePerEye ? settings.OpennessLow : settings.BlinkHigh;
                // Repeated polls and reordered source frames cannot advance gaze, derivatives or eyelids.
                xy = filter.Update(frame.SourceTimestamp.Value, xy, lid, curveLow, curveHigh, out observed,
                    useDualOverrides: gazeSource == GazeSource.NativePerEye);
            }
            // Legacy536 has no reliable eye source clock: bypass rather than use reception time.
            if (observed && xy is { } value)
            {
                state.Gaze = value; state.GazeTime = frame.Arrival;
                state.GazeExpiryMs = config.Stale.EyeMs;
                state.Origin = gazeSource == GazeSource.NativePerEye ? "native-per-eye" : "native-combined";
            }
        }

        // Select eyelids by policy/mode, never by this frame's validity. Missing data
        // keeps this eye's last value; Auto uses native only in confirmed enhanced mode.
        if (nativeOpenness && eye is { OpennessValid: true } &&
            frame is not null && float.IsFinite(eye.Openness))
        {
            state.Openness = eye.Openness; state.OpennessTime = frame.Arrival; state.OpennessExpiryMs = config.Stale.EyeMs;
            state.OpennessOrigin = "native-openness";
        }
        else if (!nativeOpenness && face is not null && face.Has(isLeft ? PicoShape.EyeBlinkL : PicoShape.EyeBlinkR))
        {
            state.Openness = 1 - face[isLeft ? PicoShape.EyeBlinkL : PicoShape.EyeBlinkR];
            state.OpennessTime = face.Arrival; state.OpennessExpiryMs = config.Stale.FaceMs;
            state.OpennessOrigin = "blendshape-blink";
        }

        // Real measurement validity is independent of openness and gaze. No 0..1 pupil clamp.
        if (frame is not null && eye is not null && HasRealPupil(eye))
        {
            state.Pupil = eye.PupilMillimeters; state.PupilTime = frame.Arrival;
        }
        var gazeAvailable = CanKeep(state.GazeTime, now, state.GazeExpiryMs);
        var opennessAvailable = CanKeep(state.OpennessTime, now, state.OpennessExpiryMs);
        var pupilAvailable = CanKeep(state.PupilTime, now, config.Stale.EyeMs);
        var gain = config.EyeGain;
        return new ResolvedEye(
            gazeAvailable ? Safe(state.Gaze.X * (isLeft ? gain.LeftX : gain.RightX), -1, 1) : 0,
            gazeAvailable ? Safe(state.Gaze.Y * (isLeft ? gain.LeftY : gain.RightY), -1, 1) : 0,
            opennessAvailable ? Safe(state.Openness * (isLeft ? gain.LeftOpenness : gain.RightOpenness), 0, 1) : 1,
            pupilAvailable ? state.Pupil : config.Stale.NeutralPupilMillimeters,
            gazeAvailable ? state.Origin : "neutral", pupilAvailable,
            opennessAvailable ? state.OpennessOrigin : "neutral");
    }

    private double? CombinedLid(RawEye frame, double now)
    {
        if (frame.SourceTimestamp is not { } eyeNs || smoothingFace is not { SourceTimestamp: { } faceNs } face ||
            !MonotonicClock.Fresh(face.Arrival, now, config.Stale.FaceMs)) return null;
        var difference = eyeNs >= faceNs ? eyeNs - faceNs : faceNs - eyeNs;
        if (difference > Math.Min(250, config.Stale.FaceMs) * 1e6) return null;
        return Math.Max(face[PicoShape.EyeBlinkL], face[PicoShape.EyeBlinkR]);
    }
    private double? PerEyeLid(NativeEye? eye) => eye is { OpennessValid: true } && float.IsFinite(eye.Openness)
        ? eye.Openness
        : null;

    private bool CanKeep(double arrival, double now, int expiryMs) =>
        arrival > 0 && now >= arrival &&
        (!config.Stale.NeutralizeOnTimeout || MonotonicClock.Fresh(arrival, now, expiryMs));

    private void DeriveGaze(EyeState state, RawFace? face, bool isLeft)
    {
        if (face is null) return;
        var inside = isLeft ? PicoShape.EyeLookInL : PicoShape.EyeLookInR;
        var outside = isLeft ? PicoShape.EyeLookOutL : PicoShape.EyeLookOutR;
        var up = isLeft ? PicoShape.EyeLookUpL : PicoShape.EyeLookUpR;
        var down = isLeft ? PicoShape.EyeLookDownL : PicoShape.EyeLookDownR;
        if (!face.Has(inside) || !face.Has(outside) || !face.Has(up) || !face.Has(down)) return;
        // Existing source coordinate convention, deliberately no normalization or convergence synthesis.
        state.Gaze = new(isLeft ? face[inside] - face[outside] : face[outside] - face[inside], face[up] - face[down]);
        state.GazeTime = face.Arrival; state.GazeExpiryMs = config.Stale.FaceMs; state.Origin = "blendshape-derived";
    }
    // Protocol and capabilities are fixed within a source generation. Select without consulting
    // frame availability or validity; mode/protocol changes reset the pipeline's cached state.
    private static GazeSource SelectGazeSource(SourceSnapshot snapshot, ModuleConfig settings)
    {
        if (settings.EyePolicy == EyePolicy.BlendshapeOnly ||
            settings.EyePolicy == EyePolicy.NativePreferred &&
            snapshot.Protocol is InputProtocol.StreamingHeader16 or InputProtocol.StreamingBodyOnly)
            return GazeSource.Blendshape;
        return IndependentAllowed(snapshot, settings) ? GazeSource.NativePerEye : GazeSource.NativeCombined;
    }
    private static Vector3? NativeGaze(NativeEye? eye, RawEye frame, GazeSource source) => source switch
    {
        GazeSource.NativePerEye when eye is { GazeValid: true } && Finite(eye.Gaze) => eye.Gaze,
        GazeSource.NativeCombined when frame.CombinedValid && Finite(frame.CombinedGaze) => frame.CombinedGaze,
        _ => null
    };
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    private static bool HasRealPupil(NativeEye eye) => eye.PupilValid && float.IsFinite(eye.PupilMillimeters) && eye.PupilMillimeters is >= 1 and <= 12;
    private static bool IndependentAllowed(SourceSnapshot snapshot, ModuleConfig settings) =>
        snapshot.Protocol == InputProtocol.BridgeSplit &&
            snapshot.Capabilities is { ModeKnown: true, Enhanced: true, IndependentPerEye: true } ||
        snapshot.Protocol == InputProtocol.DaemonLegacy536 && settings.LegacyPerEyeGaze;
    private static bool NativeOpennessPreferred(SourceSnapshot snapshot, ModuleConfig settings) =>
        settings.OpennessPolicy == OpennessPolicy.NativePreferred ||
        settings.OpennessPolicy == OpennessPolicy.Auto && snapshot.Capabilities is { ModeKnown: true, Enhanced: true };

    // Source selection uses the same policy, capability and unit checks as resolution.
    // A valid wire field that this policy ignores must not capture Auto's sticky lock.
    public static bool HasConsumableNative(SourceSnapshot snapshot, ModuleConfig settings)
    {
        if (snapshot.Eye is not { } eye) return false;
        if (HasRealPupil(eye.Left) || HasRealPupil(eye.Right)) return true;
        var gazeSource = SelectGazeSource(snapshot, settings);
        if (NativeGaze(eye.Left, eye, gazeSource) is not null ||
            NativeGaze(eye.Right, eye, gazeSource) is not null) return true;
        return NativeOpennessPreferred(snapshot, settings) &&
            (eye.Left.OpennessValid && float.IsFinite(eye.Left.Openness) ||
                eye.Right.OpennessValid && float.IsFinite(eye.Right.Openness));
    }
    public static bool HasConsumableFace(SourceSnapshot snapshot, ModuleConfig settings)
    {
        if (snapshot.Face is not { } face) return false;
        if (!NativeOpennessPreferred(snapshot, settings) &&
            (face.Has(PicoShape.EyeBlinkL) || face.Has(PicoShape.EyeBlinkR))) return true;
        if (SelectGazeSource(snapshot, settings) != GazeSource.Blendshape) return false;
        return face.Has(PicoShape.EyeLookInL) && face.Has(PicoShape.EyeLookOutL) && face.Has(PicoShape.EyeLookUpL) && face.Has(PicoShape.EyeLookDownL) ||
            face.Has(PicoShape.EyeLookInR) && face.Has(PicoShape.EyeLookOutR) && face.Has(PicoShape.EyeLookUpR) && face.Has(PicoShape.EyeLookDownR);
    }
    private static float Safe(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : Math.Clamp(0, min, max);
}

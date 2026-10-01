using System.Numerics;
using UnifiedPicoModule.Configuration;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Mapping;

public sealed class EyeResolver(ModuleConfig config)
{
    private sealed class EyeState
    {
        public Vector2 Gaze;
        public float Openness = 1;
        public float Pupil;
        public double GazeTime, OpennessTime, PupilTime;
        public int GazeExpiryMs, OpennessExpiryMs;
        public string Origin = "neutral";
    }
    private EyeState left = new(), right = new();
    public void Reset() { left = new(); right = new(); }

    public (ResolvedEye Left, ResolvedEye Right) Resolve(SourceSnapshot snapshot, double now)
    {
        var native = snapshot.Eye is { } e && MonotonicClock.Fresh(e.Arrival, now, config.Stale.EyeMs) ? e : null;
        var face = snapshot.Face is { } f && MonotonicClock.Fresh(f.Arrival, now, config.Stale.FaceMs) ? f : null;
        var independent = IndependentAllowed(snapshot, config);
        return (ResolveSide(left, native?.Left, native, face, true, independent, now),
            ResolveSide(right, native?.Right, native, face, false, independent, now));
    }

    private ResolvedEye ResolveSide(EyeState state, NativeEye? eye, RawEye? frame, RawFace? face,
        bool isLeft, bool independent, double now)
    {
        bool nativePolicy = config.EyePolicy != EyePolicy.BlendshapeOnly;
        if (nativePolicy && frame is not null && eye is not null)
        {
            Vector3? gaze = independent && eye.GazeValid && Finite(eye.Gaze) ? eye.Gaze :
                frame.CombinedValid && Finite(frame.CombinedGaze) ? frame.CombinedGaze : null;
            if (gaze is { } vector)
            {
                state.Gaze = new(vector.X, vector.Y); state.GazeTime = frame.Arrival;
                state.GazeExpiryMs = config.Stale.EyeMs;
                state.Origin = independent && eye.GazeValid ? "native-per-eye" : "native-combined";
            }
            else if (config.EyePolicy == EyePolicy.NativePreferred) DeriveGaze(state, face, isLeft);
        }
        else if (config.EyePolicy != EyePolicy.LegacyNative) DeriveGaze(state, face, isLeft);

        if (nativePolicy && eye is { OpennessValid: true } && frame is not null && float.IsFinite(eye.Openness))
        {
            state.Openness = eye.Openness; state.OpennessTime = frame.Arrival; state.OpennessExpiryMs = config.Stale.EyeMs;
        }
        else if (face is not null && face.Has(isLeft ? PicoShape.EyeBlinkL : PicoShape.EyeBlinkR))
        {
            state.Openness = 1 - face[isLeft ? PicoShape.EyeBlinkL : PicoShape.EyeBlinkR];
            state.OpennessTime = face.Arrival; state.OpennessExpiryMs = config.Stale.FaceMs;
        }

        // Real measurement validity is independent of openness and gaze. No 0..1 pupil clamp.
        if (frame is not null && eye is not null && HasRealPupil(eye))
        {
            state.Pupil = eye.PupilMillimeters; state.PupilTime = frame.Arrival;
        }
        var gazeFresh = MonotonicClock.Fresh(state.GazeTime, now, state.GazeExpiryMs);
        var opennessFresh = MonotonicClock.Fresh(state.OpennessTime, now, state.OpennessExpiryMs);
        var pupilFresh = MonotonicClock.Fresh(state.PupilTime, now, config.Stale.EyeMs);
        var gain = config.EyeGain;
        return new ResolvedEye(
            gazeFresh ? Safe(state.Gaze.X * (isLeft ? gain.LeftX : gain.RightX), -1, 1) : 0,
            gazeFresh ? Safe(state.Gaze.Y * (isLeft ? gain.LeftY : gain.RightY), -1, 1) : 0,
            opennessFresh ? Safe(state.Openness * (isLeft ? gain.LeftOpenness : gain.RightOpenness), 0, 1) : 1,
            pupilFresh ? state.Pupil : config.Stale.NeutralPupilMillimeters,
            gazeFresh ? state.Origin : "neutral", pupilFresh);
    }

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
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    private static bool HasRealPupil(NativeEye eye) => eye.PupilValid && float.IsFinite(eye.PupilMillimeters) && eye.PupilMillimeters is >= 1 and <= 12;
    private static bool IndependentAllowed(SourceSnapshot snapshot, ModuleConfig settings) =>
        snapshot.Capabilities is { ModeKnown: true, Enhanced: true, IndependentPerEye: true } ||
        snapshot.Protocol == InputProtocol.DaemonLegacy536 && settings.LegacyPerEyeGaze;

    // Source selection uses the same policy, capability and unit checks as resolution.
    // A valid wire field that this policy ignores must not capture Auto's sticky lock.
    public static bool HasConsumableNative(SourceSnapshot snapshot, ModuleConfig settings)
    {
        if (snapshot.Eye is not { } eye) return false;
        if (HasRealPupil(eye.Left) || HasRealPupil(eye.Right)) return true;
        if (settings.EyePolicy == EyePolicy.BlendshapeOnly) return false;
        if (eye.CombinedValid && Finite(eye.CombinedGaze)) return true;
        if (IndependentAllowed(snapshot, settings) &&
            (eye.Left.GazeValid && Finite(eye.Left.Gaze) || eye.Right.GazeValid && Finite(eye.Right.Gaze))) return true;
        return eye.Left.OpennessValid && float.IsFinite(eye.Left.Openness) ||
            eye.Right.OpennessValid && float.IsFinite(eye.Right.Openness);
    }
    public static bool HasConsumableFace(RawFace face, ModuleConfig settings)
    {
        if (face.Has(PicoShape.EyeBlinkL) || face.Has(PicoShape.EyeBlinkR)) return true;
        if (settings.EyePolicy == EyePolicy.LegacyNative) return false;
        return face.Has(PicoShape.EyeLookInL) && face.Has(PicoShape.EyeLookOutL) && face.Has(PicoShape.EyeLookUpL) && face.Has(PicoShape.EyeLookDownL) ||
            face.Has(PicoShape.EyeLookInR) && face.Has(PicoShape.EyeLookOutR) && face.Has(PicoShape.EyeLookUpR) && face.Has(PicoShape.EyeLookDownR);
    }
    private static float Safe(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : Math.Clamp(0, min, max);
}

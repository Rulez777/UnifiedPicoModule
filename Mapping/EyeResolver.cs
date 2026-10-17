using System.Numerics;
using UnifiedPicoModule.Configuration;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Mapping;

// Gaze and eyelids always come from the face expression model: gaze from the
// EyeLook shapes and openness from 1 - EyeBlink. Native eye packets only supply
// a measured pupil diameter.
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
        public string OpennessOrigin = "neutral";
        public bool IndependentGaze, IndependentOpenness;
    }
    private EyeState left = new(), right = new();
    public void Reset() { left = new(); right = new(); }

    public (ResolvedEye Left, ResolvedEye Right) Resolve(SourceSnapshot snapshot, double now)
    {
        var native = snapshot.Eye is { } e && MonotonicClock.Fresh(e.Arrival, now, config.Stale.EyeMs) ? e : null;
        var face = snapshot.Face is { } f && MonotonicClock.Fresh(f.Arrival, now, config.Stale.FaceMs) ? f : null;
        var rightFace = snapshot.GroupedExpressions
            ? snapshot.RightFace is { } r && MonotonicClock.Fresh(r.Arrival, now, config.Stale.FaceMs) ? r : null
            : face;
        bool independentExpressions = snapshot.GroupedExpressions || snapshot.Face?.IndependentEyes is not null;
        return (ResolveSide(left, native?.Left, native, face, true, independentExpressions, now),
            ResolveSide(right, native?.Right, native, rightFace, false, independentExpressions, now));
    }

    private ResolvedEye ResolveSide(EyeState state, NativeEye? eye, RawEye? frame, RawFace? face,
        bool isLeft, bool independentExpressions, double now)
    {
        // A first invalid X must not inherit a previously fused/native or ordinary
        // expression sample. Existing independent history belongs to this eye.
        if (independentExpressions && !state.IndependentGaze)
        { state.Gaze = Vector2.Zero; state.GazeTime = 0; state.Origin = "neutral"; }
        if (independentExpressions && !state.IndependentOpenness)
        { state.Openness = 1; state.OpennessTime = 0; state.OpennessOrigin = "neutral"; }
        DeriveGaze(state, face, isLeft);
        if (face is not null && face.Has(isLeft ? PicoShape.EyeBlinkL : PicoShape.EyeBlinkR))
        {
            state.Openness = 1 - face[isLeft ? PicoShape.EyeBlinkL : PicoShape.EyeBlinkR];
            state.OpennessTime = face.Arrival; state.OpennessExpiryMs = config.Stale.FaceMs;
            state.OpennessOrigin = "blendshape-blink";
            state.IndependentOpenness = face.IndependentEyes is not null;
        }

        // Real measurement validity is independent of the expression-derived outputs.
        // No 0..1 pupil clamp.
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
        state.IndependentGaze = face.IndependentEyes is not null;
    }
    private static bool HasRealPupil(NativeEye eye) => eye.PupilValid && float.IsFinite(eye.PupilMillimeters) && eye.PupilMillimeters is >= 1 and <= 12;

    // Native eye packets count as consumable only when they carry a measured pupil.
    // Gaze and openness fields in these packets do not drive the avatar.
    public static bool HasConsumableNative(SourceSnapshot snapshot, ModuleConfig settings)
    {
        if (snapshot.Eye is not { } eye) return false;
        return HasRealPupil(eye.Left) || HasRealPupil(eye.Right);
    }
    public static bool HasConsumableFace(SourceSnapshot snapshot, ModuleConfig settings)
    {
        if (snapshot.Face is not { } face) return false;
        if (face.Has(PicoShape.EyeBlinkL) || face.Has(PicoShape.EyeBlinkR)) return true;
        return face.Has(PicoShape.EyeLookInL) && face.Has(PicoShape.EyeLookOutL) && face.Has(PicoShape.EyeLookUpL) && face.Has(PicoShape.EyeLookDownL) ||
            face.Has(PicoShape.EyeLookInR) && face.Has(PicoShape.EyeLookOutR) && face.Has(PicoShape.EyeLookUpR) && face.Has(PicoShape.EyeLookDownR);
    }
    private static float Safe(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : Math.Clamp(0, min, max);
}

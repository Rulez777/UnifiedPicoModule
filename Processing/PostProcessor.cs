using UnifiedPicoModule.Configuration;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Processing;

/// <summary>
/// Independently implemented from the project's 2026-10-01 post-processing specification,
/// not from SAFT, pike or lonelyicer implementation code. Contains no SDK dependency.
/// The caller supplies validated settings and calls Process only for a new F sample.
/// Single-consumer instance: reset on stale input, session/mode/config changes or output stop.
/// </summary>
public sealed class PostProcessor
{
    private static readonly ExpressionChannel[] Channels = Enum.GetValues<ExpressionChannel>();
    private readonly PostProcessingSettings settings;
    private bool mouthInitialized;
    private double mouthTime;
    private float mouthLeft;
    private float mouthRight;
    private bool tongueWasHigh;
    private bool tongueHolding;
    private float tongueHigh;
    private double tongueDeadline;
    private float tongueExpiredOutput;
    private bool tongueExpirationPending;

    public PostProcessor(PostProcessingSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public ExpressionFrame Process(RawFace raw, ExpressionFrame neutral, double now)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(neutral);
        RequireTime(now);
        var output = neutral.Clone();

        // Face loss must not suppress valid eye-region expressions from this same F.
        if (!raw.FaceValid)
        {
            ResetMouth();
            ResetTongue();
        }

        ApplyBrowInnerSeparation(raw, output);
        ApplyBrowDownResponse(raw, output);
        ApplySquintResponse(raw, output);
        ApplyFrownJawAttenuation(raw, output);
        ApplyShrugToFrown(raw, output);
        ApplyUpperUpPressGate(raw, output);
        ApplyPuckerFunnelAssist(raw, output);
        ApplySmileRollSuppression(raw, output);
        ApplyStretchCompetition(raw, output);
        ApplySymmetry(raw, output);
        ApplyCheekAsymmetry(raw, output, now);

        // Retain the unheld tongue, then apply exactly the same downstream multiplier once.
        // Expire substitutes this already finalized value without rerunning any rule.
        var unheldTongue = output[ExpressionChannel.TongueOut];
        ApplyTongueHold(raw, output, now);
        var tongueGate = ApplyTongueJawGate(raw, output);
        ApplyChannelGain(output);
        tongueExpiredOutput = SafeUnit(unheldTongue * tongueGate * ChannelGain(ExpressionChannel.TongueOut));
        output.Clamp();
        return output;
    }

    /// <summary>
    /// Call only while the cached F remains fresh. Returns a copy even before expiry.
    /// Does not rerun nonlinear rules, advance mouth smoothing or apply gain twice.
    /// Repeated calls also repair copies of the original held snapshot after expiration.
    /// </summary>
    public ExpressionFrame Expire(ExpressionFrame frame, double now)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RequireTime(now);
        var output = frame.Clone();
        if (tongueExpirationPending && now >= tongueDeadline)
        {
            tongueHolding = false;
            output[ExpressionChannel.TongueOut] = tongueExpiredOutput;
        }
        return output;
    }

    public void Reset()
    {
        ResetMouth();
        ResetTongue();
    }

    private void ApplyBrowInnerSeparation(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.BrowInnerSeparation;
        if (!rule.Enabled) return;
        if (raw.HasInnerBrow(true) && raw.Has(PicoShape.BrowOuterUpL))
            output[ExpressionChannel.BrowOuterUpLeft] = Math.Max(0, output[ExpressionChannel.BrowOuterUpLeft] - raw.InnerBrow(true) * rule.Strength);
        if (raw.HasInnerBrow(false) && raw.Has(PicoShape.BrowOuterUpR))
            output[ExpressionChannel.BrowOuterUpRight] = Math.Max(0, output[ExpressionChannel.BrowOuterUpRight] - raw.InnerBrow(false) * rule.Strength);
    }

    private void ApplyBrowDownResponse(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.BrowDownResponse;
        if (!rule.Enabled) return;
        bool useAsymmetry = raw.IndependentEyes is null && rule.BlinkAsymmetryStrength > 0;
        if (useAsymmetry &&
            (!raw.Has(PicoShape.EyeBlinkL) || !raw.Has(PicoShape.EyeBlinkR))) return;
        Apply(PicoShape.BrowDownL, PicoShape.EyeBlinkL, PicoShape.EyeBlinkR,
            ExpressionChannel.BrowLowererLeft, ExpressionChannel.BrowPinchLeft);
        Apply(PicoShape.BrowDownR, PicoShape.EyeBlinkR, PicoShape.EyeBlinkL,
            ExpressionChannel.BrowLowererRight, ExpressionChannel.BrowPinchRight);

        void Apply(PicoShape brow, PicoShape blink, PicoShape otherBlink,
            ExpressionChannel lowerer, ExpressionChannel pinch)
        {
            if (!raw.Has(brow)) return;
            var value = NormalizedSigmoid(raw[brow], rule.Center, rule.Slope) * rule.Gain;
            if (useAsymmetry)
                value *= 1 - rule.BlinkAsymmetryStrength * Math.Clamp(raw[otherBlink] - raw[blink], 0, 1);
            output[lowerer] = value;
            output[pinch] = value;
        }
    }

    private void ApplySquintResponse(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.SquintResponse;
        if (!rule.Enabled) return;
        Apply(PicoShape.EyeSquintL, PicoShape.EyeBlinkL, ExpressionChannel.EyeSquintLeft);
        Apply(PicoShape.EyeSquintR, PicoShape.EyeBlinkR, ExpressionChannel.EyeSquintRight);

        void Apply(PicoShape squint, PicoShape blink, ExpressionChannel channel)
        {
            if (!raw.Has(squint) || (rule.BlinkInfluence > 0 && !raw.Has(blink))) return;
            var value = MathF.Pow(Math.Clamp(raw[squint], 0, 1), rule.Gamma) * rule.Gain;
            if (rule.BlinkInfluence > 0)
                value *= 1 - rule.BlinkInfluence * (1 - Math.Clamp(raw[blink], 0, 1));
            output[channel] = value;
        }
    }

    private void ApplyFrownJawAttenuation(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.FrownJawAttenuation;
        if (!rule.Enabled || !raw.Has(PicoShape.JawOpen)) return;
        var multiplier = 1 - rule.MaxReduction * Above(raw[PicoShape.JawOpen], rule.JawThreshold, rule.TransitionWidth);
        if (raw.Has(PicoShape.MouthFrownL)) output[ExpressionChannel.MouthFrownLeft] *= multiplier;
        if (raw.Has(PicoShape.MouthFrownR)) output[ExpressionChannel.MouthFrownRight] *= multiplier;
    }

    private void ApplyShrugToFrown(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.ShrugToFrown;
        if (!rule.Enabled || !raw.Has(PicoShape.MouthShrugUpper) || !raw.Has(PicoShape.MouthShrugLower) ||
            !raw.Has(PicoShape.MouthRollUpper) || !raw.Has(PicoShape.MouthRollLower) ||
            !raw.Has(PicoShape.MouthPucker) || !raw.Has(PicoShape.MouthFunnel)) return;
        var upper = Math.Clamp((raw[PicoShape.MouthShrugUpper] - rule.Threshold) / rule.Range, 0, 1);
        var lower = Math.Clamp((raw[PicoShape.MouthShrugLower] - rule.Threshold) / rule.Range, 0, 1);
        var roll = Math.Max(raw[PicoShape.MouthRollUpper], raw[PicoShape.MouthRollLower]);
        var allowed = Below(roll, rule.RollBlockThreshold, rule.TransitionWidth) *
            Below(raw[PicoShape.MouthPucker], rule.PuckerBlockThreshold, rule.TransitionWidth) *
            Below(raw[PicoShape.MouthFunnel], rule.FunnelBlockThreshold, rule.TransitionWidth);
        var candidate = Math.Max(upper, lower) * rule.Gain * allowed;
        output[ExpressionChannel.MouthFrownLeft] = Math.Max(output[ExpressionChannel.MouthFrownLeft], candidate);
        output[ExpressionChannel.MouthFrownRight] = Math.Max(output[ExpressionChannel.MouthFrownRight], candidate);
        if (rule.ConsumeShrug)
        {
            var remaining = 1 - Math.Clamp(candidate, 0, 1);
            output[ExpressionChannel.MouthRaiserUpper] *= remaining;
            output[ExpressionChannel.MouthRaiserLower] *= remaining;
        }
    }

    private void ApplyUpperUpPressGate(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.UpperUpPressGate;
        if (!rule.Enabled) return;
        Apply(PicoShape.MouthUpperUpL, PicoShape.MouthPressL,
            ExpressionChannel.MouthUpperUpLeft, ExpressionChannel.MouthUpperDeepenLeft);
        Apply(PicoShape.MouthUpperUpR, PicoShape.MouthPressR,
            ExpressionChannel.MouthUpperUpRight, ExpressionChannel.MouthUpperDeepenRight);

        void Apply(PicoShape upper, PicoShape press, ExpressionChannel up, ExpressionChannel deepen)
        {
            if (!raw.Has(upper) || !raw.Has(press)) return;
            var multiplier = 1 - rule.MaxReduction * Above(raw[press], rule.PressThreshold, rule.TransitionWidth);
            MultiplyPair(output, up, deepen, multiplier);
        }
    }

    private void ApplyPuckerFunnelAssist(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.PuckerFunnelAssist;
        if (!rule.Enabled || !raw.Has(PicoShape.MouthPucker) || !raw.Has(PicoShape.MouthFunnel)) return;
        var pucker = raw[PicoShape.MouthPucker];
        var active = Above(pucker, rule.PuckerThreshold, rule.TransitionWidth) * rule.Strength;
        Apply(PicoShape.MouthPressL, ExpressionChannel.LipFunnelUpperLeft, ExpressionChannel.LipFunnelLowerLeft);
        Apply(PicoShape.MouthPressR, ExpressionChannel.LipFunnelUpperRight, ExpressionChannel.LipFunnelLowerRight);

        void Apply(PicoShape press, ExpressionChannel upper, ExpressionChannel lower)
        {
            if (!raw.Has(press)) return;
            var weight = active * Below(raw[press], rule.PressThreshold, rule.TransitionWidth);
            output[upper] = Lerp(output[upper], Math.Max(output[upper], pucker), weight);
            output[lower] = Lerp(output[lower], Math.Max(output[lower], pucker), weight);
        }
    }

    private void ApplySmileRollSuppression(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.SmileRollSuppression;
        if (!rule.Enabled || !raw.Has(PicoShape.MouthRollLower)) return;
        var multiplier = 1 - rule.MaxReduction * Above(raw[PicoShape.MouthRollLower], rule.RollThreshold, rule.TransitionWidth);
        if (raw.Has(PicoShape.MouthSmileL))
            MultiplyPair(output, ExpressionChannel.MouthCornerPullLeft, ExpressionChannel.MouthCornerSlantLeft, multiplier);
        if (raw.Has(PicoShape.MouthSmileR))
            MultiplyPair(output, ExpressionChannel.MouthCornerPullRight, ExpressionChannel.MouthCornerSlantRight, multiplier);
    }

    private void ApplyStretchCompetition(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.StretchCompetition;
        if (!rule.Enabled) return;
        Apply(PicoShape.MouthStretchL, PicoShape.MouthSmileL, PicoShape.MouthUpperUpL,
            ExpressionChannel.MouthCornerPullLeft, ExpressionChannel.MouthCornerSlantLeft,
            ExpressionChannel.MouthUpperUpLeft, ExpressionChannel.MouthUpperDeepenLeft);
        Apply(PicoShape.MouthStretchR, PicoShape.MouthSmileR, PicoShape.MouthUpperUpR,
            ExpressionChannel.MouthCornerPullRight, ExpressionChannel.MouthCornerSlantRight,
            ExpressionChannel.MouthUpperUpRight, ExpressionChannel.MouthUpperDeepenRight);

        void Apply(PicoShape stretch, PicoShape smile, PicoShape upperUp,
            ExpressionChannel pull, ExpressionChannel slant, ExpressionChannel up, ExpressionChannel deepen)
        {
            if (!raw.Has(stretch) || !raw.Has(smile)) return;
            var weight = Above(raw[stretch], rule.StretchThreshold, rule.TransitionWidth) *
                Below(raw[smile], rule.SmileCeiling, rule.TransitionWidth);
            MultiplyPair(output, pull, slant, 1 - rule.SmileReduction * weight);
            if (raw.Has(upperUp)) MultiplyPair(output, up, deepen, 1 - rule.UpperUpReduction * weight);
        }
    }

    private void ApplySymmetry(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.Symmetry;
        if (!rule.Enabled) return;
        foreach (var group in rule.Groups)
        {
            switch (group)
            {
                case "Smile" when raw.Has(PicoShape.MouthSmileL) && raw.Has(PicoShape.MouthSmileR):
                    Pair(ExpressionChannel.MouthCornerPullLeft, ExpressionChannel.MouthCornerPullRight);
                    Pair(ExpressionChannel.MouthCornerSlantLeft, ExpressionChannel.MouthCornerSlantRight);
                    break;
                case "Frown" when raw.Has(PicoShape.MouthFrownL) && raw.Has(PicoShape.MouthFrownR):
                    Pair(ExpressionChannel.MouthFrownLeft, ExpressionChannel.MouthFrownRight);
                    break;
                case "Stretch" when raw.Has(PicoShape.MouthStretchL) && raw.Has(PicoShape.MouthStretchR):
                    Pair(ExpressionChannel.MouthStretchLeft, ExpressionChannel.MouthStretchRight);
                    break;
                case "UpperUp" when raw.Has(PicoShape.MouthUpperUpL) && raw.Has(PicoShape.MouthUpperUpR):
                    Pair(ExpressionChannel.MouthUpperUpLeft, ExpressionChannel.MouthUpperUpRight);
                    Pair(ExpressionChannel.MouthUpperDeepenLeft, ExpressionChannel.MouthUpperDeepenRight);
                    break;
            }
        }

        void Pair(ExpressionChannel left, ExpressionChannel right)
        {
            var l = output[left];
            var r = output[right];
            var target = rule.Method == "Max" ? Math.Max(l, r) : (l + r) / 2;
            output[left] = Lerp(l, target, rule.Strength);
            output[right] = Lerp(r, target, rule.Strength);
        }
    }

    private void ApplyCheekAsymmetry(RawFace raw, ExpressionFrame output, double now)
    {
        var rule = settings.CheekAsymmetry;
        if (!rule.Enabled || !raw.Has(PicoShape.MouthLeft) || !raw.Has(PicoShape.MouthRight) ||
            !raw.Has(PicoShape.CheekPuff))
        {
            ResetMouth();
            return;
        }
        var left = raw[PicoShape.MouthLeft];
        var right = raw[PicoShape.MouthRight];
        if (!mouthInitialized || now < mouthTime || rule.MouthSmoothingMs == 0)
        {
            mouthLeft = left;
            mouthRight = right;
            mouthInitialized = true;
        }
        else
        {
            // Exact exponential time-constant response, independent of the F frame rate.
            var alpha = (float)(1 - Math.Exp(-(now - mouthTime) / (rule.MouthSmoothingMs / 1000d)));
            mouthLeft = Lerp(mouthLeft, left, alpha);
            mouthRight = Lerp(mouthRight, right, alpha);
        }
        mouthTime = now;
        var difference = mouthLeft - mouthRight;
        // Only the directional condition is smoothed; the amount is the raw displacement.
        output[ExpressionChannel.CheekPuffRight] += left * rule.Gain *
            Above(difference, rule.MouthDifferenceThreshold, rule.TransitionWidth);
        output[ExpressionChannel.CheekPuffLeft] += right * rule.Gain *
            Above(-difference, rule.MouthDifferenceThreshold, rule.TransitionWidth);
    }

    private void ApplyTongueHold(RawFace raw, ExpressionFrame output, double now)
    {
        var rule = settings.TongueHold;
        // Missing/invalid is not a measured drop and cannot start or retain a hold.
        if (!rule.Enabled || rule.HoldMs == 0 || !raw.Has(PicoShape.TongueOut))
        {
            ResetTongue();
            return;
        }
        var tongue = raw[PicoShape.TongueOut];
        if (tongue > rule.DropThreshold)
        {
            tongueHigh = tongue;
            tongueWasHigh = true;
            tongueHolding = false;
            tongueExpirationPending = false;
            return;
        }
        if (tongueWasHigh)
        {
            tongueWasHigh = false;
            tongueHolding = true;
            tongueDeadline = now + rule.HoldMs / 1000d;
        }
        tongueExpirationPending = tongueHolding && now < tongueDeadline;
        if (tongueExpirationPending) output[ExpressionChannel.TongueOut] = tongueHigh;
        else tongueHolding = false;
    }

    private float ApplyTongueJawGate(RawFace raw, ExpressionFrame output)
    {
        var rule = settings.TongueJawGate;
        if (!rule.Enabled || !raw.Has(PicoShape.JawOpen) || !raw.Has(PicoShape.TongueOut)) return 1;
        var multiplier = 1 - rule.MaxReduction * Below(raw[PicoShape.JawOpen], rule.JawThreshold, rule.TransitionWidth);
        output[ExpressionChannel.TongueOut] *= multiplier;
        if (multiplier == 0) ResetTongue();
        return multiplier;
    }

    private void ApplyChannelGain(ExpressionFrame output)
    {
        if (!settings.ChannelGain.Enabled) return;
        foreach (var channel in Channels) output[channel] *= ChannelGain(channel);
    }

    private float ChannelGain(ExpressionChannel channel)
    {
        var rule = settings.ChannelGain;
        if (!rule.Enabled) return 1;
        var name = channel.ToString();
        if (rule.Gains.TryGetValue(name, out var gain)) return gain;
        // JSON may construct a case-sensitive Dictionary; the validator accepts names
        // case-insensitively, so lookup must agree even after deserialization.
        foreach (var entry in rule.Gains)
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)) return entry.Value;
        return rule.DefaultGain;
    }

    private void ResetMouth()
    {
        mouthInitialized = false;
        mouthTime = 0;
        mouthLeft = mouthRight = 0;
    }

    private void ResetTongue()
    {
        tongueWasHigh = tongueHolding = tongueExpirationPending = false;
        tongueHigh = tongueExpiredOutput = 0;
        tongueDeadline = 0;
    }

    private static void RequireTime(double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now), "Expected finite monotonic seconds.");
    }

    private static float Above(float value, float threshold, float width)
    {
        if (width == 0) return value > threshold ? 1 : 0;
        var lower = Math.Max(0, (double)threshold - width / 2d);
        var upper = Math.Min(1, (double)threshold + width / 2d);
        // A positive but sub-ULP width must not produce 0/0 and wipe a valid output.
        if (upper <= lower) return value > threshold ? 1 : 0;
        var x = Math.Clamp((value - lower) / (upper - lower), 0, 1);
        return (float)(x * x * (3 - 2 * x));
    }

    private static float Below(float value, float threshold, float width) => 1 - Above(value, threshold, width);
    private static float Lerp(float from, float to, float weight) => from + (to - from) * weight;
    private static float SafeUnit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static float NormalizedSigmoid(float value, float center, float slope)
    {
        // Double precision preserves endpoint normalization even near Center=0/1, Slope=40.
        var x = Math.Clamp(value, 0, 1);
        var lower = Sigmoid(-slope * (double)center);
        var upper = Sigmoid(slope * (1d - center));
        return (float)((Sigmoid(slope * ((double)x - center)) - lower) / (upper - lower));
    }

    private static double Sigmoid(double value) => 1 / (1 + Math.Exp(-value));
    private static void MultiplyPair(ExpressionFrame output, ExpressionChannel first, ExpressionChannel second, float multiplier)
    {
        output[first] *= multiplier;
        output[second] *= multiplier;
    }
}

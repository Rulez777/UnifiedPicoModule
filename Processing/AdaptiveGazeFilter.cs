using System.Numerics;
using UnifiedPicoModule.Configuration;

namespace UnifiedPicoModule.Processing;

// Independently implemented from the project's adaptive OneEuro specification.
// Mode-specific defaults live in GazeSmoothingSettings. One instance per eye;
// only source nanoseconds advance state. Strength follows a continuous curve of time-smoothed
// eyelid input after rolling opening-side confirmation, with no separate attack/release or gaze gate.
public sealed class AdaptiveGazeFilter(GazeSmoothingSettings settings)
{
    private ulong? lastSeen, lastValid, lidTime;
    private readonly LinkedList<(ulong SourceNs, double Value)> lidWindow = new();
    private double x, y, previousX, previousY, derivativeX, derivativeY, lid;
    public double Weight { get; private set; }

    public void Reset()
    {
        lastSeen = lastValid = lidTime = null;
        lidWindow.Clear();
        x = y = previousX = previousY = derivativeX = derivativeY = lid = Weight = 0;
    }

    public Vector2? Update(ulong sourceNs, Vector2? input, double? rawLid, double curveLow, double curveHigh,
        out bool observed, bool useDualOverrides = false)
    {
        observed = false;
        if (lastSeen is { } seen && sourceNs <= seen) return Value;
        lastSeen = sourceNs;
        if (rawLid is { } known && double.IsFinite(known))
        {
            var confirmed = ConfirmLid(sourceNs, known, curveHigh > curveLow);
            if (lidTime is not { } time || settings.LidSmoothingMs == 0) lid = confirmed;
            else lid += (1 - Math.Exp(-(double)(sourceNs - time) / (settings.LidSmoothingMs * 1e6))) * (confirmed - lid);
            lidTime = sourceNs;
        }
        // Unknown eyelids retain the smoothed value and its timestamp. A known eyelid still
        // advances on an invalid gaze frame; a gaze gap reset below does not reset eyelids.
        Weight = lidTime is null ? 0 : SmoothStep(lid, curveLow, curveHigh);

        if (input is not { } value || !float.IsFinite(value.X) || !float.IsFinite(value.Y)) return Value;
        observed = true;
        var gap = lastValid is { } valid ? (double)(sourceNs - valid) : double.PositiveInfinity;
        lastValid = sourceNs;
        if (gap > settings.ResetGapMs * 1e6)
        {
            x = previousX = value.X; y = previousY = value.Y;
            derivativeX = derivativeY = 0;
            return Value;
        }
        var dt = gap / 1e9;
        var derivativeAlpha = Alpha(settings.DerivativeCutoffHz, dt);
        derivativeX += derivativeAlpha * ((value.X - previousX) / dt - derivativeX);
        derivativeY += derivativeAlpha * ((value.Y - previousY) / dt - derivativeY);
        var light = useDualOverrides ? settings.DualLightCutoffHz ?? settings.LightCutoffHz : settings.LightCutoffHz;
        var beta = useDualOverrides ? settings.DualBeta ?? settings.Beta : settings.Beta;
        var minCutoff = light * Math.Pow(settings.HeavyCutoffHz / light, Weight);
        var cutoff = minCutoff + beta * Math.Sqrt(derivativeX * derivativeX + derivativeY * derivativeY);
        var alpha = Alpha(cutoff, dt);
        x += alpha * (value.X - x); y += alpha * (value.Y - y);
        previousX = value.X; previousY = value.Y;
        return Value;
    }

    private double ConfirmLid(ulong sourceNs, double raw, bool closingIncreases)
    {
        if (settings.LidConfirmMs == 0) { lidWindow.Clear(); return raw; }
        var windowNs = (ulong)settings.LidConfirmMs * 1_000_000;
        var earliest = sourceNs >= windowNs ? sourceNs - windowNs : 0;
        // Inclusive window boundary, as in the reference rolling minimum. Each known sample
        // enters and leaves at most once: O(1) amortized, independent of poll/sample count.
        while (lidWindow.First is { } first && first.Value.SourceNs < earliest) lidWindow.RemoveFirst();
        // Increasing closure (Blink) keeps a minimum; decreasing closure (openness) a maximum.
        // A newer equal or more-open sample dominates older candidates for their entire lifetime.
        while (lidWindow.Last is { } last && (closingIncreases ? last.Value.Value >= raw : last.Value.Value <= raw))
            lidWindow.RemoveLast();
        lidWindow.AddLast((sourceNs, raw));
        return lidWindow.First!.Value.Value;
    }

    private Vector2? Value => lastValid is null ? null : new Vector2((float)x, (float)y);
    private static double Alpha(double cutoffHz, double dt) => 1 / (1 + 1 / (2 * Math.PI * cutoffHz * dt));
    public static double SmoothStep(double value, double low, double high)
    {
        var q = Math.Clamp((value - low) / (high - low), 0, 1);
        return q * q * (3 - 2 * q);
    }
}

using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Runtime;

public sealed class DiagnosticReporter(Action<string> log)
{
    private double lastReport;
    private readonly Dictionary<SourceKind, (long Generation, long Eye, long Face, long Right, long Mouth)> previous = [];
    public void Report(IReadOnlyList<SourceSnapshot> snapshots, SourceSnapshot? selected, TrackingOutput output, double now,
        ConsumptionDiagnostics? consumption = null)
    {
        if (now - lastReport < 5) return;
        double elapsed = lastReport > 0 ? now - lastReport : 5;
        foreach (var snapshot in snapshots)
        {
            previous.TryGetValue(snapshot.Source, out var old);
            var sameSession = old.Generation == snapshot.Generation;
            var eyeHz = sameSession ? Math.Max(0, snapshot.EyeSequence - old.Eye) / elapsed : 0;
            var faceHz = sameSession ? Math.Max(0, snapshot.FaceSequence - old.Face) / elapsed : 0;
            var rightHz = sameSession ? Math.Max(0, snapshot.RightFaceSequence - old.Right) / elapsed : 0;
            var mouthHz = sameSession ? Math.Max(0, snapshot.MouthSequence - old.Mouth) / elapsed : 0;
            previous[snapshot.Source] = (snapshot.Generation, snapshot.EyeSequence, snapshot.FaceSequence,
                snapshot.RightFaceSequence, snapshot.MouthSequence);
            log($"{snapshot.Source}/{snapshot.Protocol}: state={snapshot.State} session={snapshot.Generation} selected={selected?.Source == snapshot.Source} " +
                $"E/morphology={eyeHz:F1}/{faceHz:F1}Hz eyeAge={Age(snapshot.Eye?.Arrival, now)} morphologyAge={Age(snapshot.Face?.Arrival, now)} " +
                $"eyeMorphValid={snapshot.Face?.EyeValid == true} faceValid={(snapshot.GroupedExpressions ? snapshot.Mouth : snapshot.Face)?.FaceValid == true} " +
                $"grouped={snapshot.GroupedExpressions} rightMorphAge={Age(snapshot.RightFace?.Arrival, now)} mouthAge={Age(snapshot.Mouth?.Arrival, now)} " +
                $"groupRxHzLRM={faceHz:F1}/{rightHz:F1}/{mouthHz:F1} " +
                $"modeKnown={snapshot.Capabilities.ModeKnown} enhance={snapshot.Capabilities.Enhanced} independent={snapshot.Capabilities.IndependentPerEye} drops={snapshot.DroppedPackets}; {snapshot.Detail}");
        }
        log($"Eye origin L/R={output.Left.GazeOrigin}/{output.Right.GazeOrigin}; openness L/R={output.Left.OpennessOrigin}/{output.Right.OpennessOrigin}; pupil L/R={(output.Left.RealPupil ? "measured" : "estimated")}/{(output.Right.RealPupil ? "measured" : "estimated")}. Connected-but-no-data cannot reliably distinguish pause from missing sensor frames.");
        if (consumption is not null) log(consumption.Report());
        lastReport = now;
    }
    private static string Age(double? arrival, double now) => arrival is { } time ? $"{Math.Max(0, now - time) * 1000:F0}ms" : "none";
}

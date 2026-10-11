using UnifiedPicoModule.Configuration;
using UnifiedPicoModule.Core;
using UnifiedPicoModule.Mapping;
using UnifiedPicoModule.Processing;

namespace UnifiedPicoModule.Runtime;

public sealed class TrackingPipeline
{
    private readonly ModuleConfig config;
    private readonly EyeResolver eyes;
    private readonly PostProcessor processor;
    private SourceKind? source;
    private long generation = -1, outputGeneration = -1, faceSequence = -1;
    private ExpressionFrame faceOutput = new();
    private bool hadFreshFace;
    private double lastProcessedFaceArrival;

    public TrackingPipeline(ModuleConfig config)
    {
        this.config = config; eyes = new(config); processor = new(config.PostProcessing);
    }
    public void Reset() => Reset(preserveOutput: false);

    private void Reset(bool preserveOutput)
    {
        // Connection timeouts can discard transport caches without neutralizing held output.
        // Explicit source/session changes and lifecycle stops still clear output state.
        if (!preserveOutput) { eyes.Reset(); faceOutput = new(); }
        else eyes.ResetSmoothing();
        processor.Reset(); faceSequence = -1;
        source = null; generation = outputGeneration = -1; hadFreshFace = false; lastProcessedFaceArrival = 0;
    }
    public TrackingOutput Evaluate(SourceSnapshot? snapshot, double now)
    {
        if (snapshot is null)
        {
            Reset(); return Neutral;
        }
        if (source != snapshot.Source || generation != snapshot.Generation)
        {
            var preserveOutput = !config.Stale.NeutralizeOnTimeout && source == snapshot.Source &&
                outputGeneration == snapshot.OutputGeneration;
            Reset(preserveOutput); source = snapshot.Source; generation = snapshot.Generation;
            outputGeneration = snapshot.OutputGeneration;
        }
        bool fresh = snapshot.Face is { } face && MonotonicClock.Fresh(face.Arrival, now, config.Stale.FaceMs);
        if (fresh && snapshot.Face is { } raw)
        {
            if (faceSequence != snapshot.FaceSequence)
            {
                // A host stall may hide the entire stale interval from Evaluate. Detect it
                // from consecutive received samples before allowing any temporal rule to resume.
                if (lastProcessedFaceArrival > 0 && (raw.Arrival < lastProcessedFaceArrival ||
                    (raw.Arrival - lastProcessedFaceArrival) * 1000 > config.Stale.FaceMs)) processor.Reset();
                var filtered = raw.WithValidity(!config.DisableEyeTracking, !config.DisableFaceTracking);
                // Temporal rules use actual monotonic reception time, not a delayed SDK poll.
                faceOutput = processor.Process(filtered, NeutralFaceMapper.Map(filtered), raw.Arrival);
                faceSequence = snapshot.FaceSequence;
                lastProcessedFaceArrival = raw.Arrival;
            }
            hadFreshFace = true;
            faceOutput = processor.Expire(faceOutput, now);
        }
        else
        {
            if (hadFreshFace) processor.Reset();
            hadFreshFace = false;
            if (config.Stale.NeutralizeOnTimeout) faceOutput = new();
        }
        var (left, right) = eyes.Resolve(snapshot, now);
        var output = faceOutput.Clone(); output.Clamp(config.LegacyExpressionCeiling ? 0.99f : 1);
        return new(output, left, right);
    }
    public TrackingOutput Neutral => new(new(), new(PupilMillimeters: config.Stale.NeutralPupilMillimeters),
        new(PupilMillimeters: config.Stale.NeutralPupilMillimeters));
    public (double Left, double Right)? GazeSmoothingWeights => eyes.SmoothingWeights;
}

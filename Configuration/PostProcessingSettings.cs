namespace UnifiedPicoModule.Configuration;

// Parameter names follow the project's 2026-10-01 post-processing specification.
// Only ChannelGain is enabled by default, for EyeWide x2; other rules stay disabled.
// These coefficients have not been calibrated on the current device/host chain.
public sealed class PostProcessingSettings
{
    public ChannelGainSettings ChannelGain { get; set; } = new();
    public BrowInnerSeparationSettings BrowInnerSeparation { get; set; } = new();
    public BrowDownResponseSettings BrowDownResponse { get; set; } = new();
    public SquintResponseSettings SquintResponse { get; set; } = new();
    public FrownJawAttenuationSettings FrownJawAttenuation { get; set; } = new();
    public ShrugToFrownSettings ShrugToFrown { get; set; } = new();
    public UpperUpPressGateSettings UpperUpPressGate { get; set; } = new();
    public PuckerFunnelAssistSettings PuckerFunnelAssist { get; set; } = new();
    public SmileRollSuppressionSettings SmileRollSuppression { get; set; } = new();
    public StretchCompetitionSettings StretchCompetition { get; set; } = new();
    public SymmetrySettings Symmetry { get; set; } = new();
    public CheekAsymmetrySettings CheekAsymmetry { get; set; } = new();
    public TongueHoldSettings TongueHold { get; set; } = new();
    public TongueJawGateSettings TongueJawGate { get; set; } = new();
}

public sealed class ChannelGainSettings
{
    public bool Enabled { get; set; } = true;
    public float DefaultGain { get; set; } = 1;
    public Dictionary<string, float> Gains { get; set; } = new(StringComparer.Ordinal)
    {
        ["EyeWideLeft"] = 2, ["EyeWideRight"] = 2,
        ["EyeSquintLeft"] = 1, ["EyeSquintRight"] = 1, ["JawOpen"] = 1
    };
}

public sealed class BrowInnerSeparationSettings
{
    public bool Enabled { get; set; }
    public float Strength { get; set; } = 1;
}

public sealed class BrowDownResponseSettings
{
    public bool Enabled { get; set; }
    public float Center { get; set; } = 0.7f;
    public float Slope { get; set; } = 10;
    public float Gain { get; set; } = 1;
    public float BlinkAsymmetryStrength { get; set; }
}

public sealed class SquintResponseSettings
{
    public bool Enabled { get; set; }
    public float Gain { get; set; } = 1;
    public float Gamma { get; set; } = 1;
    public float BlinkInfluence { get; set; }
}

public sealed class FrownJawAttenuationSettings
{
    public bool Enabled { get; set; }
    public float JawThreshold { get; set; } = 0.1f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float MaxReduction { get; set; } = 0.5f;
}

public sealed class ShrugToFrownSettings
{
    public bool Enabled { get; set; }
    public float Threshold { get; set; } = 0.6f;
    public float Range { get; set; } = 0.2f;
    public float Gain { get; set; } = 1;
    public float RollBlockThreshold { get; set; } = 0.3f;
    public float PuckerBlockThreshold { get; set; } = 0.4f;
    public float FunnelBlockThreshold { get; set; } = 0.1f;
    public float TransitionWidth { get; set; } = 0.05f;
    public bool ConsumeShrug { get; set; }
}

public sealed class UpperUpPressGateSettings
{
    public bool Enabled { get; set; }
    public float PressThreshold { get; set; } = 0.1f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float MaxReduction { get; set; } = 1;
}

public sealed class PuckerFunnelAssistSettings
{
    public bool Enabled { get; set; }
    public float PuckerThreshold { get; set; } = 0.3f;
    public float PressThreshold { get; set; } = 0.2f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float Strength { get; set; } = 1;
}

public sealed class SmileRollSuppressionSettings
{
    public bool Enabled { get; set; }
    public float RollThreshold { get; set; } = 0.2f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float MaxReduction { get; set; } = 1;
}

public sealed class StretchCompetitionSettings
{
    public bool Enabled { get; set; }
    public float StretchThreshold { get; set; } = 0.05f;
    public float SmileCeiling { get; set; } = 0.4f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float SmileReduction { get; set; } = 0.5f;
    public float UpperUpReduction { get; set; } = 0.5f;
}

public sealed class SymmetrySettings
{
    public bool Enabled { get; set; }
    public List<string> Groups { get; set; } = ["Smile"];
    public string Method { get; set; } = "Max";
    public float Strength { get; set; } = 1;
}

public sealed class CheekAsymmetrySettings
{
    public bool Enabled { get; set; }
    public float MouthDifferenceThreshold { get; set; } = 0.1f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float Gain { get; set; } = 1;
    public int MouthSmoothingMs { get; set; } = 80;
}

public sealed class TongueHoldSettings
{
    public bool Enabled { get; set; }
    public int HoldMs { get; set; } = 120;
    public float DropThreshold { get; set; } = 0.15f;
}

public sealed class TongueJawGateSettings
{
    public bool Enabled { get; set; }
    public float JawThreshold { get; set; } = 0.5f;
    public float TransitionWidth { get; set; } = 0.1f;
    public float MaxReduction { get; set; } = 1;
}

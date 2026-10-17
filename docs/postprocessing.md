# Post-processing reference

English | [简体中文](postprocessing_zh.md)

This document describes each rule under `PostProcessing`: what it does, its parameters, how it is computed, and what to watch for when rules are combined. For an overview of the rules and how to enable them, see [Expression post-processing](../README.md#expression-post-processing) in the README. For the configuration file format, see the [configuration reference](configuration.md).

## Overview

For `X` v2, new left-eye, right-eye and mouth groups are processed separately. Mouth temporal state advances only on a new mouth sample; intervening eye updates cannot restart a tongue hold or apply mouth gain again. Each group follows its own freshness and the configured timeout behavior.

Post-processing sits between the neutral mapping and VRCFT output. When a new face sample arrives, the module first converts the PICO expressions to VRCFT expression channels through the neutral mapping, then runs the enabled rules in order, and finally clamps the result before handing it to VRCFT. Between face samples, the module reuses the result of the previous sample; the only change that can occur between samples is the expiry of a `TongueHold` hold.

The default configuration enables only `ChannelGain`, and uses it only to multiply `EyeWideLeft` and `EyeWideRight` by 2. All other channels have a gain of 1, and the other 13 rules are disabled. Post-processing applies only to expression channels. Gaze, eye openness and pupil diameter are handled separately by eye resolution and are not affected.

None of the default coefficients have been calibrated on a real headset. The EyeWide gain of 2 was chosen from offline data analysis and has not been verified on a headset either. The parameter ranges are validation limits only; a combination within range is not necessarily suitable for every avatar.

Rules are configured under `PostProcessing.<RuleName>` in `UnifiedPicoModule.json`, and changes take effect after VRCFT is restarted. Every rule has an `Enabled` field, which defaults to `true` for `ChannelGain` and `false` for all other rules. Fields omitted from a rule object keep their default values.

Most rules are modeled on the fixed compensation logic in pikepikeid/VRCFTPicoModule (pike). `TongueHold` was added in this project to address the tongue value briefly dropping to zero. All rules are independent implementations whose curves and constants differ from upstream. For how earlier configurations map to these rules, see [Migrating earlier configurations](configuration.md#migrating-earlier-configurations) in the configuration reference. For source versions and license boundaries, see the [third-party notices](../THIRD_PARTY_NOTICES.md).

## Execution order

Rules run in the following fixed order, which cannot be changed:

1. Neutral mapping
2. `BrowInnerSeparation`
3. `BrowDownResponse`
4. `SquintResponse`
5. `FrownJawAttenuation`
6. `ShrugToFrown`
7. `UpperUpPressGate`
8. `PuckerFunnelAssist`
9. `SmileRollSuppression`
10. `StretchCompetition`
11. `Symmetry`
12. `CheekAsymmetry`
13. `TongueHold`
14. `TongueJawGate`
15. `ChannelGain`
16. Non-finite values are set to zero and all expression channels are clamped to 0–1

If `LegacyExpressionCeiling` is enabled in the configuration, the final upper limit is 0.99.

## Common conventions

### Raw input and output

Below, `R` denotes raw PICO data and `O` denotes the value of an output channel at the point the current rule runs. Rule conditions read `R` and rules modify `O`, so changes made to the output by earlier rules or by `ChannelGain` do not shift the thresholds of later rules. Changes to `O` accumulate: each rule operates on the output left by the rules before it. Intermediate values may exceed 1 while rules are running; clamping happens only after all rules have run.

### Neutral mapping

The output channels that rules modify come from the neutral mapping below. The neutral mapping only assigns values one-to-one or one-to-many and clamps them to 0–1; it applies no compensation.

| PICO raw data | VRCFT output channels |
|---|---|
| `EyeSquintL/R` | `EyeSquintLeft/Right` |
| `EyeWideL/R` | `EyeWideLeft/Right` |
| `BrowDownL/R` | `BrowPinchLeft/Right`, `BrowLowererLeft/Right` |
| `BrowInnerUp` | Ordinary packets: `BrowInnerUpLeft` and `BrowInnerUpRight` both take this one value. `X` packets: `BrowInnerUpLeft` takes it, and `BrowInnerUpRight` takes the independent right inner brow |
| `BrowOuterUpL/R` | `BrowOuterUpLeft/Right` |
| `CheekPuff` | `CheekPuffLeft`, `CheekPuffRight` |
| `JawOpen` | `JawOpen` |
| `MouthRollUpper` / `MouthRollLower` | `LipSuckUpperLeft/Right` / `LipSuckLowerLeft/Right` |
| `MouthFunnel` | All four `LipFunnel*` channels |
| `MouthPucker` | All four `LipPucker*` channels |
| `MouthUpperUpL/R` | `MouthUpperUpLeft/Right`, `MouthUpperDeepenLeft/Right` |
| `MouthLeft` / `MouthRight` | `MouthUpperLeft`, `MouthLowerLeft` / `MouthUpperRight`, `MouthLowerRight` |
| `MouthSmileL/R` | `MouthCornerPullLeft/Right`, `MouthCornerSlantLeft/Right` |
| `MouthFrownL/R` | `MouthFrownLeft/Right` |
| `MouthStretchL/R` | `MouthStretchLeft/Right` |
| `MouthShrugUpper` / `MouthShrugLower` | `MouthRaiserUpper` / `MouthRaiserLower` |
| `MouthPressL/R` | `MouthPressLeft/Right` |
| `TongueOut` | `TongueOut` |

Only the mappings relevant to the rules are listed. The complete mapping is in `Mapping/NeutralFaceMapper.cs`. `EyeBlink` is not mapped to an expression channel; some rules use it only as a condition.

### Missing data

A raw field is treated as missing if it is absent, not a finite number, or belongs to data that is currently invalid. Missing values are not treated as zero. Depending on the rule, the affected side is skipped or the whole rule does not run; see the description of each rule.

Brow, squint, eye-widening and blink fields belong to the eye region; all other fields belong to the face region. Validity is determined separately for each region. With `DisableEyeTracking` enabled, all eye-region fields are treated as missing; with `DisableFaceTracking` enabled, all face-region fields are treated as missing.

In ordinary packets the eye region has one validity flag for both eyes. In `X` packets each eye has its own quality state: a state of 0 (unusable) marks only that eye's fields as missing, including its inner brow, while -1 (unknown) and 1 keep the data. The other eye is unaffected.

### Smooth step

Most rules express their conditions as a threshold plus a transition width. With threshold `t` and transition width `w` (the `TransitionWidth` parameter), the high-side weight `A` and low-side weight `B` are defined as:

```text
c(x)          = clamp(x, 0, 1)
lerp(a, b, k) = a + (b - a) * k

A(x, t, w):
  w = 0:  x > t ? 1 : 0
  w > 0:  lo = max(0, t - w/2)
          hi = min(1, t + w/2)
          u  = c((x - lo) / (hi - lo))
          A  = u * u * (3 - 2u)

B(x, t, w) = 1 - A(x, t, w)
```

The transition zone is centered on the threshold but is cut off at 0 and 1, so it is no longer symmetric when the threshold is close to either end. With `w = 0` the test is a strict greater-than: when `x` equals `t`, `A = 0` and `B = 1`.

## Rules

### ChannelGain

Applies a per-channel gain to the output. It runs after all other rules and applies only to expression channels.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `DefaultGain` | 1 | 0–10 | Gain for channels not listed in `Gains` |
| `Gains` | 2 for `EyeWideLeft` and `EyeWideRight`; 1 for `EyeSquintLeft`, `EyeSquintRight` and `JawOpen` | 0–10 each | Keys are channel names, values are gains |

```text
O[ch] = O[ch] * (ch in Gains ? Gains[ch] : DefaultGain)
```

Channel names are case-insensitive but must be one of the names listed under [Available channels](#available-channels). Gaze, eye openness and pupil cannot be used as keys. When `Gains` is written in the configuration, it replaces the default table as a whole, and any channel not listed, including EyeWide, falls back to `DefaultGain`. To keep the EyeWide gain of 2 while adjusting other channels, list it explicitly:

```json
"ChannelGain": {
  "Enabled": true,
  "DefaultGain": 1,
  "Gains": { "EyeWideLeft": 2, "EyeWideRight": 2, "JawOpen": 1.2 }
}
```

A gain amplifies residual output and can cause saturation, but it never turns zero into a non-zero value. The module's gains also compound with VRCFT's own parameter adjustments.

### BrowInnerSeparation

Reduces the outer brow raise that accompanies an inner brow raise.

Affected channels: `BrowOuterUpLeft`, `BrowOuterUpRight`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `Strength` | 1 | 0–1 | Fraction of the inner brow value subtracted from the outer brow |

```text
O.BrowOuterUp = max(0, O.BrowOuterUp - R.BrowInnerUp * Strength)    // per side
```

For ordinary packets, the common `BrowInnerUp` is used on both sides. Independent-expression `X` packets use each side's own inner brow. A side is skipped if its inner or outer brow is unavailable. The inner brow channels are unchanged. A genuine full-brow raise is also reduced.

### BrowDownResponse

Remaps brow lowering through a normalized sigmoid curve, optionally attenuated by the blink difference between the eyes.

Affected channels: `BrowLowererLeft/Right` and `BrowPinchLeft/Right`, which receive the same value.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `Center` | 0.7 | 0–1 | Center of the curve |
| `Slope` | 10 | 0.1–40 | Steepness of the curve; higher values approach a switch |
| `Gain` | 1 | 0–10 | Output gain |
| `BlinkAsymmetryStrength` | 0 | 0–1 | Strength of blink-difference attenuation; 0 disables it |

```text
s(z) = 1 / (1 + exp(-z))
N(x) = [s(Slope * (c(x) - Center)) - s(-Slope * Center)]
     / [s(Slope * (1 - Center))    - s(-Slope * Center)]

v = N(R.BrowDown) * Gain
if BlinkAsymmetryStrength > 0:
  v = v * (1 - BlinkAsymmetryStrength * c(R.OtherBlink - R.SameBlink))
O.BrowLowerer = O.BrowPinch = v                                      // per side
```

`N` maps 0 to 0 and 1 to 1, with the shape in between set by `Center` and `Slope`. The blink-difference term weakens a side's brow lowering when the other eye is more closed, which reduces brow jitter caused by single-eye blinks.

The rule overwrites the output rather than adding to the neutral mapping. A side is skipped if its `BrowDown` is missing. For ordinary packets, if `BlinkAsymmetryStrength > 0` and either `EyeBlink` is missing, the whole rule does not run and both sides keep the neutral mapping. Independent-expression `X` packets skip cross-eye BlinkAsymmetry but retain the per-side response curve.

### SquintResponse

Remaps squinting through a power curve, optionally attenuated when the eye is not closed.

Affected channels: `EyeSquintLeft`, `EyeSquintRight`. Eye openness is not affected.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `Gain` | 1 | 0–10 | Output gain |
| `Gamma` | 1 | 0.1–5 | Exponent. 1 is linear; above 1 suppresses small movements, below 1 boosts them |
| `BlinkInfluence` | 0 | 0–1 | Strength of blink coupling; 0 disables it |

```text
v = c(R.EyeSquint) ^ Gamma * Gain
if BlinkInfluence > 0:
  v = v * (1 - BlinkInfluence * (1 - c(R.EyeBlink)))
O.EyeSquint = v                                                      // per side
```

With the same-side `EyeBlink` at 1 there is no attenuation; at 0 the value is reduced by the fraction `BlinkInfluence`. The rule overwrites the output. A side is skipped if its `EyeSquint` is missing, or if `BlinkInfluence > 0` and its `EyeBlink` is missing.

### FrownJawAttenuation

Reduces the frown that accompanies jaw opening.

Affected channels: `MouthFrownLeft`, `MouthFrownRight`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `JawThreshold` | 0.1 | 0–1 | Jaw opening threshold |
| `TransitionWidth` | 0.1 | 0–1 | Transition width |
| `MaxReduction` | 0.5 | 0–1 | Maximum reduction |

```text
m = 1 - MaxReduction * A(R.JawOpen, JawThreshold, TransitionWidth)
O.MouthFrown = O.MouthFrown * m                                      // per side
```

The rule does not run if `JawOpen` is missing, and a side is skipped if its `MouthFrown` is missing. A genuine frown with the mouth open is also reduced.

### ShrugToFrown

Derives a frown candidate from mouth shrug, blocked while the lips are rolled, puckered or funneled.

Affected channels: `MouthFrownLeft/Right`; with `ConsumeShrug` enabled, also `MouthRaiserUpper/Lower`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `Threshold` | 0.6 | 0–1 | Shrug value at which the candidate starts |
| `Range` | 0.2 | Greater than 0, with `Threshold + Range ≤ 1` | Span above `Threshold` over which the candidate reaches full response |
| `Gain` | 1 | 0–10 | Candidate gain |
| `RollBlockThreshold` | 0.3 | 0–1 | Lip roll blocking threshold |
| `PuckerBlockThreshold` | 0.4 | 0–1 | Pucker blocking threshold |
| `FunnelBlockThreshold` | 0.1 | 0–1 | Funnel blocking threshold |
| `TransitionWidth` | 0.05 | 0–1 | Transition width shared by the three blocking conditions |
| `ConsumeShrug` | `false` | Boolean | Whether to reduce `MouthRaiser` by the candidate |

```text
h = max(c((R.ShrugUpper - Threshold) / Range),
        c((R.ShrugLower - Threshold) / Range))
r = max(R.RollUpper, R.RollLower)
q = h * Gain
      * B(r,        RollBlockThreshold,   TransitionWidth)
      * B(R.Pucker, PuckerBlockThreshold, TransitionWidth)
      * B(R.Funnel, FunnelBlockThreshold, TransitionWidth)

O.MouthFrownLeft  = max(O.MouthFrownLeft,  q)
O.MouthFrownRight = max(O.MouthFrownRight, q)
if ConsumeShrug:
  O.MouthRaiserUpper = O.MouthRaiserUpper * (1 - c(q))
  O.MouthRaiserLower = O.MouthRaiserLower * (1 - c(q))
```

The mapping from shrug to candidate is a linear ramp, not a smooth step. The candidate is the same on both sides. The rule does not run if any of `MouthShrugUpper`, `MouthShrugLower`, `MouthRollUpper`, `MouthRollLower`, `MouthPucker` or `MouthFunnel` is missing. Because the result is a maximum, the rule can only increase the frown, never decrease it.

### UpperUpPressGate

Suppresses the upper lip raise on the side where the lips are pressed.

Affected channels: `MouthUpperUpLeft/Right`, `MouthUpperDeepenLeft/Right`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `PressThreshold` | 0.1 | 0–1 | Lip press threshold |
| `TransitionWidth` | 0.1 | 0–1 | Transition width |
| `MaxReduction` | 1 | 0–1 | Maximum reduction |

```text
m = 1 - MaxReduction * A(R.SamePress, PressThreshold, TransitionWidth)
O.MouthUpperUp     = O.MouthUpperUp     * m                          // per side
O.MouthUpperDeepen = O.MouthUpperDeepen * m
```

A side is skipped if its `MouthUpperUp` or `MouthPress` is missing. With the default parameters, the upper lip raise is fully suppressed once the same-side lip press reaches 0.15.

### PuckerFunnelAssist

When the lips are clearly puckered and not pressed on the same side, raises the funnel to at least the pucker value.

Affected channels: `LipFunnelUpperLeft/Right`, `LipFunnelLowerLeft/Right`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `PuckerThreshold` | 0.3 | 0–1 | Pucker threshold |
| `PressThreshold` | 0.2 | 0–1 | Lip press threshold above which assistance stops |
| `TransitionWidth` | 0.1 | 0–1 | Transition width shared by both conditions |
| `Strength` | 1 | 0–1 | Assistance strength |

```text
k = Strength * A(R.Pucker, PuckerThreshold, TransitionWidth)
             * B(R.SamePress, PressThreshold, TransitionWidth)
O.LipFunnel = lerp(O.LipFunnel, max(O.LipFunnel, R.Pucker), k)       // per side, upper and lower
```

The interpolation target is the larger of the current funnel and the raw pucker, so the rule can only increase the funnel. The rule does not run if `MouthPucker` or `MouthFunnel` is missing, and a side is skipped if its `MouthPress` is missing.

### SmileRollSuppression

Suppresses smiling while the lower lip is rolled in.

Affected channels: `MouthCornerPullLeft/Right`, `MouthCornerSlantLeft/Right`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `RollThreshold` | 0.2 | 0–1 | Lower lip roll threshold |
| `TransitionWidth` | 0.1 | 0–1 | Transition width |
| `MaxReduction` | 1 | 0–1 | Maximum reduction |

```text
m = 1 - MaxReduction * A(R.MouthRollLower, RollThreshold, TransitionWidth)
O.MouthCornerPull  = O.MouthCornerPull  * m                          // per side
O.MouthCornerSlant = O.MouthCornerSlant * m
```

The rule does not run if `MouthRollLower` is missing, and a side is skipped if its `MouthSmile` is missing. A genuine smile with the lower lip rolled in is also reduced; with the default parameters it is fully suppressed once `MouthRollLower` reaches 0.25.

### StretchCompetition

When the mouth is stretched horizontally and the smile is weak, suppresses the accompanying smile and upper lip raise. A smile above `SmileCeiling` is treated as genuine and is not suppressed.

Affected channels: `MouthCornerPullLeft/Right`, `MouthCornerSlantLeft/Right`, `MouthUpperUpLeft/Right`, `MouthUpperDeepenLeft/Right`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `StretchThreshold` | 0.05 | 0–1 | Stretch threshold |
| `SmileCeiling` | 0.4 | 0–1 | Smile level above which suppression stops |
| `TransitionWidth` | 0.1 | 0–1 | Transition width shared by both conditions |
| `SmileReduction` | 0.5 | 0–1 | Maximum reduction of the smile |
| `UpperUpReduction` | 0.5 | 0–1 | Maximum reduction of the upper lip raise |

```text
k = A(R.Stretch, StretchThreshold, TransitionWidth)
  * B(R.Smile,   SmileCeiling,     TransitionWidth)                  // per side
O.MouthCornerPull, O.MouthCornerSlant   *= 1 - SmileReduction   * k
O.MouthUpperUp,    O.MouthUpperDeepen   *= 1 - UpperUpReduction * k  // only if same-side MouthUpperUp is present
```

A side is skipped if its `MouthStretch` or `MouthSmile` is missing.

### Symmetry

Brings selected left and right mouth channels closer together.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `Groups` | `["Smile"]` | `Smile`, `Frown`, `Stretch`, `UpperUp`; case-sensitive, no duplicates | Groups to make symmetric |
| `Method` | `"Max"` | `Max` or `Average`; case-sensitive | How the target value is computed |
| `Strength` | 1 | 0–1 | How far each side moves toward the target |

| Group | Output channel pairs | Required raw data |
|---|---|---|
| `Smile` | `MouthCornerPull`, `MouthCornerSlant` | `MouthSmileL`, `MouthSmileR` |
| `Frown` | `MouthFrown` | `MouthFrownL`, `MouthFrownR` |
| `Stretch` | `MouthStretch` | `MouthStretchL`, `MouthStretchR` |
| `UpperUp` | `MouthUpperUp`, `MouthUpperDeepen` | `MouthUpperUpL`, `MouthUpperUpR` |

```text
q = Method == "Max" ? max(O.Left, O.Right) : (O.Left + O.Right) / 2
O.Left  = lerp(O.Left,  q, Strength)
O.Right = lerp(O.Right, q, Strength)
```

The rule operates on the output of the earlier rules. A group is skipped if either side of its required raw data is missing. An empty `Groups` array has no effect. Gaze, eye openness, pupil and brows are not processed.

### CheekAsymmetry

Infers a one-sided cheek puff from sideways mouth movement: when the mouth moves left, the right cheek puff increases, and when it moves right, the left cheek puff increases. This is an inference; PICO provides only a single `CheekPuff` value.

Affected channels: `CheekPuffLeft`, `CheekPuffRight`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `MouthDifferenceThreshold` | 0.1 | 0–1 | Threshold on the left–right movement difference |
| `TransitionWidth` | 0.1 | 0–1 | Transition width |
| `Gain` | 1 | 0–10 | Gain of the added amount |
| `MouthSmoothingMs` | 80 | Integer, 0–500 | Smoothing time constant in milliseconds for the direction test; 0 disables smoothing |

```text
alpha = 1 - exp(-Δt / (MouthSmoothingMs / 1000))       // Δt: time between sample arrivals, in seconds
SL = lerp(SL, R.MouthLeft,  alpha)
SR = lerp(SR, R.MouthRight, alpha)
d  = SL - SR

O.CheekPuffRight += R.MouthLeft  * Gain * A( d, MouthDifferenceThreshold, TransitionWidth)
O.CheekPuffLeft  += R.MouthRight * Gain * A(-d, MouthDifferenceThreshold, TransitionWidth)
```

Smoothing is based on actual arrival time and is independent of the frame rate. On the first sample, when time goes backwards, or when `MouthSmoothingMs = 0`, the smoothed values are set directly to the current raw values. Smoothing applies only to the direction test; the added amount uses the unsmoothed raw movement.

The rule requires `MouthLeft`, `MouthRight` and `CheekPuff`; if any is missing, it does not run and its smoothing state is cleared. The added amount does not depend on the `CheekPuff` value, so moving the mouth sideways without puffing the cheeks still produces a one-sided cheek puff.

### TongueHold

When the tongue value drops briefly from a high level, holds the value from before the drop for a limited time. This reduces flicker while the tongue is out.

Affected channel: `TongueOut`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `HoldMs` | 120 | Integer, 0–500 | Maximum hold time in milliseconds for a single drop; 0 disables holding |
| `DropThreshold` | 0.15 | 0–1 | Value at or below which a drop is detected |

```text
R.TongueOut > DropThreshold:
  record high = R.TongueOut, end any current hold, leave the output unchanged
R.TongueOut ≤ DropThreshold:
  if the previous sample was above the threshold: deadline = arrival time + HoldMs / 1000
  before the deadline: O.TongueOut = high
  at or after the deadline: the output returns to the current value
```

The held value is the raw value of the last sample above the threshold, not the maximum over an interval. Consecutive low samples during a hold do not extend the deadline; a new hold is timed only after the value has risen above the threshold again. The held value still passes through `TongueJawGate` and `ChannelGain`.

Expiry does not wait for the next sample. The module checks the deadline on every output and, once it has passed, replaces `TongueOut` with the current sample's unheld result (with `TongueJawGate` and `ChannelGain` applied and clamped to 0–1). No other rule is rerun.

If `TongueOut` is missing, face data is invalid, or `HoldMs = 0`, the rule does not run and the hold state is cleared. A missing value is not treated as a drop and does not start a hold.

### TongueJawGate

Suppresses tongue output when the mouth is not open wide enough, which reduces false tongue triggers. It addresses a different problem from `TongueHold`.

Affected channel: `TongueOut`.

| Parameter | Default | Range | Description |
|---|---|---|---|
| `JawThreshold` | 0.5 | 0–1 | Jaw opening below which the tongue is suppressed |
| `TransitionWidth` | 0.1 | 0–1 | Transition width |
| `MaxReduction` | 1 | 0–1 | Maximum reduction |

```text
m = 1 - MaxReduction * B(R.JawOpen, JawThreshold, TransitionWidth)
O.TongueOut = O.TongueOut * m
if m = 0: clear the TongueHold state
```

The rule applies to the value after `TongueHold`, so held values are suppressed as well. When `m` is exactly 0, the hold state is cleared so that a blocked hold does not reappear once the mouth opens. With the default parameters, tongue output is fully suppressed while `JawOpen` is at or below 0.45. The rule does not run if `JawOpen` or `TongueOut` is missing.

## Combining rules

- `Symmetry` runs after the one-sided gating rules. With the `Max` method, an unsuppressed residue on one side propagates to the other, which can undo one-sided suppression from earlier rules.
- `FrownJawAttenuation` reduces the frown first, then `ShrugToFrown` takes the maximum with its candidate. The two do not cancel out when the mouth is open, and the candidate can raise a frown that was just reduced.
- `SmileRollSuppression` and `StretchCompetition` both reduce `MouthCornerPull` and `MouthCornerSlant`, and their multipliers compound. The reductions of the upper lip raise by `UpperUpPressGate` and `StretchCompetition` compound in the same way.
- `BrowDownResponse` and `SquintResponse` overwrite their outputs. Any `ChannelGain` gain for those channels is applied on top of their result.
- `ChannelGain` compounds with VRCFT's own parameter adjustments. VRCFT's downstream calibration, filtering and Correctors still change the final output.

## Configuration validation

Each rule is validated independently at module startup. Any of the following disables only the affected rule; the neutral mapping and all other valid rules continue to work, and the field and reason are written to the log:

- a value is not finite or is outside the range given in this document;
- `Threshold + Range` for `ShrugToFrown` is greater than 1 (with a floating-point tolerance of about 1e-7);
- a rule object contains an unknown or duplicate field, or a field of the wrong type, such as a fractional `HoldMs`;
- `Gains` contains an unknown channel name, a non-expression channel or a duplicate channel (case-insensitive);
- `Groups` contains an unknown or duplicate group, or `Method` is not `Max` or `Average`.

Unknown rule names are ignored and do not affect other rules. If the disabled rule is `ChannelGain`, the EyeWide gain of 2 is lost as well. If `PostProcessing` itself is not a JSON object, all rules, including `ChannelGain`, are disabled. If the file cannot be parsed, or `ConfigVersion` is missing, duplicated or unknown, the module uses the default configuration and does not rewrite the file.

After validation, the log lists every enabled rule with its parameters, which can be used to confirm the configuration actually in effect. The configuration tool's `validate` command checks a configuration without starting the module; see the [configuration tool guide](../tools/ConfigTool/README.md). Passing validation only means the configuration is well-formed, not that its effect is appropriate.

## State reset

The smoothed values of `CheekAsymmetry` and the hold state of `TongueHold` are the only state carried across samples. They are cleared when:

- the selected source changes, or the source's session changes, for example on reconnection, protocol lock, Bridge mode change or streaming session timeout;
- face data has not been updated for longer than `Stale.FaceMs` (250 ms by default);
- the arrival times of two consecutive face samples are more than `Stale.FaceMs` apart, or arrival time goes backwards, for example after a host stall;
- the module is deactivated, no source is available, or an exception occurs during output;
- face data is invalid, in which case eye-region rules still run on the current sample;
- a rule's own conditions are not met; see `CheekAsymmetry`, `TongueHold` and `TongueJawGate`.

Configuration changes require a VRCFT restart, after which all state starts empty. All time-based calculations use arrival times recorded by the local monotonic clock, not sender timestamps.

## Available channels

`ChannelGain.Gains` accepts only the following channel names:

```text
EyeSquintLeft EyeSquintRight EyeWideLeft EyeWideRight
BrowPinchLeft BrowPinchRight BrowLowererLeft BrowLowererRight
BrowInnerUpLeft BrowInnerUpRight BrowOuterUpLeft BrowOuterUpRight
CheekSquintLeft CheekSquintRight CheekPuffLeft CheekPuffRight
JawOpen JawLeft JawRight JawForward MouthClosed
LipSuckUpperLeft LipSuckUpperRight LipSuckLowerLeft LipSuckLowerRight
LipFunnelUpperLeft LipFunnelUpperRight LipFunnelLowerLeft LipFunnelLowerRight
LipPuckerUpperLeft LipPuckerUpperRight LipPuckerLowerLeft LipPuckerLowerRight
MouthUpperUpLeft MouthUpperUpRight MouthUpperDeepenLeft MouthUpperDeepenRight
NoseSneerLeft NoseSneerRight MouthLowerDownLeft MouthLowerDownRight
MouthUpperLeft MouthUpperRight MouthLowerLeft MouthLowerRight
MouthCornerPullLeft MouthCornerPullRight MouthCornerSlantLeft MouthCornerSlantRight
MouthFrownLeft MouthFrownRight MouthStretchLeft MouthStretchRight
MouthDimpleLeft MouthDimpleRight MouthRaiserUpper MouthRaiserLower
MouthPressLeft MouthPressRight TongueOut
```

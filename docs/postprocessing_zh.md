# 后处理参考

[English](postprocessing.md) | 简体中文

本文说明 `PostProcessing` 中各条规则的作用、参数、计算方式以及组合使用时的注意事项。规则的概览与启用方法请参阅 README 的[表情后处理](../README_zh.md#表情后处理)一节，配置文件的写法请参阅[配置说明](configuration_zh.md)。

## 概述

`X` v2 分别处理新的左眼、右眼和嘴组。嘴部时间状态只随新嘴样本推进；中间的眼快帧不会重启舌头保持或重复施加嘴部增益。各组按自己的新鲜度与配置的超时行为处理。

后处理位于中性映射与 VRCFT 输出之间。模块收到一帧新的面部数据后，先通过中性映射把 PICO 表情转换为 VRCFT 表情通道，再依次执行已启用的规则，最后将结果限幅后交给 VRCFT。在两帧面部数据之间，模块沿用上一帧的处理结果，只有 `TongueHold` 的保持到期会在帧间生效。

默认配置只启用 `ChannelGain`，并且只将 `EyeWideLeft` 与 `EyeWideRight` 放大 2 倍，其余通道的倍率为 1。另外 13 条规则默认关闭。后处理只作用于表情通道，不影响视线、睁眼度和瞳孔直径，这三项由眼动解析单独处理。

所有规则的默认系数都没有在真机上标定。EyeWide 的 2 倍增益是根据离线数据分析选定的值，同样未经实机验证。参数的取值范围只是校验边界，范围内的组合不一定适合所有 Avatar。

规则的配置位于 `UnifiedPicoModule.json` 的 `PostProcessing.<规则名>` 下，修改后需要重启 VRCFT。每条规则都有一个 `Enabled` 字段，除 `ChannelGain` 默认为 `true` 外，其余规则默认均为 `false`。规则对象中未写出的字段使用默认值。

多数规则的设计参考了 pikepikeid/VRCFTPicoModule（pike）中的固定补偿逻辑，`TongueHold` 是本项目针对舌头数值短时掉零新增的规则。所有规则均为本项目的独立实现，曲线和常量与上游不同。旧配置迁移时的近似对应关系请参阅[配置说明](configuration_zh.md#旧配置迁移)，来源版本与许可边界请参阅[第三方声明](../THIRD_PARTY_NOTICES_zh.md)。

## 执行顺序

规则按以下固定顺序执行，顺序不可调整：

1. 中性映射
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
16. 非有限值归零，所有表情通道限幅到 0–1

如果配置中启用了 `LegacyExpressionCeiling`，最终上限为 0.99。

## 通用约定

### 原始输入与输出

下文用 `R` 表示原始 PICO 数据，用 `O` 表示执行到当前规则时的输出通道值。规则的判断条件读取 `R`，修改的是 `O`，因此前面规则或 `ChannelGain` 对输出的改动不会影响后面规则的阈值判断。对 `O` 的修改会逐条累积，后面的规则在前面规则的输出上继续计算。规则执行过程中的中间值可能超过 1，最终限幅只在全部规则执行完后进行。

### 中性映射

规则修改的输出通道来自以下中性映射。中性映射只做一对一或一对多的赋值，并将数值限幅到 0–1，不做任何补偿。

| PICO 原始数据 | VRCFT 输出通道 |
|---|---|
| `EyeSquintL/R` | `EyeSquintLeft/Right` |
| `EyeWideL/R` | `EyeWideLeft/Right` |
| `BrowDownL/R` | `BrowPinchLeft/Right`、`BrowLowererLeft/Right` |
| `BrowInnerUp` | 普通包：`BrowInnerUpLeft`、`BrowInnerUpRight` 共用这一个值。`X` 包：`BrowInnerUpLeft` 取该值，`BrowInnerUpRight` 取独立的右内眉 |
| `BrowOuterUpL/R` | `BrowOuterUpLeft/Right` |
| `CheekPuff` | `CheekPuffLeft`、`CheekPuffRight` |
| `JawOpen` | `JawOpen` |
| `MouthRollUpper` / `MouthRollLower` | `LipSuckUpperLeft/Right` / `LipSuckLowerLeft/Right` |
| `MouthFunnel` | 四个 `LipFunnel*` 通道 |
| `MouthPucker` | 四个 `LipPucker*` 通道 |
| `MouthUpperUpL/R` | `MouthUpperUpLeft/Right`、`MouthUpperDeepenLeft/Right` |
| `MouthLeft` / `MouthRight` | `MouthUpperLeft`、`MouthLowerLeft` / `MouthUpperRight`、`MouthLowerRight` |
| `MouthSmileL/R` | `MouthCornerPullLeft/Right`、`MouthCornerSlantLeft/Right` |
| `MouthFrownL/R` | `MouthFrownLeft/Right` |
| `MouthStretchL/R` | `MouthStretchLeft/Right` |
| `MouthShrugUpper` / `MouthShrugLower` | `MouthRaiserUpper` / `MouthRaiserLower` |
| `MouthPressL/R` | `MouthPressLeft/Right` |
| `TongueOut` | `TongueOut` |

表中只列出与规则相关的映射，完整的对应关系见源码 `Mapping/NeutralFaceMapper.cs`。`EyeBlink` 不映射到表情通道，只作为部分规则的判断条件。

### 缺失数据

某个原始字段不存在、不是有限数值或所属数据无效时，视为缺失。缺失不按 0 处理：规则会跳过对应的一侧，或者整条规则不执行，具体见各规则说明。

眉毛、眯眼、瞪眼和眨眼属于眼部区域字段，其余属于面部区域字段，两者的有效性分别判断。启用 `DisableEyeTracking` 后眼部区域字段全部视为缺失，启用 `DisableFaceTracking` 后面部区域字段全部视为缺失。

普通包的眼部区域只有一个对两眼共用的有效位。`X` 包中每只眼有各自的质量状态：状态为 0（不可用）时，只有该眼的字段（包括其内眉）视为缺失；-1（未知）和 1 保留数据。另一只眼不受影响。

### 平滑阶跃

多数规则用“阈值 + 过渡宽度”描述条件。记阈值为 `t`、过渡宽度为 `w`（即参数 `TransitionWidth`），高值权重 `A` 与低值权重 `B` 定义如下：

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

过渡区以阈值为中心，但会在 0 和 1 处截断，因此阈值靠近两端时过渡区不再对称。`w = 0` 时为严格的大于判断，`x` 恰好等于 `t` 时 `A = 0`、`B = 1`。

## 规则说明

### ChannelGain

按通道调整输出倍率，在所有其他规则之后执行。只作用于表情通道。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `DefaultGain` | 1 | 0–10 | `Gains` 中未列出的通道使用的倍率 |
| `Gains` | `EyeWideLeft`、`EyeWideRight` 为 2；`EyeSquintLeft`、`EyeSquintRight`、`JawOpen` 为 1 | 每项 0–10 | 键为通道名，值为倍率 |

```text
O[ch] = O[ch] * (Gains[ch] 存在 ? Gains[ch] : DefaultGain)
```

通道名不区分大小写，但必须是下文[可用通道](#可用通道)中的名称，视线、睁眼度和瞳孔不能作为键。在配置中写出 `Gains` 时，该对象会整体替换默认表，未列出的通道（包括 EyeWide）回到 `DefaultGain`。如需在调整其他通道的同时保留 EyeWide 的 2 倍增益，需要在 `Gains` 中显式写出：

```json
"ChannelGain": {
  "Enabled": true,
  "DefaultGain": 1,
  "Gains": { "EyeWideLeft": 2, "EyeWideRight": 2, "JawOpen": 1.2 }
}
```

倍率会放大残余输出并可能导致饱和，但不会把 0 变成非零值。模块的倍率还会与 VRCFT 自身的参数调整叠加。

### BrowInnerSeparation

抬内眉时减少外眉的联动上抬。

影响通道：`BrowOuterUpLeft`、`BrowOuterUpRight`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `Strength` | 1 | 0–1 | 从外眉中扣除内眉数值的比例 |

```text
O.BrowOuterUp = max(0, O.BrowOuterUp - R.BrowInnerUp * Strength)    // 每侧
```

普通包两侧使用共用的 `BrowInnerUp`；独立表情 `X` 包使用各自的内眉。某侧内眉或外眉不可用时跳过该侧。内眉通道本身不变。真实的整眉上抬也会被削弱。

### BrowDownResponse

用归一化 sigmoid 曲线重新映射眉毛下压，可选地按左右眨眼差衰减。

影响通道：`BrowLowererLeft/Right`、`BrowPinchLeft/Right`（两者写入相同的值）。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `Center` | 0.7 | 0–1 | 曲线中心 |
| `Slope` | 10 | 0.1–40 | 曲线陡峭程度，越大越接近开关 |
| `Gain` | 1 | 0–10 | 输出倍率 |
| `BlinkAsymmetryStrength` | 0 | 0–1 | 眨眼差衰减强度，0 表示不使用 |

```text
s(z) = 1 / (1 + exp(-z))
N(x) = [s(Slope * (c(x) - Center)) - s(-Slope * Center)]
     / [s(Slope * (1 - Center))    - s(-Slope * Center)]

v = N(R.BrowDown) * Gain
BlinkAsymmetryStrength > 0 时:
  v = v * (1 - BlinkAsymmetryStrength * c(R.OtherBlink - R.SameBlink))
O.BrowLowerer = O.BrowPinch = v                                      // 每侧
```

`N` 将 0 映射为 0、1 映射为 1，中间部分由 `Center` 和 `Slope` 决定。眨眼差项的含义是：另一只眼闭合程度高于本侧时，本侧的下压被削弱，用于减少单眼眨眼带动的眉毛抖动。

规则直接覆盖输出值，不与中性映射的结果叠加。某侧 `BrowDown` 缺失时跳过该侧。普通包中，`BlinkAsymmetryStrength > 0` 且任一侧 `EyeBlink` 缺失时，整条规则不执行，两侧都保留中性映射的结果。独立表情 `X` 包跳过跨眼 BlinkAsymmetry，保留本侧响应曲线。

### SquintResponse

用幂曲线重新映射眯眼，可选地在眼睛未闭合时衰减。

影响通道：`EyeSquintLeft`、`EyeSquintRight`。不影响睁眼度。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `Gain` | 1 | 0–10 | 输出倍率 |
| `Gamma` | 1 | 0.1–5 | 幂指数。1 为线性，大于 1 压低小幅动作，小于 1 抬升小幅动作 |
| `BlinkInfluence` | 0 | 0–1 | 眨眼联动强度，0 表示不使用 |

```text
v = c(R.EyeSquint) ^ Gamma * Gain
BlinkInfluence > 0 时:
  v = v * (1 - BlinkInfluence * (1 - c(R.EyeBlink)))
O.EyeSquint = v                                                      // 每侧
```

同侧 `EyeBlink` 为 1 时不衰减，为 0 时衰减 `BlinkInfluence` 对应的比例。规则直接覆盖输出值。某侧 `EyeSquint` 缺失，或 `BlinkInfluence > 0` 且同侧 `EyeBlink` 缺失时，跳过该侧。

### FrownJawAttenuation

张嘴时减少联动出现的撇嘴。

影响通道：`MouthFrownLeft`、`MouthFrownRight`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `JawThreshold` | 0.1 | 0–1 | 张嘴阈值 |
| `TransitionWidth` | 0.1 | 0–1 | 过渡宽度 |
| `MaxReduction` | 0.5 | 0–1 | 最大削弱比例 |

```text
m = 1 - MaxReduction * A(R.JawOpen, JawThreshold, TransitionWidth)
O.MouthFrown = O.MouthFrown * m                                      // 每侧
```

`JawOpen` 缺失时整条规则不执行，某侧 `MouthFrown` 缺失时跳过该侧。张嘴同时撇嘴的真实表情也会被削弱。

### ShrugToFrown

由下巴上抬（mouth shrug）生成撇嘴候选值，在卷唇、嘟嘴或圆口时阻断。

影响通道：`MouthFrownLeft/Right`；启用 `ConsumeShrug` 时还影响 `MouthRaiserUpper/Lower`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `Threshold` | 0.6 | 0–1 | 开始生成候选值的 shrug 数值 |
| `Range` | 0.2 | 大于 0，且 `Threshold + Range ≤ 1` | 从 `Threshold` 起升至完整响应的区间 |
| `Gain` | 1 | 0–10 | 候选值倍率 |
| `RollBlockThreshold` | 0.3 | 0–1 | 卷唇阻断阈值 |
| `PuckerBlockThreshold` | 0.4 | 0–1 | 嘟嘴阻断阈值 |
| `FunnelBlockThreshold` | 0.1 | 0–1 | 圆口阻断阈值 |
| `TransitionWidth` | 0.05 | 0–1 | 三个阻断条件共用的过渡宽度 |
| `ConsumeShrug` | `false` | 布尔 | 是否按候选值削弱 `MouthRaiser` |

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
ConsumeShrug 时:
  O.MouthRaiserUpper = O.MouthRaiserUpper * (1 - c(q))
  O.MouthRaiserLower = O.MouthRaiserLower * (1 - c(q))
```

shrug 到候选值的映射是线性斜坡，不是平滑阶跃。候选值对左右两侧相同。`MouthShrugUpper`、`MouthShrugLower`、`MouthRollUpper`、`MouthRollLower`、`MouthPucker`、`MouthFunnel` 中任一缺失时，整条规则不执行。由于取最大值，规则只会增加撇嘴，不会减少。

### UpperUpPressGate

抿嘴时抑制同侧上唇抬起。

影响通道：`MouthUpperUpLeft/Right`、`MouthUpperDeepenLeft/Right`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `PressThreshold` | 0.1 | 0–1 | 抿嘴阈值 |
| `TransitionWidth` | 0.1 | 0–1 | 过渡宽度 |
| `MaxReduction` | 1 | 0–1 | 最大削弱比例 |

```text
m = 1 - MaxReduction * A(R.SamePress, PressThreshold, TransitionWidth)
O.MouthUpperUp     = O.MouthUpperUp     * m                          // 每侧
O.MouthUpperDeepen = O.MouthUpperDeepen * m
```

某侧 `MouthUpperUp` 或 `MouthPress` 缺失时跳过该侧。默认参数下，同侧抿嘴达到 0.15 时上唇抬起被完全抑制。

### PuckerFunnelAssist

嘟嘴明显且同侧未抿嘴时，将圆口提升到不低于嘟嘴的程度。

影响通道：`LipFunnelUpperLeft/Right`、`LipFunnelLowerLeft/Right`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `PuckerThreshold` | 0.3 | 0–1 | 嘟嘴阈值 |
| `PressThreshold` | 0.2 | 0–1 | 抿嘴阈值，超过时停止辅助 |
| `TransitionWidth` | 0.1 | 0–1 | 两个条件共用的过渡宽度 |
| `Strength` | 1 | 0–1 | 辅助强度 |

```text
k = Strength * A(R.Pucker, PuckerThreshold, TransitionWidth)
             * B(R.SamePress, PressThreshold, TransitionWidth)
O.LipFunnel = lerp(O.LipFunnel, max(O.LipFunnel, R.Pucker), k)       // 每侧上、下各一次
```

插值目标是当前圆口与原始嘟嘴中的较大者，因此规则只会增加圆口，不会减少。`MouthPucker` 或 `MouthFunnel` 缺失时整条规则不执行，某侧 `MouthPress` 缺失时跳过该侧。

### SmileRollSuppression

卷下唇时抑制笑容。

影响通道：`MouthCornerPullLeft/Right`、`MouthCornerSlantLeft/Right`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `RollThreshold` | 0.2 | 0–1 | 卷下唇阈值 |
| `TransitionWidth` | 0.1 | 0–1 | 过渡宽度 |
| `MaxReduction` | 1 | 0–1 | 最大削弱比例 |

```text
m = 1 - MaxReduction * A(R.MouthRollLower, RollThreshold, TransitionWidth)
O.MouthCornerPull  = O.MouthCornerPull  * m                          // 每侧
O.MouthCornerSlant = O.MouthCornerSlant * m
```

`MouthRollLower` 缺失时整条规则不执行，某侧 `MouthSmile` 缺失时跳过该侧。卷唇同时微笑的真实表情也会被削弱，默认参数下 `MouthRollLower` 达到 0.25 时笑容被完全抑制。

### StretchCompetition

横向咧嘴较强而笑容较弱时，抑制联动出现的笑容和上唇抬起。笑容超过 `SmileCeiling` 时视为真实笑容，不再抑制。

影响通道：`MouthCornerPullLeft/Right`、`MouthCornerSlantLeft/Right`、`MouthUpperUpLeft/Right`、`MouthUpperDeepenLeft/Right`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `StretchThreshold` | 0.05 | 0–1 | 咧嘴阈值 |
| `SmileCeiling` | 0.4 | 0–1 | 笑容上限，超过时停止抑制 |
| `TransitionWidth` | 0.1 | 0–1 | 两个条件共用的过渡宽度 |
| `SmileReduction` | 0.5 | 0–1 | 笑容的最大削弱比例 |
| `UpperUpReduction` | 0.5 | 0–1 | 上唇抬起的最大削弱比例 |

```text
k = A(R.Stretch, StretchThreshold, TransitionWidth)
  * B(R.Smile,   SmileCeiling,     TransitionWidth)                  // 每侧
O.MouthCornerPull, O.MouthCornerSlant   *= 1 - SmileReduction   * k
O.MouthUpperUp,    O.MouthUpperDeepen   *= 1 - UpperUpReduction * k  // 仅当同侧 MouthUpperUp 存在
```

某侧 `MouthStretch` 或 `MouthSmile` 缺失时跳过该侧。

### Symmetry

使选定的嘴部通道左右趋同。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `Groups` | `["Smile"]` | `Smile`、`Frown`、`Stretch`、`UpperUp`，区分大小写，不可重复 | 要对称的分组 |
| `Method` | `"Max"` | `Max` 或 `Average`，区分大小写 | 目标值的计算方式 |
| `Strength` | 1 | 0–1 | 向目标值靠拢的比例 |

| 分组 | 输出通道对 | 需要的原始数据 |
|---|---|---|
| `Smile` | `MouthCornerPull`、`MouthCornerSlant` | `MouthSmileL`、`MouthSmileR` |
| `Frown` | `MouthFrown` | `MouthFrownL`、`MouthFrownR` |
| `Stretch` | `MouthStretch` | `MouthStretchL`、`MouthStretchR` |
| `UpperUp` | `MouthUpperUp`、`MouthUpperDeepen` | `MouthUpperUpL`、`MouthUpperUpR` |

```text
q = Method == "Max" ? max(O.Left, O.Right) : (O.Left + O.Right) / 2
O.Left  = lerp(O.Left,  q, Strength)
O.Right = lerp(O.Right, q, Strength)
```

规则读取的是前面规则处理后的输出。分组所需的左右原始数据有一侧缺失时跳过该分组。`Groups` 为空数组时规则不产生任何效果。规则不处理视线、睁眼度、瞳孔和眉毛。

### CheekAsymmetry

根据嘴部左右偏移推测单侧鼓腮：嘴偏向左侧时增加右颊鼓起，偏向右侧时增加左颊鼓起。这是推测，PICO 只提供单一的 `CheekPuff` 数据。

影响通道：`CheekPuffLeft`、`CheekPuffRight`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `MouthDifferenceThreshold` | 0.1 | 0–1 | 左右偏移差的阈值 |
| `TransitionWidth` | 0.1 | 0–1 | 过渡宽度 |
| `Gain` | 1 | 0–10 | 增加量倍率 |
| `MouthSmoothingMs` | 80 | 0–500 的整数 | 偏移方向判断的平滑时间常数（毫秒），0 表示不平滑 |

```text
alpha = 1 - exp(-Δt / (MouthSmoothingMs / 1000))       // Δt 为两帧接收时间差（秒）
SL = lerp(SL, R.MouthLeft,  alpha)
SR = lerp(SR, R.MouthRight, alpha)
d  = SL - SR

O.CheekPuffRight += R.MouthLeft  * Gain * A( d, MouthDifferenceThreshold, TransitionWidth)
O.CheekPuffLeft  += R.MouthRight * Gain * A(-d, MouthDifferenceThreshold, TransitionWidth)
```

平滑按实际接收时间计算，与帧率无关。首个样本、时间倒退或 `MouthSmoothingMs = 0` 时，平滑值直接取当前原始值。平滑只用于判断方向，增加量使用未平滑的原始偏移。

规则要求 `MouthLeft`、`MouthRight` 和 `CheekPuff` 都存在，否则不执行并清空平滑状态。增加量与 `CheekPuff` 的数值无关，因此未鼓腮时只做嘴部侧移，也会产生单侧鼓腮输出。

### TongueHold

舌头数值从高位短时掉落时，在一段时间内保持掉落前的值，用于减少伸舌过程中的闪断。

影响通道：`TongueOut`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `HoldMs` | 120 | 0–500 的整数 | 单次掉落的最长保持时间（毫秒），0 表示不保持 |
| `DropThreshold` | 0.15 | 0–1 | 判定为掉落的阈值 |

```text
R.TongueOut > DropThreshold:
  记录 high = R.TongueOut，结束当前保持，输出不变
R.TongueOut ≤ DropThreshold:
  若上一帧高于阈值: deadline = 当前接收时间 + HoldMs / 1000
  在 deadline 之前: O.TongueOut = high
  到达 deadline 后: 输出恢复为当前值
```

保持的是最后一帧高于阈值时的原始值，不是区间内的最大值。保持期间的连续低值帧不会延长截止时间，只有数值重新超过阈值后，下一次掉落才会重新计时。保持值随后仍会经过 `TongueJawGate` 和 `ChannelGain`。

保持到期不依赖下一帧数据到达。模块在每次输出时检查截止时间，到期后将 `TongueOut` 替换为当前帧未保持时的结果（已应用 `TongueJawGate` 和 `ChannelGain`，并限幅到 0–1），不会重新执行其他规则。

`TongueOut` 缺失、面部数据无效或 `HoldMs = 0` 时，规则不执行并清空保持状态。缺失不视为掉落，不会触发保持。

### TongueJawGate

嘴部张开不足时抑制伸舌，用于减少未张嘴时的伸舌误触发。它与 `TongueHold` 针对的是不同的问题。

影响通道：`TongueOut`。

| 参数 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| `JawThreshold` | 0.5 | 0–1 | 张嘴阈值，低于时抑制 |
| `TransitionWidth` | 0.1 | 0–1 | 过渡宽度 |
| `MaxReduction` | 1 | 0–1 | 最大削弱比例 |

```text
m = 1 - MaxReduction * B(R.JawOpen, JawThreshold, TransitionWidth)
O.TongueOut = O.TongueOut * m
m = 0 时清空 TongueHold 的保持状态
```

规则作用于 `TongueHold` 之后的值，因此保持值同样受到抑制。`m` 恰好为 0 时清空保持状态，避免嘴部张开后已被阻断的保持值重新出现。默认参数下，`JawOpen` 不超过 0.45 时舌头输出被完全抑制。`JawOpen` 或 `TongueOut` 缺失时规则不执行。

## 组合使用注意

- `Symmetry` 在单侧门控规则之后执行。使用 `Max` 方法时，一侧未被抑制的残留会传播到另一侧，前面规则的单侧抑制效果可能因此失效。
- `FrownJawAttenuation` 先削弱撇嘴，`ShrugToFrown` 随后与候选值取最大值。张嘴时两者不会相互抵消，候选值可能重新抬高已被削弱的撇嘴。
- `SmileRollSuppression` 与 `StretchCompetition` 都会削弱 `MouthCornerPull` 和 `MouthCornerSlant`，倍率相乘。`UpperUpPressGate` 与 `StretchCompetition` 对上唇抬起的削弱同样相乘。
- `BrowDownResponse` 与 `SquintResponse` 直接覆盖输出值。`ChannelGain` 中对应通道的倍率会在其结果之上再乘一次。
- `ChannelGain` 的倍率会与 VRCFT 自身的参数调整叠加。VRCFT 下游的标定、滤波和 Correctors 仍会改变最终输出。

## 配置校验

模块启动时校验每条规则，规则之间互相独立。以下任一情况只会禁用出错的规则，中性映射和其他合法规则不受影响，日志中会记录字段与原因：

- 数值不是有限数，或超出本文给出的范围；
- `ShrugToFrown` 的 `Threshold + Range` 大于 1（允许约 1e-7 的浮点误差）；
- 规则对象中出现未知字段或重复字段，或字段类型错误，例如 `HoldMs` 写成小数；
- `Gains` 中出现未知通道名、非表情通道或重复通道（大小写不敏感）；
- `Groups` 中出现未知或重复的分组，`Method` 不是 `Max` 或 `Average`。

未知的规则名不会被采用，也不影响其他规则。如果被禁用的是 `ChannelGain`，EyeWide 的 2 倍增益同样失效。`PostProcessing` 本身不是 JSON 对象时，全部规则（包括 `ChannelGain`）关闭。整个文件无法解析，或 `ConfigVersion` 缺失、重复、未知时，模块使用默认配置，不改写原文件。

校验通过后，日志会列出每条已启用的规则及其参数，可以据此确认实际生效的配置。配置工具的 `validate` 命令也可以在不启动模块的情况下检查配置，用法见[配置工具说明](../tools/ConfigTool/README_zh.md)。校验通过只说明配置合法，不代表效果合适。

## 状态重置

`CheekAsymmetry` 的平滑值和 `TongueHold` 的保持状态是仅有的跨帧状态。以下情况会清空这些状态：

- 选定的数据源变化，或数据源的会话发生变化，例如重新连接、协议锁定、Bridge 模式变化、串流会话超时；
- 面部数据超过 `Stale.FaceMs`（默认 250 ms）未更新；
- 相邻两帧面部数据的接收时间间隔超过 `Stale.FaceMs`，或接收时间倒退，例如宿主程序卡顿；
- 模块停用、没有可用的数据源，或输出过程中出现异常；
- 面部数据无效，此时眼部区域的规则仍按本帧数据执行；
- 规则自身的条件不满足，见 `CheekAsymmetry`、`TongueHold` 和 `TongueJawGate` 的说明。

配置修改需要重启 VRCFT 才能生效，重启后所有状态从空开始。时间相关的计算均使用本机单调时钟记录的接收时间，不使用发送端时间戳。

## 可用通道

`ChannelGain.Gains` 只接受以下通道名：

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

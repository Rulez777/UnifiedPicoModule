# UnifiedPicoConfigTool

[English](README.md) | 简体中文 · [返回主 README](../../README_zh.md)

UnifiedPicoConfigTool 是 UnifiedPicoModule 的离线命令行配置工具，提供三项功能：校验新版配置文件、导出默认配置，以及将旧版 PICO 模块的配置迁移为新版配置。

工具只编译 `Core/Contracts.cs` 与 `Configuration` 目录中的源码，不引用 VRCFaceTracking SDK，也不需要 VRCFT 处于运行状态。工具不会安装模块，不会将配置写入模块目录，也不会修改旧配置文件、VRCFT 设置或 Avatar 设置。生成的配置需要在核对后手动替换。

## 构建

模块包不包含本工具，需要从源码构建。构建需要 .NET 10 SDK，以下命令均在项目根目录执行。

执行 `tools/build.ps1` 时，脚本会在构建模块之后一并构建本工具：

```powershell
./tools/build.ps1 -Configuration Release
```

该脚本需要固定版本的 VRCFaceTracking 源码，准备方法请参阅[开发说明](../../docs/development_zh.md)。本工具本身不依赖 SDK，如只需要配置工具，也可以单独构建：

```powershell
dotnet build tools/ConfigTool/ConfigTool.csproj -c Release
```

构建产物为 `tools/ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll`，同目录下还有可直接运行的 `UnifiedPicoConfigTool.exe`。不带参数或使用 `--help`、`-h` 运行时，工具会输出命令语法和可用的来源版本标签。

```powershell
dotnet tools/ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll --help
```

## 使用流程

从旧模块迁移时，建议按以下顺序操作。示例中的 `legacy/` 表示存放旧配置副本的目录，需要自行准备，工具不会扫描或创建该目录。

```powershell
$tool = "tools/ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll"

# 1. 校验现有的新版配置（如有）
dotnet $tool validate UnifiedPicoModule.json

# 2. 导出一份默认配置，作为对照
dotnet $tool defaults UnifiedPicoModule.defaults.json

# 3. 迁移旧配置，生成新配置与迁移报告
dotnet $tool import legacy/PicoFacialDataModule.json bridge-upstream-0ab66348 UnifiedPicoModule.imported.json migration-report.txt

# 4. 校验迁移结果
dotnet $tool validate UnifiedPicoModule.imported.json
```

完成上述步骤后：

1. 阅读 `migration-report.txt`，确认其中列出的未导入字段和未复现行为是否可以接受。必要时可以对照第 2 步导出的默认配置修改迁移结果。
2. 完全退出 VRCFT，备份模块目录中现有的 `UnifiedPicoModule.json`。
3. 将确认后的配置复制到模块目录，并命名为 `UnifiedPicoModule.json`。模块目录为 `%APPDATA%\VRCFaceTracking\CustomLibs\8322e16d-c38b-42f0-8d35-68f27227b442\`。
4. 重新启动 VRCFT，在 Output 页面检查是否有关于配置字段的提示。

上述命令仅演示文件操作。示例中的版本标签需要按实际的旧配置来源选择，参见[来源版本](#来源版本)。

## 命令

| 命令 | 作用 |
|---|---|
| `validate <配置文件>` | 以只读方式校验已存在的新版配置，并逐项输出诊断 |
| `defaults <输出文件>` | 将模型默认值写入一个尚不存在的文件 |
| `import <旧配置文件> <来源版本> <新配置输出> <报告输出>` | 读取一个旧配置文件，生成新配置和迁移报告 |

命令名为 `defaults`，不存在 `default` 命令。参数数量不符或命令未知时，工具报错并以退出码 1 结束。

### validate

`validate` 通过 `ConfigStore.ReadOnly` 读取文件，不会创建、修正或重写输入文件。输入文件不存在时直接报错，不会生成默认配置。只有模块在 VRCFT 中加载时，才会在配置文件缺失的情况下自动创建默认文件。

校验规则与模块运行时一致：

- `ConfigVersion` 必须存在、唯一且等于 `1`。缺失、重复或取其他值时，整份配置按默认值处理，并输出相应诊断。
- 字段名区分大小写。未知字段和重复字段不予采用。
- 枚举值必须写成字符串，例如 `"Bridge"`，不接受数字。
- 数值和地址字段超出范围时，回退到模型默认值。主要范围为：`EyeGain` 各项 0–10；`Bridge.ReconnectMs` 100–60000；`Bridge.ControlTimeoutMs` 1000–120000；`Streaming.SessionTimeoutMs` 100–60000；`AutoSelectionWindowMs` 100–60000；`Stale.EyeMs`、`Stale.FaceMs` 1–5000；`Stale.NeutralPupilMillimeters` 1–10。IP 地址必须是单播 IPv4 地址，`Bridge.IP` 与 `Streaming.IP` 不得为 `0.0.0.0`。
- `Streaming.HeaderProfile` 不为 `null` 时，六个字段都必须显式给出（`Version` 为 0–65535，其余为 0–255），否则回退为 `null`。
- 某条后处理规则存在未知参数、重复参数、非法数值、未知的 `Method` 或 `Groups`、未知或重复的增益通道时，只禁用该规则，不影响其他规则。后处理参数的取值范围请参阅[后处理参考](../../docs/postprocessing_zh.md)。
- `ChannelGain` 的通道名不区分大小写，校验时在内存中规范化，不改写文件。

输出中以 `启用 ` 开头的行列出已启用规则的参数，属于信息输出，不计为问题。其余诊断行表示存在回退、禁用或未采用的字段，需要人工确认。

JSON 语法错误或根节点不是对象时，`validate` 报告失败。模块运行时遇到同样的文件会改用默认配置并保留原文件，`validate` 不做这种回退。

### defaults

`defaults` 将模型默认值写入指定路径。生成的配置包含全部字段：`ConfigVersion` 为 1，`Bridge.IP`、`Streaming.IP` 和 `Migration` 为 `null`；后处理只启用 `ChannelGain`，其中 EyeWide 左右通道为 2 倍，其他通道为 1 倍，其余 13 条规则关闭。

输出文件必须尚不存在，父目录必须已经存在。工具以 `CreateNew` 方式创建文件，路径已被占用时报错，不会覆盖。

### import

`import` 只读取命令中指定的一个旧配置文件，不扫描其他位置，也不合并多个来源。执行顺序如下：

1. 确认旧配置文件、新配置输出和报告输出是三个不同的路径（比较完整路径，不区分大小写）。
2. 确认两个输出路径都不存在文件或目录。
3. 确认来源版本是工具支持的标签之一。
4. 读取旧配置并转换。
5. 以 `CreateNew` 方式创建报告文件，写入迁移报告。
6. 以 `CreateNew` 方式写入新配置，成功后在报告末尾追加一行 `新配置成功写入：<路径>`。

前四步任一失败时不会写出任何文件。第 6 步失败时，报告文件已经存在，但末尾没有上述确认行，工具不会自动删除它。旧配置文件在任何情况下都不会被修改。

退出码 0 只表示两个文件已写入，不表示迁移结果与旧版行为一致，也不表示设备兼容性已经验证。

## 退出码

| 退出码 | `validate` | `defaults` | `import` |
|---|---|---|---|
| 0 | 没有需要确认的诊断 | 默认配置已写入 | 新配置与报告均已写入，报告中列出的差异不影响退出码 |
| 1 | 输入不存在、无法读取、JSON 语法错误、根节点不是对象，或参数错误 | 输出路径已被占用、父目录不存在、无权限，或参数错误 | 路径重复、输出已被占用、来源版本未知、旧配置无法读取或解析、写入失败，或参数错误 |
| 2 | 存在回退、禁用或未采用字段等需要人工确认的诊断 | 不使用 | 不使用 |

使用 `--help` 时退出码为 0。失败信息输出到标准错误流。

## 来源版本

工具不根据文件名或文件内容推断来源，必须在命令中显式指定来源版本标签。`PicoFacialDataModule.json` 存在上游版本和分流改版两种同名文件，字段含义不同，因此需要按实际来源选择。

| 来源版本标签 | 旧配置 | 解析方式 |
|---|---|---|
| `bridge-current-0.1-beta-split-fix1` | 分流改版 PicoFacialDataModule 的 `PicoFacialDataModule.json`（0.1-beta-split-fix1） | JSON |
| `bridge-upstream-0ab66348` | 上游 PicoFacialDataModule 固定提交 [`0ab66348`](https://github.com/thoricelli/PicoFacialDataModule/tree/0ab66348bf970763322fe261b54445bcc5a2b23e) 的 `PicoFacialDataModule.json` | JSON |
| `saft-ab500f03` | SAFT 固定提交 [`ab500f03`](https://github.com/regzo2/PicoStreamingAssistantFTUDP/tree/ab500f0355cd8bbf8f9f5b4e6bcfe43d16499cb7) 的 `PicoModuleConfig.json` | JSON |
| `pike-c026aa4` | pike 分支固定提交 [`c026aa4`](https://github.com/pikepikeid/VRCFTPicoModule/tree/c026aa455da60dd50492b3d36c9d360d0c343b8e) 的 `config.ini` | 冒号分隔的键值文本 |

标签区分大小写。JSON 来源的根节点必须是对象。

## 字段映射

所有来源的共同处理：

- 迁移结果中的 `ChannelGain` 初始为关闭，不套用新安装默认的 EyeWide 2 倍补偿，以保留旧配置的增益语义。是否重新启用由旧配置中的增益设置决定，规则见下文各来源的说明。
- 旧配置未涉及的字段取新版模型默认值。
- 增益值必须是 0–10 之间的有限数值，否则不予采用并写入报告。
- 旧配置中的重复键全部不予采用，未知键不导入，两者都会写入报告。
- 转换结束后，结果会再经过一次与 `validate` 相同的校验，校验产生的修正同样写入报告。

### PicoFacialDataModule（两个 bridge 标签）

| 旧字段 | 迁移结果 |
|---|---|
| `DisableEyeTracking`、`DisableFaceTracking` | 同名字段。非布尔值保持默认并写入报告 |
| `IP` | `Bridge.IP`。不是有效 IPv4 地址时改为自动发现 |
| `TrackingSettings.eyePupilDilationEyeOpennessThreshold` | 不导入。新版不再用睁眼度阈值推算瞳孔，改用实测瞳孔或中性默认值 |
| `ShapeGain`（仅 `bridge-current`） | `PostProcessing.ChannelGain` 的 `Enabled`、`DefaultGain`、`Gains`。任一条目非法时，迁移得到的 `ChannelGain` 关闭 |
| `ShapeGain`（`bridge-upstream`） | 不导入。上游版本没有该字段，报告中会注明 |
| `BridgeMode` | 不导入。该字段在旧版中是运行时能力公告，不是持久设置 |

`ShapeGain.Gains` 省略时，旧版会将所有已映射通道的倍率设为 1，迁移结果同样为所有通道写入 1。显式写成空对象时，迁移结果保留空对象，未列出的通道使用 `DefaultGain`。

两个标签的固定设置：

| 设置 | `bridge-current-0.1-beta-split-fix1` | `bridge-upstream-0ab66348` |
|---|---|---|
| `Source` | `Bridge` | `Bridge` |
| `Bridge.Protocol` | `BridgeSplit` | `DaemonLegacy536` |
| `EyePolicy` | `NativePreferred` | `LegacyNative` |
| `LegacyPerEyeGaze` | `false` | `true` |
| `LegacyExpressionCeiling` | `false` | `false` |

### SAFT（`saft-ab500f03`）

| 旧字段 | 迁移结果 |
|---|---|
| `scales.EyeXGazeLeft`、`scales.EyeYGazeLeft` | `EyeGain.LeftX`、`EyeGain.LeftY` |
| `scales.EyeXGazeRight`、`scales.EyeYGazeRight` | `EyeGain.RightX`、`EyeGain.RightY` |
| `scales.EyeOpennessLeft`、`scales.EyeOpennessRight` | `EyeGain.LeftOpenness`、`EyeGain.RightOpenness` |
| `scales` 中的其他键 | 按表情通道名（不区分大小写）写入 `ChannelGain.Gains`，`DefaultGain` 为 1 |

`scales` 中出现未知键、重复键，或表情通道的值非法、通道重复时，`ChannelGain` 整体关闭。眼动键的值非法时，只有对应的 `EyeGain` 项保持为 1。`scales` 缺失或不是对象时，`ChannelGain` 关闭，`EyeGain` 全部保持为 1。旧版 SAFT 在右眼 Y 轴上误用了 `EyeXGazeLeft` 的值，迁移时右眼 Y 轴使用 `EyeYGazeRight`，不沿用这一错误。

固定设置：`Source` 为 `Streaming`，`Streaming.Protocol` 为 `StreamingHeader16`，`EyePolicy` 为 `BlendshapeOnly`，`LegacyExpressionCeiling` 为 `true`。`Streaming.HeaderProfile` 保持 `null`，即按与 SAFT 一致的宽松条件接收 29765 端口数据。迁移本身不代表该路径已经验证，各发送端的验证情况请参阅[协议参考](../../docs/protocol_zh.md)。

### pike（`pike-c026aa4`）

`config.ini` 按行解析，每行格式为 `键: 值`。空行和以 `#` 开头的行会被忽略，键名不区分大小写，没有冒号的行写入报告。

| 旧字段 | 迁移结果 |
|---|---|
| `eye-tracking` | `enable` / `disable` 对应 `DisableEyeTracking` 为 `false` / `true` |
| `expression-tracking` | `enable` / `disable` 对应 `DisableFaceTracking` 为 `false` / `true` |
| `eye_gain` | 格式为 `X,Y`。X 写入 `EyeGain.LeftX` 与 `RightX`，Y 写入 `EyeGain.LeftY` 与 `RightY`。格式不符时保持为 1 |
| `mode` | 取值 0、1、2，展开为显式的后处理规则与通道增益。mode 0 不启用任何规则；mode 1、2 启用 `ChannelGain` 及若干规则。缺失时按该提交源码的默认值 2 处理；非法或重复时，所有后处理规则保持关闭 |

固定设置：`Source` 为 `Auto`，`EyePolicy` 为 `BlendshapeOnly`。

`mode` 只在迁移时展开一次，结果是普通的配置字段，模块运行时不存在隐藏预设。展开所用的规则和系数按本项目规格构造，未在真机上标定，与旧版算法并不逐项等同。各模式展开后的具体参数请参阅[配置说明](../../docs/configuration_zh.md)中的旧配置迁移部分。

## 迁移报告

迁移报告是一个独立的 UTF-8 文本文件，同样的内容也保存在新配置的 `Migration.Differences` 中。`Migration` 还记录 `SourceFile`（旧配置的绝对路径）、`SourceVersion` 和 `ImportedAtUtc`。

报告包含以下内容：

- 旧配置的路径、来源版本标签和导入时间；
- 迁移的总体说明：迁移按字段和行为近似转换，系数未经真机标定，兼容性未经实测，模块增益可能与 VRCFT 自身的参数调整叠加；
- 逐项记录的未知键、重复键、非法值，以及未导入的字段；
- 未复现的旧版行为，例如旧版的 0.99 限幅差异、瞳孔阈值推算、pike 各模式中未迁移的非线性曲线、帧平滑和死区等；
- 迁移后校验产生的修正；
- 所参考的旧项目及固定提交。

迁移转换为本项目的独立实现，只参考了旧项目公开的配置字段和可观察行为，没有复制 SAFT 或 lonelyicer 中许可不明的代码。

## 文件安全与隐私

- 工具不会覆盖任何已存在的文件或目录，也不会自动创建父目录。
- 迁移报告和 `Migration` 字段包含旧配置的绝对路径，可能含有用户名；旧配置中的 IP 地址和未知字段的原始值也可能原样出现。工具不做任何脱敏处理。
- 分享报告或迁移后的配置前，请同时检查这两个文件，去除个人信息。也可以在确认配置无误后，将 `Migration` 设为 `null` 再使用。

更多说明请参阅[隐私与网络安全](../../docs/privacy_zh.md)。

## 相关文档

- [配置说明](../../docs/configuration_zh.md)
- [后处理参考](../../docs/postprocessing_zh.md)
- [协议参考](../../docs/protocol_zh.md)
- [开发说明](../../docs/development_zh.md)
- [默认配置样例](../../config/UnifiedPicoModule.example.json)

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Configuration;

/// <summary>
/// Independently implemented from the local module plan and publicly observable settings
/// fields. Sources are recorded in each report; no legacy updater/algorithm is copied.
/// </summary>
public static class LegacyImporter
{
    public const string BridgeCurrent = "bridge-current-0.1-beta-split-fix1";
    public const string BridgeUpstream = "bridge-upstream-0ab66348";
    public const string Saft = "saft-ab500f03";
    public const string Pike = "pike-c026aa4";
    public static IReadOnlyList<string> SourceVersions { get; } = [BridgeCurrent, BridgeUpstream, Saft, Pike];

    public static (ModuleConfig Config, IReadOnlyList<string> Report) Import(string sourcePath, string sourceVersion)
    {
        if (!SourceVersions.Contains(sourceVersion, StringComparer.Ordinal))
            throw new ArgumentException("必须显式选择已核查 sourceVersion：" + string.Join(", ", SourceVersions), nameof(sourceVersion));
        var path = Path.GetFullPath(sourcePath);
        var importedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var report = new List<string>
        {
            $"只读导入文件：{path}", $"显式来源版本：{sourceVersion}",
            $"导入 UTC 时间：{importedAt}",
            "只读取此文件，不扫描其他配置、不合并来源、原文件保留。",
            "迁移是字段和行为近似，不是旧 DLL/算法逐位复现；设备、VRCFT、Avatar 兼容性尚未实测。",
            "系数未经本项目实机标定，不是推荐参数；模块增益可能与 VRCFT 调整叠乘，不改客户端或 Avatar。"
        };
        var text = File.ReadAllText(path);
        var config = new ModuleConfig();
        report.Add("视线与眼睑开合统一由表情模型的 EyeLook 和 1 - EyeBlink 驱动，原生眼动包只提供真实瞳孔直径；旧配置中的 gaze/openness 来源选择没有对应字段，不再保留。");
        // Imports preserve legacy gain behavior instead of adding the fresh-install EyeWide x2.
        config.PostProcessing.ChannelGain.Enabled = false;
        report.Add("迁移不自动应用新安装的 EyeWide ×2 默认补偿；按旧配置保留增益语义。");
        if (sourceVersion == Pike) ImportPike(text, config, report);
        else
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("旧 JSON 根必须为对象。");
            if (sourceVersion == Saft) ImportSaft(root, config, report);
            else ImportBridge(root, sourceVersion, config, report);
        }
        ConfigValidator.Validate(config, report.Add);
        config.Migration = new MigrationMetadata
        {
            SourceFile = path, SourceVersion = sourceVersion,
            ImportedAtUtc = importedAt,
            Differences = new List<string>(report)
        };
        return (config, report.AsReadOnly());
    }

    private static void ImportBridge(JsonElement root, string version, ModuleConfig config, List<string> report)
    {
        config.Source = SourceSelection.Bridge;
        config.LegacyExpressionCeiling = false;
        config.Bridge.Protocol = version == BridgeCurrent ? InputProtocol.BridgeSplit : InputProtocol.DaemonLegacy536;
        var fields = Fields(root, "", report);
        ReadBoolean(fields, "DisableEyeTracking", v => config.DisableEyeTracking = v, report);
        ReadBoolean(fields, "DisableFaceTracking", v => config.DisableFaceTracking = v, report);
        if (fields.Remove("IP", out var ip))
        {
            if (ip.ValueKind == JsonValueKind.String || ip.ValueKind == JsonValueKind.Null) config.Bridge.IP = ip.GetString();
            else report.Add($"IP={ip.GetRawText()} 非法，使用自动发现。");
        }
        if (fields.Remove("TrackingSettings", out var tracking))
        {
            var settings = Fields(tracking, "TrackingSettings", report);
            if (settings.Remove("eyePupilDilationEyeOpennessThreshold", out var pupil))
                report.Add($"TrackingSettings.eyePupilDilationEyeOpennessThreshold={pupil.GetRawText()} 未复现：不再用眼睑阈值伪造瞳孔；使用有效真实瞳孔或中性默认。");
            Unknown(settings, "TrackingSettings", report);
        }
        else report.Add("旧 TrackingSettings 缺失：不复现旧默认 pupil/openness 阈值启发式。");
        if (fields.Remove("ShapeGain", out var gain))
        {
            if (version != BridgeCurrent) report.Add($"ShapeGain={gain.GetRawText()} 不属于所选 upstream 版本，未导入；不凭同名文件推断当前版。");
            else ImportShapeGain(gain, config, report);
        }
        else if (version == BridgeCurrent) report.Add("ShapeGain 缺失：保持 ChannelGain 关闭，未擅自推断为 upstream。");
        if (fields.Remove("BridgeMode", out var mode)) report.Add($"BridgeMode={mode.GetRawText()} 未导入：核查版为运行时能力公告，不是可解锁增强能力的持久设置。");
        Unknown(fields, "", report);
        report.Add("来源证据：thoricelli/PicoFacialDataModule SettingsManager.cs；upstream 固定 0ab66348bf970763322fe261b54445bcc5a2b23e；current 为 reference 工作区 0.1-beta-split-fix1（非 HEAD）。");
        report.Add("迁移不保留旧右眼状态位错误、无限等待或旧 pupil 布尔阈值。Bridge 未设置 0.99 全局上限：旧 ShapeGain 仅非等倍时局部限幅 0.99，新 ChannelGain 统一安全限幅 0–1，饱和值可能不同；不擅自把关闭/等倍输出也降至 0.99。");
        report.Add(version == BridgeCurrent ? "旧 JSON 的 normal/enhance 记录不导入：模式公告是运行时能力信息，视线与眼睑统一由表情来源决定。" : "旧 upstream 的分眼视线开关不再导入：原生包的视线字段不参与输出，视线统一由表情推算。");
    }

    private static void ImportShapeGain(JsonElement json, ModuleConfig config, List<string> report)
    {
        var fields = Fields(json, "ShapeGain", report);
        var valid = json.ValueKind == JsonValueKind.Object && fields.Count == json.EnumerateObject().Count();
        var enabled = false;
        if (fields.Remove("Enabled", out var flag))
        {
            if (flag.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = flag.GetBoolean();
            else { valid = false; report.Add($"ShapeGain.Enabled={flag.GetRawText()} 非法。"); }
        }
        var defaultGain = 1f;
        if (fields.Remove("DefaultGain", out var defaultValue)) valid &= Gain(defaultValue, "ShapeGain.DefaultGain", out defaultGain, report);
        Dictionary<string, float> gains;
        if (fields.Remove("Gains", out var entries))
        {
            gains = new();
            valid &= ExpressionGains(entries, "ShapeGain.Gains", gains, report);
            report.Add("ShapeGain.Gains 已显式提供：保留其键集合；显式空对象使用 DefaultGain，不与旧缺省字典混淆。");
        }
        else
        {
            // Verified current legacy model initializes every mapped channel to 1 when
            // this property is omitted; it does NOT start with an empty dictionary.
            gains = Enum.GetNames<ExpressionChannel>().ToDictionary(name => name, _ => 1f, StringComparer.Ordinal);
            report.Add("ShapeGain.Gains 缺省：按核查的旧模型保留所有已映射通道倍率1；不将其误迁为全部使用 DefaultGain。");
        }
        if (fields.Count > 0) { valid = false; Unknown(fields, "ShapeGain", report); }
        Rule(config, "ChannelGain", new { Enabled = enabled && valid, DefaultGain = defaultGain, Gains = gains });
        report.Add(valid ? "ShapeGain → 显式 PostProcessing.ChannelGain（Enabled/DefaultGain/Gains）。" : "ShapeGain 配置非法；仅禁用迁移的 ChannelGain。");
    }

    private static void ImportSaft(JsonElement root, ModuleConfig config, List<string> report)
    {
        config.Source = SourceSelection.Streaming;
        config.Streaming.Protocol = InputProtocol.StreamingHeader16;
        config.LegacyExpressionCeiling = true;
        var fields = Fields(root, "", report);
        var gains = new Dictionary<string, float>();
        var valid = true;
        if (fields.Remove("scales", out var scales))
        {
            if (scales.ValueKind != JsonValueKind.Object) { report.Add("scales 必须为对象；ChannelGain 关闭，EyeGain 保持中性。"); valid = false; }
            else
            {
                var scaleFields = Fields(scales, "scales", report);
                valid = scaleFields.Count == scales.EnumerateObject().Count();
                foreach (var pair in scaleFields)
                {
                    var eyeProperty = pair.Key switch
                    {
                        "EyeXGazeLeft" => "LeftX", "EyeYGazeLeft" => "LeftY",
                        "EyeXGazeRight" => "RightX", "EyeYGazeRight" => "RightY",
                        "EyeOpennessLeft" => "LeftOpenness", "EyeOpennessRight" => "RightOpenness", _ => null
                    };
                    if (eyeProperty is not null)
                    {
                        if (Gain(pair.Value, "scales." + pair.Key, out var value, report))
                            typeof(EyeGainSettings).GetProperty(eyeProperty)!.SetValue(config.EyeGain, value);
                    }
                    else if (ConfigValidator.TryChannel(pair.Key, out var channel))
                    {
                        if (Gain(pair.Value, "scales." + pair.Key, out var value, report) && !gains.ContainsKey(channel.ToString())) gains.Add(channel.ToString(), value);
                        else { valid = false; report.Add($"scales.{pair.Key} 非法或通道重复；ChannelGain 将禁用。"); }
                    }
                    else { valid = false; report.Add($"未知 scales.{pair.Key}={pair.Value.GetRawText()}，未导入；ChannelGain 将禁用。"); }
                }
            }
        }
        else { valid = false; report.Add("scales 缺失；保持所有增益中性且 ChannelGain 关闭。"); }
        Rule(config, "ChannelGain", new { Enabled = valid, DefaultGain = 1f, Gains = gains });
        Unknown(fields, "", report);
        report.Add("scales 表情键 → ChannelGain，六个眼动/睁眼键 → EyeGain；右眼 Y 明确使用 EyeYGazeRight，不沿用旧模块误用 EyeXGazeLeft 的错误。缺失键取中性 1，不猜测旧自定义值。");
        report.Add("来源证据：regzo2/PicoStreamingAssistantFTUDP ab500f0355cd8bbf8f9f5b4e6bcfe43d16499cb7 的配置字段及右眼 Y 调用点，只读核查；未复制其许可不明实现。");
        report.Add("SAFT 的旧眼动 ±0.99/openness 0.99 限幅、下游默认瞳孔和未支持的其他 SDK 通道不逐位复现；表情 0.99 上限已显式开启。");
    }

    private static void ImportPike(string text, ModuleConfig config, List<string> report)
    {
        config.Source = SourceSelection.Auto;
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon < 1) { report.Add($"config.ini 第 {lineNumber} 行未导入（应为冒号键值）：{line}"); continue; }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!entries.TryAdd(key, value)) { duplicates.Add(key); report.Add($"config.ini 字段重复 {key}；所有该字段值均不采用。"); }
        }
        foreach (var key in duplicates) entries.Remove(key);
        foreach (var key in new[] { "eye-tracking", "expression-tracking" })
        {
            if (!entries.Remove(key, out var value)) continue;
            if (!value.Equals("enable", StringComparison.OrdinalIgnoreCase) && !value.Equals("disable", StringComparison.OrdinalIgnoreCase))
            { report.Add($"{key}:{value} 非法，应为 enable/disable；保持安全默认。"); continue; }
            var disabled = value.Equals("disable", StringComparison.OrdinalIgnoreCase);
            if (key == "eye-tracking") config.DisableEyeTracking = disabled; else config.DisableFaceTracking = disabled;
        }
        if (entries.Remove("eye_gain", out var eyeGain))
        {
            var parts = eyeGain.Split(',');
            if (parts.Length == 2 && FloatGain(parts[0], out var x) && FloatGain(parts[1], out var y))
            { config.EyeGain.LeftX = config.EyeGain.RightX = x; config.EyeGain.LeftY = config.EyeGain.RightY = y; report.Add($"eye_gain → EyeGain.LeftX/RightX={x}, LeftY/RightY={y}。"); }
            else report.Add($"eye_gain:{eyeGain} 非法（须为两个有限的 0–10 倍率）；保持眼增益 1；不沿用旧解析器忽略额外分量行为。");
        }
        var mode = 2;
        var validMode = !duplicates.Contains("mode");
        if (entries.Remove("mode", out var modeText)) validMode = int.TryParse(modeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out mode) && mode is >= 0 and <= 2;
        else if (validMode) report.Add("mode 缺失：显式按所选 c026aa4 源码默认 mode=2 转换（README 的旧默认 1 不采用）。");
        foreach (var entry in entries) report.Add($"未知 config.ini 字段 {entry.Key}:{entry.Value}，未导入。");
        if (validMode) ApproximatePikeMode(config, mode, report);
        else report.Add("mode 非法或重复：所有可选后处理保持关闭，不猜测旧模式。");
        report.Add("保留 Auto 来源但不复现旧 100ms 首包探测、536 字节/串流长度宽松解析；不会修改 PICO Connect faceTrackingTransferProtocol。");
        report.Add("来源证据：pikepikeid/VRCFTPicoModule origin/master c026aa455da60dd50492b3d36c9d360d0c343b8e，ReadConfiguration 的冒号语法/字段及模式可观察行为；未复制 Updater/lonelyicer 继承实现。转换仅使用本项目规格规则。");
    }

    private static void ApproximatePikeMode(ModuleConfig config, int mode, List<string> report)
    {
        report.Add($"pike mode={mode} 仅展开为显式参数；没有 runtime preset，也没有旧算法复刻。");
        if (mode == 0) { report.Add("mode 0 → 中性映射、全部可选规则关闭；新中性映射含旧原始模式可能遗漏的 MouthUpperDeepen 等通道，安全限幅不复现异常值。"); return; }
        // Interpret simple gains and independent effects using the new spec, not an old updater.
        var gains = new Dictionary<string, float>
        {
            ["JawOpen"] = 1f / 0.75f, ["JawLeft"] = 2, ["JawRight"] = 2,
            ["MouthUpperLeft"] = 2, ["MouthLowerLeft"] = 2, ["MouthUpperRight"] = 2, ["MouthLowerRight"] = 2,
            ["MouthUpperUpLeft"] = 1.25f, ["MouthUpperUpRight"] = 1.25f,
            ["MouthCornerPullLeft"] = 2, ["MouthCornerPullRight"] = 2,
            ["MouthCornerSlantLeft"] = 2, ["MouthCornerSlantRight"] = 2
        };
        Rule(config, "FrownJawAttenuation", new { Enabled = true, JawThreshold = 0.1f, TransitionWidth = 0f, MaxReduction = 0.5f });
        Rule(config, "UpperUpPressGate", new { Enabled = true, PressThreshold = 0.1f, TransitionWidth = 0f, MaxReduction = 1f });
        Rule(config, "PuckerFunnelAssist", new { Enabled = true, PuckerThreshold = 0.3f, PressThreshold = 0.2f, TransitionWidth = 0f, Strength = 1f });
        Rule(config, "SmileRollSuppression", new { Enabled = true, RollThreshold = 0.2f, TransitionWidth = 0f, MaxReduction = 1f });
        Rule(config, "TongueJawGate", new { Enabled = true, JawThreshold = 0.5f, TransitionWidth = 0f, MaxReduction = 1f });
        Rule(config, "Symmetry", new { Enabled = true, Groups = mode == 1 ? new[] { "Smile" } : new[] { "Smile", "Frown", "Stretch", "UpperUp" }, Method = "Max", Strength = 1f });
        if (mode == 2)
        {
            gains["MouthClosed"] = 1f / 0.65f;
            foreach (var channel in new[] { "MouthStretchLeft", "MouthStretchRight" }) gains[channel] = 1f / 0.3f;
            foreach (var channel in new[] { "BrowInnerUpLeft", "BrowInnerUpRight", "BrowOuterUpLeft", "BrowOuterUpRight" }) gains[channel] = 2;
            Rule(config, "BrowInnerSeparation", new { Enabled = true, Strength = 1f });
            Rule(config, "ShrugToFrown", new { Enabled = true, Threshold = 0.6f, Range = 0.05f, Gain = 1f, RollBlockThreshold = 0.3f, PuckerBlockThreshold = 0.4f, FunnelBlockThreshold = 0.1f, TransitionWidth = 0f, ConsumeShrug = false });
            report.Add("mode 2 额外近似：内外眉分离、眉/Stretch/MouthClosed 增益、ShrugToFrown。未迁移旧 BrowDown sigmoid×4.5 或 Squint 乘 8 后幂/眨眼联动（与新曲线语义、0–10 增益边界不同），相关规则保持关闭。");
            report.Add("mode 2 未复现：低 Jaw 时 Frown 的二次衰减、MouthLeft/Right 输出死区、旧 Stretch/Smile/UpperUp 基于放大后输出的竞争、无条件删除 Shrug Raiser。StretchCompetition 保持关闭；新辅助只生成有界候选。");
        }
        else report.Add("mode 1 未复现：RollLower 时 Frown×2.5+Roll 的旧分支；仅迁移张嘴时 Frown 衰减。");
        Rule(config, "ChannelGain", new { Enabled = true, DefaultGain = 1f, Gains = gains });
        report.Add("近似展开：ChannelGain、FrownJawAttenuation、UpperUpPressGate、PuckerFunnelAssist、SmileRollSuppression、Symmetry、TongueJawGate；阈值相等处、新条件读 raw、固定顺序/安全限幅及 Funnel 只补不减，与旧模式存在差异。");
        report.Add("未复现：旧鼓腮 0.5/帧平滑与 CheekPuff>0.1 门槛（CheekAsymmetry 关闭）、NoseSneer>0.6 门槛、Slant 减 RollLower、旧模式未写字段/残留行为。Deepen 同步门控、Tongue 阈值边界也会与旧版不同；TongueHold 未自动启用。");
    }

    private static void Rule(ModuleConfig config, string name, object values)
    {
        var property = typeof(PostProcessingSettings).GetProperty(name) ?? throw new InvalidOperationException($"共享契约缺少 PostProcessing.{name}。");
        var rule = property.GetValue(config.PostProcessing)!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(values));
        foreach (var field in json.RootElement.EnumerateObject())
        {
            var parameter = rule.GetType().GetProperty(field.Name) ?? throw new InvalidOperationException($"共享契约缺少 {name}.{field.Name}。");
            parameter.SetValue(rule, JsonSerializer.Deserialize(field.Value.GetRawText(), parameter.PropertyType, ConfigStore.JsonOptions));
        }
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement json, string prefix, List<string> report)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (json.ValueKind != JsonValueKind.Object) { report.Add($"{prefix}={json.GetRawText()} 必须为对象；未采用。"); return result; }
        foreach (var group in json.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal))
        {
            if (group.Count() != 1) { report.Add($"{prefix}.{group.Key} 重复，未采用：{string.Join(" / ", group.Select(p => p.Value.GetRawText()))}"); continue; }
            result.Add(group.Key, group.First().Value);
        }
        return result;
    }
    private static void Unknown(Dictionary<string, JsonElement> fields, string prefix, List<string> report)
    { foreach (var field in fields) report.Add($"未知旧字段 {(prefix.Length == 0 ? "" : prefix + ".")}{field.Key}={field.Value.GetRawText()}，未导入。"); }
    private static void ReadBoolean(Dictionary<string, JsonElement> fields, string name, Action<bool> assign, List<string> report)
    { if (!fields.Remove(name, out var value)) return; if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) assign(value.GetBoolean()); else report.Add($"{name}={value.GetRawText()} 非法，保持默认。"); }
    private static bool Gain(JsonElement json, string path, out float value, List<string> report)
    {
        value = 1;
        if (json.ValueKind == JsonValueKind.Number && json.TryGetSingle(out var number) && float.IsFinite(number) && number is >= 0 and <= 10) { value = number; return true; }
        report.Add($"{path}={json.GetRawText()} 必须为有限的 0–10 倍率，未采用。"); return false;
    }
    private static bool FloatGain(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value) && value is >= 0 and <= 10;
    private static bool ExpressionGains(JsonElement json, string path, Dictionary<string, float> gains, List<string> report)
    {
        if (json.ValueKind != JsonValueKind.Object) { report.Add($"{path}={json.GetRawText()} 必须为对象。"); return false; }
        var valid = true;
        foreach (var field in json.EnumerateObject())
        {
            if (!ConfigValidator.TryChannel(field.Name, out var channel)) { valid = false; report.Add($"未知 {path}.{field.Name}={field.Value.GetRawText()}，未导入。"); continue; }
            if (!Gain(field.Value, path + "." + field.Name, out var gain, report) || !gains.TryAdd(channel.ToString(), gain))
            { valid = false; report.Add($"{path}.{field.Name} 非法或重复；ChannelGain 将禁用。"); }
        }
        return valid;
    }
}

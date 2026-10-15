# UnifiedPicoConfigTool

English | [简体中文](README_zh.md) · [Back to main README](../../README.md)

UnifiedPicoConfigTool is an offline command-line configuration tool for UnifiedPicoModule. It provides three functions: validating a configuration file in the current format, exporting the default configuration, and migrating configurations from earlier PICO modules to the current format.

The tool does not need the VRCFaceTracking SDK or a running VRCFT. It does not install the module, apply configurations automatically, or modify earlier configuration files, VRCFT settings or avatar settings; it writes only the paths you give it. Generated configurations must be reviewed and put in place manually.

## Building

The tool is not included in the module package and must be built from source with the .NET 10 SDK. It does not depend on the VRCFaceTracking SDK, so it can be built on its own. Run these commands from the project root:

```powershell
dotnet build tools/ConfigTool/ConfigTool.csproj -c Release
dotnet tools/ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll --help
```

The output is `tools/ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll`, with an executable `UnifiedPicoConfigTool.exe` in the same directory. Running the tool without arguments, or with `--help` or `-h`, prints the command syntax and the available source version labels.

`./tools/build.ps1 -Configuration Release` builds the module and this tool together, if you have already prepared the pinned VRCFaceTracking source for the module (see the [development guide](../../docs/development.md)).

## Workflow

Before migrating, prepare two things:

- A copy of the earlier configuration, for example in `legacy/` (the tool does not scan for or create it).
- The source version label that matches where that file actually comes from; the example below uses `bridge-upstream-0ab66348`. See [Source versions](#source-versions).

```powershell
$tool = "tools/ConfigTool/bin/Release/net10.0/UnifiedPicoConfigTool.dll"

# Optional: validate the module's current configuration, if you already have one
dotnet $tool validate UnifiedPicoModule.json

# Optional: export the defaults as a reference for review
dotnet $tool defaults UnifiedPicoModule.defaults.json

# Migrate the earlier configuration into a new configuration and a report
dotnet $tool import legacy/PicoFacialDataModule.json bridge-upstream-0ab66348 UnifiedPicoModule.imported.json migration-report.txt

# Validate the migrated configuration
dotnet $tool validate UnifiedPicoModule.imported.json
```

Then put the result in place by hand:

1. Read `migration-report.txt` and decide whether the fields that were not imported and the behavior that was not reproduced are acceptable. If necessary, adjust the migrated configuration, using the exported defaults as a reference.
2. Exit VRCFT completely and back up the existing `UnifiedPicoModule.json` in the module directory.
3. Copy the reviewed configuration into the module directory as `UnifiedPicoModule.json`. The module directory is `%APPDATA%\VRCFaceTracking\CustomLibs\8322e16d-c38b-42f0-8d35-68f27227b442\`.
4. Start VRCFT again and check the Output page for messages about configuration fields.

## Commands

| Command | Purpose |
|---|---|
| `validate <config-file>` | Validates an existing configuration in the current format without modifying it, and prints diagnostics field by field |
| `defaults <output-file>` | Writes the model defaults to a file that does not yet exist |
| `import <earlier-config> <source-version> <new-config-output> <report-output>` | Reads one earlier configuration file and generates a new configuration and a migration report |

An unknown command or a wrong number of arguments results in an error and exit code 1.

### validate

`validate` reads the file through `ConfigStore.ReadOnly` and never creates, corrects or rewrites the input file. If the input file does not exist, it fails without generating a default configuration. Only the module, when loaded by VRCFT, creates a default file if the configuration is missing.

The validation rules are the same as at runtime:

- `ConfigVersion` must be present exactly once and equal to `1`. If it is missing, duplicated or has another value, the whole configuration is treated as defaults and a diagnostic is printed.
- Field names are case-sensitive. Unknown and duplicated fields are not used.
- Enum values must be written as strings, for example `"Bridge"`; numbers are not accepted.
- Numeric and address fields outside their valid range fall back to the model default. The main ranges are: each `EyeGain` entry 0–10; `Bridge.ReconnectMs` 100–60000; `Bridge.ControlTimeoutMs` 1000–120000; `Streaming.SessionTimeoutMs` 100–60000; `AutoSelectionWindowMs` 100–60000; `Stale.EyeMs` and `Stale.FaceMs` 1–5000; `Stale.NeutralPupilMillimeters` 1–10. IP addresses must be unicast IPv4 addresses, and `Bridge.IP` and `Streaming.IP` must not be `0.0.0.0`.
- When `Streaming.HeaderProfile` is not `null`, all six of its fields must be given explicitly (`Version` 0–65535, the others 0–255); otherwise it falls back to `null`.
- If a post-processing rule contains an unknown or duplicated parameter, an invalid value, an unknown `Method` or `Groups` entry, or an unknown or duplicated gain channel, only that rule is disabled; other rules are unaffected. For the parameter ranges, see the [post-processing reference](../../docs/postprocessing.md).
- Channel names in `ChannelGain` are case-insensitive. They are normalized in memory during validation; the file is not rewritten.

Output lines beginning with `启用 ` list the parameters of enabled rules. They are informational and do not count as problems. All other diagnostic lines indicate fallbacks, disabled rules or unused fields and need to be reviewed.

If the JSON is malformed or its root is not an object, `validate` reports a failure. The module at runtime falls back to defaults for such a file and leaves the file unchanged; `validate` does not perform this fallback.

### defaults

`defaults` writes the model defaults to the specified path. The generated configuration contains every field: `ConfigVersion` is 1, and `Bridge.IP`, `Streaming.IP` and `Migration` are `null`. Of the post-processing rules, only `ChannelGain` is enabled, with the left and right EyeWide channels at 2× and other channels at 1×; the other 13 rules are disabled.

The output file must not already exist, and its parent directory must exist; an occupied path is reported as an error and never overwritten.

### import

`import` reads only the earlier configuration file named on the command line. It does not search other locations and does not merge multiple sources. The earlier configuration, the new configuration output and the report output must be three different paths (compared case-insensitively), neither output may already exist, and the source version must be a supported label. If any of these checks or the conversion fails, no files are written and the earlier configuration is untouched.

The tool writes the migration report first, then the new configuration, and on success appends the line `新配置成功写入：<path>` ("new configuration written") to the end of the report; that line shows the import finished. If the run fails midway, the report and possibly a partial configuration remain at the paths you gave. Check them, keep them if useful, and retry with two new unused paths, since an occupied output path is never overwritten.

Exit code 0 only means both files were written. It does not mean the migrated configuration behaves the same as the earlier module, nor that device compatibility has been verified.

## Exit codes

| Code | `validate` | `defaults` | `import` |
|---|---|---|---|
| 0 | No diagnostics need review | The default configuration was written | The new configuration and the report were written; differences listed in the report do not affect the code |
| 1 | Input missing or unreadable, malformed JSON, root not an object, or invalid arguments | Output path occupied, parent directory missing, insufficient permissions, or invalid arguments | Duplicate paths, output occupied, unknown source version, earlier configuration unreadable or unparsable, write failure, or invalid arguments |
| 2 | Diagnostics that need review, such as fallbacks, disabled rules or unused fields | Not used | Not used |

`--help` exits with code 0. Error messages are written to standard error.

## Source versions

The tool does not infer the source from the file name or contents; the source version label must be specified on the command line. `PicoFacialDataModule.json` exists both in the upstream version and in a split fork, with the same file name but different field semantics, so the label must match the actual source.

| Source version label | Earlier configuration | Format |
|---|---|---|
| `bridge-current-0.1-beta-split-fix1` | `PicoFacialDataModule.json` from the split fork of PicoFacialDataModule (0.1-beta-split-fix1) | JSON |
| `bridge-upstream-0ab66348` | `PicoFacialDataModule.json` from upstream PicoFacialDataModule at pinned commit [`0ab66348`](https://github.com/thoricelli/PicoFacialDataModule/tree/0ab66348bf970763322fe261b54445bcc5a2b23e) | JSON |
| `saft-ab500f03` | `PicoModuleConfig.json` from SAFT at pinned commit [`ab500f03`](https://github.com/regzo2/PicoStreamingAssistantFTUDP/tree/ab500f0355cd8bbf8f9f5b4e6bcfe43d16499cb7) | JSON |
| `pike-c026aa4` | `config.ini` from the pike branch at pinned commit [`c026aa4`](https://github.com/pikepikeid/VRCFTPicoModule/tree/c026aa455da60dd50492b3d36c9d360d0c343b8e) | Colon-separated key-value text |

Labels are case-sensitive. For JSON sources, the root must be an object.

## Field mapping

Common handling for all sources:

- `ChannelGain` starts disabled in the migrated configuration. The EyeWide 2× compensation of a fresh installation is not applied, so that the gain semantics of the earlier configuration are preserved. Whether `ChannelGain` is enabled again depends on the gain settings in the earlier configuration, as described for each source below.
- Fields not covered by the earlier configuration take the model defaults.
- Gains must be finite numbers between 0 and 10; other values are not used and are recorded in the report.
- Duplicated keys in the earlier configuration are not used, and unknown keys are not imported. Both are recorded in the report.
- After conversion, the result goes through the same validation as `validate`, and any corrections are also recorded in the report.

### PicoFacialDataModule (both bridge labels)

| Earlier field | Migrated to |
|---|---|
| `DisableEyeTracking`, `DisableFaceTracking` | The fields of the same name. Non-boolean values keep the default and are reported |
| `IP` | `Bridge.IP`. If it is not a valid IPv4 address, automatic discovery is used |
| `TrackingSettings.eyePupilDilationEyeOpennessThreshold` | Not imported. The current module no longer estimates pupil size from an openness threshold; it uses measured pupil data or the neutral default |
| `ShapeGain` (`bridge-current` only) | `Enabled`, `DefaultGain` and `Gains` of `PostProcessing.ChannelGain`. If any entry is invalid, the migrated `ChannelGain` is disabled |
| `ShapeGain` (`bridge-upstream`) | Not imported. The upstream version has no such field, and the report says so |
| `BridgeMode` | Not imported. In the earlier module this field is a runtime capability announcement, not a persistent setting |

When `ShapeGain.Gains` is omitted, the earlier module sets every mapped channel to 1, and the migrated configuration likewise writes 1 for every channel. When it is given as an empty object, the migrated configuration keeps the empty object, and channels not listed use `DefaultGain`.

Fixed settings for the two labels:

| Setting | `bridge-current-0.1-beta-split-fix1` | `bridge-upstream-0ab66348` |
|---|---|---|
| `Source` | `Bridge` | `Bridge` |
| `Bridge.Protocol` | `BridgeSplit` | `DaemonLegacy536` |
| `LegacyExpressionCeiling` | `false` | `false` |

Neither label imports the earlier gaze or openness source: the migrated configuration derives gaze from the `EyeLook` shapes and openness from `1 - EyeBlink`, and the difference is recorded in the report.

### SAFT (`saft-ab500f03`)

| Earlier field | Migrated to |
|---|---|
| `scales.EyeXGazeLeft`, `scales.EyeYGazeLeft` | `EyeGain.LeftX`, `EyeGain.LeftY` |
| `scales.EyeXGazeRight`, `scales.EyeYGazeRight` | `EyeGain.RightX`, `EyeGain.RightY` |
| `scales.EyeOpennessLeft`, `scales.EyeOpennessRight` | `EyeGain.LeftOpenness`, `EyeGain.RightOpenness` |
| Other keys in `scales` | Written to `ChannelGain.Gains` by expression channel name (case-insensitive), with `DefaultGain` 1 |

If `scales` contains an unknown or duplicated key, or an expression channel has an invalid value or appears twice, `ChannelGain` is disabled as a whole. If an eye key has an invalid value, only the corresponding `EyeGain` entry stays at 1. If `scales` is missing or not an object, `ChannelGain` is disabled and all `EyeGain` entries stay at 1. The earlier SAFT module applied the `EyeXGazeLeft` value to the right-eye Y axis by mistake; the migration uses `EyeYGazeRight` for the right-eye Y axis and does not carry this error over.

Fixed settings: `Source` is `Streaming`, `Streaming.Protocol` is `StreamingHeader16`, and `LegacyExpressionCeiling` is `true`. `Streaming.HeaderProfile` stays `null`, so data on port 29765 is accepted under the same permissive conditions as SAFT. The migration itself does not mean this path has been verified; for the verification status of each sender, see the [protocol reference](../../docs/protocol.md).

### pike (`pike-c026aa4`)

`config.ini` is parsed line by line in the form `key: value`. Empty lines and lines starting with `#` are ignored, key names are case-insensitive, and lines without a colon are recorded in the report.

| Earlier field | Migrated to |
|---|---|
| `eye-tracking` | `enable` / `disable` set `DisableEyeTracking` to `false` / `true` |
| `expression-tracking` | `enable` / `disable` set `DisableFaceTracking` to `false` / `true` |
| `eye_gain` | Format `X,Y`. X is written to `EyeGain.LeftX` and `RightX`, Y to `EyeGain.LeftY` and `RightY`. If the format does not match, the gains stay at 1 |
| `mode` | Values 0, 1 or 2, expanded into explicit post-processing rules and channel gains. Mode 0 enables no rules; modes 1 and 2 enable `ChannelGain` and several other rules. A missing `mode` is treated as 2, the default in the source at that commit; an invalid or duplicated `mode` leaves all post-processing rules disabled |

Fixed settings: `Source` is `Auto`.

`mode` is expanded once, at migration time, into ordinary configuration fields; the module has no hidden presets at runtime. The rules and coefficients used for the expansion follow this project's specification, have not been calibrated on a real headset, and are not equivalent to the earlier algorithms item by item. For the exact parameters of each mode, see the section on migrating earlier configurations in the [configuration reference](../../docs/configuration.md).

## Migration report

The migration report is a separate UTF-8 text file. The same content is also stored in `Migration.Differences` of the new configuration. `Migration` additionally records `SourceFile` (the absolute path of the earlier configuration), `SourceVersion` and `ImportedAtUtc`.

The report is currently written in Chinese and contains:

- the path of the earlier configuration, the source version label and the import time;
- general notes on the migration: it converts fields and behavior approximately, the coefficients have not been calibrated on a real headset, compatibility has not been tested, and module gains may compound with VRCFT's own parameter adjustments;
- each unknown key, duplicated key, invalid value and field that was not imported;
- earlier behavior that is not reproduced, such as differences in the old 0.99 limits, pupil estimation from thresholds, and the nonlinear curves, frame smoothing and dead zones of the pike modes that were not migrated;
- corrections made by the validation after migration;
- the earlier projects and pinned commits that were consulted.

The migration is an independent implementation based on the publicly visible configuration fields and observable behavior of the earlier projects. For sources and licensing, see the [third-party notices](../../THIRD_PARTY_NOTICES.md).

## File safety and privacy

- The tool never overwrites an existing file or directory and does not create parent directories.
- The migration report and the `Migration` field contain the absolute path of the earlier configuration, which may include a user name. IP addresses from the earlier configuration and the raw values of unknown fields may also appear verbatim. The tool does not redact anything.
- Before sharing a report or a migrated configuration, review both files and remove personal information. Once the configuration has been confirmed, `Migration` can also be set to `null` before use.

For more information, see [Privacy and network security](../../docs/privacy.md).

## Related documents

- [Configuration reference](../../docs/configuration.md)
- [Post-processing reference](../../docs/postprocessing.md)
- [Protocol reference](../../docs/protocol.md)
- [Development guide](../../docs/development.md)
- [Default configuration example](../../config/UnifiedPicoModule.example.json)

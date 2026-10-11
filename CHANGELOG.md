# Changelog

English | [简体中文](CHANGELOG_zh.md)

## [0.2.0] - 2026-10-11

### Added
- Enable per-eye OneEuro smoothing for native gaze by default, adapting strength to eyelid closure.
- Add `DualLightCutoffHz` and `DualBeta` for independent enhanced-mode light filtering and speed response. Logs show effective parameters and per-eye filter weights.

### Changed
- Default normal gaze to 100/4 Hz, Beta 1 and Blink 0.30/0.60; dual to 250/4 Hz, Beta 8 and Openness 0.80/0.45. Confirmation and eyelid smoothing are 350/150 ms.
- Fresh configurations and omitted fields use the defaults; explicit values are retained. Legacy-module imports continue to disable the added smoothing.

### Fixed
- Fix eyelid jumps caused by temporary Blink fallback when enhanced native openness is invalid. Hold that eye's last valid value instead.
- Select gaze sources by protocol and capability, retaining the last value during invalidity; align automatic source eligibility with this selection.
- Hold the last output across data and connection timeouts by default. Set the new `Stale.NeutralizeOnTimeout` option to `true` to restore timeout neutralization.
- Disable filtering when smoothing configuration validation fails.

## [0.1.2] - 2026-10-10

### Fixed
- Use EyeBlink for eyelid closure in normal mode, fixing partial closure being overridden by native Openness fixed at 1.

### Added
- Add `OpennessPolicy`, defaulting to `Auto`: normal mode uses Blink, while enhanced mode prefers native Openness with Blink fallback. Select `BlendshapeOnly` or `NativePreferred` for a fixed eyelid source policy; gaze remains configured by `EyePolicy`.
- Log the openness source and support independent eyelid policy in automatic source selection and configuration migration.

### Validation
- Module and ConfigTool Release builds pass with no warnings or errors; the default and example configurations pass validation.
- Headset revalidation is pending.

## [0.1.1] - 2026-10-03

### Fixed
- Capture immutable source snapshots before sampling the evaluation clock. A concurrently received frame can no longer appear to arrive in the future, briefly clear facial output, reset temporal postprocessing, or trigger an unnecessary source fallback.
- Use the same snapshots and timestamp for source selection, tracking evaluation and diagnostics.

### Validation
- Module and ConfigTool Release builds pass with no warnings or errors.
- The concurrency fix has not yet been revalidated on a headset or under network scheduling stress.

## [0.1.0] - 2026-10-01

### Added
- Initial independent VRCFaceTracking module targeting VRCFT 5.4.5 / .NET 10.
- BridgeSplit subscriptions, legacy daemon packets, and streaming formats on ports 29765 and 29763, with automatic source selection and optional fallback.
- Native-priority eye tracking, expression mapping, configurable postprocessing, and an offline configuration and migration tool.

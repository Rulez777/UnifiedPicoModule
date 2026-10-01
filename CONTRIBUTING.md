# Contributing

English | [简体中文](CONTRIBUTING_zh.md)

Thank you for your interest in this project. Before contributing, it is recommended to read the [development guide](docs/development.md), the [configuration reference](docs/configuration.md) and [Privacy and network security](docs/privacy.md).

## Reporting issues

When opening an issue, please include as much of the following as possible:

- what happens and the steps to reproduce it;
- the module version and the VRCFT version;
- the sender and its version, for example PicoFacialBridge 0.3.0 or PICO Connect 10.6.6;
- the headset model, for example PICO 4 Pro;
- the relevant log from VRCFT's Output page, including the status lines the module writes every 5 seconds;
- if the problem relates to configuration, `UnifiedPicoModule.json` or the output of the `validate` command.

## Before sharing logs and configurations

Logs, configuration files and migration reports may contain personal information. Remove the following before sharing:

- user names in local paths;
- IP addresses of the headset and the PC;
- the `Migration` field of the configuration, which records the absolute path of the earlier configuration file;
- raw tracking data.

Do not upload unfiltered full packet captures. If protocol evidence is needed, keep only the packet fields relevant to the issue.

## Code requirements

### Layers

The directories have the following responsibilities:

| Directory | Responsibility |
|---|---|
| `Core` | Input data, sessions and capabilities, module output model |
| `Protocol`, `Transport` | Wire format parsing, source validation, connection and reception |
| `Mapping` | Neutral mapping and eye data resolution |
| `Processing` | Post-processing rules and their timing state |
| `Configuration` | Configuration model, validation and migration |
| `Runtime` | Source selection, scheduling, timeouts and diagnostics |
| `Sdk` | VRCFT lifecycle and output |

Changes must observe the following constraints:

- Only the `Sdk` directory may reference VRCFaceTracking. The other layers do not depend on VRCFT, and the configuration tool compiles only the sources in `Core` and `Configuration`.
- Protocol parsing, source selection, eye policy and post-processing are independent of one another. Changing one of them should not alter the behavior of the others.
- The configuration file is the only basis for the module's behavior. Do not add hidden runtime presets in code, and do not rewrite user configuration automatically based on the data source.

### Code style

Keep consistent with the surrounding code, including naming, file organization and comment density. Use comments only to explain reasons or constraints that are not evident from the code itself.

### Configuration changes

When adding or changing a configuration field, its valid range or how it is calculated, update the following together:

- the model in `Configuration` and `ConfigValidator`;
- the migration logic in `LegacyImporter`, if affected;
- `config/UnifiedPicoModule.example.json`, re-exported with the `defaults` command of the configuration tool;
- the documentation in both languages.

If a change alters the meaning of an existing field, state in the commit message whether `ConfigVersion` needs to be increased.

## Third-party code

- Do not introduce code or assets with unclear licensing. SAFT (PicoStreamingAssistantFTUDP) and lonelyicer's VRCFTPicoModule have no clear license in the snapshots reviewed by this project; their implementations must not be copied, translated or rewritten.
- When drawing on another project's protocol material or behavior, implement it independently and record the source and version in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- When reusing third-party content with a clear license, record the source, exact version and file scope, and keep the applicable license and copyright notices.

## Commit messages

In the commit message, state the verification actually performed, such as builds, static checks or observations on a real headset, and list what remains unverified. A successful build does not mean device compatibility has been verified.

The following must be marked accurately in code comments or documentation:

- senders, versions or combinations that have not been verified on a real headset must be marked as unverified;
- coefficients that have not been calibrated on a real headset must be marked as uncalibrated and must not be called recommended values.

For the current verification status of each combination, see the [README](README.md#compatibility). If you have tested a combination that has not been verified yet, please open an issue with your log and results.

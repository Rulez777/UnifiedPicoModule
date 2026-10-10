# UnifiedPicoModule

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/branding/logo-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="assets/branding/logo-light.svg">
    <img src="assets/branding/module-icon.png" alt="UnifiedPicoModule" width="200" height="200">
  </picture>
</p>

English | [简体中文](README_zh.md)

UnifiedPicoModule is a PICO eye and face tracking module for VRCFaceTracking (VRCFT).

Tracking data from a PICO headset can be delivered by several senders, including PicoFacialBridge, PICO Connect, Streaming Assistant and the earlier PicoFacialDataDaemon. Each sender uses its own data format and port. This module supports all of these formats, detects which source is currently available, and forwards its data to VRCFT through a single output, so there is no need to install a separate module for each sender.

> The current release is a 0.1.2 preview, built against VRCFT 5.4.5 (net10.0). See [Compatibility](#compatibility) for which combinations have been verified and the [changelog](CHANGELOG.md) for release changes.

## Supported senders

| Sender | Port | Eye tracking | Face tracking |
|---|---|---|---|
| [PicoFacialBridge](https://github.com/WolalaQAQ/PicoFacialBridge) 0.3.0 or later | 9030 | Native gaze and eyelid data; enhanced mode supports per-eye gaze and pupil diameter | Supported |
| PICO Connect (legacy face tracking protocol) | 29765 | No native eye data; gaze is derived from eye expressions | Supported |
| Headerless streaming format (e.g. Streaming Assistant) | 29763 | No native eye data; gaze is derived from eye expressions | Supported |
| PicoFacialDataDaemon (536-byte format) | 9030 | Combined gaze | Supported |

PicoFacialBridge is the recommended sender: it provides native gaze and sends only the channels the PC has subscribed to. If you already stream with PICO Connect, the module can also receive face tracking directly on port 29765, with gaze derived from the `EyeLook` expressions.

## Installation

### Requirements

- Windows 10 or Windows 11
- VRCFaceTracking 5.4.5
- A working sender on the same local network as the PC
- A VRChat avatar that supports face tracking

### Steps

1. **Disable older PICO modules.** This includes PicoFacialDataModule, PicoStreamingAssistantFTUDP (SAFT) and VRCFTPicoModule (the lonelyicer and pike branches). These modules occupy ports 9030, 29765 and 29763, and turning off their tracking toggles does not always release the ports. Disable or uninstall them in the module list.
2. **Obtain the module package.** Download the module ZIP from the Releases page and verify it against the accompanying `.sha256` file. Alternatively, build the package yourself as described in [Building from source](#building-from-source).
3. **Install the module.** In VRCFT's Module Registry, choose Install Module from .zip (the label may differ slightly between versions) and select the package. After installation, exit VRCFT completely and start it again.
4. **Start the sender.** Start the sender on the headset and make sure the headset and the PC are on the same local network.

Once installed, Unified PICO Face Tracking appears in the module list. "Initialized" only means the module has loaded; it does not mean tracking data is being received. See [Connection status](#connection-status) for how to confirm the connection.

## Configuration

### Configuration file

The configuration file is named `UnifiedPicoModule.json` and is located in the same directory as the module DLL:

```
%APPDATA%\VRCFaceTracking\CustomLibs\8322e16d-c38b-42f0-8d35-68f27227b442\
```

If the file does not exist on first start, the module generates a default configuration. Note the following:

- Changes take effect only after VRCFT is restarted. Hot reloading is not supported.
- The file is standard JSON. Comments are not allowed and field names are case-sensitive. Unrecognized fields and invalid values are ignored and reported in the log; they do not prevent the rest of the configuration from loading.
- Before updating or reinstalling the module, back up the configuration file to another location. VRCFT may replace the entire module directory during installation.

### Common fields

| Field | Default | Description |
|---|---|---|
| `Bridge.IP` | `null` | IPv4 address of the headset. Use this when automatic discovery fails or when more than one headset is on the network. The address is shown in the PicoFacialBridge interface |
| `Source` | `Auto` | How the data source is selected: `Auto`, `Bridge` or `Streaming` |
| `DisableEyeTracking` / `DisableFaceTracking` | `false` | Disable eye or face output respectively. With Bridge, the headset also stops sending the corresponding channel |
| `EyePolicy` | `NativePreferred` | Gaze source; prefers native gaze by default |
| `OpennessPolicy` | `Auto` | Openness source: normal mode uses `1 - EyeBlink`; enhanced mode prefers native Openness, falling back to Blink when invalid |
| `EyeGain` | All `1` | Gain for the gaze X and Y axes and eye openness, set separately for each eye |
| `AllowFallback` | `false` | Whether to switch to the other source when the current one becomes unavailable |
| `Streaming.LocalOnly` | `true` | Accept streaming data only from the local machine. PICO Connect runs locally, so this normally does not need to be changed |

For the complete list of fields, valid ranges and defaults, see the [configuration reference](docs/configuration.md).

Bridge also sends Blink data when only eye tracking is enabled; face output can remain disabled.

### Source selection

When `Source` is `Auto`, the module listens on both the Bridge and streaming ports. For the first 1.5 seconds after startup it waits preferentially for Bridge data; after that, it adopts whichever source first delivers valid data. Once a source has been selected, it remains in use for the rest of the session, even if the other source starts sending data later. The module never combines data from both sources, for example eye tracking from one and face tracking from the other.

As a result, if PICO Connect starts sending data before Bridge, the module selects streaming. To use Bridge, start Bridge before VRCFT, or set `Source` to `Bridge` explicitly.

With `AllowFallback` enabled, the module switches entirely to the other source when the current one becomes unavailable.

## Connection status

The module writes a status line to VRCFT's Output page every 5 seconds. A normal connection through PicoFacialBridge looks like this:

```
Bridge/BridgeSplit: state=Connected ... selected=True E/morphology=90.0/24.0Hz ... enhance=True independent=True drops=0
Eye origin L/R=native-per-eye/native-per-eye; openness L/R=native-openness/native-openness; pupil L/R=measured/measured
```

The main fields are:

| Field | Meaning |
|---|---|
| `state` | `Connected`: connected and receiving data. `MissingFrames`: the peer is online but data has stopped, for example because the headset is asleep or the sender is paused. `Searching`: no peer has been found yet |
| `selected` | Whether this is the source currently in use |
| `E/morphology` | Receive rates of eye data and expression data |
| `Eye origin` | Where gaze comes from. `native-per-eye`: native per-eye gaze. `native-combined`: native combined gaze. `blendshape-derived`: derived from expressions. `neutral`: no usable data at the moment |
| `openness` | Where openness comes from. `blendshape-blink`: face-tracking Blink. `native-openness`: native openness. `neutral`: expired or no valid value |
| `pupil` | `measured` is a measured pupil diameter; `estimated` means the default of 3.5 mm is used |

With PICO Connect, the log shows the source as `Streaming/StreamingHeader16` and the gaze origin as `blendshape-derived`. This is expected.

When eyelid data briefly becomes invalid, the module holds the last valid openness. By default, the eyes return to open when the last valid eyelid sample is older than 250 ms. Expired gaze returns to center and expressions go to zero.

## Expression post-processing

Post-processing is an optional correction layer between data decoding and VRCFT output. The default configuration enables only one rule, `ChannelGain`, and uses it only to double the left and right EyeWide channels, compensating for PICO's low eye-widening values. All other channels are output unchanged.

Besides `ChannelGain`, 13 further rules are available and disabled by default. They mainly suppress cross-talk commonly seen in PICO data, such as a frown appearing when the mouth opens, the upper lip rising during a lip press, a smile lingering while the lower lip is rolled in, or the tongue value briefly dropping to zero.

| Rule | Purpose |
|---|---|
| `ChannelGain` | Adjusts output gain per channel (enabled by default) |
| `BrowInnerSeparation` | Reduces outer brow movement when the inner brow is raised |
| `BrowDownResponse` | Adjusts the response curve of brow lowering |
| `SquintResponse` | Adjusts the response curve of squinting |
| `FrownJawAttenuation` | Reduces frowning that accompanies jaw opening |
| `ShrugToFrown` | Uses mouth shrug (chin raise) to help produce a frown |
| `UpperUpPressGate` | Suppresses upper lip raising during a lip press |
| `PuckerFunnelAssist` | Helps form a funnel shape during a pucker |
| `SmileRollSuppression` | Suppresses smiling while the lower lip is rolled in |
| `StretchCompetition` | Suppresses smiling and upper lip raising during a horizontal mouth stretch |
| `Symmetry` | Makes selected mouth movements symmetric |
| `CheekAsymmetry` | Infers a one-sided cheek puff from sideways mouth movement |
| `TongueHold` | Holds the tongue output when its value briefly drops to zero |
| `TongueJawGate` | Suppresses tongue output when the mouth is not open wide enough |

The default coefficients of these rules have not been calibrated on a real headset. Each rule can weaken genuine compound expressions while suppressing false triggers, so enable them one at a time and compare the results. If expressions are simply too weak overall, adjust VRCFT's own parameters first.

To enable a rule, change only its `Enabled` field and leave the other fields as they are. For example:

```json
"TongueHold": { "Enabled": true, "HoldMs": 120, "DropThreshold": 0.15 }
```

Rules run in a fixed order that cannot be changed. For the formulas, parameter ranges and caveats when combining rules, see the [post-processing reference](docs/postprocessing.md).

## Migrating configuration

The configuration tool `UnifiedPicoConfigTool` (built from source; not included in the module package) can read the following earlier configurations: `PicoFacialDataModule.json` from PicoFacialDataModule, `PicoModuleConfig.json` from SAFT, and `config.ini` from the pike branch. From these it generates a new configuration and a difference report.

The tool does not apply the generated configuration. Review the differences and replace the file manually. The migrated result approximates the earlier behavior; it does not reproduce the earlier algorithms exactly. See the [configuration tool guide](tools/ConfigTool/README.md) for usage.

## Troubleshooting

**The module is initialized, but the avatar does not respond**

Check the `state` field in the log first. If it stays at `Searching`, common causes include:

- an older module is still occupying the port;
- Windows Firewall is blocking VRCFT;
- the PC and the headset are on different subnets.

Automatic discovery skips VPN and virtual network adapters. If the PC has such adapters, or if discovery fails, specify the headset address in `Bridge.IP`.

**Using Bridge, but the log shows Streaming as selected**

Streaming data arrived before Bridge data. See [Source selection](#source-selection).

**Configuration changes have no effect**

Exit VRCFT completely and start it again, then check the log for messages about unknown fields or invalid values.

**Expressions work, but gaze does not**

Make sure `DisableEyeTracking` is not enabled. With PICO Connect, gaze depends on the `EyeLook` expression data provided by the sender, so its quality depends on the sender.

**The tongue output occasionally flickers**

If the tongue value briefly drops to zero, enable `TongueHold`. If the tongue appears before the mouth is noticeably open, use `TongueJawGate` instead. The two rules address different problems.

## Compatibility

As of 2026-10-02, the following combinations have been verified on a PICO 4 Pro:

| Sender | Verified |
|---|---|
| PicoFacialBridge 0.3.0 (BridgeSplit, enhanced mode) | Channel subscription, all four combinations of the eye and face toggles, return to neutral when data stops, automatic recovery after an application restart, and avatar expression tracking |
| PICO Connect 10.6.6 (`faceTrackingTransferProtocol=2`, port 29765) | Face data at about 72 Hz with no packet loss; avatar tracking works as expected |

If you use another sender or version, please open an issue with your log and results.

## Network security and privacy

The module only receives UDP data on the local network. It does not access the internet, does not upload any data and does not write tracking data to disk.

UDP has no encryption or authentication. The module checks source addresses and packet lengths, which reduces accidental reception but cannot defend against deliberately forged data. Use the module only on trusted networks, and do not expose ports 9030, 29765 or 29763 to the internet. See [Privacy and network security](docs/privacy.md) for details.

## Building from source

Building requires Windows, PowerShell, the .NET 10 SDK and Git. The module is compiled against a pinned version of the VRCFaceTracking source:

```powershell
git clone https://github.com/benaclejames/VRCFaceTracking.git .sdk/source/VRCFaceTracking
git -C .sdk/source/VRCFaceTracking switch --detach 2cafd0e387386b54997797ee4709cad4befe9274

./tools/build.ps1 -Configuration Release
./tools/package.ps1
```

The build script verifies the SDK commit and the digests of key files. The module package is written to `artifacts/` together with a `.sha256` checksum file. The package does not contain VRCFaceTracking DLLs; these runtime dependencies are provided by the host. To use an existing SDK source directory, or for the release process, see the [development guide](docs/development.md).

## Documentation

- [Configuration reference](docs/configuration.md)
- [Post-processing reference](docs/postprocessing.md)
- [Protocol reference](docs/protocol.md)
- [Privacy and network security](docs/privacy.md)
- [Development guide](docs/development.md)
- [Configuration tool guide](tools/ConfigTool/README.md)
- [Contributing](CONTRIBUTING.md)

## Acknowledgements

This module is an independent implementation based on the protocol material and behavioral observations published by the following projects:

- thoricelli: [PicoFacialDataModule](https://github.com/thoricelli/PicoFacialDataModule), PicoFacialDataDaemon, [PICO-documentation](https://github.com/thoricelli/PICO-documentation)
- WolalaQAQ: [PicoFacialBridge](https://github.com/WolalaQAQ/PicoFacialBridge)
- regzo2: [PicoStreamingAssistantFTUDP](https://github.com/regzo2/PicoStreamingAssistantFTUDP)
- lonelyicer: [VRCFTPicoModule](https://github.com/lonelyicer/VRCFTPicoModule)
- pikepikeid: [VRCFTPicoModule](https://github.com/pikepikeid/VRCFTPicoModule), the design reference for most post-processing rules
- benaclejames and the [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking) contributors
- crx: [Pico Wiki](https://pico.crx.moe/)

These acknowledgements identify reference sources only; this project does not copy code from them. For the exact versions and license boundaries of each source, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

The original code and documentation of this project are licensed under [MIT](LICENSE). The VRCFaceTracking SDK / Core referenced by this project remain under Apache-2.0, and this project does not distribute their DLLs. Names such as PICO and VRCFaceTracking are used only to identify the software this module interoperates with and do not imply official endorsement or sponsorship.

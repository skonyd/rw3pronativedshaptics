# Research and reference log (2026-11-05)

## 2026-11-08 multiband correction

[Official `.haps` melodies](https://doc.wyvrn.com/docs/interhaptics-sdk/haptic-composer/haps-file-format/) can express simultaneous patterns subject to device capability; [public AddHM/UpdateHM](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-sdk-for-game-engines/engine-control/) is a documented route distinct from the earlier parametric API experiment. Local separate-melody tests yield repeatable extra Wolverine provider payload regions and are implemented as an opt-in prototype. This corrects the blanket one-band claim, without claiming a decoded/physically measured multiband waveform. [Evidence](MULTIBAND_RESULTS_20261108.md).

The official [RazerSensa_DevKit README](https://github.com/WyvrnOfficial/RazerSensa_DevKit/blob/master/README.md#how-it-works) explicitly describes approximately33ms/30fps output quantization in its **WYVRN event-service integration** context. It supports the chosen cadence and agrees with historical backlog tests, but does not prove an immutable controller-hardware limit for all direct-provider versions/routes. Distinguish software scheduling, service buffering and physical latency.

Evidence tags: **VERIFIED** = official source or teardown; **THIRD-PARTY** = author claim; **HYPOTHESIS** = not proven; **TO RECHECK** = update risk.

| Statement | State | Source/qualifier |
|---|---|---|
| Wolverine V3 Pro officially supports Razer Sensa HD Haptics in PC mode | VERIFIED | [Razer](https://www.razer.com/console-controllers/razer-wolverine-v3-pro); [WYVRN DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit) |
| PC mode requires controller key combo and Synapse/Chroma components | VERIFIED | [WYVRN DevKit README](https://github.com/WyvrnOfficial/RazerSensa_DevKit) |
| Wolverine V3 Pro contains two body voice-coil actuator units | VERIFIED teardown description | [iFixit repair guide](https://www.ifixit.com/Guide/Razer+Wolverine+V3+Pro+Voice-Coil+Actuators+Replacement/214388) |
| DualSense body contains two advanced haptic actuators | VERIFIED | [iFixit DualSense teardown](https://www.ifixit.com/Teardown/PlayStation+5+DualSense+Controller+Teardown/138280) |
| Razer body actuators are physically same quality, bandwidth or driver interface as Sony's | **UNVERIFIED** | No standardized force/bandwidth/PCM transfer comparison available |
| Four VCMs on Flydigi APEX 6 Pro means four body motors | **NOT ASSUMED** | Two appear at triggers; model architecture and teardown require re-verification |
| Razer/WYVRN SDK exposes lossless arbitrary streaming stereo DualSense PCM on Wolverine | **UNVERIFIED / KEY BLOCKER** | [WYVRN DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit) explicitly describes **game event names** → `.haps` effects via Haptic Service, not raw streaming |
| Interhaptics effect SDK can target left/right / advanced & transient effects | VERIFIED for API documentation; applicability to Wolverine is **UNPROVEN** | [Interhaptics API](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-unity-sdk/api-usage/); [maintenance note](https://doc.wyvrn.com/docs/interhaptics-sdk/) |
| DSX+ offers usable raw dual-channel PCM capture/output API for arbitrary external controller | **UNVERIFIED** | [DSX](https://store.steampowered.com/app/1812620/DualSenseX/) — functionality depends on release, proprietary integration; never assume |
| PadForge includes gamepad/virtual DualSense and experimental Razer integration | THIRD-PARTY; inspect source, branch, license before use | [PadForge](https://github.com/hifihedgehog/PadForge) |
| ApexSenseBridge transfers DualSense haptic signal to Flydigi APEX 6 Pro | THIRD-PARTY, not a Wolverine backend | [ApexSenseBridge](https://github.com/ReynArts/ApexSenseBridge) |
| Razer Sensa Audio-to-Haptics differs from authored per-game haptics | VERIFIED by its signal origin | [WYVRN](https://doc.wyvrn.com/); [Razer](https://www.razer.com/console-controllers/razer-wolverine-v3-pro) |
| Windows WPF is still supported and appropriate for tray desktop apps | VERIFIED | [Microsoft Windows developer FAQ](https://learn.microsoft.com/en-us/windows/apps/get-started/windows-developer-faq) |
| Possible motor bench: two generic LRA + DRV2605L + ESP32 | HYPOTHESIS, NOT equivalent to DualSense voice-coil PCM | [Texas Instruments DRV2605L](https://www.ti.com/product/DRV2605L) |

## Follow-up research order
1. Inspect WYVRN Sensa DevKit and current Windows SDK capabilities; find or disprove **continuous stereo waveform** real-time device access. Check whether event-to-.haps limitations can be overcome lawfully.
2. Verify Wolverine left/right independent routing on actual device, and establish reliable conservative impulse & stop.
3. Inspect PadForge's exact implementation and licensing, note mono/envelope conversion and untested behavior.
4. Inspect DSX+/virtual DualSense haptic endpoint interception alternatives (no user-mode raw stream assumed).
5. Compare end-to-end original DualSense with test WAV and actual user game; measure channel separation, latency, frequency response.
6. Only if native sink API cannot be obtained evaluate an independent dual-actuator bench; this **does not** unlock the closed Wolverine driver.

## Research caution
Previous chat discussed “two VCM” based on teardown and speculated that a high-fidelity mapping is feasible. Motor count **alone** cannot establish PCM acceptance or subjective quality. No APEX “8–9/10”, Wolverine “7–8/10” or firmware latency score is an experimentally established metric. Do not turn these into facts.

## Follow-up research (2026-11-06) — decision record

See [ADR-002](ADR-002-NATIVE-BRIDGE-ROUTE.md) for date-qualified alternatives, caveats and physical gates. In particular:
- [PadForge 5.2](https://padforge.org/) advertises virtual DualSense Full with controller audio; it also translates generic rumble to mono Interhaptics Razer Sensa. These are **distinct paths**, not proof of a built-in stereo DualSense→Wolverine bridge.
- [DSX+ 3.2 beta notes](https://steamcommunity.com/app/1812620/announcements/) indicate virtual DualSense audio/haptics support, while the Steam DLC store description may still say upcoming. Wolverine as input is not established.
- [Official Wwise Sensa HD plugin](https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/) documents 2-channel WAV-to-Motion rendering, but runtime arbitrary PCM, output waveform identity and commercial distribution require separate proof/license.

## Local evidence update — 2026-11-07

[Pipeline analysis](SENSA_PIPELINE_ANALYSIS.md) and [hardware log](HARDWARE_TEST_LOG.md) supersede earlier untested Hand L/R statements under the recorded Windows/Synapse/SDK conditions. Hand L/R pulses were user-felt; v2 uses a target 5ms one-band amplitude/frequency/transient mapping. This is lossy, not original waveform output. Real-game provenance and calibrated physical fidelity remain unverified. DualSense reference now fails explicitly if Exclusive is unsupported; no conversion/Shared fallback. No new external vendor API claim is introduced by this correction.

## Channel conversion lab increment — 2026-11-07

Test card **DualSense → W3 Pro** adds seven channel/negative-control scenes plus the 23 demos, one canonical unmodified 4ch PCM source, the shared production decoder boundary, strict separate DualSense Exclusive reference and optional microphone level metering. Original-source SHA256 and software saturation/channel statistics are separate from acoustic observations. This is synthetic replay, not USB/WASAPI source capture or game-authored evidence; M3/M4 remain open. See [channel lab contract and procedure](CHANNEL_LAB.md).

## Synapse independence review — 2026-11-08

Re-read the current [official DevKit setup](https://github.com/WyvrnOfficial/RazerSensa_DevKit/blob/master/README.md), [official haptic configuration](https://doc.wyvrn.com/docs/wyvrn-sdk/wyvrn-configuration/haptics/), and PadForge's own [Sensa implementation](https://github.com/hifihedgehog/PadForge/blob/main/PadForge.App/Services/SensaHapticsService.cs) / [internals](https://padforge.org/docs/reference/sensa-haptics-internals/). These support an installed vendor-runtime path, not a standalone Wolverine waveform sink. The official DevKit's approximately33ms output quantization supersedes the early inference that generating more SDK frames proves finer actuator cadence.

The author-maintained [razer_ui static Sensa analysis](https://github.com/han1548772930/razer_ui/blob/main/docs/products/host/sensa.md) supplies third-party reverse-engineering evidence for IPC/proxy/host/device boundaries. Its direct branch and successful callbacks are not physical delivery or a completed independent backend. Its current Rust status explicitly lacks the independent Sensa production chain. Do not transfer addresses/contracts to the locally installed versions without verification. [Resulting staged research plan](SYNAPSE_INDEPENDENCE.md).

An adjacent Kishi project was checked at its own [README](https://github.com/XipleETH/RetroRazer-/): that current README describes a Nexus audio-haptics dependency on different hardware. Public claims about another controller's direct USB path are not Wolverine evidence. No binaries or modified games were downloaded/run.

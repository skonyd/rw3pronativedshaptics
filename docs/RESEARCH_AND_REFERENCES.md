# Research and reference log (2026-11-05)

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

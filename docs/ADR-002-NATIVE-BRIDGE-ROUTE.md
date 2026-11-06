# ADR-002 — Native DualSense → Wolverine route choice (2026-11-06)

Status: **Adopt staged, vendor-supported feasibility gates. No claim of lossless native replay.**  
Primary user goal: reproduce **game-authored DualSense** stereo haptics on Wolverine V3 Pro body actuators, NOT audio-to-rumble and NOT adaptive triggers.

## New evidence, ordered by strength

| Fact | Evidence and caveat |
| --- | --- |
| Games can write haptic PCM to a composite virtual DualSense 4-channel audio endpoint | [PadForge composite DualSense issue #255](https://github.com/hifihedgehog/PadForge/issues/255), [PadForge FAQ](https://padforge.org/docs/faq/). Developer implementation claims; validate exact version and actual game, and never infer from an ordinary 4ch endpoint alone |
| PadForge 5.2 exposes controller mapping and a native Sensa path | [PadForge](https://padforge.org/) and [Sensa internal implementation](https://padforge.org/docs/reference/sensa-haptics-internals/). Its **current Sensa mapping is ONE looping mono effect** driven by generic rumble; not independent left/right original DualSense PCM |
| DSX+ 3.2 beta added Virtual DualSense with Audio for game-authored haptics | [DSX Steam announcements](https://steamcommunity.com/app/1812620/announcements/). Its proprietary virtual device targets its supported physical controllers. Wolverine physical-input interoperability is **not established**. Steam store text may lag beta features |
| An existing physical DualSense via USB exposes four-channel UAC | [PadForge issue #255](https://github.com/hifihedgehog/PadForge/issues/255), [DS4Windows docs](https://github.com/hbashton/DS4Windows/blob/main/docs/dualsense-bluetooth-audio-haptics.md). This is only a helpful control/source comparison unless the game actually sends its haptics to that endpoint |
| Razer WYVRN native game-facing integration uses event names and hosted .haps clips | [Official Razer Sensa DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit), [WYVRN docs](https://doc.wyvrn.com/docs/wyvrn-sdk/wyvrn-configuration/haptics/). It does **not** prove general low-latency arbitrary incoming Sony PCM |
| Razer Interhaptics exposes parametric amplitude, pitch and transient APIs | [Official API](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-unity-sdk/api-usage/) and [PadForge internals](https://padforge.org/docs/reference/sensa-haptics-internals/). Our current 50Hz amplitude-only prototype is lossy and physical left/right mapping has not been verified |
| The official Razer Sensa Wwise plugin allows a stereo WAV on a Motion Factory Bus with Generic Haptics 2-Channel drivers A/B | [Official WYVRN Wwise plugin guide](https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/). **This is an authored WAV playback workflow, NOT proof of a supported real-time PCM streaming API or waveform bit-identity**. Wwise Motion license required for generating SoundBanks/distribution; authoring evaluation can be free |
| Official Razer integrated Sensa support and Dynamic Haptics are already available in supported games | [Razer's March 2026 announcement](https://www.razer.com/newsroom/product-news/razer-dynamic-haptics). These are Razer-authored effects, **not** original DualSense game effects |

## Decision: input and output must evolve independently

1. **Native input discovery:** First test an existing physical USB DualSense endpoint when possible, then prefer an *already installed* PadForge composite DualSense Full UAC endpoint if Wolverine buttons/sticks can be mapped without double input. DSX+ is optional and not assumed capable of Wolverine-as-input. The app will **never silently install USB/IP, HIDMaestro, DSX, HidHide or any kernel driver**. If a driver is elected manually, require backup/restore point and owner-reviewed rollback.
2. **Input provenance:** The app validates exact 4ch/48k PCM format. It separately measures front-speaker RMS and rear haptic left/right RMS/peak; nonzero front channels or a vendor-like endpoint name cannot establish actual game authorship. Record game/scene, app source assignment, no fixture active, and L/R changes. Captured PCM is not logged.
3. **Output fast feasibility gate:** Keep existing vendor-supported Interhaptics 2-effect parametric prototype behind explicit experiment consent. Physical independent left/right motion MUST pass; provider success is not device proof. Try official Razer native game/demo first as an equipment baseline.
4. **High-fidelity candidate A:** Evaluate official Wwise 2025.1.6+ Sensa Motion sink **offline** using our reproducible stereo WAV fixture for left/right separation and perceptual comparison. Do not bundle or redistribute Wwise/Interhaptics binaries or create proprietary-compatible loaders. Without documented runtime stream interface and acceptable license this stays an independent research track.
5. **High-fidelity candidate B:** With explicit API and vendor permission, evaluate Interhaptics multiple frequency-band/pitch/transient events from native PCM. Measure latency, CPU, directionality and transient fidelity against the original DualSense, rather than calling it bit-perfect.
6. **Fallback (only if user explicitly elects):** Native Razer integrated games, first-party Synapse A2H, or ordinary XInput. Always label these as **different content**, never native DualSense success.

## What we will not do
- No game injection, HID hijacking, unsigned/undocumented controller firmware writes, Windows driver installation, or reverse-engineered vendor protocol as a default workflow.
- No claim of perfect hardware parity: Sony/Razer actuator bandwidth and response may differ despite both using haptic coils.
- No proprietary or CC BY-NC-SA third-party source copying into this repo without a completed license review.
- No Wwise licensing assumptions or silent Wwise runtime bundling.

## Hardware gate/acceptance order
- Baseline: Sensa demo/native integrated Razer game works on actual Wolverine in PC mode.
- Source: 4ch/48k format is visible and speaker-only content does not register as haptics; synthetic rear L/R fixture isolates channels.
- Provider: each left/right Interhaptics pulse moves the matching physical body actuator only, with reset/stop/reconnect test.
- Transport: a supported game generates real rear-channel PCM with no fixture or system-audio leakage and no input duplication.
- Quality: A/B game scene vs original DualSense, measured end-to-end latency, frequency response, perceived texture and left/right separation. No perfect-fidelity release claim unless independently demonstrated.

## Release criterion
CI green and hardware-free suite passing = **software ready for physical R&D**. Real end-to-end gameplay quality and streaming remain **hardware-blocked** until recorded evidence meets all gates above.

## Decision comparison and exit criteria (avoid false guarantees)

| Route | Authored Sony signal? | Razer native actuator output? | Deployment friction | Decision |
| --- | --- | --- | --- | --- |
| Razer Sensa integrated game / Dynamic Haptics | **No** (different game integration) | Official supported Razer path | Low: Synapse 4, Chroma, PC mode | **Reliable baseline and useful everyday option** |
| PadForge virtual DS Full -> RW3 4ch WASAPI -> Interhaptics | **Possible** where game writes genuine UAC rear channels | **Experimental**, currently lossy two-effect L/R mapping | Medium-high: opt-in signed virtual device stack, input mapping, possible double-input | **Preferred native-source pilot** |
| DSX+ 3.2 beta virtual DS -> RW3 UAC -> Interhaptics | **Possible** in supported DSX configurations | **Experimental**, same output blocker | Medium: paid/proprietary DLC; Wolverine input unverified | Alternative pilot, no default dependency |
| Official Wwise Sensa stereo WAV authored motion bus | Offline *synthetic/recorded* waveform, **not live game provenance** | Official 2-driver playback workflow, physical routing TBD | High: Wwise authoring/runtime licensing; manual project setup | **High-fidelity output feasibility spike only** |
| Current plain XInput Audio-to-Haptics | **No**, general system sound | Conventional XInput rumble | Low | Explicit fallback, not native |

**Key physical limit:** 48 kHz is the PCM *transport/sample rate*, not proof the actuators can reproduce every frequency in an audio waveform. The official Wwise guide itself recommends focusing on low-frequency (e.g. sub-200 Hz) content. Frame accuracy, perceived texture and physical motor bandwidth need measurements.

**Go/no-go:** If true left/right Sensa body actuator addressing fails on actual Wolverine, suspend the native bridge and do not spend effort on new virtual-driver code. If side routing passes but 50 Hz envelopes feel poor, prioritize the Wwise/offline or supported pitch/transient pilot before investing in broad game compatibility. If the proprietary API still cannot support sufficiently faithful low-latency output, retain the app as an honest experimental tool and use first-party Sensa for supported games; never promise DualSense-equivalent quality.

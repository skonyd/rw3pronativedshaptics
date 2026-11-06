# Implementation status & acceptance gates

**Snapshot:** 2026-11-06 (Europe/Istanbul) · **main** `ad2aed3b7437dba4c01e7ab712f9be6cbf1b9cc9` · **main Windows CI:** [run 42214409530](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530), **success**.

## Executive answer: where are we?

**Current stage: M3 — physical source/output integration and validation (OPEN / BLOCKED).** The preceding Windows application, offline PCM pipeline, and hardware-free test infrastructure are implemented. The primary product objective—live **authentic, game-authored DualSense stereo HD-haptic PCM** reproduced on the two **physical Wolverine V3 Pro Sensa actuators** with convincing fidelity—is **not complete**. Do not assign an overall completion percentage based on code/test counts: the unproven hardware and vendor-output contract is the critical dependency.

This project has two separate kinds of progress:

1. **Engineering/software readiness (well advanced):** the Windows WPF application, explicit modes, nine route profiles, offline and passive 4ch/48kHz PCM processing, synthetic A/B lab, status/evidence rules, installation packages and automated contract tests work at the code/Windows CI level.
2. **End-to-end product validation (not established):** actual supported game source, gamepad binding to a virtual DualSense, correct physical Razer left/right outputs, **continuous high-resolution waveform output** and measured latency/spectral/perceptual fidelity require vendor-supported hardware access and physical tests.

## Stage-gated plan (not a fabricated percent-complete estimate)

| Stage | Deliverable | Evidence | Status |
| --- | --- | --- | --- |
| **M0: Scope and research** | Separate original DualSense PCM from conventional rumble/system audio; document source options, licensing and blockers | Docs / ADR-002 | **Complete as baseline** (research continues) |
| **M1: Windows shell and safe conventional paths** | WPF/tray/settings, ordinary XInput and opt-in sound-to-rumble, package/update infrastructure | Code + successful Windows CI/installer smoke | **Implemented / CI-passing**; physical Windows performance still unmeasured |
| **M2: Controlled native-source and A/B test infrastructure** | Strict 4ch/48k UAC source-only capture, rear L/R telemetry, PCM16/Float32 parsing, synthetic DualSense/Wolverine scenes, explicit motor confirmation, 10-scene WAV export, opt-in 10s RAM capture/save/discard | Code + hardware-free contracts + Windows CI | **Implemented / CI-passing**; real controllers still unverified |
| **M3: Authentic game and physical actuator gates** | Confirm actual game's stereo DualSense PCM and Wolverine input mapping; verify genuinely independent, safe L/R Razer Sensa physical output on hardware; capture baseline latency and frame times | Reproducible recorded test protocol and real hardware observations | **Current stage: open / blocked by hardware and licensed runtime access** |
| **M4: High-fidelity live bridge and release acceptance** | Obtain and implement legal/supported real-time stereo Sensa waveform sink (not 50Hz envelopes); source-to-actuator A/B and performance acceptance; reliable supported release | Live game waveform provenance + hardware measurements + licensing | **NOT IMPLEMENTED / CRITICAL BLOCKER** |

M2 "complete" means the tooling is implemented and software-tested, **not that Wolverine HD haptics already matches DualSense**. M3 and M4 cannot be marked complete by a green CI, a rendered event, a WAV file or user-perceived vibration alone.

## Current implementation inventory

| Workstream | What exists today | Still missing / caveat | Main reference |
| --- | --- | --- | --- |
| Windows desktop | .NET 10 WPF, system tray, persistent mode, start-up option, release updater | Fresh user Windows 11 and in-game performance checks | [WINDOWS_APP](WINDOWS_APP.md) |
| Nine user-selectable methods | Normal XInput, RW3 audio-to-XInput; native UAC physical/PadForge/DSX+/manual; external official Synapse/Sensa; research-only Wwise | External methods are **not** internally implemented; PadForge is only a promising source candidate | [ROUTE_SELECTION](ROUTE_SELECTION.md) |
| Native capture diagnostics | Loopback from user-confirmed 4ch/48k PCM16 or Float32 render endpoint; energy, peaks, stale/no-signal states; Razer-free source-only mode | Endpoint name and nonzero rear PCM **do not prove game-authored provenance** | [Issue #2](https://github.com/skonyd/rw3pronativedshaptics/issues/2) |
| Existing native Razer sink | Two Interhaptics parametric Palm Left/Right effects refreshed at **20ms / 50Hz** via licensed user-supplied HAR and Razer provider | **LOSSY** envelope, no physical side mapping or original 48k waveform replay proven | [Issue #1](https://github.com/skonyd/rw3pronativedshaptics/issues/1), [Issue #4](https://github.com/skonyd/rw3pronativedshaptics/issues/4) |
| Behavioral lab | Ten original scenarios (rain, sand/gravel/grass footsteps, pistol/rifle, idle/acceleration, explosion, thunder), each separately or both controllers; physical response ratings | Synthetic scenarios **are not** first-party Sony game-authored data; Razer path remains lossy; physical comparison awaits hardware | [A/B lab](AB_BEHAVIORAL_TEST_LAB.md) |
| Original USB DualSense reference | Sends original *generated fixture bytes* to physical USB DualSense 4ch/48k WASAPI path with **no RW3 DSP/gain**; Exclusive preflight or explicitly selected Shared | Windows hardware/driver transformations and physical response unmeasured; no proof of genuine authored game effect | [A/B lab](AB_BEHAVIORAL_TEST_LAB.md) |
| Presence/confirmation | Windows present USB PnP ✓/✕/unknown for Sony and Razer; Wolverine wired checkbox; controller feel/no feel/unsure; local rated report | USB Razer receiver can mimic direct connection, so green ✓ is **not proof of wired controller or working actuators** | [A/B lab](AB_BEHAVIORAL_TEST_LAB.md) |
| Stereo reference/capture | Bit-preserving 4ch rear 3/4 to stereo WAV PCM16/Float32, ten fixture exports; optional consented **10-second** source-only RAM capture and local save/discard | Offline WAV is **not** a real-time Razer HD haptic backend; capture is not origin-authenticated | [WAV/Wwise](STEREO_WAV_CAPTURE_AND_WWISE.md) |
| Output intensity | Persisted 0–100% RW3 output gain plus per-Wolverine A/B test level | Does **not** change the physical DualSense reference, game's native XInput or Synapse effects | [A/B lab](AB_BEHAVIORAL_TEST_LAB.md) |
| Build/package/security | Contract suite, self-test, WPF build, portable ZIP, per-user Inno Setup install/uninstall and SHA256 manifests | CI does not contain either physical controller or licensed Wwise/Razer runtime; no certificate-backed code signing claim | [Windows CI](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530) |

## Top-priority remaining work, in dependency order

**P0 — physical Razer output and original-source feasibility (M3).**

1. **[Issue #1](https://github.com/skonyd/rw3pronativedshaptics/issues/1)**: with Wolverine V3 Pro plugged into Windows 11 and legally obtained licensed HAR/Razer provider binaries + Synapse/Chroma, test separate Left/Right Sensa pulses, physical localization, safe STOP/unplug, pulse duration and channel separation. Log firmware, app/runtime versions and video/sensor observations; API init/ticks are not sufficient.
2. **[Issue #2](https://github.com/skonyd/rw3pronativedshaptics/issues/2)** + **[Issue #5](https://github.com/skonyd/rw3pronativedshaptics/issues/5)**: run a DualSense-haptics-capable real PC game; confirm actual 4ch rear-channel PCM comes from the game and that buttons/sticks can bind a **user-installed** virtual DualSense + UAC endpoint (PadForge Full, DSX+ only if compatible). Avoid dual-input and do not automatically install any drivers.
3. **[Issue #3](https://github.com/skonyd/rw3pronativedshaptics/issues/3)**: perform physical dual-controller 10-scene A/B and error/unplug safety, plus average FPS/1% low/p95–p99 frame-time baseline during actual game sessions; normal audio rumble is not HD parity.

**P0 — high-fidelity waveform destination (M4, critical path).**

4. **[Issue #4](https://github.com/skonyd/rw3pronativedshaptics/issues/4)**: evaluate official Razer/WYVRN Sensa streaming contracts. Licensed Wwise Motion/Sensa plugin can import 2ch haptic WAV for an **offline** comparison, but WAV-to-Motion is not demonstrated live audio-stream compatibility. Wwise Audio Input Source → Motion → Razer Sensa is an **unproven hypothesis**; establish both software compatibility and physical stereo actuator output *before* implementing a live adapter.
5. If—and only if—the documented legal hardware contract exists and passes physical proof, implement a bounded low-latency stereo PCM pipeline, independent L/R safety limiter where appropriate on Wolverine only, measurable buffers/latency and robust source/endpoint recovery. **Do not replace a lossy method with an equally lossy route and call it native HD.**

**P1 — production hardening after the P0 feasibility gates:** real Win11 device/reconnect/cleanup, licensed runtime compatibility matrix, game profiles, installer/release confidence, performance and code-signing decision.

## What would count as finished?

All of the following must be evidenced, **not simply planned**:

- [ ] A supported, reproducible PC game emits authentic authored DualSense rear L/R PCM through a known endpoint, with suitable source proof and without double input.
- [ ] Independently identifiable **real Wolverine V3 Pro left and right body actuators** render the requested output and stop safely.
- [ ] A vendor-supported/legal **live high-fidelity stereo waveform** route preserves materially more of the original frequency/transient/detail than today's lossy 50Hz envelope; measured output, latency and failure behavior meet documented acceptance thresholds.
- [ ] Side-by-side physical original DualSense/Wolverine tests (including blind/repeated texture/impact/stereo scenes) demonstrate useful fidelity; any limitations are disclosed rather than silently masked.
- [ ] Real Windows 11 and game-performance/driver/reconnect tests pass; packaging and licensing remain compliant.

## Source of truth and update rules

- Source `main`: `ad2aed3b` as of snapshot; PRs [#6](https://github.com/skonyd/rw3pronativedshaptics/pull/6)–[#12](https://github.com/skonyd/rw3pronativedshaptics/pull/12) covered the engineering increments. **Do not assume a subsequent PR is merged or CI-green until checked.**
- Latest snapshot CI: [run 42214409530](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530) succeeded for `ad2aed3b`. The portable/installer and contract results are **hardware-free**.
- For active work, use [ROADMAP](ROADMAP.md) for work order and [VALIDATION_PLAN](VALIDATION_PLAN.md) for explicit acceptance. The five numbered [issues](https://github.com/skonyd/rw3pronativedshaptics/issues) track remaining M3/M4 dependencies.
- Do not report an overall completion percentage without documented milestone weights and independent physical evidence. Keep "software test passing" distinct from "hardware verified" and "shipped product".

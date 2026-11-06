# Roadmap — reconciled with main on 2026-11-06

**Current stage: M3 (authentic-source / physical Sensa verification), OPEN.** The full product is **not finished** because M4 (live high-fidelity stereo DualSense → Wolverine) remains unimplemented. See [canonical implementation status](IMPLEMENTATION_STATUS.md) for the evidence map and success criteria.

Status vocabulary: **[x] implemented + software/CI tested**, **[ ] external or physical verification pending**, **BLOCKED** vendor/hardware/license proof not available. This distinguishes code presence from real two-controller hardware success.

## M0 — requirements and technical design: baseline complete
- [x] Three modes and the distinction between game-authored DualSense PCM, official Razer-native effects, system-audio rumble, and ordinary XInput recorded in [PROJECT_BRIEF](PROJECT_BRIEF.md).
- [x] Razer/WYVRN, PadForge/DSX+, Wwise and independent source/output gates documented in [ADR-002](ADR-002-NATIVE-BRIDGE-ROUTE.md).
- [x] Device/API licensing limits and "no silent driver/SDK install" rules recorded.

## M1 — Windows 11 client, conventional modes and packaging: implemented
- [x] .NET10 WPF + tray, settings, optional start-up, releases/update flow and 0–100% RW3-generated vibration level.
- [x] Normal XInput mode and opt-in WASAPI system audio → conventional XInput rumble; independent silence/stale-frame stop and conservative CPU guard.
- [x] Persistent method selector for 9 routes; external-only and research-only routes are informational, never auto-started as a working native backend.
- [x] .NET10 Windows CI, contract tests, published standalone EXE self-test, portable ZIP and per-user Inno Setup install/uninstall smoke; SHA256 manifests.
- [x] Current main CI [42214409530](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530) successful on `ad2aed3b`.
- [ ] User-side Windows 11 tray/install/updates and actual gameplay/performance acceptance — **not covered by CI**.

## M2 — experimental native-source / stereo behavioral lab: implemented in code
- [x] Strict 4ch/48k UAC format preflight; PCM16/Float32 rear-channel L/R extraction and front/silent/stale diagnostics, source-only mode without Razer libraries.
- [x] Experimental HAR/Interhaptics Left/Right parametric envelope output, 20ms/50Hz, source/provider disconnect watchdog and conservative cleanup; **NOT** true high-fidelity PCM output.
- [x] Ten project-authored reproducible 4ch PCM haptic behaviors: rain, sand/gravel/grass footsteps, pistol/rifle, engine idle/acceleration, explosion, thunder.
- [x] Physical DualSense receives synthetic reference PCM without **RW3** software gain/EQ/re-mix; optional WASAPI Exclusive (no silent fallback) or Shared.
- [x] Dual-device source selection, approximate simultaneous/individual tests, USB PnP checkmarks for Sony and Razer, manual Wolverine wire confirmation, user physical feel checks.
- [x] Local subjective (texture/impact/stereo) A/B scoring; negative/uncertain outcomes are recorded without faked scores. Original 48k DualSense test PCM and Wolverine 50Hz envelope are not claimed identical.
- [x] Opt-in bounded 10-second source-only rear haptic PCM recording in RAM, explicit local WAV save/discard, two-channel signed PCM16/IEEE Float32 output.
- [x] All ten synthetic references can be exported as unchanged stereo 2ch/48k WAV for **licensed** Wwise offline experiments.
- [x] Byte-exact channel extraction, WAV headers, capture limits, scenario/reliability rules in Windows hardware-free contract suite.
- [ ] Test all source-only capture, A/B and physical PCM behaviors on **actual two USB controllers**; a green CI is not physical proof.

## M3 — CURRENT: prove game-authored source and physical Razer stereo (P0)
- [ ] **[#1](https://github.com/skonyd/rw3pronativedshaptics/issues/1)** Load legally obtained Razer HAR/provider on actual Windows PC. Verify distinct left and right Wolverine body actuators, command timing, 250ms pulse, STOP/disconnect and no stuck motors; document hardware/firmware/Synapse versions.
- [ ] **[#2](https://github.com/skonyd/rw3pronativedshaptics/issues/2)** Demonstrate a supported *real PC game* producing game-authored DualSense 4ch rear haptic data; independently establish provenance rather than infer from RMS or endpoint name.
- [ ] **[#5](https://github.com/skonyd/rw3pronativedshaptics/issues/5)** Demonstrate user-configured Wolverine physical input → compatible virtual DualSense and game haptic UAC association (PadForge Full/DSX+ where supported); avoid double input; no background/silent driver installation.
- [ ] **[#3](https://github.com/skonyd/rw3pronativedshaptics/issues/3)** Real-world dual-controller A/B, hot unplug/endpoint changes, physical side isolation and latency; game performance average/1% low and p95/p99 frame time on/off; idle and sustained CPU/RAM.
- [ ] Measure/record negative results and non-functioning routes, not just successful provider/OS acknowledgments.

**M3 gate:** Source authenticity and Wolverine actuator independence must both be demonstrated physically. Failing here blocks the product regardless of how complete the UI or CI is.

## M4 — critical: high-fidelity LIVE hardware output and production proof (BLOCKED)
- [ ] **[#4](https://github.com/skonyd/rw3pronativedshaptics/issues/4)** Evaluate documented WYVRN/Razer waveform output; separately test licensed Wwise Motion 2ch WAV → Razer Sensa on actual hardware.
- [ ] Confirm (or disprove) **Wwise Audio Input Source → Motion bus → Razer Sensa** live dynamic stereo PCM compatibility. It is **only a hypothesis**; offline WAV import is not live waveform streaming.
- [ ] Obtain permission/licenses for external Wwise and Razer runtimes; do not distribute vendor DLLs, inject proprietary games, patch device firmware or install driver silently.
- [ ] If supported, implement real-time bounded jitter/underrun/latency handling and precise independent left/right PCM-to-Sensa backend; reject unsupported modes explicitly.
- [ ] Measure spectral/transient/side and onset fidelity against an unchanged physical DualSense with authentic game source (not just 10 synthetic scenarios).
- [ ] Verify robust game/Windows hotplug, restart, concurrency, long-session CPU and game FPS/frame-time impacts, installer/upgrade and optional signing.
- [ ] Mark Native HD **supported/finished only after all acceptance tests** in [VALIDATION_PLAN](VALIDATION_PLAN.md) are evidenced.

## Recommended next iteration
1. Start with M3 **hardware gate #1 and #2**; software-only work on a speculative API cannot prove output.
2. If hardware is unavailable, gather official Wwise/WYVRN live streaming contract evidence in #4 and improve repeatable test/report tooling, but **never relabel the existing 50Hz sink as native fidelity**.
3. After a documented live sink and real source are validated, implement M4. If the sink is unavailable under acceptable license, report the **hard blocker** rather than invent a product-completion date.

**No meaningful honest overall percentage** is defined: M3 and M4 contain the product's decisive, currently unproven functionality.

## M4 implementation prerequisite: original-resolution PCM transport (software only)
- [x] Strict 4ch PCM16/Float32 rear L/R samples copied byte-for-byte into a bounded 48kHz stereo ring without gain, EQ, 50Hz RMS or audio remixing
- [x] Deterministic buffer overflow (drop oldest + count), underrun (zero-fill + count), queue depth in milliseconds, reset/stop RAM zeroing
- [x] Native Bridge source-only opt-in **discard-sink** transport probe and live lost/underrun counters; Razer driver and DLL not used
- [x] Deterministic input/ring-wrap/large-callback/Float32/concurrent access contract tests
- [ ] Verify raw transport 48k source clock behavior, burst latency and dropped samples on Windows 11 with an actual game/virtual or USB DualSense UAC
- [ ] Connect only to a documented, legally licensed **real-time Sensa waveform** sink after independent vendor/physical proof; this transport alone cannot play haptics
- [ ] External physical latency/spectra/perceptual testing remains M3/M4 blocker

[Technical design / UI test procedure](REALTIME_PCM_TRANSPORT.md).

## Low-latency source pipeline, with honest timing (software preparation)
- [x] Replace source-only 20ms timer/padding diagnostic with **event-driven** wake on actual WASAPI callback; consume byte-exact original stereo PCM in 240-frame (5ms) blocks.
- [x] Record monotonic callback→reader PCM queue age (last/mean/max), oldest retained age, peak backlog and source callback gap instead of claiming motor latency.
- [x] Offer explicit **10/20/50/100ms WASAPI loopback capture buffer requests** (default 20ms); legacy NAudio capture had requested 100ms. No automatic invisible fallback and no unsubstantiated hardware-period promise.
- [x] Add 5/10ms deterministic latency-metric and callback notification tests, null-sink no-fake-underrun test and buffer policy checks.
- [ ] Validate real Windows 4ch UAC callback period, actual capture buffering, CPU/FPS and waveform drops with 10ms versus 20ms settings; **20ms ring capacity is not an automatic improvement if packets are larger**.
- [ ] Verify physical game event→Wolverine left/right Sensa latency and frequency/texture fidelity only when a licensed LIVE waveform sink exists.

[Latency budget / Windows measurement steps](LATENCY_BUDGET.md).

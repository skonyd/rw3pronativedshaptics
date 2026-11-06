# Roadmap (as of 2026-11-05)

## Phase 0 — project memory and requirements
- [x] Three modes and user intention documented
- [x] References and uncertainty classified

## Phase 1 — Windows 11 desktop shell
- [x] C# WPF application source, tray context menu, close-to-tray, mode selection
- [x] Current-user installer and standalone portable package pipelines specified
- [x] GitHub Releases check and SHA256-verified download designed
- [x] Source implementation: live provenance panel, error log, manual motor confirmation, CPU/RAM display
- [x] Source implementation: WASAPI frame counters and acknowledged XInput motor commands
- [x] Source implementation: 3% normalized process CPU guard for 3 successive 2-second samples; audio callback bounded to ≤256 sample points and 20 command/s
- [x] Source implementation: 30 hardware-free contract checks with CI verification
- [x] Successful Windows CI build and packaging verified after PR #6/main (2026-11-06)
- [ ] Windows 11 user device smoke test

## Phase 2 — conventional physical motor pipeline
- [x] XInput optional rumble test implemented in source
- [x] WASAPI audio → XInput prototype implemented in source
- [ ] Verify actual Wolverine connection and physical vibration, and that game input is not interrupted
- [ ] Per-device XInput slot selection and UI meter/limiter control

## Phase 2b — truthful status and performance
- [x] Windows build, contract checks, published EXE, portable ZIP and installer installation/removal passed in Actions
- [ ] Verify live status with silence, game audio, missing controller and XInput device changes
- [ ] Measure games' average/1% low FPS and frame-time spikes with/without app to establish low-impact setting
- [ ] Confirm short physical rumble pulse manually; no HD source assertions from XInput
- [ ] Add configurable CPU guard/profiling telemetry only after measurement, avoid telemetry overhead

## Phase 2c — game-free stereo test lab & experimental native bridge
- [x] Source: game-free L/R XInput tests with manual confirmation
- [x] Source: actual stereo test sound via Windows output + WASAPI return counters
- [x] Source: reproducible 48 kHz stereo fixture and 4-channel UAC fixture (rear haptic L/R)
- [x] Source: optional real 4ch/48k UAC haptic channel capture (strict source validation; no stereo fallback)
- [x] Source: experimental Interhaptics parametric Left/Right output via two HAR effects
- [x] Source: full 4-channel UAC synthetic playback → source monitor → Razer provider render test and user confirmation
- [x] Source: 30 deterministic assertions covering identity, L/R isolation, audio safety and PCM formats
- [x] **Windows build and PR release packaging verified in Actions**
- [ ] **Verify correct Interhaptics x64 library loading and correct Razer physical L/R routing**
- [ ] **Verify real DSX+/physical DualSense 4ch UAC endpoint and actual game's authored haptic samples**
- [ ] Measure latency, stereo separation and physical accuracy vs original DualSense
- [ ] Discover supported **direct stereo waveform sink**, as current parametric 50Hz envelope is lossy
- [ ] Make native bridge safe when launched multiple times and when other Razer apps compete for haptic provider

## Phase 3 — Razer Sensa HD output R&D
- [ ] Locate supported public API for precise independent left/right voice-coil output
- [ ] Test event + .haps capabilities and short on-the-fly effects; establish limits
- [ ] Prove stop, channel separation, timing, spectral response on hardware
- [ ] Build `IRazerSensaBackend` only after confirming device API

## Phase 4 — DualSense-native PCM capture and bridge
- [ ] Implement optional virtual DualSense/capture backend with documented interfaces
- [ ] Add two-channel PCM resampling/ringbuffer/dynamic haptic output
- [ ] Compare against real DualSense; no silent fallback to audio/rumble
- [ ] Profiles for games, DSX+ interoperability, diagnostics recording
- [ ] Windows signed release if signing certificate is available

Prioritize proving the Sensa driver output contract **before** invasive virtual device work.

## Test-readiness engineering increment
- [x] Source fix for ambiguous WPF/WinForms application and MessageBox symbols
- [x] Strict GitHub Actions exit-code handling and portable executable self-test step
- [x] Independent safety watchdog for stalled WASAPI DataAvailable, lock against stop/callback races
- [x] Interhaptics worker restart guard; stop timeout retains live thread reference
- [x] Recent channel energy timestamps and negative opposite-channel isolation check
- [x] New hardware-free contract assertions and manual hardware acceptance runbook
- [x] Windows Actions successful build + portable/installer artifact observed (PR #6 and main)
- [ ] Real Windows 11 and Wolverine / DualSense smoke and physical latency tests
- [ ] Prove lossless stereo waveform sink (not currently implemented)

## Direction v2 (ADR-002, 2026-11-06)
- [x] Independently verified evidence map: PadForge virtual DualSense Full UAC, DSX+ 3.2 beta, official WYVRN/Interhaptics and Wwise Sensa WAV sink
- [x] Adopt staged input/output feasibility gates rather than writing an arbitrary virtual audio driver
- [x] Native input candidate format preflight (4ch/48k) with speaker-vs-haptic energy diagnostics and finite/clamped PCM parsing
- [x] Hardware-free PCM16/Float32 L/R/speaker-only and malformed-data regression tests
- [x] Pull-request read-only GitHub permission, isolated release permission on version tags
- [ ] Baseline Razer Sensa game/demo and measure actual left/right controller actuator mapping
- [ ] Verify PadForge Wolverine physical-input → virtual DualSense Full with four-channel UAC, double-input avoidance
- [ ] Reproduce true authored-game rear haptic PCM without fixtures
- [ ] Verify official Wwise Motion stereo WAV playback on Wolverine, assess license and runtime interface (NO assumption of streaming)
- [ ] Investigate supported dynamic pitch/transient candidate only after physical output is proven
- [ ] Compare against physical DualSense with repeatable latency, channel separation and tactile tests

## Selectable method profiles (2026-11-06)
- [x] Persist nine selectable routes and show honest input source, output backend and readiness
- [x] Distinguish "best prospect for original DualSense authored source" from "best supported Razer Sensa gameplay option"
- [x] Pass selected native route to bridge, prefer only compatible endpoints, require explicit mapping confirmation before source start
- [x] Research-only Wwise and external Synapse options never launch unsupported playback or drivers
- [x] Contract coverage extended to 45 deterministic checks for route options, safe defaults, consent and fallbacks
- [ ] Verify each physical pathway, Wolverine input mapping, authentic game source signal, latency and L/R physical actuators
- [ ] Determine vendor-documented real-time Sensa waveform sink; Wwise offline authoring is not live stream integration

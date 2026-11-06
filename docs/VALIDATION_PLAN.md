# Acceptance and validation plan (2026-11-06)

Reference: [current implementation stage and evidence](IMPLEMENTATION_STATUS.md). Distinguish (A) software test/build success, (B) manual physical test, (C) authenticated original-game haptic streaming, and (D) a proven high-fidelity Wolverine waveform sink. **Only (A) has CI confirmation**; M3/M4 are not finished.

## A — executed hardware-free software gates

- [x] [Main Windows CI 42214409530](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530): contracts, .NET10 WPF release build (zero errors), published EXE `--self-test`, portable ZIP and installer install/self-test/uninstall, SHA256 assets.
- [x] Strict 4ch/48k PCM16/Float32 channel parser: synthetic Left/Right, speaker-only, malformed formats/nonfinite samples, age-of-signal rules.
- [x] 10 deterministic synthetic 4ch fixture behaviors, independent rear channels and silent front channels, bounded signal amplitude, tests for exact PCM frame counts.
- [x] USB physical-device PnP identity classifier rejects purely virtual names/HID and reports Unknown on scan error; Razer receiver vs direct cable requires a separate user confirmation.
- [x] Source-only monitor independent of HAR, save/discard of explicit 10s RAM-limited rear haptic PCM, bit-for-bit rear stereo export, correct PCM16/IEEE Float32 WAV/fact chunks. These are software correctness tests, **not genuine-game-source tests**.
- [x] Experimental parametric Sensa provider lifecycle and bounded output/stop code; independent side test commands. **No CI motor on hardware.**
- [x] 10-scene A/B UI for Both, DualSense-only, Wolverine-only with explicit user physical confirmation, negative/uncertain scores, and local JSONL summary. DualSense raw fixture path has no RW3 gain/EQ; WASAPI Exclusive may be unsupported and Shared may transform audio.

## B — not run: user-device safety, source binding and actuator acceptance

- [ ] On actual Windows 11 PC connect real USB DualSense and real Wolverine V3 Pro; record exact firmware, wired/wireless receiver state, Windows build, Synapse/Chroma version and Razer SDK/runtime version.
- [ ] Verify USB ✓/✕/unknown state when connecting/removing each unit; confirm a receiver alone cannot pass the separate user-wired confirmation. Reproduce false positives/virtual device cases.
- [ ] Test XInput manual pulse for no device, valid slot, invalid explicit slot, stop, reconnect and game coexistence. A command ACK is **not** physical confirmation.
- [ ] With **licensed user-provided HAR.dll / Interhaptics.RazerProvider.dll** verify two distinct *physical body* Sensa actuator responses with isolated Left/Right pulses. Confirm no repeated locked motor on source silence, STOP, provider failure, hotplug, exit and restart.
- [ ] Test 10 synthetic A/B scenes with both controllers USB. For each: first one controller, then the other, then near-simultaneous; swap hands. Distinguish physical response, dB/perceptual detail, left/right separation, user rating, latency. Negative/uncertain outcomes are valid.
- [ ] Run unsupported WASAPI Exclusive 4ch format and ensure no silent fallback; confirm changing live source/mode doesn't leave independent output active.
- [ ] Measure 10-minute gameplay average FPS, 1% lows, p95 and p99 frame time, CPU/memory across app-off/Normal/Audio/Native experiments; verify no unacceptable frame-time spikes.
- [ ] Test Windows startup, tray close/reopen/quit, native mode switching, portable ZIP, update consent and installer/uninstaller on real user's PC. The CI packaging result does not validate every real driver/runtime combination.

## C — not demonstrated: authentic game-authored DualSense capture and virtual input

- [ ] Choose a documented PC title that actually supports DualSense native haptic audio; record exact game version and controller input/output mapping.
- [ ] Validate a physically connected original DualSense independently outside RW3—when game's own haptic channel works, original playback must stay untouched.
- [ ] Route a genuinely supported game to a user-configured 4ch/48k UAC physical or compatible virtual DualSense endpoint. Capture rear L/R PCM *source evidence* (device route + game event controlled comparison); remove synthetic fixture from evidence and control for speaker-only sound.
- [ ] Demonstrate Wolverine physical buttons/sticks acting as a single virtual DualSense input with PadForge Full or compatible DSX+ where possible, without double input from Steam Input/native XInput and without any auto-installed driver.
- [ ] Use opt-in 10s recording to compare observed stereo rear-channel sample bits to source capture; WAV alone is **not** provenance.

## D — critical and unimplemented: live high-fidelity Sensa destination

- [ ] Obtain a documented, legally usable realtime stereo 48kHz native Sensa output contract on Wolverine, or experimentally establish Wwise Audio Input Source → Motion Factory Bus → Razer Sensa compatibility with required licensed plugins/binaries. A documented *offline stereo WAV Motion import alone* is insufficient.
- [ ] On hardware, verify distinct body voice-coil left/right driver output and safe stop/underrun/reconnect with known test waveform; do not confuse with trigger motors or generic XInput rumble.
- [ ] Implement only the vendor-allowed supported dynamic streaming backend. No proprietary DLL bundling, silent driver installation, undocumented firmware packets, game injection or claimed bit-perfect output without external measurements.
- [ ] Collect independent side-specific actuator output with physical sensing/audio accelerometers if available; compare spectra, impulse shape, timbre, attack and timing to an unchanged original USB DualSense playing a genuine authored game.
- [ ] Define defensible acceptance tolerances based on actual baselines; disclose limitations, any lossy fallback, unsupported games and device revisions. Only then make a "Native HD supported" claim.

## Test report template

| Field | Capture |
| --- | --- |
| Machine | Windows build, CPU, GPU, audio driver, .NET runtime, firmware |
| Software | RW3 commit, SHA256 build, selected route, provider & Synapse versions, licenses |
| Devices | Sony DualSense USB PnP/UAC endpoint, Razer Wolverine PC mode/wire/dongle |
| Game and effect | Game build, scene, exact expected DualSense event; synthetic vs authentic |
| Software evidence | UAC ch count/sample format, rear PCM L/R frame/timestamp/energy, provider status |
| Physical observation | Left/Right actual VCM felt/observed, noise, any stuck output, device isolation |
| Timing/fidelity | Latency p50/p95/p99, waveform spectral/transient comparison, subjective score |
| Performance | FPS average/1% low, p95/p99 frame-time, process CPU/memory, 10min |
| Outcome | PASS, FAIL, BLOCKED or NOT RUN; reproducible steps and local evidence path |

**Interpretation:** Software assertions and CI passing are necessary but nowhere near sufficient for the central feature. The live source and device sink gates remain [Issue #2](https://github.com/skonyd/rw3pronativedshaptics/issues/2), [Issue #1](https://github.com/skonyd/rw3pronativedshaptics/issues/1), [Issue #5](https://github.com/skonyd/rw3pronativedshaptics/issues/5) and [Issue #4](https://github.com/skonyd/rw3pronativedshaptics/issues/4).

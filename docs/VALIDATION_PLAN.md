# Acceptance and validation plan (2026-11-06)

> **2026-11-07 scope update:** See [current implementation](IMPLEMENTATION_STATUS.md). Separate A/B, Test Lab and expert bridge windows and the route selector are removed. Any instructions below using those controls describe historical workflows; underlying fixture/evidence/WAV/recorder/transport libraries remain. Current Test has 23 synthetic Wolverine/DualSense demos; DualSense reference is Exclusive-only without Shared fallback. Current Sensa v2 targets 5ms amplitude/frequency/transient updates, remains lossy and does not prove game provenance or physical HD. Hardware acceptance requirements remain open unless explicitly recorded in [HARDWARE_TEST_LOG](HARDWARE_TEST_LOG.md).

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
- [~] On hardware, verify distinct body voice-coil left/right driver output and safe stop/underrun/reconnect with known test waveform; do not confuse with trigger motors or generic XInput rumble. **Partial 2026-11-07:** left and right body actuators each felt by the user via Interhaptics Hand L/R parametric pulses ([log](HARDWARE_TEST_LOG.md)); cross-leak rating, stop/underrun/reconnect and sensor measurement still open.
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

## E — original-resolution stereo transport (software vs physical proof)
- [x] Contract test: 4ch 48k PCM16/Float32 rear 3/4 → stereo original sample bits; front speaker content never leaks to output.
- [x] Contract test: 20–500ms bounded RAM queue, oversized callbacks and previously buffered overflow cause exact counted oldest drops.
- [x] Contract test: underrun zero-padding, no replay of stale source, full reset, invalid-frame rejection, ring wrap and simultaneous writer/reader pairing.
- [x] Source-only UI transport probe wakes after PCM callbacks and consumes available complete 240-frame (5ms) blocks without inventing zero-filled underrun samples during source silence; reports offered/consumed/dropped, queue age and callback spacing, **no actual motor output** or WAV file.
- [ ] Windows physical test: start source-only/transport; confirm buffer counters under real USB/virtual 4ch game traffic and silence, no sample distortion and no accumulating unbounded backlog.
- [ ] Windows physical test: verify switching endpoint/bridge mode/closing cancels reader, zeroes retained PCM, and recording mode and probe remain mutually exclusive.
- [ ] Source clock, buffer jitter/dropout and eventual physical hardware latency need independent measurement; discard-sink counters are **not** end-to-end latency.
- [ ] Confirm vendor-supported legal real-time waveform sink before substituting the null/discard consumer.

## F — low-latency engineering acceptance (2026-11-06)
- [x] Software test: original 4ch→rear stereo PCM bytes remain unchanged when queued and drained in source-notified 5ms blocks.
- [x] Software test: deterministic monotonic 5/10ms callback timestamps produce accurate queue age, average/maximum residence and callback-gap metrics; reset clears latency history.
- [x] Software test: event-driven null sink wakes on input without a 20ms timer and drains without artificial underrun padding.
- [x] Software test: validate explicit 10/20/50/100ms WASAPI legacy loopback capture buffer requests; reject other values.
- [ ] Physical Windows test: verify which requested WASAPI buffer is granted or approximated, measure source callback distribution and compare PCM drop counts at 10/20/50/100ms.
- [ ] Performance test: 10-minute actual game run and baseline capture of CPU, frame times and source callback gaps; choose smallest **stable** buffer and capacity without dropped samples.
- [ ] External sensor test: original game-to-physical DualSense and Wolverine onset p50/p95/p99 and physical L/R isolation. The current app has no supported HD sink and cannot provide this evidence.
See [LATENCY_BUDGET](LATENCY_BUDGET.md).

## 2026-11-07 reference/provenance correction — software verification

Windows x64 .NET 10.0.403 Release WPF build with warnings-as-errors passed (zero warnings/errors). Contract suite passed, including unsupported Exclusive rejection without fallback or PCM mutation, complete reference stream byte identity and empty/incomplete quad frame rejection. Existing provenance contracts remain passing; Native UI now uses their source-only wording. No physical controller test was performed for this correction, and local results do not replace CI on the uncommitted changes.

## Channel conversion lab increment — 2026-11-07

Test card **DualSense → W3 Pro** adds seven channel/negative-control scenes plus the 23 demos, one canonical unmodified 4ch PCM source, the shared production decoder boundary, strict separate DualSense Exclusive reference and optional microphone level metering. Original-source SHA256 and software saturation/channel statistics are separate from acoustic observations. This is synthetic replay, not USB/WASAPI source capture or game-authored evidence; M3/M4 remain open. See [channel lab contract and procedure](CHANNEL_LAB.md).

## Current audio audit gate — 2026-11-08

The historical A/B window checks above describe retained library coverage, not current UI availability. The physical DualSense reference is Exclusive-only with no Shared fallback. Current Sensa output pacing is 33ms, not the earlier 5ms target.

Software gates now reject frame truncation/empty data, missing frequency, invalid values and both transient timing/amplitude differences. Delayed-tail preservation, bounded overflow, sink failure, no late writes after stop, 250ms watchdog, stale-feature clearing and clipping diagnostics pass on Windows. The additional finite 4,452-case frequency grid also passes; it is not physical validation.

Next run the controlled single-mic sequence in [AUDIO_AUDIT_20261108.md](AUDIO_AUDIT_20261108.md): two interleaved idle/engine-off/reference/audio controls, 13 frequencies × 3 levels × 2 paths, right-side scenes and repeats. Current physical run is blocked on microphone capture access and Wolverine wired-PC availability. Keep calibrated fidelity, absolute latency, stereo isolation, real-game source and Xbox-mode acceptance open.

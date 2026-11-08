# Runtime evidence and performance — 2026-11-07

2026-11-08: [opt-in multiband `.haps` output](MULTIBAND_RESULTS_20261108.md) adds4096-sample source spectral history. FFT is on the consumer outside the capture lock; warmed mono median≈.07ms, zero per-window managed allocations, but JSON/vendor rendering adds separate cost. This is not full-bridge/game CPU acceptance. The shorter2048 profile fails55/240 low-tone tests and is diagnostic only. Source history85.33ms and render cadence33ms are not measured total latency. [Cadence source and12-case30/33/36ms audit](LATENCY_BUDGET.md) do not support lowering the selected33ms. Multiple melodies improve provider representation, not proven motor phase/waveform fidelity.

M3 remains open and M4 high-fidelity live output is not implemented. [Current inventory](IMPLEMENTATION_STATUS.md).

| Observation | Establishes | Does not establish |
| --- | --- | --- |
| Selected mode / USB identity | Preference / observed device node | Active output or actuator response |
| WASAPI rear PCM activity | Selected 4ch/48k endpoint has rear-channel energy | Game authorship or HD reproduction |
| HAR/provider active and tick rate | Software engine/render activity | Hardware motion, fidelity or game-to-motor latency |
| Exclusive reference playback accepted | RW3 requests unchanged quad PCM16 without Shared fallback | Physical bit-perfect playback |
| User felt side pulse | Subjective motion under recorded conditions | Sensor-calibrated isolation, latency or frequency response |
| WAV byte identity / raw queue metrics | Sample-copy correctness / callback-to-reader timing | Physical Sensa waveform sink |

## Current safeguards and limits

Native v2 uses 1ms feature slots and current 33ms high-resolution output pacing after provider-backlog findings with amplitude/frequency/transients; it remains lossy. The bridge requests 10ms shared WASAPI loopback buffering; this is not IAudioClient3 low-latency mode and the driver can round the request. Native UI states game provenance and physical HD remain unverified, including during active PCM.

Audio-to-XInput bounds analysis and writes (up to 256 sampled frames/callback, at most 20 writes/sec), uses an independent 250ms silence watchdog and below-normal process priority. Three consecutive 2-second samples above 3% normalized system CPU pause audio for 60 seconds. These are software safeguards, not measured FPS guarantees.

Provider/source failures stop the bridge; mode retries every 5 seconds. Tests pause the selected mode. Motors and safe shutdown still require real-device acceptance. Acoustic desk/webcam observations in the pipeline analysis are relative exploratory evidence, not calibrated actuator or absolute latency measurements. The recorded idle-output issue remains open.

Current diagnostics include local logs, CPU/RAM, source/engine state and copyable report. No automatic recording/upload. Source-only WAV and bounded original-PCM transport libraries remain tested but their expert UI was removed. Recorder requires opt-in, 10-second bounds, a separate save action, and discard on stop. Raw transport preserves bits and counts losses; queue age excludes pre-callback, provider and physical delay.

## Required hardware acceptance

Record exact firmware (missing from the prior session), Windows/Synapse/SDK versions, connection mode and source/game build. Repeat isolated sides, silence, STOP/unplug/reconnect, idle output, DualSense reference and genuine-game provenance. Measure physical spectra/transients/onset with appropriate sensing, then >=10-minute game CPU/RAM/FPS/1% lows/p95–p99 frame times. No success claim from counters or synthetic demos alone. [Validation plan](VALIDATION_PLAN.md).

## Physical USB endpoint / full loopback increment — 2026-11-07

After the user connected DualSense, Windows exposed a physical **4ch/48k Float32 quad** render endpoint. The lab now has a separate independent-process Shared source → real WASAPI → v2 → Wolverine route, with an 8-second bounded source-only sample-comparison pass before live Sensa. Controls verified exact active source samples at unity Windows level, rear-channel isolation and no rear activity for speaker-only/silence. The user confirmed vibration on Wolverine. This proves a **synthetic full Windows route**, not actual game authorship or DualSense-equivalent physical fidelity. The Exclusive reference is unchanged; no new driver was installed. Optional temporary source unmute/unity level is explicit and restored after the test. See [route details](CHANNEL_LAB.md) and the hardware log. M3/M4 remain open.

## Audio prototype review — 2026-11-08

The local PC audio route now uses Sensa, and Xbox mode uses WGI body/trigger rumble; older Audio-to-XInput limits above apply only to that separate legacy route. Shared delivery has a 2s bound, explicit failure, an independent 250ms stale-output watchdog, and no post-stop sink writes. Resampler preparation occurs before callbacks; bursts over 250ms are rejected. Sensa input now defaults to unity: the old automatic 2.2 pre-gain caused input clipping and is diagnostic only. Wolverine output gain can still saturate parametric envelopes. Bluetooth automatic 300ms delay is a heuristic, not measured synchronization for every endpoint. See [repeated-scenario results](SCENARIO_AUDIT_20261108.md); telemetry is not physical fidelity evidence.

Windows contracts and a 4,452-case equalized tone grid pass; hardware microphone tests did not complete because WASAPI microphone initialization was denied and Wolverine subsequently disappeared from USB inventory. No new physical/FPS/latency results. [Detailed audit](AUDIO_AUDIT_20261108.md).

## Completed microphone acquisition — 2026-11-08

The earlier access blocker is resolved for the explicitly selected .NET Host (apphost .exe still denied); no privacy settings changed. 130 controlled acoustic cases completed. Voice Clarity/automatic Voice Focus restored afterwards. There are zero microphone-clipped samples in those reports, but physical repeatability is poor in selected N=3 groups (CV roughly 48–73%). 24/41 sweep pairs clear a descriptive 6dB background screen; that is not an HD acceptance threshold. Source hashes match and software safety contracts pass; neither establishes physical waveform fidelity. [Full results](ACOUSTIC_RESULTS_20261108.md). No sustained gameplay/FPS or calibrated actuator latency test completed.

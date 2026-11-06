# Runtime evidence and performance contract (updated 2026-11-06)

**Project stage:** [M3 physical/authentic-source validation](IMPLEMENTATION_STATUS.md) is open. Main [CI run 42214409530](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530) succeeded for `ad2aed3b`, but exercises **no physical DualSense/Wolverine**.

## What does "working" actually mean?

The application must not equate mode choice, USB detection, OS command acknowledgment, provider rendering or WAV files with genuine original DualSense HD haptics physically reproduced by Wolverine.

| Signal/evidence | What it really establishes | What it never establishes alone |
| --- | --- | --- |
| Mode / route selection | User preference and planned source/output path | Installed external SDK, live game or actuator success |
| XInput slot present / command accepted | Windows sees an XInput target and accepted a conventional rumble command | Real Razer model, motor displacement or HD Sensa waveform |
| User felt conventional motor pulse | Subjective, manual motion confirmation | Precise stereo HD texture/frequency |
| Windows present USB PnP green ✓ | A matching Sony/Razer USB identity is present | Wired Wolverine vs its dongle; physical motor response |
| Passive 4ch/48k WASAPI callback, rear L/R RMS | Selected render endpoint supplied channel frames | Game authored them; exact original game haptic PCM |
| Native `HAR` initialized / render ticks | Experiment can invoke licensed Interhaptics/provider event rendering | Independent body voice coils moved correctly or sound is lossless |
| Synthetic physical A/B lab | User can compare ten **project-authored**, bounded reference stimuli | They are real Sony game assets; waveform equality across hardware |
| Source-only WAV/PCM16/Float32 extracted | Channel 3/4 bytes are preserved into an offline file | Game provenance; successful live Razer streaming or device quality |
| Real-game provenance evidence | Verified target game's haptic audio mapping and isolated output | Physical Wolverine stereo response |
| Physical response, latency and frequency measurements | Specific hardware/revision/firmware behavior under specific conditions | All games/users/environments guaranteed to match |

**Native high-fidelity result: NOT IMPLEMENTED / NOT VERIFIED.** The live RW3 Sensa prototype is **50 Hz / 20ms parameter intensity envelopes**. Do not report lossless HD. The physical DualSense reference path avoids RW3 gain/EQ/mix on the emitted 4ch PCM; Windows shared-mode signal processing and actual actuator output are not proven bit perfect.

## Current software capabilities and truthful limits

- **Conventional XInput:** Normal mode preserves game-handled rumble; optional manual XInput motor pulses and system audio-to-rumble do not claim HD.
- **Native source-only:** strict 4ch/48k PCM16/Float32 selected endpoint, rear-channel telemetry, speaker vs haptic, recent/stale signal; HAR/Razer libraries not required.
- **Experimental Sensa:** two independently targeted *logical* HAR parametric effects; physical Left/Right identity requires the actual Wolverine.
- **Behavioral lab:** ten reproducible source fixtures and either/both physical controller requests; Razer receives the lossy 50Hz envelope. A/B rating is a human subjective response, with unplayed/uncertain/negative separated and local JSONL summary.
- **Gain:** RW3 global output and Wolverine-specific lab test level adjust RW3-generated output; the genuine wired USB DualSense reference PCM is never multiplied by these controls.
- **Offline WAV + optional recording:** project fixture export is local and offline. Separately, **only after explicit consent**, Native source-only can buffer at most 10 seconds of rear PCM in RAM and write a local WAV only when Save is clicked; Discard/Stop clears unsaved data. Source endpoint/recording do not prove authentic game origin.

## FPS and resource guardrails (software logic, not measured performance)

- Conventional audio-to-XInput mode: bounded capture callback samples (up to 256 points/callback), at most 20 XInput writes/s, stale/silent source motor stop. Process below-normal priority where allowed.
- Audio CPU guard: three consecutive 2-second samples over 3% normalized total-system CPU trigger audio-mode stop/return to Normal; this is a heuristic only, not an observed FPS guarantee.
- Sensa parametric provider: ~20ms compute/render cycle; aborts on provider absence and serializes stop/start. Device driver timings, physical motor release and concurrency must be measured on hardware.
- Diagnostics: stage/frames/RMS/peaks and local error logs; distinguish sound received from logical provider/rendered pulses. Do not print raw recorded PCM in diagnostics.
- **Privacy:** there is **no silent audio recording** and no captured PCM telemetry upload. Explicitly requested WAV export *does* write the selected rear-channel content to the user's local disk. Treat recorded game clips as user-controlled potentially copyrighted content; files are neither submitted nor uploaded by the app.

## Acceptance measurements still required on a real Windows 11 rig

1. Validate real controller identities, driver/mode/firmware, Synapse/Chroma and user-provided legally licensed provider DLL; test physical L/R actuators and STOP/unplug with no stuck haptics.
2. Prove that an actual DualSense-enabled PC game, not just a synthetic fixture, sends rear haptic PCM; verify physical/virtual UAC endpoint mapping and prevent double gamepad input.
3. Run each of the 10 behaviors with DualSense alone, Wolverine alone and paired. Log physical *Felt/NotFelt/Uncertain*, stereo separation, texture/impact score and both hands swapped. Software render success is not a sensor measurement.
4. Collect wall-clock/callback timestamps and external physical response/onset where possible; compare p50/p95/p99 latency, stereo isolation and spectral/transient detail. At present there is no validated timing/motor-frequency benchmark.
5. Profile game disabled/Normal/Audio/Native experiment for average FPS, 1% lows and p95/p99 frame times plus process CPU/RAM over >=10 minutes. Test endpoint changes, device unplug/replug and shutdown.
6. For Wwise path, require licensed Motion plugin and real Wolverine success; verify any future real-time Audio Input interoperability independently from offline WAV import.

Hardware acceptance checklist and reproducible reporting are in [VALIDATION_PLAN](VALIDATION_PLAN.md). Open gates: [#1](https://github.com/skonyd/rw3pronativedshaptics/issues/1), [#2](https://github.com/skonyd/rw3pronativedshaptics/issues/2), [#3](https://github.com/skonyd/rw3pronativedshaptics/issues/3), [#4](https://github.com/skonyd/rw3pronativedshaptics/issues/4), [#5](https://github.com/skonyd/rw3pronativedshaptics/issues/5).

## 48kHz stereo source transport metrics (opt-in diagnostic only)
A bounded source-side 120ms stereo PCM RAM queue now measures **offered**, **delivered**, **buffered**, **overrun-dropped** and **underrun-silence** frames. The source remains 48kHz PCM16/Float32 with original rear-channel sample bits; the independent discard-only reader takes 960 PCM frames every ~20ms. These **are not 50Hz envelope samples or device-render ticks**. Actual haptic motor output and precise driver latency still have no evidence. No new vendor SDK, game injection or audio upload is involved. The queue is attached only under explicit source-only monitoring and cannot be used simultaneously with 10s WAV recording. Its lock is bounded by current buffer sizes and only enabled when requested; repeat CPU/frame-time measurements are required on real Windows. [Contract](REALTIME_PCM_TRANSPORT.md).

## Latency and jitter: avoid confusing software and physical metrics
Native 4ch WASAPI loopback now supports an explicit **10/20/50/100ms requested buffer** (20ms default) and source-triggered, 5ms-block original stereo PCM drain. `Stopwatch` queue timestamps measure callback-arrival → software-reader delay, maximum, average, oldest buffered frame, peak queue and source callback spacing. They **do not include** game/authored event time, the WASAPI engine buffer before callback, licensed Razer output or body actuator onset. The shortest requested buffer may be rejected or rounded by Windows; `IAudioClient3.WithLowLatency` is documented as incompatible with loopback. Only real machine/system-CPU and physical latency tests can identify the best stable low-latency setting. Zero-data source silence no longer generates bogus null-sink underrun counts. [LATENCY_BUDGET](LATENCY_BUDGET.md).

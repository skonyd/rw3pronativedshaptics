# Wolverine V3 Pro Sensa pipeline — measured analysis and v2 architecture

**2026-11-08 correction:** later public multi-melody `.haps` tests change additional provider payload regions independently and repeatably; the older “bands2–3 only through unknown encoder” conclusion below was too broad. It described parametric/multiple-event tests, not AddHM/UpdateHM with separate melodies. An opt-in FFT→`.haps` prototype now exists, default off,33ms output/85.33ms history; carrier phase and calibrated physical fidelity remain open. Read [new results](MULTIBAND_RESULTS_20261108.md) before relying on historical tick/backend claims below. No raw packet packing or undocumented encoder API was added.

**Date:** 2026-11-07 · **Machine:** Windows 11 26320, Synapse 4.0.896, Interhaptics SDK v1.8.0 (`HAR.dll`, `Interhaptics.RazerProvider.dll`), Wolverine V3 Pro wired PC mode (`1532:0A4C`).
**Method:** local inspection (USB descriptors, DLL exports/strings, RzInterHaptics logs) and controlled engine experiments that read the exact frame bytes sent toward the controller. Physical feel/latency was **not** measured (no sensor yet); everything below is labelled measured / documented / inferred.

## 1. How a haptic sample reaches the Wolverine

```
our app ──HAR (Interhaptics engine)── ComputeAllEvents(t) per tick
            │  parametric effects (amplitude curve, pitch curve, transients)
            ▼
   Interhaptics.RazerProvider.dll ── GetInterhapticsHapticFrame → 41–47 byte frame per tick
            │  V1 IPC: Global\SharedMemory_InterhapticsMixerCommand + Event (PlayMultipleBufferCommand)
            ▼
   Synapse haptic mixer ("Synesthesia") — mixes programs (Sensa HD, Audio-to-Haptics, SDK apps), gain per source
            ▼
   RzInterHaptics → setInterhapticsStreamFrame → USB (≤64-byte chunks)
            ▼
   Wolverine firmware synthesises the vibration on Left_palm / Right_palm VCMs
```

Sources: provider exports/strings (measured), reverse-engineering notes in `han1548772930/razer_ui` `docs/products/host/sensa.md` (documented, static), our own frame dumps (measured).

## 2. What the Wolverine says about itself (measured, RzInterHaptics log)

Razer codename **"Sage T1"**, device ID `2636-{…}`:

| Field | Left | Right |
| --- | --- | --- |
| BodypartID | 216 (Left_palm) | 116 (Right_palm) |
| ActuatorType | 2 (voice coil) | 2 |
| Frequency range | **30–400 Hz** | 30–400 Hz |
| **Resonance** | **50 Hz** | 50 Hz |
| StreamCharacteristics | **3 bands, 4 points, 2 transients** per frame | same |

USB (measured descriptor): IF0 XUSB gamepad (interrupt 64 B @ 1 ms), IF1 HID, IF2–4 **USB Audio 2.0 headset jack only** (terminal type 0x0402). There is **no audio-class path to the actuators**; the Wolverine cannot be driven like a DualSense (PCM on rear channels). All haptics go through the Razer stream protocol.

## 3. Frame experiments (measured with `RenderHapticsDebug(buffer, index, size)`)

| Experiment | Observation | Consequence |
| --- | --- | --- |
| Frame size | 41 bytes = 1 header + two **20-byte blocks**; **44/47 bytes** when 1/2 transients are present | Compact parametric packet, fits one 64-byte USB report |
| Left effect (Hand, side 2) | only block 1 (bytes 1–7) changes | **Left/right are separate in the protocol.** Felt cross-talk is mechanical (one-piece shell), not software mixing |
| Right effect (side 1) | only block 2 changes | |
| Amplitude 0.5 vs 1.0 | packed values scale | amplitude is carried per point |
| Pitch 0 vs 1 | bits saturate (`FF…F8`) | frequency is carried in the same block |
| Two/three concurrent effects on one side | values add and saturate; bytes 8–13 stay 0 | parametric API fills **one band + transients**; bands 2–3 are only reachable via Razer's audio encoder |
| Tick 10 ms vs 2 ms | frame produced every call | **frame rate is chosen by the caller** |
| `UpdateParametricEffect` on a playing effect | new amplitude **and** pitch appear in the **next frame**, no `PlayEvent` needed | low-latency continuous streaming is supported by the public API |
| Original 1 s envelope | effect expired 1 s after start (fixed earlier today) | always long envelopes / continuous updates |

## 4. What today's app does wrong (measured)

| Area | Current | Problem |
| --- | --- | --- |
| Tick | `Thread.Sleep(20)` | Windows default timer → **~31 ms** real period (~32 Hz), not 50 Hz |
| Data per tick | one intensity value | ignores pitch, transients and the 4 points per frame |
| Frequency window | 65–300 Hz, pitch unset (= lowest) | device range is 30–400 Hz with **50 Hz resonance** |
| Capture | 20 ms WASAPI request, 50 Hz RMS | adds ≥20 ms and smears transients |

## 5. v2 architecture (implemented 2026-11-07)

1. **Source** (unchanged contract): DualSense rear channels 3/4, 48 kHz, bit-exact, event-driven 240-frame (5 ms) blocks; 10 ms loopback request.
2. **Analyser per side** (new, pure C#, allocation-free):
   - envelope follower (≈1 ms attack / 15 ms release) sampled as **4 points per tick**;
   - dominant frequency in 30–400 Hz (zero-crossing / short autocorrelation) → pitch 0..1;
   - onset detector (energy jump over ~5 ms) → **transient triple** (time, amplitude, frequency), max 2 per frame.
3. **Output**: one parametric effect per side (Left_palm / Right_palm), updated **every tick** with `UpdateParametricEffect` (amplitude points, pitch, transients); `pitchMin/Max = 30/400`.
4. **Clock**: dedicated thread with a high-resolution waitable timer (or `timeBeginPeriod(1)`) at **4–5 ms** ticks, MMCSS priority; no `Thread.Sleep(20)`.
5. **Tuning layer**: per-side gain, resonance-aware frequency mapping (DualSense actuator band → Wolverine 30–400 Hz with emphasis near 50 Hz), optional cross-talk compensation (slight attenuation of the opposite side when one side dominates).
6. **Diagnostics**: frame-rate counter, frame dump via `RenderHapticsDebug`, end-to-end budget display.

**Expected latency (inferred):** 5 ms capture block + ≤5 ms tick + Razer IPC/mixer/USB (unknown, est. 2–10 ms) ≈ **10–20 ms**, versus today's ≈ 20 ms capture + ~31 ms tick + smoothing.

## 6. Open options and how to test them

| Option | Status | Next test |
| --- | --- | --- |
| Use bands 2–3 via HAR `EncodeAudio/EncodeBand/EncodeTransients` | exports exist, **signatures undocumented** | probe in isolated process, compare frames with Synapse Sensa HD |
| Feed DualSense haptic PCM into Synapse's own **Sensa HD** (audio capture → FFT 20 480 Hz → encoder, presets Controlled/Balanced/Dynamic) | documented in RE notes; Synapse captures one audio endpoint | find which endpoint Synapse captures; try routing rear channels as stereo to it (virtual cable) |
| Physical measurement | no sensor | (a) DualSense mic once its charging fault is fixed; (b) **wired headset mic in the Wolverine's own 3.5 mm jack** pressed to the grip — same USB clock as the controller |

## 7. Not pursued

- Re-implementing Razer's bit-packed frame format by hand: unnecessary while the public parametric API reaches the same block fields; revisit only if bands 2–3 cannot be reached through Razer's encoder.
- Driving the actuators through the headset audio interface: the descriptor shows it is a headset path only.

## 8. v2 implementation status (2026-11-07)

| Part | File | Verified |
| --- | --- | --- |
| Feature extractor (1 ms slots: envelope, period-based frequency over 40 ms, onsets) | `HapticFeatureExtractor.cs` | 10 contract tests: silence, 60/80/120/200 Hz within ±5 Hz, envelope level, L/R isolation, single onset per hit, Float32/NaN, 4-point ramp |
| Engine: 5 ms absolute schedule on a high-resolution waitable timer, MMCSS "Games", `UpdateParametricEffect` + `PlayEvent` per side per tick, 30–400 Hz | `SensaStereoPrototype.cs` | real hardware run: **200 Hz tick** (v1 ≈ 32 Hz); left source fills only block 1, right only block 2; 6 hits → 6 transient frames |
| Capture → extractor (raw rear PCM, 10 ms loopback request) | `NativeDualSenseCapture.cs`, `NativeBridgeService.cs` | build + contract suite |
| Test tab "Native v2 demo" (hits, 30→400 Hz sweep, L↔R, rain) through the same path | `NativeDemoSignals.cs`, `MainWindow` | engine run; **feel not yet confirmed** |

Still open: physical latency/feel measurement, live game source, bands 2–3 via Razer's encoder.

## 9. Acoustic measurements (webcam microphone, 2026-11-07)

**Setup:** A4tech FHD 1080P webcam mic (48 kHz) on the wooden desk facing the Wolverine; the controller's
vibration on the desk is picked up as sound. Envelope 30–600 Hz at 1 ms. 15–20 pulses per run (250 ms, full
test amplitude, both sides) at random 0.7–1.0 s gaps. The mic path has its own unknown delay that also varies
between recordings, so **only differences measured inside the same recording are trusted**: XInput rumble pulses
(direct USB) are interleaved with the method under test.

| Method | Δ vs XInput rumble (same recording) | Notes |
| --- | --- | --- |
| v1 emulation (`Sleep(20)`, `SetEventIntensity`, looping effect) | **+11 ms** | |
| v2 as first written (skips updates while silent) | +10–15 ms (separate runs) | cold stream restart at onset |
| **v2 (warm stream, updates every 5 ms tick)** | **+3 / +8 ms** (two runs) | shipped |
| Variant B: `UpdateParametricEffect` only, effect always playing | +2 ms | |
| Variant C: Update + `PlayEvent` every tick | −3 ms | |
| Variant A: intensity-only at 5 ms | +20 ms vs others (separate run) | engine smooths `SetEventIntensity` |

Detection 15/15 in every run, signal/noise 200–15 000×. Measurement noise ≈ ±5 ms.

**Conclusions:** the Sensa path through HAR + Synapse mixer is within a few ms of plain XInput rumble when the
stream is kept warm; v2 removes ~5–8 ms versus v1. Never skip silent ticks; never drive amplitude via
`SetEventIntensity`.

**Idle output (open issue):** with any Razer engine running and zero amplitude, the desk mic hears intermittent
low-level output: v1/v2 median ≈1.5e-3 / p95 ≈2e-2 vs engine off ≈6e-5 / ≈1.5e-3 (≈20 dB below a full pulse);
variants A/B/C ≈3–7e-4 / ≈5e-3. Frame dumps also show provider "transient" bits on idle frames. Next: find which
frame fields cause it and whether it is felt in the hand.

**Frequency sweep (30→400 Hz):** the desk/mic radiates best at 30–39 Hz and 110–310 Hz and shows a dip at
50–65 Hz — exactly the actuator's stated resonance. Airborne sound is a poor proxy for felt vibration at low
frequency, so this does **not** show the haptic response; a contact sensor or hand ratings are needed.

## 10. Demo library (Test tab, 2026-11-07)

15 synthetic DualSense rear-channel scenes run through the same extractor + engine as live capture
(`NativeDemoSignals`): Hits, 30→400 Hz sweep, L↔R, Rain, **8 directions**, Footsteps, Heartbeat, Engine rev,
Explosion, Machine gun, Bow draw, Thunder, Gravel, Sword clash, Fire crackle. Contract tests check every scene is
finite, ≤ 1.0 peak and non-silent.

**8 directions:** the Wolverine has two actuators, so only the lateral axis is physical (equal-power pan). Front/back
is a perceptual code: front = light 190 Hz taps gated at 12 Hz, back = continuous 45 Hz near resonance, centre = 80 Hz.
Tests verify through the extractor: "Sağ" right-only, "Sol" left-only, front/back centred, front 190 Hz vs back 45 Hz.
Engine run: 200 Hz tick; right/left steps load the matching frame block. Feel not yet rated.

## 11. Cleanup and UX changes (2026-11-07, later)

- Modes auto-run: selecting a mode starts it; the saved mode starts with the app; failures retry every 5 s; tests pause it and it resumes afterwards. No Start button.
- Removed the right-motor finder (Hand L/R confirmed on hardware), the Native expert panel and the A/B lab windows.
- Native gain presets (source envelope → Sensa amplitude): Normal 140 %, Güçlü 200 %, Maksimum 300 %. Peaks are already at the engine's 1.0 ceiling (frame bytes saturate); more strength needs Synapse's device haptic intensity.
- Demos: 23 scenes; each can play on the Wolverine (v2 path) or directly on the USB DualSense (Exclusive PCM16 + HID keep-alive) as reference.

## 12. Trigger motors and game-output capture (research, 2026-11-07)

**Trigger motors.** PCGamingWiki states the Xbox-licensed Wolverine V3 models support impulse triggers. On this PC the
wired PC-mode device (`1532:0A4C`) is an **Xbox 360 class (XUSB, `xusb22`)** device; XInput has no trigger channel.
Windows.Gaming.Input probe (webcam mic, same recording as an XInput control at 155–1146× noise): `LeftTrigger`,
`RightTrigger`, `LeftMotor`, `RightMotor` all **silent**, from a console and from a focused window. ⇒ In PC mode
the trigger motors are not reachable from software. Next test: Xbox mode / HyperSpeed dongle (`1532:0A3F`, GIP class),
and whether Sensa (RzInterHaptics) is still available in that mode.

**Capturing what a game sends.** Windows offers no way to observe another process's controller output. Every tool
that converts game rumble/trigger output (PadForge, DSX virtual DualSense) presents a **virtual controller** to the
game and receives the output in a callback (PadForge: HIDMaestro `OutputReceived`, DualSense report 0x02 with
`valid_flag0` rumble 0x03 / trigger 0x0E frames at 30–118 Hz). ViGEmBus 1.22 (installed with DSX) offers virtual
Xbox 360 / DS4 targets with rumble callbacks, but no DualSense and no impulse-trigger channel.

**Xbox mode result (same day).** Switched in Synapse to Xbox mode, HyperSpeed dongle: device `1532:0A3F`, driver
`dc1-controller` (GIP), WGI name "Xbox One Game Controller". Webcam-mic levels vs silence: LeftTrigger **185×**,
RightTrigger **219×**, LeftMotor 152×, RightMotor 65×, XInput both 164× ⇒ **impulse-trigger motors exist and work
in Xbox mode**. Sensa in Xbox mode: RzInterHaptics `GetAllDeviceIDs = []`, 0/6 Sensa pulses detected ⇒ **Sensa SDK
unavailable**. The two capabilities are mutually exclusive per mode:

| Controller mode | Protocol | Sensa (Interhaptics) | Trigger motors | Main rumble |
| --- | --- | --- | --- | --- |
| PC (`0A4C`) | Xbox 360 / XUSB + Razer HID + UAC2 | ✅ 2 VCMs, 30–400 Hz stream | ❌ | XInput only |
| Xbox (`0A3F`) | Xbox One / GIP | ❌ | ✅ WGI/GameInput | XInput + WGI |

## 13. Canonical PCM channel lab (2026-11-07)

The new channel card and hardware probe use one deterministic source and the same production PCM ingress for offline replay; no manual pulse substitute. All 23 demos ran on Wolverine at approximately 200Hz; source hashes stayed unchanged. DualSense was explicitly deferred by the user and no compatible source endpoint existed. Single-microphone background/active/tail and 0% output controls did not reliably isolate motor sound, so no physical spectral/transient/latency score is supported. Full conditions and results: [hardware log](HARDWARE_TEST_LOG.md); source/test contract: [channel lab](CHANNEL_LAB.md).

## 13. Mode-aware "Sesten titreşim" (A, 2026-11-07)

The audio mode now detects the Wolverine protocol at start (`WolverineModeDetector`):

| Controller mode | Route | Implementation |
| --- | --- | --- |
| PC (`0A4C`) | default playback device (WASAPI loopback, 10 ms, any rate → 48 kHz) → 4th-order low-pass 220 Hz ×2.2 → `HapticFeatureExtractor.PushStereo` → v2 Sensa engine | `AudioHapticsBridge`, `SensaAudioSink` |
| Xbox (`0A3F`) | same capture → bass (<120 Hz) envelope → heavy motor; 120–400 Hz → light motor; bass hits per side → **impulse triggers** (decaying pulse) via Windows.Gaming.Input | `XboxAudioSink` |
| unknown | classic audio → XInput rumble (v1 path) | `XInputHaptics` |

Project TFM moved to `net10.0-windows10.0.19041.0` for Windows.Gaming.Input; output folder is now
`bin/<cfg>/net10.0-windows10.0.19041.0` (HAR/provider DLLs must sit there). Verified: contract test (60 Hz passes 0.63,
1 kHz 0.001, L-only stays L); hardware run on the 44.1 kHz Soundcore output: ~1000 slots/s, left block fills during a
left-only 60 Hz burst and both return to the silence pattern afterwards. Xbox-mode route not yet run on hardware;
desk-mic A/B was inconclusive (room noise varied 6e-4…1e-2 between runs).

**Synapse Audio-to-Haptics conflict (measured).** Synapse's Wolverine profile has
`audioHapticIntensity.enableAudioToHaptics = true` (intensity 4–6). With RW3's engine completely off, the pan-test
tone on the default output raised the desk-mic level ~10× during playback and it stayed ~8.5× for 6+ s afterwards ⇒
Synapse renders all PC audio into Sensa itself, delayed and with a long tail. It must be turned off in Synapse
whenever an RW3 audio/Native mode runs; the app now says so. RW3's own route followed the pan in step with the caption.

**Second external audio→haptics source: DSX (2026-11-07).** After Synapse A2H was switched off the user still felt
lag and a long tail. DSX was running with the DualSense profile `haptics_mode = AUDIO_TO_HAPTICS`, motor intensity
10/10, so every PC sound also drives the USB DualSense lying next to the Wolverine; the desk-mic noise floor rose
~100× (7e-5 → 6e-3) after DSX started. A clean RW3-only measurement needs DSX's audio haptics off (or DSX closed).
With DSX closed the desk-mic floor returned to 5e-5 and XInput pulses were detected 6/6 at 16 000× SNR. Later runs
had a drifting room floor (7e-4…4e-3), so tail measurements of the idle Sensa engine were inconclusive: brief
intermittent output after engine start (the known idle-output issue), no steady multi-second tail after stop.

## 14. Frame-queue backlog: 5 ms ticks were wrong (2026-11-07, webcam mic)

Symptom (user): the audio pan test did not follow the on-screen caption, and the
controller kept vibrating 30-50 s after the test ended.

Measured with the A4tech webcam mic (its noise gate outputs exact zeros in silence,
which makes tails easy to see):

| Experiment | Result |
|---|---|
| Pan test with RW3 engine OFF | no vibration at all, no tail (Synapse A2H not involved) |
| Engine started and stopped with no input | no tail |
| Manual 0.8 pulse, 5 ms ticks, 1 / 2 / 4 s drive | vibration until 7 / 13 / ~28 s (x7) |
| Same 2 s drive at 20 / 25 / 33 / 40 ms ticks | x10 (noisy) / x1.26 / x1.10 / x1.12 |
| 15 s continuous drive at 33 / 36 / 40 ms | ends 0.20-0.25 s after the drive (no growth) |

Conclusion: Synapse plays every rendered frame as a fixed-length chunk of roughly
30-37 ms, whatever the call rate. Section 4's "frame rate is chosen by the caller" was
true for frame *production*, not for playback. At 5 ms the queue filled ~7x faster than
it drained: vibration smeared, lagged the sound more and more, and played on after
the source stopped. The earlier +3 ms latency figures were short pulses into an empty
queue and did not reveal this.

Changes:
- `TickMs` 5 -> **33**.
- Idle gate: after 100 ms of silence the events are stopped (empty frames). A playing
  all-zero curve made the controller hum while idle (mic 1000-2400 vs 6-28).
- While idle the loop polls input every 5 ms and sends the first frame of a new sound
  immediately; empty frames keep the 33 ms period.
- Before disconnecting, a few empty frames are rendered.

Result: pulse/audio tails end within ~0.25 s; latency vs XInput +23 ms
(63-69 ms, previously +35 ms at a fixed 33 ms schedule).

Also: loopback hears audio before a Bluetooth speaker plays it (Soundcore Motion+
A2DP measured 320-570 ms). "Ses senkronu" setting (auto: Bluetooth 300 ms, wired 0)
delays the audio->haptics path for games; the Test tab's pan test always uses 0.

## 15. Equalized audio/Native feature comparison on a finite catalogue (2026-11-08)

Goal (user): the "Sesten titreşim" path must hand the engine exactly what the Native path
does for the same haptic waveform, at every frequency and level.

`AudioPathEquivalence` (src) runs a catalogue of 94 signals through both paths and compares
every 33 ms frame (4 amplitude points, frequency, transients): tones 20–400 Hz × levels
0.05/0.2/0.5/0.9, hard hits 40–400 Hz, a level ramp, right-only, all 23 demos and the four
sound tests. Audio is played at 1/PreGain (the level that reaches the engine at the same
strength).

Differences found and removed:

| # | Difference | Size before | Fix |
|---|---|---|---|
| 1 | Audio-only 2× 2nd-order 220 Hz low-pass | 150 Hz ×0.82, 250 Hz ×0.36, 400 Hz ×0.08; amp error up to 0.76 | removed from the audio sink |
| 2 | Out-of-band content (>550 Hz: hard-onset harmonics, noise textures, square buzz) analysed by Native but not by audio | Hz error up to 190, transient amp error up to 0.69 | one band limit (8th-order Butterworth 550 Hz, flat ≥0.997 to 400 Hz, 0.008 at 1 kHz) inside the extractor, applied to **both** paths |
| 3 | Onset frame frequency "0" → 50 Hz resonance kick | every side switch | engine keeps the side's last pitch for up to 1 s |
| 4 | Loopback includes the endpoint's software volume | Steam speakers at −7 dB captured at 0.446 | capture divided by the live endpoint volume when the device has no hardware volume |

Result: offline 94/94 identical (max amp error 0.000, Hz error 0.0, same transients) — now a
contract test. Real playback through Windows (Steam Streaming Speakers, 6 ch, volume 63 %):
166 s, captured samples within 3e-8 of what was played, level ratio 1.0000, **94/94 identical
to Native**. The band limit adds ~1.5 ms to both paths alike.

### 15b. Device sample rates, transient robustness, final real-device result

| # | Difference | Fix |
|---|---|---|
| 5 | 44.1 kHz endpoints converted 44.1→48 kHz by linear interpolation (onsets changed by up to 0.17) | `SincResampler`: 32-tap Kaiser windowed sinc, ~0.36 ms look-ahead |
| 6 | Zero-crossing hysteresis fixed at 0.004: ringing tails after abrupt stops counted as cycles (2.8–5.7 Hz) | hysteresis = max(0.004, 10 % of the envelope) |
| 7 | Native itself: the same hit shifted by ≤ 0.5 ms changed transient strength by up to 0.70 (single 1 ms slot energy) | onset test on 2 ms sliding energy; strength = envelope peak over the first 3 ms (×1.41 keeps the old scale) → 0.10–0.25 |

Offline: the 94 selected equalized-input signals pass at 48, 44.1 and 96 kHz within declared tolerances. Resampled paths are not byte-identical. This tests shared lossy feature extraction, not arbitrary audio or physical equivalence.

Real playback, all 94 signals, Soundcore Motion+ (Bluetooth, 44.1 kHz, hardware volume):
88 strictly identical (amp ≤ 0.01, Hz ≤ 1, same transients) + 6 within Native's own
sub-millisecond variation (noise/click textures), 0 outside; worst amplitude error 0.003.

Steam Streaming Speakers (virtual 5.1): one run 94/94 identical; other runs dropped loopback
data (raw captured samples already differ from what was played, mean 0.07; lag varied
−16…−664 ms) — an observed end-to-end capture/transport failure in those runs. Root cause is not isolated; those runs cannot establish a working audio→haptics route.

Remaining inherent limit (both paths alike): the Wolverine frame carries 4 amplitude points per
33 ms, so where a click lands inside a frame varies its 4-point shape by up to ~0.15–0.18.

## 16. Review correction and reproducible gates — 2026-11-08

The earlier section 15 hardware/capture claims are historical reports from the other agent. Its comparator could ignore a missing frequency, a truncated ending and second-transient/timing differences; it cannot establish every-value or physical equivalence. The strengthened comparator rejects all of those, empty inputs and nonfinite features. Current Windows contracts, the finite catalogue and a denser 4,452-case equalized tone grid pass declared tolerances. Both paths still share lossy analysis, 2.2 audio gain can clip general input, and source resampling is not byte-identical.

Audio delivery now has bounded serialized writes, source-stop health, independent 250ms stale-output protection, no post-stop delivery, and explicit feature/filter clearing. Current 33ms output pacing is retained. A new aggregate-only microphone audit records controls, 10ms level bins, tone magnitude, repeats and tails without recording raw microphone PCM. No new physical result completed: WASAPI microphone open returned E_ACCESSDENIED, then Wolverine PC USB identity disappeared before the explicit MME attempt. [Conditions, results and procedure](AUDIO_AUDIT_20261108.md).

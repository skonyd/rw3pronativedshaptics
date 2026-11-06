# Realtime DualSense PCM transport: source-only diagnostic prototype

**Stage:** M4 *preparation infrastructure*; source-side PCM queue and diagnostic discard consumer implemented. **NOT a live Razer Sensa HD output.** The product remains in **M3 hardware/source validation** as defined in [IMPLEMENTATION_STATUS](IMPLEMENTATION_STATUS.md).

## Why this exists

The current physical Wolverine experiment (`SensaStereoPrototype.cs`) converts stereo rear-channel 48kHz data to left/right **RMS envelope intensity once every 20ms (50Hz)**. Fine-grained timing and original waveform samples are lost. A future vendor-supported, licensed, live stereo sink requires a source-to-sink transport that can retain **every 48k sample**, bound its queue, and honestly measure data loss.

`RealtimeStereoPcmTransport.cs` is that isolated foundation. It performs only:
- Four-channel, interleaved 48kHz source PCM16 or Float32 → two-channel rear haptic PCM with **unchanged raw sample bits** (speaker channels 1/2 are never forwarded).
- Fixed-capacity RAM ring (20–500ms, chosen 120ms in the UI). Producer and reader share one short-held lock; no network, storage or Razer native calls.
- Deterministic overflow: retain latest complete stereo frames; **count discarded oldest frames**. Never silently substitute mono or a system-audio signal.
- Deterministic underrun: emit **digital silence for missing frames**, do not replay stale data or invent a synthetic effect. Count padded frames.
- Snapshot fields: offered/delivered/buffered/dropped/underrun frames and conversion to milliseconds. These represent **software queue status**, not physical-actuator end-to-end latency.
- Reset on detach/stop/end of session, clearing retained PCM bytes and counters.

**Float32 note:** This transport retains raw IEEE sample bits even for NaN or other nonfinite payloads. It is a *source fidelity and offline diagnostic* stage; before connecting any future physical sink, that backend must validate/sanitize nonfinite values and apply hardware-safe output limits **to the Wolverine output only**, without changing the independently observable original DualSense source.

## How to run the transport probe

1. In **Native Stereo Bridge**, choose and approve a 4ch/48k UAC device.
2. Click **Yalnızca 4ch PCM kaynağını izle**. This source-only mode needs no HAR.dll, Razer provider, Synapse, or physical Wolverine.
3. Under **2C • 48kHz ORİJİNAL STEREO PCM TAŞIMA PROVASI**, click **Stereo PCM tampon testini başlat**. It attaches an optional 120ms stereo ring to the WASAPI callback. **No physical controllers are energized**, no WAV is saved and no USB DualSense playback sample is changed.
4. A background **discarding consumer** requests **960 original 48kHz PCM frames every ~20ms**. That 20ms timer is a scheduling approximation, **not** an RMS 50Hz output conversion, sample-aligned source clock, or Razer device clock.
5. The display updates once per second: total source frames, consumed frames, current buffered milliseconds, overrun lost milliseconds and padded-underrun milliseconds. During game silence, Windows may not dispatch loopback frames; underrun is expected and **not by itself a fault or proof of game authorship**.
6. Stop via the same button or stop/close/switch the Native Bridge. The queue is detached and zeroed. WAV capture and transport probe cannot run concurrently. An explicit source endpoint change stops the active session.

## What can and cannot be claimed

| Observation | Allowed conclusion | NOT implied |
| --- | --- | --- |
| Full PCM16/Float32 source/consumer identity tests pass | Internal channel bytes are forwarded unchanged under this transport contract | Any physical Wolverine vibration |
| Source-only 4ch capture + ring counters increase | This Windows UAC endpoint sent accepted rear PCM frames into RW3 | Genuine authored DualSense gameplay PCM |
| Dropped/underrun frames remain zero in one test | This one software queue/consumer schedule had no observed ring loss | Source-to-actuator latency, motor response or zero drop in all games |
| A licensed Wwise Audio Input test accepts streams in the future | Potential for a *separately verified* legal runtime sink | Existing 50Hz HAR parametric backend became high fidelity |

The actual live stereo endpoint remains gated by [Issue #4](https://github.com/skonyd/rw3pronativedshaptics/issues/4): independently validate Razer/WYVRN or Wwise Audio Input → Motion Bus → Sensa in a correctly licensed setup and prove distinct left/right Wolverine motor motion. Source authenticity and virtual gamepad binding are [#2](https://github.com/skonyd/rw3pronativedshaptics/issues/2) and [#5](https://github.com/skonyd/rw3pronativedshaptics/issues/5). No vendor library/device driver bundled.

## Deterministic CI validation

Contract tests include: 48k ring frame/time budget, ten-scene stereo channel identity, fractional/burst reads and ring-wrap, oversize source truncation and buffered eviction, explicit zero-fill, no uncounted dropped frames, full reset, malformed stereo/interleaved input, buffer capacity cap, untouched IEEE Float32 payloads, and concurrent producer/consumer stereo pairing.

Physical acceptance **not run**: 48k source-clock coherence versus a real hardware sample clock, Windows WASAPI callback scheduling and worst-case latency/jitter, source/game identity, real Sensa streaming, real device stereo separation and 10-minute performance measurement.

A green Windows runner only validates the source-only transport software. Do not label this as M4 completed.

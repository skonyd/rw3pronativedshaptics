# Realtime DualSense PCM transport: source-only diagnostic prototype

> **2026-11-07 scope update:** See [current implementation](IMPLEMENTATION_STATUS.md). Separate A/B, Test Lab and expert bridge windows and the route selector are removed. Any instructions below using those controls describe historical workflows; underlying fixture/evidence/WAV/recorder/transport libraries remain. Current Test has 23 synthetic Wolverine/DualSense demos; DualSense reference is Exclusive-only without Shared fallback. Current Sensa v2 targets 5ms amplitude/frequency/transient updates, remains lossy and does not prove game provenance or physical HD. Hardware acceptance requirements remain open unless explicitly recorded in [HARDWARE_TEST_LOG](HARDWARE_TEST_LOG.md).

**Stage:** M4 *preparation infrastructure*; source-side PCM queue and diagnostic discard consumer implemented. **NOT a live Razer Sensa HD output.** The product remains in **M3 hardware/source validation** as defined in [IMPLEMENTATION_STATUS](IMPLEMENTATION_STATUS.md).

## Why this exists

The current physical Wolverine experiment (`SensaStereoPrototype.cs`) converts stereo rear-channel 48kHz data to left/right **RMS envelope intensity once every 20ms (50Hz)**. Fine-grained timing and original waveform samples are lost. A future vendor-supported, licensed, live stereo sink requires a source-to-sink transport that can retain **every 48k sample**, bound its queue, and honestly measure data loss.

`RealtimeStereoPcmTransport.cs` is that isolated foundation. It performs only:
- Four-channel, interleaved 48kHz source PCM16 or Float32 → two-channel rear haptic PCM with **unchanged raw sample bits** (speaker channels 1/2 are never forwarded).
- Fixed-capacity RAM ring (20–500ms, user-selectable 20/40/120/250ms; 120ms remains the safer default when Windows sends larger WASAPI callback bursts). **Capacity is not a deliberate playback delay**: an event-driven null consumer drains received PCM immediately. Producer and reader share one short-held lock; no network, storage or Razer native calls.
- Deterministic overflow: retain latest complete stereo frames; **count discarded oldest frames**. Never silently substitute mono or a system-audio signal.
- Deterministic underrun: emit **digital silence for missing frames**, do not replay stale data or invent a synthetic effect. Count padded frames.
- Snapshot fields: offered/delivered/buffered/dropped/underrun frames, peak buffered milliseconds, **monotonic Stopwatch callback-arrival → reader-dequeue age (last/mean/max)**, oldest queued frame age, latest/max source callback gap and last source activity. These represent **software queue status**, not physical-actuator end-to-end latency.
- Reset on detach/stop/end of session, clearing retained PCM bytes and counters.

**Float32 note:** This transport retains raw IEEE sample bits even for NaN or other nonfinite payloads. It is a *source fidelity and offline diagnostic* stage; before connecting any future physical sink, that backend must validate/sanitize nonfinite values and apply hardware-safe output limits **to the Wolverine output only**, without changing the independently observable original DualSense source.

## How to run the transport probe

1. In **Native Stereo Bridge**, choose and approve a 4ch/48k UAC device.
2. Click **Yalnızca 4ch PCM kaynağını izle**. This source-only mode needs no HAR.dll, Razer provider, Synapse, or physical Wolverine.
3. **Before starting source-only**, choose an explicit WASAPI capture buffer request: **10ms (aggressive), 20ms (default), 50ms or 100ms**. Earlier `WasapiLoopbackCapture(MMDevice)` implicitly requested 100ms. RW3 now uses a NAudio 2.2.1 `WasapiCapture` subclass with Loopback and the `(device, useEventSync:false, bufferMilliseconds)` overload. Unsupported requests/errors are surfaced; the OS may round up or deliver differently. This is NOT `IAudioClient3.WithLowLatency()`, which is documented as unavailable for loopback.
4. Under **2C • 48kHz ORİJİNAL STEREO PCM TAŞIMA PROVASI**, choose transport **capacity** (20/40/120/250ms; default 120ms) then **Stereo PCM tampon testini başlat**. The smaller capacity does **not** speed up event delivery; it just limits possible accumulated backlog and increases the chance of dropping complete original frames on bursts larger than capacity. **No controllers are energized**, no WAV is saved, and USB DualSense physical playback is unchanged.
5. The independent **discard-only** consumer receives a source notification after each actual audio callback, waking without a 20ms periodic timer. It drains available stereo PCM in **240 frame = 5ms** chunks, immediately after scheduling allows. It never requests synthetic missing samples while no source callback is available, so source silence does **not** create misleading diagnostic-only underruns. The transport's `ReadStereo` itself still zero-pads on a genuine consumer request that lacks data.
6. The display shows total offered/read/dropped frames, queue fill/peak, **software callback-to-reader average/max age**, oldest queued frame age, current/max callback interval and age since last callback. None of these measurements include **the game, WASAPI engine buffering before callback, device drivers, Razer runtime or physical actuator motion**.
7. Stop via the same button or stop/close/switch the Native Bridge. The queue is detached and zeroed. WAV capture and transport probe cannot run concurrently. An explicit source endpoint change stops the active session.

## What can and cannot be claimed

| Observation | Allowed conclusion | NOT implied |
| --- | --- | --- |
| Full PCM16/Float32 source/consumer identity tests pass | Internal channel bytes are forwarded unchanged under this transport contract | Any physical Wolverine vibration |
| Source-only 4ch capture + ring counters increase | This Windows UAC endpoint sent accepted rear PCM frames into RW3 | Genuine authored DualSense gameplay PCM |
| Dropped/underrun frames remain zero in one test | This one software queue/consumer schedule had no observed ring loss | Source-to-actuator latency, motor response or zero drop in all games |
| A licensed Wwise Audio Input test accepts streams in the future | Potential for a *separately verified* legal runtime sink | Existing 50Hz HAR parametric backend became high fidelity |

The actual live stereo endpoint remains gated by [Issue #4](https://github.com/skonyd/rw3pronativedshaptics/issues/4): independently validate Razer/WYVRN or Wwise Audio Input → Motion Bus → Sensa in a correctly licensed setup and prove distinct left/right Wolverine motor motion. Source authenticity and virtual gamepad binding are [#2](https://github.com/skonyd/rw3pronativedshaptics/issues/2) and [#5](https://github.com/skonyd/rw3pronativedshaptics/issues/5). No vendor library/device driver bundled.

## Deterministic CI validation

Contract tests include: 48k ring frame/time budget, ten-scene stereo channel identity, fractional/burst reads and ring-wrap, oversize source truncation and buffered eviction, explicit zero-fill, no uncounted dropped frames, full reset, malformed stereo/interleaved input, buffer capacity cap, untouched IEEE Float32 payloads, concurrent producer/consumer stereo pairing, **synthetic monotonic 5/10ms timestamp measurements**, a source-notification driven 240-frame drain and **explicit low-latency WASAPI request validation**. Windows CI cannot assert that a vendor driver grants 10ms or that physical Sensa haptic playback is low latency.

Physical acceptance **not run**: 48k source-clock coherence versus a real hardware sample clock, Windows WASAPI callback scheduling and worst-case latency/jitter, source/game identity, real Sensa streaming, real device stereo separation and 10-minute performance measurement.

A green Windows runner only validates the source-only transport software. Do not label this as M4 completed.

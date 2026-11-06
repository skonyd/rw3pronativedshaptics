# Latency budget and low-latency source engineering (2026-11-06)

**Priority:** lower added delay **without dropping the original stereo HD haptic signal**. **Status:** source-side improvements implemented in the experimental branch; hardware-end-to-end validation and a supported realtime Razer waveform sink are outstanding. Parent [project stage M3 / M4 blocker](IMPLEMENTATION_STATUS.md).

## Latency is additive — separate the clocks

```text
Real game authors an original DualSense haptic packet
  -> game/audio scheduling delay (not currently measured)
  -> physical/virtual 4ch/48k render endpoint
  -> Windows WASAPI shared-engine & loopback capture (not measured)
  -> RW3 capture callback (high-resolution stopwatch begins HERE)
  -> rear ch3/4 raw PCM copy to bounded stereo ring
  -> event-driven reader wakes, drains available PCM in <=240-frame blocks
  -> RW3 software residence measured HERE (mean/max, oldest buffered)
  -> [NO RAZER SINK CONNECTED: discard diagnostic only]
  -> [future: legally supported live Razer PCM sink + driver + actuator]
```

Thus **10/20ms WASAPI request ≠ 10/20ms game-to-controller delay**. The current bridge that actually targets Razer (`SensaStereoPrototype`) is still a **LOSSY 50Hz parametric amplitude** conversion; this source-only optimized transport does not control physical voice coils.

## Changed capture strategy

The old source used NAudio 2.2.1 `new WasapiLoopbackCapture(device)`. The inherited base default requests **100 milliseconds** and uses polling. A configurable `ConfigurableWasapiLoopbackCapture` now subclasses the same NAudio `WasapiCapture`, requests loopback polling at an **explicit 10, 20, 50, or 100 ms buffer**, and leaves original PCM unchanged. UI default is **20ms**, 10ms is opt-in for low-latency experimentation. An unsupported device can reject a request; choose a larger supported value explicitly instead of silently claiming that 10ms was granted. Smaller requests cause more frequent callbacks/CPU activity.

**Crucial upstream limitation:** Audiokinetic and NAudio output contracts aside, **NAudio's `IAudioClient3` minimum engine-period `WithLowLatency()` capture is documented as *incompatible with loopback***. The shorter standard shared-loopback buffer is only a **request**, not a vendor/driver minimum period promise, and capture callbacks need real timing measurements on the target Windows machine. The legacy API remains so the entire app does not incur an untested large NAudio 3 migration. References:
- [NAudio WasapiCapture constructor and 100ms legacy default](https://github.com/naudio/NAudio/blob/main/src/NAudio.Wasapi/WasapiCapture.cs)
- [NAudio Recorder: `WithLowLatency()` cannot be used with loopback](https://naudio.github.io/NAudio/docs/WasapiRecorder.html)
- [NAudio loopback capture](https://github.com/naudio/NAudio/blob/main/src/NAudio.Wasapi/WasapiLoopbackCapture.cs)

## Changed internal transport strategy

Before: a `PeriodicTimer(20ms)` diagnostic reader requested 960 frames every wake, potentially waiting even when PCM had already arrived, and registered diagnostic underrun padding during silence despite not having a physical sink clock.

Now: `NativeDualSenseCapture.OnAudio` copies rear L/R PCM **byte-exact**, signals a coalesced `SemaphoreSlim` wake and a background worker immediately drains **available** data in **240-frame / 5ms** chunks. Every chunk contains the original samples, not a new 50Hz RMS value. It does not invent silence. In production `ReadStereo` still zero-fills an explicit sink request if data is absent, rather than repeat a stale haptic texture.

`RealtimeStereoPcmTransport.Snapshot()` exposes:
- `OfferedFrames`, `DeliveredFrames`, `DroppedFrames`, `BufferedFrames` — loss accounting
- `BufferedMs`, `PeakBufferedMs`, `OldestBufferedAgeMs` — buffered source time and actual queue age
- `MeanTransitMs`, `LastTransitMs`, `MaxTransitMs` — **callback-arrival-to-reader-dequeue** age, measured via monotonic `Stopwatch` timestamps per retained PCM frame
- `LastSourceGapMs`, `MaxSourceGapMs`, `SinceLastSourceMs` — callback gaps, NOT original game rendering period

The timestamp is the time the callback reached RW3, **not** the moment a game generated the sample. No presentation as end-to-end latency or physical output time.

## Sensible tuning order, with original waveform quality protected

1. Start with **20ms WASAPI requested buffer** and **120ms transport capacity**. This does *not* intentionally hold audio 120ms; it creates headroom for callback burst while the reader consumes it immediately.
2. Observe `LostOverrunMs` (must ideally remain zero) and source callback-gap jitter during at least ten minutes of actual game playback. If reliable, try **10ms capture request** and **40ms transport capacity**. Test 20ms transport only if actual callback bursts fit 20ms; otherwise it will discard original haptic samples.
3. A nominally smaller buffer that produces drops, high CPU, frame-time spikes, or callback skips is **worse** than a larger stable buffer. No automatic silent change to selected capture buffer or source PCM.
4. Measure software `MeanTransitMs`/`MaxTransitMs` **separately** from actual WASAPI capture lag and physical motor onset. Without a calibrated external sensor and supported waveform sink, source software age is only a partial number.
5. On real hardware, use repeatable source event timestamps, direct physical DualSense reference, an accelerometer/contact microphone or other suitable sensor on Wolverine (with synchronized clocks), and report p50/p95/p99 total haptic onset and drift. Avoid relying only on video/audio event timestamps or subjective impressions.

**Hard limit:** The current 50Hz Razer parametric path cannot preserve original 48k waveform spectral/transient detail, regardless of queue tuning. Completing the product requires an official licensed *live* stereo Razer output API and physical verification. See [Issue #4](https://github.com/skonyd/rw3pronativedshaptics/issues/4).

## Outstanding latency acceptance — NOT RUN

- [ ] Verify Windows driver accepts 10/20ms WASAPI shared loopback request with the real/virtual 4ch DualSense endpoint, and record **observed** callback durations and jitter; choose the smallest setting with no waveform drops.
- [ ] Profile end-to-end actual original game event → real physical Wolverine Sensa actuator alongside unchanged DualSense reference, separate capture-lag from vendor-driver/output latency.
- [ ] Verify stop/unplug clears all retained sample buffers and hardware vibration, without stale texture replay or additional tail time.
- [ ] Compare idle/10min CPU and demanding-game FPS, 1% lows, p95/p99 frame times while changing capture buffer, transport capacity and mode.
- [ ] Validate licensed Wwise/WYVRN live waveform sink (if supported) with its actual device clock, not the diagnostics null-consumer clock.

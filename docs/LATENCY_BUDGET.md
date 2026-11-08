# Latency budget and low-latency source engineering (2026-11-06)

## 2026-11-08 current output and33ms evidence

Historical50Hz/5ms descriptions below are superseded for current live Sensa output. Default and opt-in multiband paths currently request33ms render cadence. The official [Razer Sensa DevKit README, How It Works](https://github.com/WyvrnOfficial/RazerSensa_DevKit/blob/master/README.md#how-it-works) describes approximate33ms/30fps quantization **in the WYVRN event-service integration context**. Earlier local5ms/25ms experiments also reported queue growth/long tails. These support the current choice; they are not proof of an immutable controller hardware limit across all direct-provider versions or backends.

Current [multiband analysis](MULTIBAND_RESULTS_20261108.md) uses4096/48k=85.33ms source history, with Hann center42.67ms behind the newest sample. It does not wait another85ms for each callback; first-window availability and response smoothing differ from computational time. Warmed mono FFT median approximately.07ms. The diagnostic2048/42.67ms profile is faster but fails55/240 low-tone tests (30–40Hz);4096 passes240/240. It was not enabled as a low-latency production option. 33ms output timing, WASAPI requested10ms, Bluetooth heuristic delay and physical actuator delay are distinct. Current automatic Bluetooth setting may intentionally add300ms in audio mode; it does not apply to Native and was overridden to0 for explicit audio audit tests.

After the user's cadence question, twelve additional real microphone cases compared **30/33/36ms ×single/multiband ×two repeats**, reverse ordering on repeat2. Same80Hz/.35/3s original source, unchanged microphone processing, wired PC Wolverine; no source/audio failure or microphone clipping. In the microphone sample-clock3400–3700ms window (source drive nominally ends3200ms on its separate clock),30ms runs remained approximately9.44–15.76dB above their own background/idle reference;33ms runs .11–3.53dB,36ms −.52 to+.52dB. This pattern is consistent with greater tail/backlog at30ms and does not justify making it the default. It is **not calibrated input-to-motor latency or a measurement of exactly33ms device quantization**. One30ms single-band repeat had a weak/contaminated steady drive window (−.30dB) and is retained as unresolved, not silently discarded. Two repetitions and a processed airborne mic cannot establish a permanent minimum.

Artifacts: `TestResults/multitone-20261108/cadence/` and `tools/analyze-cadence-audit.py`. Reported EngineHz includes idle scheduling and must not be mistaken for active motor cadence. TickMs is restored after the bounded diagnostic. Existing33ms remains unchanged. Further gains should target a validated low/high-frequency estimator, measured source scheduling, Bluetooth sync configuration when appropriate, and a documented vendor transport/queue contract. Do not flood the existing provider or claim the USB1ms polling interval is1ms haptic output. Total real-game→physical-motor p50/p95/p99 remains unmeasured.

> **2026-11-07 scope update:** See [current implementation](IMPLEMENTATION_STATUS.md). Separate A/B, Test Lab and expert bridge windows and the route selector are removed. Any instructions below using those controls describe historical workflows; underlying fixture/evidence/WAV/recorder/transport libraries remain. Current Test has 23 synthetic Wolverine/DualSense demos; DualSense reference is Exclusive-only without Shared fallback. Current Sensa v2 targets 5ms amplitude/frequency/transient updates, remains lossy and does not prove game provenance or physical HD. Hardware acceptance requirements remain open unless explicitly recorded in [HARDWARE_TEST_LOG](HARDWARE_TEST_LOG.md).

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

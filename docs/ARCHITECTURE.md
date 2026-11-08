# Architecture — 2026-11-07

2026-11-08: [current virtual Native Xbox routing and audio separation](XBOX_NATIVE_ROUTING_20261108.md) supersedes the WGI audio-trigger route below. Native Xbox uses a single verified GameInput device for physical input plus four-motor output; Audio Xbox uses body-only XInput, without DualSense trigger interpretation. PC Direct USB/Hybrid/Multiband are already implemented; see [standalone status](STANDALONE_STATUS_20261108.md). Historical provider-only statements below are not the complete current inventory.

Read [implementation status](IMPLEMENTATION_STATUS.md) and [Sensa analysis](SENSA_PIPELINE_ANALYSIS.md). Native v2 is a lossy prototype; M3 open, M4 not implemented.

## Active paths

- Normal: game → ordinary controller rumble; RW3 does not observe game output.
- Audio PC: WASAPI front L/R (mono duplicated; no surround downmix) → sinc to 48k → software-volume compensation where applicable → bounded AudioSinkDelivery → SensaAudioSink (unity input, explicit clipping/nonfinite counters) → shared extractor/engine. Legacy 2.2 gain is diagnostic only; Wolverine output gain remains separate and can saturate envelope commands. Xbox mode selects XboxAudioSink (WGI body/trigger rumble); generic XInput remains separate. See SCENARIO_AUDIT_20261108.md.
- Native: compatible named 4ch/48k PCM16/Float32 render endpoint → WASAPI loopback (10ms requested) → rear channels 3/4 → `HapticFeatureExtractor` (1ms slots) → `SensaStereoPrototype` (current 33ms output pacing) → licensed HAR/Razer provider → Synapse → Wolverine PC-mode actuators.
- Synthetic demo: `NativeDemoSignals` → same Wolverine extractor/engine, or unchanged quad PCM16 → Exclusive WASAPI DualSense reference.

`NativeBridgeService` owns capture/engine. `MainWindow` owns mutually exclusive mode/test operation and auto-run/retry. There are three tabs; separate expert, Test Lab and A/B windows are removed. The old route catalog remains library code without a current UI selector.

## Output and evidence invariants

Experimental option (default off): original samples also enter a bounded4096-frame history. Consumer-thread FFT resolves up to three separated peaks; public AddHM/UpdateHM sends independent `.haps` melodies per side, with shared command headroom scaling and amplitude zero after66ms without renewal. This route has no carrier-phase command, separate transient replay or proof of motor spectra. It requires85.33ms history and stays at33ms provider cadence. Normal idle rendering avoids indexing a nonexistent `.haps` debug frame. Retained channel/endpoint lab routes remain single-band. See [multiband implementation and evidence](MULTIBAND_RESULTS_20261108.md).

v2 uses amplitude points, one dominant frequency and at most two transients per side. It targets Hand 302 Left/Right, 30–400Hz. It is not original 48k waveform replay. Higher bands require the vendor encoder; do not hand-pack frames. High-resolution waitable timer is the primary clock; the existing timer failure path remains a timing limitation requiring hardware validation.

Original DualSense reference PCM is never scaled, converted or mixed by RW3. `DualSenseAudioFormat.OpenReferenceStream` rejects unsupported Exclusive or malformed/empty quad PCM before playback. A busy/init failure propagates; no automatic Shared fallback. Physical bit-perfect output is not established by WASAPI success.

Rear PCM activity proves channel activity only. UI uses `NativeCaptureHealth.Describe` for the provenance limitation. Provider/render ticks and USB identities are separate from actual motor response. Subjective Hand L/R findings are in [hardware log](HARDWARE_TEST_LOG.md).

## Retained source-only libraries

`RealtimeStereoPcmTransport` copies original rear stereo PCM bits to a bounded ring (120ms default). The discard probe is event-woken, consumes 240-frame/5ms blocks and measures callback→reader residence and losses. It is not a physical sink. `AttachTransport` is restricted to source-only capture, no Sensa and no recording; detach/stop clears retained samples.

`HapticStereoRecorder` remains explicit opt-in, bounded to 10 seconds in RAM/wall time, separate save/discard. WAV copies rear sample bits without gain/resampling. Those APIs and evidence/fixture rules remain tested, but are not exposed in the current simplified UI. See [transport](REALTIME_PCM_TRANSPORT.md) and [latency](LATENCY_BUDGET.md) for library contracts.

## Channel conversion lab increment — 2026-11-07

Test card **DualSense → W3 Pro** adds seven channel/negative-control scenes plus the 23 demos, one canonical unmodified 4ch PCM source, the shared production decoder boundary, strict separate DualSense Exclusive reference and optional microphone level metering. Original-source SHA256 and software saturation/channel statistics are separate from acoustic observations. This is synthetic replay, not USB/WASAPI source capture or game-authored evidence; M3/M4 remain open. See [channel lab contract and procedure](CHANNEL_LAB.md).

## Physical USB endpoint / full loopback increment — 2026-11-07

After the user connected DualSense, Windows exposed a physical **4ch/48k Float32 quad** render endpoint. The lab now has a separate independent-process Shared source → real WASAPI → v2 → Wolverine route, with an 8-second bounded source-only sample-comparison pass before live Sensa. Controls verified exact active source samples at unity Windows level, rear-channel isolation and no rear activity for speaker-only/silence. The user confirmed vibration on Wolverine. This proves a **synthetic full Windows route**, not actual game authorship or DualSense-equivalent physical fidelity. The Exclusive reference is unchanged; no new driver was installed. Optional temporary source unmute/unity level is explicit and restored after the test. See [route details](CHANNEL_LAB.md) and the hardware log. M3/M4 remain open.

## Audio delivery safeguard — 2026-11-08

AudioSinkDelivery serializes sink writes, bounds retained stereo samples to 2s and stops explicitly on overflow/failure. An independent 250ms watchdog silences after the queue drains while preserving intentional sync delay. RecordingStopped clears capture health. Stop releases pooled PCM, endpoint notifications and the delivery worker, preventing later Accept calls. Sensa silence clears old features; Xbox silence resets filters/partial energy. This is software safety, not a measured physical stop deadline. [Audit](AUDIO_AUDIT_20261108.md).

# Status and performance evidence contract

## User question
"Haptic gerçekten çalışıyor mu; sesten mi, normal rumble'dan mı, yoksa oyunun özgün DualSense PCM verisinden mi?" UI MUST NEVER infer real HD haptics from selected mode, device detection or successful XInputSetState alone.

## Evidence levels

| Evidence level | Observable | What it proves | What it **does not** prove |
| --- | --- | --- | --- |
| Mode selected | App settings | Requested route | Capture/output active |
| XInput slot detected | XInputGetState return == 0 | Some XInput controller connected | That it is a Wolverine |
| Selected XInput slot (Auto or 0–3) | User-selected slot, persisted in settings | Motor test and Audio output target the requested slot; no silent fallback when explicit slot is absent | Physical identity of Razer; confirm manual pulse |
| XInput command accepted | XInputSetState == 0 | OS accepted vibration command | Motor physically moved; HD voice-coil playback |
| User confirms pulse | Voluntary Yes/No after 250ms test | User reports feeling vibration | Automatic proof of any waveform |
| WASAPI capture running | IsCaptureRunning | Default audio loopback started | Audible samples are present |
| Audio frame received | Atomic counter & timestamp | Software received decoded system audio frames | Original DualSense haptic PCM |
| Output command from audio accepted | XInput success counter | Conventional rumble written from sound data | Razer Sensa quality, native DualSense haptics |
| Experimental 4ch UAC rear PCM input | Selected endpoint's rear-channel frame and energy counters | Windows routed 4-channel audio to the chosen endpoint | Game provenance, or genuine authored haptics from a DualSense-aware game |
| Experimental Razer Sensa parametric provider | Provider initialized and render loop ticks | Native Interhaptics provider was invoked | Physical stereo motion or 48 kHz waveform fidelity |
| Real waveform fidelity | Bench/human test | Functionally accurate playback | Not achieved as of this document |

**Native fidelity status is BLOCKED.** The opt-in UAC experiment can capture 4-channel audio from a selected compatible render endpoint, but cannot establish that a PC game authored the signal. The 50 Hz parametric Razer path is lossy; no bit-perfect native waveform replay has been proven. No original-game provenance counters may be invented.

## Performance goals
- WASAPI callback: no UI calls, no allocations proportional to sample rate, no disk/network operations; at most 256 samples per buffer; XInput update ≤20Hz.
- Diagnostics: atomic counters on callback, 2s UI timer; log transitions/failures only, rotate at 1 MiB; never record audio or raw game controller data.
- CPU guard: normalize process CPU by processor count, budget <=3% whole-system; on >3% in 3 successive 2s samples in Audio mode, automatically stop capture & vibration and return Normal. This is a protective heuristic, not proof of no FPS loss.
- Audio mode sets process priority BelowNormal if permitted (restore when stopped). No background updates/download unless user chooses.
- Hard stop on mode change, app exit or capture failure; avoid leaving rumble active. No arbitrary hardware commands beyond documented XInput API.
- Resource panel shows process CPU%, working set MB, callback rate, last frame age and motor command acknowledgment.
- No claim that any mode is currently running from a previous session. Audio capture must be explicitly authorized each run.

## Test plan
- Test with no controller, disconnected controller, silence, actual sound, output switch mid-recording, device reconnection and XInput command failure.
- Test with Wolverine + DualSense + DSX+ virtual XInput simultaneously: explicit slot selection must never silently choose another slot. Manual 250 ms pulse confirmation remains required for physical identity.
- Assert no audio callback logs, no growing queue, counters are monotonic and stale data is shown as stale.
- Measure frame timing in Gears and Death Stranding 2 before/after enabling audio, including 1% low FPS; 3% process CPU threshold is only an upper guard, not a frame-time guarantee.
- Verify user-confirmed physical pulse after API acknowledgment; native mode remains disabled in actual path until stereo PCM receive + independent Sensa output + hardware validation.
- Inspect logs for errors without recording personal audio or game sound.

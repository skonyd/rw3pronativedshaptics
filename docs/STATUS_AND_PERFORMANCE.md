# Status and performance evidence contract

## User question
"Haptic gerçekten çalışıyor mu; sesten mi, normal rumble'dan mı, yoksa oyunun özgün DualSense PCM verisinden mi?" UI MUST NEVER infer real HD haptics from selected mode, device detection or successful XInputSetState alone.

## Evidence levels

| Evidence level | Observable | What it proves | What it **does not** prove |
| --- | --- | --- | --- |
| Mode selected | App settings | Requested route | Capture/output active |
| XInput slot detected | XInputGetState return == 0 | Some XInput controller connected | That it is a Wolverine |
| XInput command accepted | XInputSetState == 0 | OS accepted vibration command | Motor physically moved; HD voice-coil playback |
| User confirms pulse | Voluntary Yes/No after 250ms test | User reports feeling vibration | Automatic proof of any waveform |
| WASAPI capture running | IsCaptureRunning | Default audio loopback started | Audible samples are present |
| Audio frame received | Atomic counter & timestamp | Software received decoded system audio frames | Original DualSense haptic PCM |
| Output command from audio accepted | XInput success counter | Conventional rumble written from sound data | Razer Sensa quality, native DualSense haptics |
| Native DualSense PCM input | Future backend, exact stereo frame counter | Native authored game haptic stream intercepted | Wolverine rendered it |
| Razer Sensa stereo output | Future independently verified hardware backend | Separate left/right output accepted | Physical fidelity without measurements |
| Real waveform fidelity | Bench/human test | Functionally accurate playback | Not achieved as of this document |

**Native status is BLOCKED; frame count cannot be invented.** Current app does NOT read DualSense PCM, even if DSX+ is installed.

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
- Assert no audio callback logs, no growing queue, counters are monotonic and stale data is shown as stale.
- Measure frame timing in Gears and Death Stranding 2 before/after enabling audio, including 1% low FPS; 3% process CPU threshold is only an upper guard, not a frame-time guarantee.
- Verify user-confirmed physical pulse after API acknowledgment; native mode remains disabled in actual path until stereo PCM receive + independent Sensa output + hardware validation.
- Inspect logs for errors without recording personal audio or game sound.

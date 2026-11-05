# Architecture / boundaries

```text
                         WPF Windows 11 UI + tray + settings + update manager
                                       |
                           Exclusive Mode Coordinator
       +----------------------+------------------------+-----------------------+
       |                      |                        |
       v                      v                        v
 Normal Rumble          Audio-to-Haptics        Native DualSense Haptics
 XInput game-native     WASAPI loopback         Game-authored DualSense stereo PCM
 pass-through (default) --> DSP (envelopes)     --> Virtual endpoint/capture TBD
 voluntary XInput test  --> XInput rumble MVP   --> resampler/limiter/diagnostics
                              (NOT HD)           --> Razer Sensa PCM sink TBD
       |                      |                        |
       +----------------------+------------------------+
                              |
                     Explicit backend status
                      (no silent fallback)
```

## Core contracts envisioned
`IMode`: `StartAsync`, `StopAsync`, `Status`, `Capabilities`.
`IHapticOutput`: `PlayRumble(left,right)`, `PlayHapticFrame(leftPcm,rightPcm,sampleRate)`, `Stop`; capability-detect and throw on unsupported HD.
`IDualSenseCapture`: independent L/R PCM source with timestamp and test-trace output.
`IAudioSource`: opt-in WASAPI loopback and device selection.
`Settings`: app-start/tray preferences, last mode, gain/limiter, update choice (no telemetry).
`UpdateService`: GitHub Releases latest; trusted asset names and SHA256 hashes, explicit user install.

## First Windows UI implementation
- **WPF .NET 10** for dark rounded main window, **WinForms NotifyIcon** for long-lived tray menu.
- **XInput** P/Invoke for *optional conventional rumble test*.
- **NAudio WasapiLoopbackCapture** for a conventional audio-reactive rumble prototype, opt-in.
- Native mode **not enabled as an actual output path** until Razer HD interface is verified; show diagnostic blocker.
- Installer via Inno Setup and portable ZIP via Windows GitHub Actions.
- Portable `portable.flag` differentiates settings and update artifact; installed version stores config under LocalAppData.

## Native mode engineering hypotheses
A. Use official WYVRN event-to-.haps API to reproduce a mapped waveform approximation; investigate dynamic effect authoring latency, rate caps, stereo group addressing.
B. If documented supported low-level real-time PCM sink exists, stream original stereo PCM with proper rate and buffering.
C. If no native driver access, explicitly mark blocker; game-haptics→ordinary rumble is a separate degraded *compatibility experiment*, not native success.

## Protect against
- Two concurrent haptics sources and unwanted XInput overriding game's normal rumble.
- Feedback capture of microphone and privacy; only user opt-in system output.
- Unbounded amplitude, stuck motor, app crash; stop and reset on deactivation/exit.
- Concurrent virtual gamepads/DSX+ and Steam Input double input.
- Blind automatic updates/unsigned installers; verify SHA256, obtain explicit action.

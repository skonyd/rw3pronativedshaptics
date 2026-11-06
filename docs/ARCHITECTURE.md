# Architecture and verified data paths (2026-11-06)

For milestone readiness see [implementation status](IMPLEMENTATION_STATUS.md). The application has **several explicitly different paths**; none of the existing software's `HAR` parametric 50Hz path should be described as lossless original HD streaming.

```text
                           Windows 11 • .NET 10 WPF / tray / settings
                                                |
              +---------------------------------+-------------------------------+
              |                                 |                               |
       Normal XInput game                Opt-in system audio               Experimental native UAC
       leaves game rumble                 WASAPI loopback                  4ch / 48k PCM16/Float32
       alone; voluntary pulse             (ordinary PC sound)             ch1/2 speaker; ch3/4 haptic
              |                                 |                               |
         XInput game/OS               20Hz bounded audio envelope         +-----+----------------+
              |                                 |                        |                      |
       Conventional motors            Standard XInput rumble          Source-only           20ms L/R RMS
       (not Sensa HD)                  (not Sensa HD)                  monitor/recorder      → HAR parametric
                                                                       |                     Palm L/R events
                                           rear bytes unchanged         |                      |
                                           2ch PCM16/Float32 WAV         |                 Razer provider
                                           for offline studies           |                 (LOSSY, unverified)
                                                                       |                      |
                                            Authored provenance          |                 Wolverine VCM
                                            NOT established             |                 NOT physically proven
```

**Independent behavioral comparison path:**
- Ten deterministic `BehavioralHapticFixtures` produce signed PCM16 48k 4ch frames, front speaker channels silent and distinct rear stereo haptic channels.
- **Physical USB DualSense reference**: identical generated PCM bytes via `RawSourceWaveStream` → WASAPI (strict user-selected Exclusive/Shared). There is **no RW3 gain, EQ, speaker mixing or normalization**. Shared Windows/driver/actual actuator processing remains externally unverified.
- **Wolverine**: the same source passes through bounded 20ms stereo RMS estimates and two user-supplied vendor Interhaptics parametric effects, with independently configurable RW3/Wolverine output gains. This is **not** the original 48k waveform, and is not equivalent to physically verified stereo separation.
- A/B software attempts near-simultaneous starts but lacks common hardware clocks, and user ratings are subjective.
- PnP ✓ indicates present known USB VID/PID, not that a Razer wireless receiver is a cabled controller or that the actuator moved.

**Stereo WAV research path:** `HapticStereoWave` removes original speaker channels 1/2 and copies rear haptic 3/4 sample bits byte-for-byte into a stereo PCM16 or IEEE Float32 WAV. `HapticStereoRecorder` is an explicit opt-in source-only, at-most-10s RAM buffer; Save writes locally, Discard/Stop drops any unsaved content. These are **offline exports**, not a live Razer Sensa Motion sink. External Wwise Motion can research stereo WAV authoring when independently installed/licensed; Wwise Audio Input → Sensa Motion real-time compatibility is not proven.

## Components and boundaries

| Component | Responsibility | Limitation |
| --- | --- | --- |
| `MainWindow`, `SettingsStore`, `HapticRouteCatalog` | UI, persistent route/mode/level and readiness | Route selection is not evidence of driver/hardware setup |
| `XInputHaptics`, `AudioChannelTest` | Conventional motor / opt-in ordinary audio-to-XInput | Not native DualSense HD |
| `NativeDualSenseCapture`, `NativeCaptureHealth`, `FourChannelHapticAnalyzer` | Selected UAC rear-channel capture, telemetry and format validation | Cannot authenticate authoring game from endpoint |
| `SensaStereoPrototype` | Current two logical HAR/Interhaptics parametric effect targets | 50Hz envelope, native physical stereo unproven |
| `BehavioralComparisonPlayer`, `BehavioralHapticFixtures` | Synthetic DualSense/Wolverine A/B test | Not Sony game recordings; independent device latency |
| `UsbGamepadMonitor`, `UsbGamepadClassifier`, `BehavioralEvidenceRules` | USB identity, user-confirmed feel, physical evidence ratings | USB dongle ambiguity; no hardware sensor |
| `HapticStereoWave`, `HapticStereoRecorder` | Unchanged rear-PCM WAV export; RAM-only source capture | Not real-time Sensa PCM routing |
| GitHub Actions / installer | Build/test/smoke, ZIP/Setup/SHA256 | No physical controller/SDK and no driver-signing evidence |

## Not yet implemented / still hypothetical

- **Authentic game route and virtual pad:** user-installed, legally supported virtual DualSense composite that maps Wolverine input AND target game's 4ch UAC; PadForge/DSX+ alternatives not accepted merely from a device name.
- **Supported stereo hardware waveform sink:** no documented/validated arbitrary realtime DualSense PCM-to-Wolverine VCM streaming implementation. The present 50Hz HAR envelope is a *degraded* prototype.
- **Wwise real-time Audio Input → Motion → Razer Sensa:** research hypothesis requiring licenses, plugin compatibility and actual hardware proof; offline WAV Motion plugin does not imply that dynamic streaming works.
- **Physical reproduction / production readiness:** device L/R identity, frequency response, onset latency, frame-time effects and robust disconnect handling require actual hardware.

## Safety/UX contracts

- No background driver installation, firmware modification, unlicensed DLL distribution or hidden game injection.
- Normal Rumble should not seize game controls; source-only does not energize Wolverine.
- Physical DualSense reference PCM remains untouched by user-defined RW3/Wolverine gains.
- Explicit audio consent; only the user-requested WAV path can store a bounded source clip locally.
- Stop/exit, source/provider loss and unplug must zero current generated effects and avoid overlapping haptic sources; code has guards, physical behavior awaits test.
- Windows build checks require no controller: a passing CI result cannot promote the M3/M4 stages.

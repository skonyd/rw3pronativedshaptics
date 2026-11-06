# RW3 Pro Native DS Haptics

**Windows 11 desktop application + research project** to bring game-authored DualSense HD body haptics to Razer Wolverine V3 Pro while retaining three explicit modes.

> **Status: Windows tray application and packaging implementation; native DualSense PCM → physical Wolverine Sensa playback remains research-blocked.** See [PROJECT_BRIEF](docs/PROJECT_BRIEF.md) and [ROADMAP](docs/ROADMAP.md). Do not confuse the app's real conventional XInput motor test / audio-to-rumble with original HD haptic replay.

## Experimental DualSense → Sensa bridge (2026-11-05)

The app now has **an opt-in prototype** behind the **“Deneysel Native Stereo Bridge”** button. It enumerates Windows render devices, requires a **4-channel 48 kHz DualSense UAC** source, reads the haptic channels separately (3/4), converts them into two left/right parametric amplitude streams and calls the Razer Interhaptics engine/provider. This is **lossy** and does not preserve the original 48 kHz HD waveform. It is neither physically tested nor a proof that a game authored the incoming samples.

The separate **Haptic Test Lab** offers side-by-side XInput conventional motor tests, real 2-channel WASAPI sound→XInput tests and synthetic 48 kHz PCM fixture export. The **Native Bridge** window also offers direct left/right Interhaptics test pulses and genuine **4-channel UAC→Sensa end-to-end synthetic PCM playback**, independent of any game. A command accepted by the API is never called physical success unless the user confirms vibration, and that confirmation does not prove raw PCM fidelity.

See [Native Bridge Implementation](docs/NATIVE_BRIDGE_IMPLEMENTATION.md). **Vendor HAR.dll / Interhaptics.RazerProvider.dll are not bundled.** You need their legally obtained official SDK copies and working Synapse/Chroma on Windows x64. No GitHub Actions build or hardware run has yet been independently verified.

## Three modes
- **Normal Rumble** — the game handles its own native XInput vibration; app offers a voluntary quick motor test.
- **Audio-to-Haptics** — opt-in WASAPI system-output capture, basic audio envelope to *standard* XInput vibration. For true Razer Sensa audio processing, use Synapse 4.
- **Native DualSense Haptics** — research target: genuine stereo game-authored DualSense haptic PCM to two Wolverine body VCM actuators. UI displays blocked until a supported low-level hardware output path and game PCM capture are demonstrated.

## Run / build on Windows 11
Use the latest .NET 10 SDK:
```powershell
dotnet run --project src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj
```
On push to `main`, GitHub Actions builds a **self-contained portable ZIP** and an **Inno Setup installer** for Windows x64. On semver tag `v0.1.0`, both are published as GitHub Release assets with SHA256 manifests. See [WINDOWS_APP](docs/WINDOWS_APP.md).

## Application experience
- Modern Windows dark UI, three mode tiles and live capability/status text.
- Close (X) and minimize => notification area; tray context menu changes mode, checks updates, restores or quits.
- Short, explicit XInput rumble test; audio mode applies conservative amplitude and stops on exit.
- Startup checkbox; settings saved locally; no tracking or telemetry.
- Update manager checks GitHub Releases, downloads verified asset; installer opens on consent; portable ZIP requires user to replace files after quitting.

## Key references
- [Razer Wolverine V3 Pro](https://www.razer.com/console-controllers/razer-wolverine-v3-pro)
- [WYVRN/Razer Sensa DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit) — currently documented **events → hosted .haps**, not raw live PCM.
- [iFixit Wolverine V3 Pro actuator replacement](https://www.ifixit.com/Guide/Razer+Wolverine+V3+Pro+Voice-Coil+Actuators+Replacement/214388)
- [PadForge](https://github.com/hifihedgehog/PadForge) and [ApexSenseBridge](https://github.com/ReynArts/ApexSenseBridge) as research references (not bundled).
- [Full research sources](docs/RESEARCH_AND_REFERENCES.md)

## Source-of-truth diagnostics (added Nov 5, 2026)
The Windows status panel explicitly distinguishes (a) a chosen mode, (b) WASAPI system audio being received, (c) XInput API accepting a conventional vibration command, (d) manual confirmation of a physical test pulse, and (e) **real DualSense stereo PCM → Razer Sensa hardware playback**. (e) remains **unsupported and unverified**. "Native" selection cannot fake a success state.

The app now displays capture frames, accepted/rejected output commands, last failure, app CPU% and memory; includes a copyable status report and locally rotated error log (no captured audio is recorded). In audio mode it samples up to 256 frames/callback, issues at most 20 XInput writes/sec, runs BelowNormal priority and stops audio processing after three consecutive 2-second CPU samples over 3% total-system CPU. These safeguards **do not guarantee zero impact on game FPS**; use actual in-game A/B frame-time measurements to confirm.

Automated provenance contract checks: `dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj`. See [Status, evidence and performance](docs/STATUS_AND_PERFORMANCE.md) and [Validation plan](docs/VALIDATION_PLAN.md).

## AI handoff
Start at [AGENTS.md](AGENTS.md); it contains user intent, evidence standards and what is still unproven.

## Test readiness and CI

The `main` branch Windows workflow runs contract checks, a WPF Release build, a self-contained win-x64 publish, and a non-interactive `--self-test` of the published executable before packaging the ZIP and installer. Synthetic tests run without any controller; **physical Wolverine/Interhaptics behaviour still requires manual acceptance**.

See [Windows hardware acceptance runbook](docs/DEVICE_TEST_RUNBOOK.md) for detailed steps, including stopping motors on audio silence, real 4-channel endpoint isolation and game-signal provenance. The Audio-to-Haptics backend has an independent 250ms silence watchdog plus a 100ms polling timer, and the experimental Sensa engine will not launch a second worker while an older worker is shutting down. These are source safeguards; validate on actual hardware before treating them as production proven.

## Native input/output direction, November 2026

[ADR-002](docs/ADR-002-NATIVE-BRIDGE-ROUTE.md) documents the preferred path: reuse a *user-installed, compatible* virtual DualSense 4ch source (PadForge Full composite where independently verified; DSX+ as a separate optional experiment) rather than build a new Windows audio driver. Native audio-channel evidence is separate from actual game provenance. The Interhaptics output remains an **experimental, lossy** parametric adapter. The official Wwise/Sensa WAV authoring plugin is a promising **offline fidelity comparison**, not yet a reusable real-time PCM streaming interface.

The experimental source picker now displays channel count, sample rate and PCM resolution; unsupported formats are blocked before the provider starts. The live panel separately shows speaker RMS, rear haptic L/R RMS and peaks. New deterministic PCM16/Float32 channel tests and GitHub Actions least-privilege publishing checks are included. No device drivers or proprietary SDKs are bundled.

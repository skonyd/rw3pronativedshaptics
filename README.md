# RW3 Pro Native DS Haptics

**Windows 11 desktop application + research project** to bring game-authored DualSense HD body haptics to Razer Wolverine V3 Pro while retaining three explicit modes.

> **Status: Windows tray application and packaging implementation; native DualSense PCM → physical Wolverine Sensa playback remains research-blocked.** See [PROJECT_BRIEF](docs/PROJECT_BRIEF.md) and [ROADMAP](docs/ROADMAP.md). Do not confuse the app's real conventional XInput motor test / audio-to-rumble with original HD haptic replay.

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

# Windows app — build, install, update

**Technology:** .NET 10 WPF, WinForms NotifyIcon, NAudio WASAPI loopback, XInput 1.4 (Windows 11).
The app UI is functional independently of the Razer native PCM research blocker.

## Development
```powershell
dotnet restore src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj
dotnet run --project src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj
```
`dotnet publish ... -c Release -r win-x64 --self-contained true` produces runtime-bundled Windows output. Use Windows 11 or GitHub windows-latest runner; Linux cannot run WPF.

## Tray behaviour
Double-click executable => window. Close/X => hides to tray, process remains. Tray double-click => opens. Right-click => Open, Normal Rumble, Audio-to-Haptics, Native (experimental), Exit; update controls are in Ayarlar. Exit resets any active controller vibration.

## Capability statuses
- Normal: game handles its own native rumble; app can send short optional XInput test.
- Audio: WASAPI loopback drives **standard XInput motors**; do not confuse with first-party Sensa HD.
- Native: explicit opt-in experimental 4ch/48k UAC rear-channel capture to two **lossy v2 Interhaptics parametric outputs** (target 5ms amplitude/frequency/transient updates). Raw Razer PCM hardware sink is not implemented or physically verified; the UI reports that blocker instead of pretending to reproduce original game effects.

## Release workflow
Push to `main`: Windows build + upload artifacts portable ZIP, installer EXE, SHA256.
Create semver tag `v0.1.0`: same build and GitHub Release assets published by Actions.
Installer is user-scoped via Inno Setup; portable ZIP adds `portable.flag` and stores settings in `data/`.
App queries `https://api.github.com/repos/skonyd/rw3pronativedshaptics/releases/latest`. It downloads the matching artifact and corresponding `.sha256`, checks hash before allowing opening installer or ZIP location. Portable update is deliberately manual replacement of extracted folder; auto-self-replace is NOT provided in v0.1.0.

**Unsigned-build warning:** Without a code-signing certificate Windows SmartScreen may warn. Do not bypass SmartScreen or claim binaries are signed. Build script intentionally uses no secrets for local release checking; publishing GitHub Releases uses Actions token.

## CI and hardware acceptance boundary
Pull requests and pushes to main run the deterministic contract suite, Windows WPF build, self-contained x64 publish and published-executable `--self-test`. Then the portable ZIP and installer are built and smoke-tested (ZIP extraction + per-user install/uninstall). CI can run without DualSense, Wolverine, Synapse or licensed Interhaptics DLLs. Follow [device acceptance runbook](DEVICE_TEST_RUNBOOK.md) for required physical motor, source-provenance, game-impact and latency tests before claiming hardware-ready output.

## Current UI (2026-11-07)

One Genel/Test/Ayarlar window; selected/saved modes auto-run and retry missing prerequisites every 5 seconds. Test offers 23 synthetic demos for Wolverine or DualSense. DualSense reference is Exclusive PCM16 only with no automatic Shared fallback. Separate expert and A/B windows were removed; retained library tools are not exposed in the current UI. Native source activity never proves game provenance or physical HD.

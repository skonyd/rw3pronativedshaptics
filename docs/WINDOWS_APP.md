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
Double-click executable => window. Close/X => hides to tray, process remains. Tray double-click => opens. Right-click => Open, Normal Rumble, Audio-to-Haptics, Native (diagnostic-only), Check Updates, Exit. Exit resets any active controller vibration.

## Capability statuses
- Normal: game handles its own native rumble; app can send short optional XInput test.
- Audio: WASAPI loopback drives **standard XInput motors**; do not confuse with first-party Sensa HD.
- Native: **Razer arbitrary PCM hardware sink not implemented**. Selecting native reveals the blocker, does not fake effects.

## Release workflow
Push to `main`: Windows build + upload artifacts portable ZIP, installer EXE, SHA256.
Create semver tag `v0.1.0`: same build and GitHub Release assets published by Actions.
Installer is user-scoped via Inno Setup; portable ZIP adds `portable.flag` and stores settings in `data/`.
App queries `https://api.github.com/repos/skonyd/rw3pronativedshaptics/releases/latest`. It downloads the matching artifact and corresponding `.sha256`, checks hash before allowing opening installer or ZIP location. Portable update is deliberately manual replacement of extracted folder; auto-self-replace is NOT provided in v0.1.0.

**Unsigned-build warning:** Without a code-signing certificate Windows SmartScreen may warn. Do not bypass SmartScreen or claim binaries are signed. Build script intentionally uses no secrets for local release checking; publishing GitHub Releases uses Actions token.

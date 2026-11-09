# Current status (single source of truth)

Updated 2026-11-09. This page replaces reading the dated reports in order; they stay as history
(see [docs/README.md](README.md)). When something changes, update this page in the same commit.

## What the app does

| Mode (main window) | Source | Wolverine in PC mode (1532:0A4C) | Wolverine in Xbox mode (1532:0A3F) |
| --- | --- | --- | --- |
| Normal | game's own Xbox rumble | game → body motors | game → body + trigger motors |
| Sesten titreşim | PC audio (WASAPI loopback) | Sensa palms, stereo | body motors, stereo (left motor ← left channel) |
| Oyunun DualSense titreşimi | game's DualSense output via the virtual DualSense (HIDMaestro) or a USB DualSense | haptic PCM → Sensa palms; trigger effects → body motors | haptic PCM → body motors; trigger effects → impulse-trigger motors (GameInput) |

- **Sensa without Synapse:** own frame encoder + HID feature report 4 ("Direct USB"), over the cable and the HyperSpeed dongle.
  Synapse/HAR remains an optional legacy backend, used only when Synapse runs and has attached the controller.
- **Virtual DualSense bridge:** needs administrator rights. The game sees a DualSense: buttons/sticks from the Wolverine (XInput in PC mode, GameInput in Xbox mode, live readback check), haptic PCM in, trigger reports in.
  HidHide hides the Wolverine from games while it runs and is restored on stop.
- **Controller mode:** PC → Xbox can be switched from the app (Synapse's command, replayed). Xbox → PC only with ○ + Menü + A (2 s).
- **Trigger motors** exist only in Xbox mode, and work while the game is in front only through GameInput (WGI vibrates only for the foreground window).

## Everyday use

- Start RW3 from the Start menu / shortcut, or `explorer.exe "<path>\RW3ProNativeDSHaptics.exe"`. A start from a packaged app (Claude/Codex) relaunches itself so settings and log stay in one place (`PackageContext`).
- One-time: Ayarlar → Sanal DualSense → "UAC'siz başlatma: Etkinleştir". After that the bridge starts without a UAC prompt (scheduled task, normal priority).
- Testing without a game: Test → "Oyun simülasyonu" sends game-like haptic PCM + trigger reports and reports each stage (bridge, Sensa / Xbox motors, mic).
- Settings and log: `%LOCALAPPDATA%\RW3ProNativeDSHaptics\` (`settings.json`, `logs\rw3.log`).

## Verified on hardware (webcam mic, medians)

- Direct USB = Synapse path within ~1 dB.
- Hybrid places mixed 80+190 Hz correctly.
- Dongle works (Direct USB).
- Xbox-mode trigger motors: GameInput from the background +27/+19 dB; WGI in the background 0 dB.
- PC mode trigger motors: unreachable (XUSB).
- Virtual DualSense input matched the Xbox pad in every readback report.

## Open / not verified

- Xbox-mode trigger effects in the bridge after the GameInput input fix: software-checked, not yet felt by the user.
- Xbox audio stereo fix (0346115): unit-tested, not yet felt by the user.
- Transient record (last 3 bits) not decoded: hits are folded into strength points.
- Spectral history 85 ms (Hybrid/Multiband); shorter validated path still to do.
- `MainWindow.xaml.cs` (~1,900 lines) still holds mode orchestration; extracting it is the next structural step.
- YouTube seek recovery (audio mode) unresolved; see the dated reports.

## Code map

- `src/RW3ProNativeDSHaptics/`: the app.
- `src/RW3Pro.Lab/`: audit/measurement code used only by tests and the hardware probe.
- `tests/RW3Pro.ContractTests/`: device-free checks; every check runs and failures are listed at the end.
- `tests/RW3Pro.HardwareProbe/`: hardware measurements (mic, USB, Sensa frames).

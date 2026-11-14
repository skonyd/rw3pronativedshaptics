# Handoff: where the work stands (for any AI tool)

Updated 2026-11-14. Start here when you pick the project up in a new tool (Claude, Codex, Gemini/Antigravity,
Cursor…). This page says **what was done, where it lives, what phase we are in and what comes next**. Rules stay
in [AGENTS.md](../AGENTS.md); behaviour facts in [STATUS.md](STATUS.md); every test and its result in
[research/EXPERIMENTS.md](research/EXPERIMENTS.md). Update this page when the phase changes.

## 1. Read in this order

1. [STATUS.md](STATUS.md): what the app does today, what is verified on hardware, what is open.
2. This page, sections 3–5.
3. [research/README.md](research/README.md) and [research/EXPERIMENTS.md](research/EXPERIMENTS.md): search before
   any hardware test. Do not repeat a finished experiment without a new reason.
4. [research/OPEN_QUESTIONS.md](research/OPEN_QUESTIONS.md): the open questions with their next step.
5. Only for the topic you work on: [SENSA_USB_PROTOCOL.md](SENSA_USB_PROTOCOL.md) (frame layouts),
   [research/XBOX_MODE_HAPTICS_ROUTE.md](research/XBOX_MODE_HAPTICS_ROUTE.md) (Xbox-mode route),
   [firmware/README.md](firmware/README.md) (firmware analysis).

## 2. Environment (this machine)

| Item | Value |
| --- | --- |
| OS / shell | Windows 11, PowerShell 5.1 and Git Bash |
| .NET | **use `C:\Program Files\dotnet\dotnet.exe`** (x64, .NET 10 SDK). `dotnet` on PATH may resolve to the x86 host without an SDK ("SDK not found"). |
| Repo | `C:\Users\fakir\Desktop\antigravitiy\rw3pronativedshaptics`, branch `main`, remote https://github.com/skonyd/rw3pronativedshaptics |
| Controller | Razer Wolverine V3 Pro. PC mode = USB 1532:0A4C (XUSB + Razer HID). Xbox mode = 1532:0A3F (GIP only). |
| Installed app | `%LOCALAPPDATA%\Programs\RW3ProNativeDSHaptics\`, Start menu "RW3 Pro Haptics" |
| App data | `%LOCALAPPDATA%\RW3ProNativeDSHaptics\` (`settings.json`, `logs\rw3.log`, `calibration\`) |
| Mic sensor | webcam mic held next to the pad (see `tests/RW3Pro.HardwareProbe`) |

### Build, test, publish, install

```powershell
$d = "C:\Program Files\dotnet\dotnet.exe"
& $d build src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj -c Release -warnaserror
& $d run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj -c Release   # must end "N passed, 0 failed"
& $d publish src/RW3ProNativeDSHaptics -c Release -r win-x64 --self-contained false -o TestResults/install-publish-20261109
explorer.exe TestResults\install-when-closed.cmd    # waits for RW3 to close, copies, self-tests, relaunches
```

- **Launch and install through `explorer.exe`.** A process started by an AI tool (packaged app) has its
  `%LOCALAPPDATA%` redirected into the package store, so settings/logs would land in the wrong place. The app
  relaunches itself when it detects this (`PackageContext`), but installs must go through explorer.
- The install result is written to `TestResults/install-result-latest.txt`.
- `TestResults/` is local working data, not committed.

### Commits

- Author: the repo owner. No global git identity is set on this machine, so pass it per commit
  (`git -c user.name=... -c user.email=... commit`), copying name and email from `git log -1 --format='%an <%ae>'`.
  Each AI adds its own `Co-Authored-By` line.
- PowerShell 5.1 mangles double quotes inside `-m` arguments: write the message to a file and use `git commit -F`.
- Commit only what the user asked for; update `docs/STATUS.md` in the same commit when behaviour changes.
- Never commit Razer/Sony binaries or firmware images; only our maps, notes and tools.

## 3. What has been done (chronological, newest last)

| Period | Result | Where |
| --- | --- | --- |
| 2026-11-05..08 | Windows app (WPF, tray, 3 modes), audio→haptics, virtual DualSense bridge (HIDMaestro), contract suite | README, STATUS |
| 2026-11-08..10 | **Sensa without Synapse**: own frame encoder + HID feature report 4 ("Direct USB"), cable and dongle; multiband/hybrid spectral path; mic-measured | STATUS, SENSA_USB_PROTOCOL |
| 2026-11-10 | Trigger motors unreachable in PC mode (XInput/WGI/GameInput all ruled out); channel map of vibration strength command 0C/06 | STATUS, EXPERIMENTS E-20261110-* |
| 2026-11-10 | Firmware 2.04.00 read from Razer's updater (not run): 2 Sensa descriptors, PC 2-part / Xbox 4-part | FIRMWARE_ANALYSIS_20261110 |
| 2026-11-11 | **Xbox-mode Sensa route found**: Razer packet in GIP 0x0F via `Windows.Gaming.Input.Custom`; 4-body-part frame decoded by emulating the firmware decoder; blind hand test: 216/116 palms, 249/149 trigger motors | research/XBOX_MODE_HAPTICS_ROUTE, E-20261111-01..04 |
| 2026-11-11 | In the app for **every mode** (setting "Xbox modunda Sensa", default on): game DualSense haptics → palms, trigger effects → trigger motors (Sensa + GameInput together), audio mode, demos, motor test | commits 977b181, 4d446e2, 1125bd5 |
| 2026-11-11 | Trigger tuning: strength floor 16/63, 160 ms clicks, resistance 85 %, weapon/bow ramp 40 % → 100 % to the break | STATUS, E-20261111-06/07 |
| 2026-11-11 | PC transient record fixed (flag + s6 + p7 + t7 + next flag; old t8 + "tail 100" shifted the right hand) | SENSA_USB_PROTOCOL, E-20261111-02 |
| 2026-11-11 | **Blind general calibration test** (Test tab): shuffled locate + "1 or 2" pairs, controller-answered, JSONL + summary | `CalibrationPlan.cs`, STATUS |
| 2026-11-11 | Console audio → Sensa: the controller does not turn headset audio into haptics; Sensa HD state 00/08/00/88 decoded. Only software route on a console: Xbox Remote Play on the PC + RW3 | E-20261111-08..10 |
| 2026-11-11..13 | **Firmware analysis (read-only)**: images are checksummed, not signed; bootloader 1532:110E, erase range; NXP LPC54605 byte map (87 % code coverage), vectors, RAM layout, 104-command Razer catalog emulated, Sensa drive (SCT PWM 40 kHz), I2S audio path | docs/firmware/, tools/firmware/, research/CUSTOM_FIRMWARE_PLAN.md |
| 2026-11-14 | Calibration: 4th answer **"Orta / yerini ayıramadım"** (Y) on every locate trial; replay moved to View (⧉); summary counts middle answers per actuator. Reason: the user felt ~3 trials "from the middle" with no answer for it | `CalibrationPlan.cs`, `MainWindow.xaml(.cs)` |

| 2026-11-14 | Audio mode **"Standart (DSX gibi)"**, default on: audio as it is + soft noise gate; auto level / hum suppression only when it is off. Reason: "vibrated all the time" in a game | `AudioLevelShaper.cs`, STATUS |

## 4. Current phase

**Phase: tuning by feel, Xbox mode first.** The haptic routes are complete in software; what remains is felt
quality and a few unverified paths.

Next steps, in order:

1. **Run the general calibration again with the "Orta" option** (Test → "Genel kalibrasyon testi", Xbox mode),
   then compare with the earlier runs in `%LOCALAPPDATA%\RW3ProNativeDSHaptics\calibration\`. Use the ORTA line
   and the per-actuator `orta ×N` counts: a mix of two actuators answered "Orta" often means the two sides blend,
   not that the user guessed wrong.
2. Feel audio mode "Standart (DSX gibi)" in a game. If quiet scenes still vibrate, raise
   `AudioLevelShaper.GateThreshold`; if soft effects are lost, lower it.
3. Feel the Xbox-mode bridge with a real DualSense game (trigger effects + palms) — STATUS "Open / not verified".
4. Repeat the transient-record mic A/B with the fixed encoder (OPEN_QUESTIONS Q4).
5. Raw pitch field of 249/149 on the trigger motors (OPEN_QUESTIONS Q2, mic held close).
6. Code health: split `MainWindow.xaml.cs` (test/simulator page, settings page).

### Firmware track: status and limits

- The **analysis** (maps, catalog, emulation of the stock firmware's command handlers) is done and pushed.
- **No firmware has been modified, built or flashed.** Claude declined to write modified firmware, firmware
  patches or a harness to test a self-made firmware; that work was stopped. The project rule in AGENTS.md still
  holds: no firmware patching or flashing without the user's explicit consent and a tested rollback, and the
  rollback analysis (research/CUSTOM_FIRMWARE_PLAN.md, phase G0) found **no software recovery once the
  application area is broken**.
- Any tool continuing here must read CUSTOM_FIRMWARE_PLAN.md first and treat flashing as a hard stop that needs
  the user's own decision.

## 5. Hard rules that bit us before

- Do not send blind/guessed GIP or Razer vendor commands to the controller. Replay only values Razer's own
  software sent (captured), or values the stock firmware's handlers accept in emulation **and** that are
  read-only.
- Sensa frames ≥ 11 ms apart (the firmware drops faster ones). Send silence on stop.
- GameInput output drops while another gamepad app (e.g. a browser) is in the foreground — not a bug.
- Trigger motors exist only in Xbox mode.
- Hardware results need the user's feel or a mic measurement; telemetry alone is not proof.
- Record every hardware test or analysis in research/EXPERIMENTS.md (format in research/README.md).

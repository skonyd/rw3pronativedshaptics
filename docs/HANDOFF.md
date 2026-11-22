# Handoff: where the work stands (for any AI tool)

Updated 2026-11-22. Start here when you pick the project up in a new tool (Claude, Codex, Gemini/Antigravity,
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
- Concurrent tools share the Git index in this checkout. Commit an explicit
  path list with `git commit --only ... -- <paths>` so another tool's staged
  files cannot enter your commit. Do not reset or discard their staged work.
- Never commit Razer/Sony binaries or firmware images; only our maps, notes and tools.

## 3. What has been done (chronological, newest last)

Firmware continuation: the offline suite now includes controller I²S
voice input → stock encoder → RF reply → stock dongle IRQ → audio timer
→ decoder/resampler → USB endpoint dispatcher and IN FIFO. It checks
lane isolation, ring wrap, missing input, duplicate response, recovery,
underrun and sequence wrap skipping zero. A separate parser checks
all 6,272 recorded IN packets and nine damaged-capture controls.
Run [the suite](firmware/README.md#run-the-offline-replay-suite) from the
local pinned images/captures; reports stay under ignored `TestResults/`.
The [public validation summary](firmware/validation/20261121-voice.json)
records the 12/12 run, input hashes, negative controls and scope limits.
See E-20261121-02/03/05. Next: actual RF synchronization and scheduling,
physical inter-chip I²S routing and whole-board GPIO/interrupt behavior.
Full-system equivalence and a flashable candidate remain unverified;
resident bootloader and recovery are still unavailable. No flashing.

Startup continuation (E-20261121-06): `replay_nxp_startup.py` follows
the stock I²C command registers through nine FLEXCOMM3 configuration
writes and a first FLEXCOMM8 four-byte read. Twelve stock/candidate
scenario pairs cover identity boundaries, NACK, missing/delayed
interrupts and timeout/retry; two incomplete-peer controls fail clearly.
The second peer is opaque and its replies are explicit synthetic data.
Next identify that peer/status protocol and extend the manually
scheduled, enable/PRIMASK-gated vector calls to exception delivery.
The [latest public summary](firmware/validation/20261121-startup.json)
records the 13/13 full-suite run (E-20261121-07), including system-control
and NVIC write/mask comparison. The earlier 12-stage report above is history.

IRQ continuation (E-20261121-08): [CORTEX_M_IRQ.md](firmware/CORTEX_M_IRQ.md)
documents `cortex_m_irq.py` and `replay_nxp_exceptions.py`. This path uses
memory-backed basic MSP/PSP frames and original EXC_RETURN instead of
whole-context restore. It checks masks, nested priority and pending
deferral, then runs the same 12 stock/candidate startup pairs. Keep the
direct-call regression. The bounded FP extension (E-20261122-03) in
`cortex_m_fpu.py` / `replay_nxp_fpu.py` now covers eager/lazy frames,
first-use FPSCR defaults and nested preservation. Original Sensa timer
vectors produce the same stock RAM and duty/depth trace in six cases,
with 792 balanced entries/returns and core/FP register checks.
The shared NXP audio Replay and core fixtures explicitly select M4 and
assert it; Unicorn 2.1.4 silently forces M33 when MCLASS mode is used.
See the IRQ report for translation-restart and SPSEL ordering fixes.
The [FP validation summary](firmware/validation/20261122-fpu.json)
records the earlier 15/15 stages, the prior 22 basic checks
and eight guards plus 16 FP checks and 12 FP guards. The 12 startup
pairs still have 2,006 balanced entries/returns.
The next DMA increment is documented in
[DMA_DSP_PREEMPTION.md](firmware/DMA_DSP_PREEMPTION.md): the original
DMA0/I²S/TX callback and candidate tap can interrupt the compiled worker;
the original Sensa timers nest inside that IRQ. Comparisons use real
stack restoration, with register snapshots only as assertions. The DMA
status model handles enable aliases, W1C and a level source. INTENCLR
readback is explicitly supplied because stock reads that write-only
register; its physical value remains unknown. Moving candidate Sensa
construction/publication from the harness into compiled code is now
implemented in the separate path below.
System exceptions, general FPU-control changes, actual second-peer
replies and physical event timing remain open; do not replace them
with invented success values. Earlier
12/13/14-stage summaries remain historical evidence.
The [DMA full-suite summary](firmware/validation/20261122-dma-preemption.json)
records 16/16 stages (E-20261122-07). New DMA coverage has seven register
checks, six continuous stock/candidate cases and 12 worker cases, with
6,056 checked returns in those main scenarios. Stack corruption and an
uncleared IRQ level fail deliberately. Do not claim lossless haptic
input: the one-slot tap skips a new copy while busy. INTENCLR setup
fixtures change channel 14's enable even when the covered TX path passes.

Compiled publication (E-20261122-20/21): read
[COMPILED_SENSA_PUBLICATION.md](firmware/COMPILED_SENSA_PUBLICATION.md).
New C/assembly owns packing, staging, protected copy/stock decode and
accepted-game priority in an opt-in simulator path. Game ownership is
latched until explicit rearm with fresh PCM. The original command handler
still handles the synthetic command; a fixture IRQ shim is not an actual
USB/game input. Full-buffer oracles retain the original short-copy tail.
Default cadence and source timeout are 12/250 stock-counter ticks; busy
can defer stops. A stale-before-commit check fixes a race found while
testing. Strict shared C types/state-size assertions build cleanly, and
three separate output/cache directories produce identical module bytes.
The [publication validation](firmware/validation/20261122-publication.json)
records the preceding 17 stages with module SHA256 starting `f2c5f153`, 8,464 encoder
cases and three compiled continuous streams (synthetic/wired/dongle).
The old host-built routes stay as references. Module text is mapped in
an 8 KiB simulator region; neither that nor private RAM is a validated
device allocation. The clock increment below covers the original counter;
full foreground scheduling, masked worst-case timing, actual runtime controls
and non-Sensa GIP rumble/trigger arbitration remain open. No firmware was flashed.

Clock continuation (E-20261122-30): read
[STOCK_CLOCK_REPLAY.md](firmware/STOCK_CLOCK_REPLAY.md).
`replay_nxp_clock.py` now runs original MRT initialization and IRQ9 to
advance the publisher's counter, sharing a supplied 96 MHz virtual
schedule with stock-initialized CTIMER2/3 and DMA completions. The new
models cover bounded repeat/one-shot/W1C behavior and reject unmodeled
modes/reconfiguration. Four continuous streams preserve PCM, render
palms without triggers and stop. Each steady case checks 2,226 exception
returns; the pause case checks 2,562. A supplied 2 ms masked-copy delay
coalesces fifteen MRT events, reaching source counter 250 at 252 ms
virtual time. This is a modeled safety limitation, not measured stock
execution time. Do not repair it with host counter catch-up. Next close
full foreground scheduling, investigate an independent time source and
measure masked worst-case timing; busy decoding remains another stop
delay. The compiled module bytes are unchanged (`f2c5f153...`).
The [latest full validation](firmware/validation/20261122-clock.json)
passes all 18 stages (E-20261122-31). Four active clock flows check
9,240 returns, plus separate silent references. Activity and final
eight snapshots match the appropriate active/idle criteria; silent,
missing-left and stuck-tail corruptions are rejected. Startup PWM
120 is not the idle oracle: the silent streams end at 288/288/0/0.

Build caveat (E-20261122-05): the firmware commit `b14f072` passed the
local 15-stage firmware suite, but Windows CI run `44956123630` fails
WPF compilation at the existing `MainWindow.xaml.cs` reference to the
missing committed `ChromaEffect` type. Its 573 contract checks pass.
The same failure already appears on parent `04f9ab3` (run `44952555468`).
The local working tree builds and has 575 passing contracts because it
also contains separate, uncommitted RGB/shortcut work. Do not report
that working-tree result as a clean build of published main. Preserve
those other files and finish their own review/commit separately.

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
| 2026-11-14 | **Main page redesign.** Audio mode: level slider (10–300 %, palms and body motors). Native mode: palm haptic source **Game HD / Oyun sesinden / Kapalı** + level slider; "Oyun sesinden" keeps DualSense triggers and rumble while the palms follow PC sound. Native now starts with any source (before: only with HD PCM on). Removed: feedback preset combo, duplicate impulse checkbox, "Sesten gövde titreşimi" checkbox, Settings gain/sensitivity presets | `MainWindow.xaml(.cs)`, `NativeBridgeService.cs`, `HapticModePolicy.cs`, STATUS |

| 2026-11-15 | Xbox-mode palms for GDK games: strict virtual Xbox pad (GameInputSvc never allowed, pad read via XInput, triggers via Sensa frame). Banishers ignored the Series pad's input, but it ignores a real DualSense too (E-20261121-01), so that says nothing about the profile; input reaches GameInput (E-20261115-04). Fixed on the way: Xbox-mode engine watchdog, mode race, replug ending the bridge, CPU guard stopping it, no retry | E-20261115-01/02, `XboxAudioGameBridge.cs` |
| 2026-11-21 | **Test game "Şişe Atışı"** (`src/RW3TestRange`): drives pads like real games (XInput, GameInput + trigger motors, DualSense HID adaptive triggers / light / LEDs, HD haptics on the 4-ch endpoint, music + SFX), button test, 22-step guided feedback test, session log + summary with RW3's log lines | [TEST_RANGE.md](TEST_RANGE.md) |
| 2026-11-15 | **Modes replaced by "Oyuna görünen kol" selector + feature matrix** (Kolun kendisi / Xbox 360 / Xbox Series / PS4 / DualSense) | `GameOutput.cs`, STATUS "What the app does" |

## 4. Current phase

**Phase: tuning by feel, Xbox mode first.** The haptic routes are complete in software; what remains is felt
quality and a few unverified paths.

Next steps, in order:

1. **Run the general calibration again with the "Orta" option** (Test → "Genel kalibrasyon testi", Xbox mode),
   then compare with the earlier runs in `%LOCALAPPDATA%\RW3ProNativeDSHaptics\calibration\`. Use the ORTA line
   and the per-actuator `orta ×N` counts: a mix of two actuators answered "Orta" often means the two sides blend,
   not that the user guessed wrong.
2. Feel audio mode "Standart (DSX gibi)" in a game. If quiet scenes still vibrate, raise
   `AudioLevelShaper.GateThreshold`; if soft effects are lost, lower it. Same for native mode with the haptic
   source "Oyun sesinden" in a game that has DualSense triggers but no HD haptics.
3. Feel the Xbox-mode bridge with a real DualSense game (trigger effects + palms) — STATUS "Open / not verified".
4. Repeat the transient-record mic A/B with the fixed encoder (OPEN_QUESTIONS Q4).
5. Raw pitch field of 249/149 on the trigger motors (OPEN_QUESTIONS Q2, mic held close).
6. Code health: split `MainWindow.xaml.cs` (test/simulator page, settings page).

### Firmware track: status and limits

- The **analysis** (maps, catalog, emulation of the stock firmware's command handlers) is done and pushed.
- An original Cortex-M4 TX tap and DSP worker run alongside the stock USB,
  DMA, timer, Sensa and PWM routines in the offline
  [audio replay](firmware/AUDIO_HAPTICS_REPLAY.md). The reset-vector replay
  also covers stock scatter load and startup peripheral timeouts.
  Continue with external transaction responses, source ownership
  and image placement checks in the
  [custom firmware plan](research/CUSTOM_FIRMWARE_PLAN.md).
  The [B91 map](firmware/TELINK_B91.md) includes executable startup
  relocation for both images, stock RF playout and retry, the controller
  SPI transaction and I²S clock initialization. The NXP analog replay
  reaches the trigger fields in the GIP input queue. The
  [offline suite](firmware/README.md#run-the-offline-replay-suite)
  runs these stages alongside stock/DSP comparisons from the local USB
  captures. The board's I²S wiring and recovery path remain open.
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

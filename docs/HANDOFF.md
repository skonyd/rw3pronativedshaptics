# Handoff: where the work stands (for any AI tool)

Updated 2026-11-28. Start here when you pick the project up in a new tool (Claude, Codex, Gemini/Antigravity,
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
The [clock-stage validation](firmware/validation/20261122-clock.json)
passes all 18 stages (E-20261122-31). Four active clock flows check
9,240 returns, plus separate silent references. Activity and final
eight snapshots match the appropriate active/idle criteria; silent,
missing-left and stuck-tail corruptions are rejected. Startup PWM
120 is not the idle oracle: the silent streams end at 288/288/0/0.

Watchdog/loop-header continuation (E-20261122-40): read
[WATCHDOG_AND_LOOP_GATE.md](firmware/WATCHDOG_AND_LOOP_GATE.md).
The original header feeds WWDT and chooses changed/unchanged counter
branches in 34 calls; the test stops before the tick tasks/background
USB path. The specialized clock fixture supplies reset WDTOSCCTRL 0x140
and runs original setup at nominal 800 kHz (TC 400000, warning 512).
Blocked-source cases request reset without executing recovery. The
original warning ISR can itself keep WWDT fed when IRQ0 is enabled,
despite stalled foreground/audio ownership. That condition is supplied;
the real board IRQ0 setup is not yet traced. Do not claim the hardware
watchdog fixes the independent 250 ms audio-stop gap. APB synchronization,
instruction time, the logical inclusive zero-edge convention and actual
reset/actuator recovery remain unverified. The C module is unchanged.
The [watchdog full validation](firmware/validation/20261122-watchdog.json)
passes all 19 stages (E-20261122-41). The complete new stage also passes
under optimized Python with identical output; attempted core resume
after reset is rejected. The preceding clock report is unchanged.

Foreground/GPIO continuation (E-20261122-60): read
[FOREGROUND_LOOP_REPLAY.md](firmware/FOREGROUND_LOOP_REPLAY.md).
`replay_nxp_loop.py` executes a complete original iteration and its
Sensa call in one explicit raw-gate fixture; the host no longer invokes
that dispatcher directly. Byte GPIO inputs are strict, with 29 pins
checked in 63 raw scenes. Stock/inactive-candidate RAM/register/PWM
comparisons and stereo/mono/captured PCM flows pass in the optimized
stage. Opposite outputs stay at the stock idle reference. Three other
gate combinations reach an unmodeled I2C configuration read; do not
label them full-loop passes. Integrate startup I2C/peer state there,
establish raw-gate/button meanings and actual IRQ0 setup, and then
extend continuous CPU/USB/peripheral timing. Module bytes are unchanged.
The [preceding full validation](firmware/validation/20261122-loop.json)
passes all 20 stages (E-20261122-61). Normal and optimized foreground
reports are identical. Five active flows execute 795 iterations,
11,130 checked IRQ returns and 45 independently checked decoder frames.
The preceding clock/watchdog reports and tool sources are unchanged
except the runner. Gate failure boundaries remain explicit.

Foreground I2C continuation (E-20261122-70): read
[FOREGROUND_I2C_REPLAY.md](firmware/FOREGROUND_I2C_REPLAY.md).
The old audio fixture lacked stock I2C completion-callback registration;
merely mapping the bus left its wrapper flag set. The new stage executes
that original registration and the real IRQ17 vector, without host-written
completion flags. Seven stock/inactive-candidate scenario pairs execute
84 complete iterations and 3,546 checked IRQ returns, comparing 81,192
RAM bytes, persistent registers, PWM, transactions and watchdog feeds.
Wrong identities retry; address NACK and suppressed IRQ use original
three-attempt timeout cleanup with explicit logical wait-clock quanta.
The (1,0) raw-gate state now completes; both (0,*) states stop later at
0x20672/0x4009AE00 (FLEXCOMM9), not full-loop successes. Active PCM in
this new state, actual gate meanings, full boot and physical timing remain
open. Keep the older stage and its controls. The C module is unchanged.
The [latest validation](firmware/validation/20261122-loop-i2c.json)
passes 21/21 (E-20261122-71); the preceding 20 reports are identical,
and the new stage matches its optimized-Python run. Execution was
interrupted after 17 stages; the final four resumed after pinned-input,
source and completed-report checks. Recovery is recorded in the JSON.

RGB/SPI continuation (E-20261123-90): read
[RGB_SPI_REPLAY.md](firmware/RGB_SPI_REPLAY.md). The earlier FLEXCOMM9
boundary belongs to an original RGB serializer/SPI DMA path, separate
from the modeled FLEXCOMM5 radio control path. The new master FIFO has
eight entries/SIZE=1 per UM10912, not the USART description in the SDK
header. Execute original global DMA_Init/reset, reconstruct I2S handles,
then original SPI/RGB setup; do not merely write a controller-enable bit.
The test checks descriptors, supplied MISO and independent encoded MOSI,
paired callbacks and shared SPI/I2S IRQs. High P3_17 with USB events stops
at 0xD7F8/0x4008C028 after 19 loops for both P0_23 levels; its output/delay
path is next. Original DMA ERRINT callbacks can also report application
success/ready with zero FIFO clocks; preserve this failure, define custom
firmware error handling and never treat ready as physical delivery proof.
The selected GPIO/input state is explicit; no full boot/timing is claimed.
The [latest full validation](firmware/validation/20261123-rgb-spi.json)
passes 22/22 (E-20261123-91), with the preceding 21 reports unchanged
and identical normal/optimized new-stage output. It has 780 RGB cases,
64 idle iterations, 25 rejection controls and five active audio cases
(795 iterations, 41 shared IRQ batches, 11,395 checked IRQ returns,
45 independent frame checks). Module bytes remain unchanged. Next
follow the GPIO/delay extension below and preserve SPI DMA error status
in a future candidate; whole-device/flashable flags remain false.

GPIO/delay continuation (E-20261123-100/101): read
[GPIO_BUSYWAIT_REPLAY.md](firmware/GPIO_BUSYWAIT_REPLAY.md). An explicit
P1_8 latch permits original high-input execution through D620. It is a
calibrated busy loop, not an MRT wait or proven 200ms. Supplied CPI
budgets advance timers/WWDT only during that routine and its IRQs;
other events remain boundary-scheduled. The
[latest validation](firmware/validation/20261123-gpio-delay.json) covers 23 stages: four
fresh executions, nineteen dependency/input/hash-checked report reuses.
Affected old reports and normal/optimized new output match. Six active
cases cover 954 iterations and 54 independent frames. Falling P3_17
now exposes FLEXCOMM2 I2C at 0x10BB6/0x40088800, slave 0x1A in the caller,
before the low latch write. Next model its real driver/initialization
with NACK/timeout/IRQ controls; peer identity is unknown. Retain masked
MRT coalescing, SPI error evidence and the partial timing scope.

FLEXCOMM2 release continuation (E-20261127-100/101): read
[I2C_RELEASE_REPLAY.md](firmware/I2C_RELEASE_REPLAY.md). Execute original
Initialize/PowerControl and verify PSELID selection, not just its return.
The synthetic 0x1A write sink now permits the original five commands and
low latch write. It has no physical chip identity or read responses.
[Latest validation](firmware/validation/20261127-release-i2c.json) covers
24 contracts: one fresh normal/optimized stage and 23 audited reused
reports, not 24 reruns. Seven active cases cover 1113 iterations and
64 independent frames. ACK release completes; full-audio NACK
and longer all-ACK cycles stop at 0x9FB2/0x400A000C (ADC channel 8).
Next model ADC sequence B and continuous arrivals/independent stop during stalls.
The previous stage keeps its original unsupported-I2C boundary controls.

ADC-B continuation (E-20261128-100/101): read
[ADC_B_FOREGROUND_REPLAY.md](firmware/ADC_B_FOREGROUND_REPLAY.md).
The original B setter/enable fragment produces 0xD0040100; the original
reader distinguishes DATA_VALID and retries, with no patched completion.
4,096 clamp inputs, 468 means and cold-ten/warm-five windows pass.
Five active streams cover 1499 iterations and 108 independent frames,
including both previously blocked paths and a 575-iteration stereo run.
[Latest validation](firmware/validation/20261128-adc-b.json) is one fresh
normal/optimized stage plus 24 source/input/runtime/hash-audited reuses.
Readiness is count-supplied; samples are not measured board voltages.
Missing readiness retains the old result and reaches power control at
0xA034/0x40000620 in that stage. E-20261128-110/111 now executes that
power fallback and complete original 0x90E0 pin/clock/calibration setup:
read [ADC_RECOVERY_REPLAY.md](firmware/ADC_RECOVERY_REPLAY.md).
Six startup, two full recovery and four averaging fault scenes pass;
15 guards reject. Full normal/optimized reports match; latest validation
covers 26 contracts (one fresh, 25 audited reuses, not 26 reruns).
Important stock failure semantics: four failed calibration attempts still
lead to enabled sequences, and all ten failed measurements can yield a
stale 3200 average marked ready. Do not treat either as healthy data.
E-20261128-120/121 now covers that A-start in a strict unified lifecycle:
read [ADC_DUAL_SEQUENCE_REPLAY.md](firmware/ADC_DUAL_SEQUENCE_REPLAY.md).
Twelve foreground and eighteen startup scans, all six warm timeout positions,
interleaved B recovery, queued trigger press/release and 20 guards pass.
A timeout aborts remaining A channels; the original ring can retain raw
payloads without success-only inversion. A new scan recovers fresh values.
Integrated stock/disabled, stereo/L/R and A-/B-timeout scenes check original
foreground, preserved PCM and independent PWM/drain/isolation. Full reports
match normal/optimized Python; latest validation is 27 contracts (one fresh,
26 audited reuses). Older stages retain their own boundaries.
Next advance concurrent peripheral/USB/DMA events during 0x1FF70 waits
and provide independent elapsed-time stop. Simultaneous A/B priority and
real conversion timing remain unsupported. Retain all GPIO events,
including NACK reassertions. Stock/DSP images remain unchanged; no flash.

Desktop update 2026-11-27: main 5ff0450 / CI 45876635460 now passes
585 contracts, WPF and package checks following later app commits.
The old ChromaEffect build failures below remain historical notes.

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

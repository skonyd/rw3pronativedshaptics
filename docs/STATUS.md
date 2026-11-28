# Current status (single source of truth)

Updated 2026-11-28. For the work history and next steps when switching AI tools, see [HANDOFF.md](HANDOFF.md). This page replaces reading the dated reports in order; they stay as history
(see [docs/README.md](README.md)). When something changes, update this page in the same commit.

## Desktop app additions (2026-11-22)

- **Chroma RGB lighting (`RGB` tab) & Battery indicator (`BatteryBadge`):**
  - Firmware-verified Razer Class `0x0F` (lighting) and Class `0x07` (battery/charging) commands in `WolverineLightingAndBattery.cs`.
  - Battery readout uses exact 0–100 % + charging flag via Razer HID `07/80` & `07/84` in PC mode, falls back to XInput (`XInputGetBatteryInformation`), `IGameInputDevice::GetBatteryState` (`GameInputRumble.TryReadBattery`), and `Windows.Gaming.Input` (`TryGetBatteryReport`) in Xbox mode, and never shows a false `⚡` charging bolt when running wirelessly in Xbox mode.
  - Disassembly of `Dev2FW.bin` `0x2EA8E` (`0x2E8E8` renderer) and `0x2B4F4` (validator) confirmed the hardware-implemented LED effects on Zone `0x05`: Off (`0x00`), Breathing (`0x02` — 1 color, 2 colors, or random), Spectrum (`0x03`), Wave (`0x04` — direction byte at `p1` = `1` or `2`, `p2` = `0`), and Static (`0x08` / `0x0D`). Unimplemented effect IDs (`0x05` Reactive, `0x07` Starlight, which `0x2EA96` jumps over as no-ops) were removed from the UI.
  - RGB slider changes are debounced (140 ms) and serialized with a 55 ms inter-command delay between Brightness (`0F/04`) and Effect (`0F/02`), and `GipSensaTransport` serializes GIP `0x0F` sends so lighting packets and 33 ms Sensa frames never collide.
- **Shortcuts (`Kısayollar` tab, `ShortcutManager.cs`):**
  - Lists built-in hardware (`○ + ☰ + Ⓐ` for 2 s → Xbox to PC mode) and foreground controller navigation shortcuts (`LB/RB`, `D-Pad`, `Ⓐ`, `Ⓑ`, `☰`).
  - Supports user-created background/in-game shortcuts via two methods:
    1. **Controller Button Combos (`ControllerCombo`):** Requires ≥ 2 buttons (or ≥ 500 ms hold) to avoid game input collisions; includes live button-combo capture ("Tuşları Dinle") and hold-duration selection (`Anında`, `0.4 sn`, `0.8 sn`).
    2. **Global Keyboard Hotkeys (`KeyboardHotkey`):** Win32 `RegisterHotKey` integration with live key capture.
  - Target actions include cycling or directly selecting any of the 5 game controller outputs (`Kolun kendisi`, `Xbox 360`, `Xbox Series`, `PS4`, `DualSense`), toggling palm audio haptics, toggling Melody mode, cycling global haptic strength, toggling RGB on/off, cycling RGB effects, and showing/hiding the window.
- **Virtual XInput pad loop & output fixes:**
  - `XInputHaptics.ReadButtonsAnyPad` now excludes virtual HIDMaestro XInput slots (`045E:028E` and `045E:0B13`), preventing double-edge navigation or self-triggering when a virtual Xbox controller is active.
  - `XboxAudioGameBridge.OnOutput` now processes XUSB rumble reports (`00 08 00 L R`) in both normal and strict modes before logging unknown packets, and silently ignores XUSB LED indicator packets (`00 0D ...`).

## Firmware lab

An original Cortex-M4 audio-to-haptics DSP module now runs in an offline replay
alongside stock NXP routines from v2.04.00. The lab compares PCM, decoded
Sensa state and SCT duty-register writes. Both Telink stock loaders now
run to their relocated application entry in RV32 replay. The dongle's
relocated Xbox USB configuration agrees with the recorded host audio endpoints.
The stock dongle endpoint dispatcher and receive handler now replay all
6,272 captured endpoint-6 slots byte-for-byte through the PCM ring,
acknowledge each slot and publish its cursor to the stock event ring.
The stock reader runs through the stock-initialized stereo resampler
and returns processed PCM for contiguous, wrapped and underfilled
ring states. Separate step and rate-offset tests check the kernel.
The stock buffer reset clears all ring and retained-sample bytes
in a dirty-memory fixture.
The controller Telink's stock I²S setup generates transmit and receive
DMA descriptors. Its stock PCM writer sends 623 synthetic stereo frames
to the modeled transmit FIFO in order, including a descriptor wrap.
The preceding stock stereo resampler also reaches that FIFO using
a test-initialized nominal ratio and 240 synthetic input frames.
The complete controller board setup now returns in replay and configures
240 decoder samples per channel. The stock decoder's null, loss, and
synthetic LC3 cases are covered. The independent tone probes yielded
weak output or silence. Host-interleaved decoder output reaches the
modeled FIFO through the stock writer.
The dongle's stock encoder supplies exact 35-byte per-channel outputs
to the controller's stock queue copy and receive dispatcher in a
paired offline test. Ten scenes produce 8,160 decoded stereo frames
and 160 startup silence frames at the modeled I²S FIFO. They cover
channel swaps, receiver-signaled loss of both lanes, recovery and
silence. The stock receiver interleaves and resamples both lanes and
clears each consumed slot. The separate decoder-level left-loss
control supplies interleaved PCM from the host.
Two recorded USB windows also pass through the stock dongle timer
handler and audio worker, with contiguous and wrapped PCM reads.
The resulting codec bytes reach the controller's modeled FIFO with
channel isolation.
The dongle's stock TX routine now points RF DMA at the same encoded
bytes. The controller's stock RF interrupt checks modeled receive
metadata and queues the audio in its eight-slot ring. Absent IRQ,
CRC-error, oversize and duplicate controls leave that ring untouched.
The scheduled replay executes the controller's stock Timer0 vector.
It chooses and consumes queued audio, marks a missing packet and
handles it with the stock loss path. In the two recorded USB tests
the first decoded block is muted during cold start. The next block
plays with the opposite channel silent. Stock timer compare writes
drive the two-phase modeled clock, which returns to a 5 ms period
after a modeled 100 µs delay. The stock system-timer handler now
advances and publishes the RF counter from one initial state.
The paired run still supplies arrival timestamps and peripheral
timing. A separate duplex replay runs stock transmission, retry
and controller reply. Local TX completion alone leaves the slot
pending. A matching reply retires it and the next audio slot sends.
The controller SPI replay now connects the stock Telink PSPI
descriptors to the stock NXP FLEXCOMM5 DMA and parser, and checks
the stock reply through the Telink receive queue and checksum
branch. The SPI bus clocks and interrupts are modeled. Physical
RF timing and the separate inter-chip I²S connection remain open.
The I²S checkpoint now reads both stock images after initialization.
The Telink RX and TX clock fields and NXP FLEXCOMM6 select master;
NXP FLEXCOMM7 selects slave. The stock Telink receive reader consumes
242 low half-words from a modeled wrapped DMA ring and returns 240
mono samples. An empty ring, 241 pending words and changes confined
to the high half-word are covered. The PCB clock and data routing
are still open.
The Telink voice replay feeds that receive lane into the stock
encoder and verifies all 40 encoded bytes in its queue and RF DMA
reply. A tone in the high lane yields the same reply as silence.
The dongle's stock RF IRQ writes both its control queue and voice
decode slots. Its stock worker decodes and resamples 240 mono samples,
wraps the mono ring and sends the expected sample bytes to USB endpoint
0x87. The exercised Xbox branch takes every second sample in a
48-sample chunk, sending a 54-byte GIP packet. Silence, opposite-lane
isolation, USB underrun, missing slots, duplicate responses and recovery
with a modeled new response sequence are covered. Hardware scheduling,
clock synchronization and the physical inter-chip audio route remain open.
The stock voice worker and USB producer also run through the stock
timer handler and endpoint dispatcher, with no consumption when pending
bits are absent. All 6,272 original dongle USB IN packets independently
agree with the replay's header, 54-byte size and nonzero sequence wrap.
The capture groups eight packets per URB. Its background-level microphone
input is not a waveform reference for the synthetic replay tone.
The stock NXP analog replay scans six modeled ADC channels, retries a
delayed conversion and verifies startup calibration and the foreground
ten-sample moving average. With fixture calibration it follows both
trigger values into the queued GIP `0x20` report and confirms that
release clears both fields. The stock USB driver arms the same bytes
on endpoint `0x81` in modeled USB RAM, and its IRQ and endpoint callback
retire the transfer only after modeled completion. An offline suite runs the stock and DSP
checks from the two original USB captures and all three stock images.
The [latest validation](firmware/validation/20261128-adc-dual.json),
2026-11-28, covers 27 stage contracts: one fresh unified ADC A/B stage
with identical normal/optimized reports, plus 26 unchanged reports reused
after input/source/import/runtime/report-hash checks. These are not
27 fresh runs. The [preceding recovery validation](firmware/validation/20261128-adc-recovery.json)
retains its own one-fresh/25-reused provenance. The [preceding ADC-B validation](firmware/validation/20261128-adc-b.json)
retains its own one-fresh/24-reused provenance. The [preceding release validation](firmware/validation/20261127-release-i2c.json)
retains its own one-fresh/23-reused provenance. The [previous GPIO validation](firmware/validation/20261123-gpio-delay.json)
retains its own four-fresh/nineteen-reused execution provenance.
The preceding [full 22-stage run](firmware/validation/20261123-rgb-spi.json)
includes active stereo capture excerpts in the PWM model. This is
modeled replay evidence, not whole-device equivalence. JSON records
input/source hashes, execution provenance and unverified boundaries.
Physical input mapping, host USB packet delivery and Xbox
authentication remain outside the analog replay.
Windows wired Xbox and dongle USB captures both carry 192-byte GIP audio
payloads. The NXP's 198-byte receive branch copies PCM to its alternate I2S
ring in emulation. The captures and replay cover the host USB boundary.
The stock audio queue now runs across startup, drain, underrun and recovery
in the replay. The CTIMER2 callback and SCT duty register updates also run
with fixture state. Stock I2S queue admission and DMA0 interrupt dispatch are
covered at their boundaries. The replay also runs the stock I2S completion
handler and follows the application callback to its next PCM or silence
submission with modeled IRQ events. The repeatable build runs these checks against
the pinned stock image. The model now also executes the initial descriptor
generated by stock I2S setup and derives its completion interrupt from the
descriptor flags. The first linked descriptor also runs, with INTB then
INTA and a same-PCM comparison against the independently compiled DSP.
Continuous replay follows stock DMA links across 128 USB blocks and checks
byte order, ring wrap and underrun silence at the modeled FIFO.
Four-block USB bursts and midstream pauses now exercise queue buffering
and ordered recovery under a deterministic event schedule.
Stock Sensa transient synthesis also reaches the modeled SCT duty registers
for both palms, with opposite-channel isolation in the fixture.
Original DSP band levels reach stock SCT duty output. The stock initializer
builds the gain tables from controlled calibration inputs. An explicit
empty frame restores idle.
The stock refresh wrapper now also passes RAM profile switching and
frequency-dependent gain interpolation checks.
Stock EEPROM calibration loading accepts a valid block, restores factory
data on four invalid inputs, and passes the selected data to the gain initializer.
The separate EEPROM user profile loader also passes validation, factory
fallback and field boundary checks.
Deferred profile save clears its pending flag after eleven modeled EEPROM
completions. Subsequent reload covers complete, interrupted and partial-page
snapshots.
Xbox Sensa trigger regions 249/149 reach independent SCT duty registers
in replay. The tested pitch codes leave duty unchanged at fixed strength,
and the stock transient cutoff clears the modeled output.
The stock palm oscillator's lookup and four pitch codes now pass
sample-domain checks. Its formula uses a 4 kHz constant.
The stock clock and SCT initializers now generate the PWM channel/event
mapping and the 1200-count match for a 40 kHz carrier in replay.
The later stock board initializer switches CTIMER3 to the 96 MHz main
clock, giving 250 µs consumption and 1 ms refill intervals in replay.
The palm queue starts at 16 samples per side. A stalled refill retains
the last PWM duty until fresh samples arrive. An ARM trampoline at the
simulated I²S TX enqueue boundary copies PCM for a separately scheduled ARM worker.
The stock/candidate comparisons preserve I²S bytes, a preceding game
effect, palm isolation and return to idle after disable or source underrun.
The worker can run from stock foreground call site 0x2F5FA in Unicorn and
defers when a game Sensa frame is already pending. Strict HEX validation
pins the original writable image envelope at 0x8000–0x37800.
The lab now starts at the application reset vector and runs the stock
scatter loader on dirty RAM. The prototype clears its own state before
stock main. The startup trace matches stock RAM and MMIO writes through
timer initialization and five peripheral waits advanced by the stock MRT0 handler.
An additional NXP scenario drives stock FLEXCOMM3 interrupts through the
first I²C register read with a synthetic peripheral response.
The register-driven startup replay now follows the stock identity check
and all nine configuration writes on FLEXCOMM3. It also reaches the first
four-byte read on FLEXCOMM8 with two explicitly synthetic response patterns.
Twelve stock/candidate scenarios compare initialized stock RAM, stack
pointer, wait order and peripheral writes, covering wrong identity, NACK,
missing/delayed interrupts and timeout followed by recovery. Missing and
truncated peer definitions fail rather than silently becoming zeros.
The comparison includes system-control/NVIC writes and final mask state.
The model checks NVIC enables and PRIMASK but still schedules vector calls
manually. It stops before interpreting the second peer's response;
whole-board startup and authentic external responses remain unverified.
An additional [basic Cortex-M IRQ replay](firmware/CORTEX_M_IRQ.md) now
uses memory-backed MSP/PSP frames, IPSR and EXC_RETURN for those original
handlers. It checks pending/active state, PRIMASK/BASEPRI/FAULTMASK,
priority nesting and deferral. The old direct-call path remains separate.
Twenty-two core checks and eight invalid/unsupported controls pass;
the same 12 stock/candidate startup scenarios also pass with frame
delivery and per-interrupt register preservation. Across those pairs,
2,006 entries and 2,006 returns balance.
The additional bounded FP model covers eager/lazy MSP/PSP frames,
memory-backed S0-S15/FPSCR restore, visible S16 corruption and nested
preservation. Sixteen checks and 12 rejected invalid/unsupported cases
pass. The original Sensa CTIMER2/3 vectors run in idle/left/right scenes
with both policies: 792 balanced entries/returns, preserved core/FP
registers, identical 112 duty/depth snapshots per case and 81,192 stock
RAM bytes matching the direct-call baseline. Underrun hold, recovery
and opposite-output isolation are retained.
Unicorn 2.1.4's MCLASS flag was found to force M33 despite an M4 model
request. The shared NXP audio Replay and core fixtures now explicitly
select and assert M4. Earlier reports predate this correction.
System exceptions, general FPU-control manipulation, physical event
timing and full NVIC cycle behavior remain open. The new FP model has
explicit policy bounds; no whole-device equivalence follows.
The [DMA/DSP preemption extension](firmware/DMA_DSP_PREEMPTION.md) now
runs the original DMA0/I²S/TX handler, compiled tap and worker with
nested stock motor timers. Six continuous cases preserve PCM delivery,
PWM/DSP traces and 81,192 stock RAM bytes against the direct-call path.
Twelve worker cases and seven DMA-register checks cover busy/released
mailboxes, masks, eager/lazy nesting, error/recovery and explicit unknown
readback inputs. The main cases check 6,056 balanced returns without
host context restoration. Injected stacked R0/S0 corruption fails.
Stock reads write-only INTENCLR: readbacks 0/0x80 leave modeled enables
0x6000, while 0xFFFFFFFF leaves 0x2000. The physical readback and channel
14 behavior are unverified. The one-slot haptic tap skips new copies
when busy; audio-byte preservation does not prove zero haptic loss.
That older path still uses host-built frames as a reference. A new
[compiled publication path](firmware/COMPILED_SENSA_PUBLICATION.md) now
packs and publishes its own Xbox Sensa frames, protects copy/stock decode
with PRIMASK, and latches accepted class-16 game ownership until explicit
fresh rearm. Its 8,464 byte-oracle cases, stock decoder checks, command
hook contracts, publication corruption controls and injected IRQ/control
races pass. Synthetic and recorded wired/dongle PCM windows each play
96 blocks through compiled DSP/packing to original PWM code without
host-constructed candidate frames. A stale-before-commit race was found
and fixed; the earlier module fails the new regression. Strict typed
builds from separate output/cache directories are byte-identical.
The [stock-clock continuation](firmware/STOCK_CLOCK_REPLAY.md) now
advances that counter only through original MRT IRQ9 stores, every eight
delivered interrupts, on a common virtual schedule with CTIMER2/3 and
DMA completions. Four 96-block flows preserve PCM and stop with palm
positive/trigger-negative controls. Ten MRT checks/five rejections and
four motor checks/two rejections pass. The stock timer ISR and callback
each clear the same W1C bit; they count as one acknowledged event.
Activity and final duty are compared with separate silent-PCM streams,
not startup duty 120. Steady idle ends at 288/288/0/0 in this fixture;
all-silent, missing-left and stuck-tail traces are rejected.
A supplied 2 ms delay while masked coalesces fifteen MRT events and
reaches source counter 250 at 252 ms virtual time. No host catch-up is
used. This confirms the foreground safeguard is not an independent
elapsed-time watchdog; a blocked decoder can separately delay a stop.
CPU instruction time, full main-loop tasks and physical timing/WCET
remain unmodeled. Real control commands, full clock/loop closure,
separate GIP rumble/trigger arbitration, image placement, bootloader
acceptance and recovery remain open. No loadable-image claim follows.
The [watchdog/main-loop-header continuation](firmware/WATCHDOG_AND_LOOP_GATE.md)
now executes original setup/feed/IRQ0 and both counter-gate branches.
The specialized fixture supplies documented WDTOSCCTRL reset 0x140,
nominal 800 kHz; setup yields TC 400000, warning 512, MOD 3. Thirty-four
headers and 128 MRT returns pass. Thirteen register checks, three reset
controls, nine rejected inputs and two stock-code/clock guards pass.
Blocked delivery requests reset while MRT counter RAM stalls, but no
reset/bootloader/PWM recovery is invented. If IRQ0 is explicitly enabled,
its original warning handler reloads WWDT even with foreground stalled;
the actual board's IRQ0 configuration remains open. This is not a new
independent 250 ms audio-only stop. Full loop tasks, clock-domain timing,
reset execution and physical safety remain unverified.
The [foreground/GPIO continuation](firmware/FOREGROUND_LOOP_REPLAY.md)
executes complete original iterations in one explicitly selected raw-gate
fixture, reaching Sensa dispatch through the loop itself. Twenty-nine
pins pack into the expected raw word in 63 cases; missing/invalid GPIO
operations fail. Idle stock/candidate loops match 81,192 RAM bytes,
persistent registers and PWM. Five optimized active streams preserve
96 PCM blocks each, check left/right isolation, original decoder frames
and return to idle. Normal and optimized new-stage reports are identical;
the preceding full suite passes 20/20 (E-20261122-61). That stage
retains explicit missing-I2C controls for its other gate settings;
unknown foreground MMIO fails. Flag meanings, full startup and physical
button/report mapping remain open. USB receive calls/clock events are
still supplied at loop boundaries; no continuous CPU timing is claimed.
The [foreground I2C extension](firmware/FOREGROUND_I2C_REPLAY.md)
now completes the additional raw-gate (1,0) fixture through original
callback registration and the real IRQ17 vector. Seven stock/inactive-
candidate scenario pairs compare 81,192 RAM bytes, registers, PWM and
transactions over 84 complete iterations and 3,546 checked IRQ returns.
Wrong identities retry; NACK and suppressed IRQ complete three original
timeouts with a supplied logical wait-clock schedule. Two remaining
gate states stop later at 0x20672/0x4009AE00 (FLEXCOMM9). Unknown peer
registers and unsupported I2C accesses fail. Active audio in the new
gate state, physical bus/instruction timing and full boot remain open.
All 21 stages pass (E-20261122-71); all 20 preceding reports remain
identical. The complete new stage also matches under optimized Python.
A tool/process interruption occurred after 17 stages; inputs/code and
saved reports were checked before resuming the final four stages.
The public validation records that recovery explicitly.
The [RGB/SPI continuation](firmware/RGB_SPI_REPLAY.md), 2026-11-23,
executes original global DMA initialization, idle register reset, I2S
reconstruction and SPI9/RGB setup. It checks real generated descriptors,
FIFO bytes and paired DMA callbacks, rather than only the ready flag.
All four initial raw-gate combinations now have a bounded idle-loop
fixture. Selected low-GPIO audio fixtures combine original I2S/SPI DMA
handlers, actual Sensa main dispatch and explicit original RGB boundary
calls. Physical clock/bus timing and a real USB RGB-command IRQ remain open.
That stage retains two high-P3_17 GPIO boundaries at 0xD7F8/0x4008C028;
the later GPIO extension below follows their output and busy wait.
Supplied SPI DMA ERRINT flags reveal that stock callbacks can
set ready without FIFO data; error propagation for custom firmware is
still required. No stock code is patched to erase this observation.
That full RGB suite passed 22/22 (E-20261123-91); all 21 preceding
reports remain identical. Normal/optimized RGB-stage reports match.
The new stage covers 780 RGB cases, 64 idle iterations across four
initial gate states, 25 rejection controls and five active audio cases.
Those audio cases run 795 iterations, 41 combined SPI/I2S IRQ batches,
11,395 checked IRQ returns and 45 independently checked Sensa frames.
The [GPIO/busy-wait extension](firmware/GPIO_BUSYWAIT_REPLAY.md) declares
only the P1_8 byte latch and executes the original D620 routine; argument
200 is not evidence of 200ms. Seven delay profiles and a masked case
check loop arithmetic, pending interrupt coalescing and supplied CPI
budgets. Seven invalid GPIO operations and nine delay negative controls
fail explicitly. Six active cases run 954 foreground iterations,
13,689 checked IRQ returns and 54 independent Sensa frame checks,
including mono isolation and drained idle.
Short-high and later-rise inputs exercise the original input filter.
Normal/optimized new-stage output and the three affected prior reports
match. Nineteen other reports are explicitly reused, not freshly rerun.
Lowering P3_17 at iteration 70 stops both P0_23 variants after 89 iterations
at 0x10BB6/0x40088800 (FLEXCOMM2 I2C); the original caller targets slave
0x1A. Physical peer identity and the final low latch write are unverified.
Instruction budgets and a latch fixture do not establish silicon timing,
pin direction/mux, full power-down, complete boot or flashable firmware.

Published firmware-source commit `d17cbba`, CI `45077139967`, also has
578 passing contracts but fails the existing `ChromaEffect` reference
at `MainWindow.xaml.cs:2937` (E-20261123-102). No Windows package was
produced; this is separate from the offline firmware validation.

The [FLEXCOMM2 release extension](firmware/I2C_RELEASE_REPLAY.md),
2026-11-27, now executes original Initialize/PowerControl, the master
driver, IRQ16 and wrapper callback against an explicit write-only sink.
It checks actual clock/reset/selection writes and five exact two-byte
commands before the original P1_8 low write. An absent I2C capability
cannot pass merely because PowerControl returns zero. Fifteen malformed
register/protocol controls and three blocked-state controls reject.
Direct NACK, missing IRQ/callback and late-IRQ retry cases retain distinct
transfer and wrapper results. The disabled-candidate pair matches 81,192
shared RAM bytes plus registers, PWM, PCM and transactions.
Seven active scenes cover 1113 iterations, 16,196 checked IRQ
returns and 64 independent Sensa frame checks; each drains to
stock idle, with separate mono isolation, filtering and repeated-cycle checks.
Full-audio all-address-NACK still reaches another unmapped register at
0x9FB2/0x400A000C (ADC0 sequence B) after GPIO low. A longer all-ACK
repeat also stops there after 199/255 iterations, so NACK is not required.
Neither partial run is a completed recovery.
The physical peer, bus timing and concurrent USB/DMA arrivals during
I2C stalls remain unverified. Full-device/flashable flags remain false.

The [ADC-B foreground extension](firmware/ADC_B_FOREGROUND_REPLAY.md),
2026-11-28, now executes the original B configuration setter/enable
fragment, channel-8 reader and ten-/five-sample averaging code. Explicit
12-bit samples and read-count readiness remain fixtures, not physical
conversion timing. Nineteen invalid register/input cases reject; seven
reader cases preserve valid zero versus missing data and the eight-poll
limit. All 4,096 raw clamp values and 468 means pass independent oracles,
including the stock percentage divisor 467 and 0..100 output range.
Both former ADC-boundary cases now return in their supplied fixtures.
Five active streams cover 1499 iterations, 23,821 checked IRQ
returns, 50 conversions and 108 independent Sensa frame checks,
including 575-iteration stereo, 255-iteration L/R and delayed-variable
ADC cases, plus active-audio NACK. Output drains to independently
retained stock-idle PWM and mono stays isolated. Two stock/disabled
pairs compare 81,192 shared RAM bytes, registers, PCM and transaction
traces. GPIO reassertions remain in the logs, not normalized away.
That preceding stage keeps its never-ready boundary at PC 0xA034,
the original 0x40000620/0x400 power-control write.
The [ADC recovery extension](firmware/ADC_RECOVERY_REPLAY.md) now executes
that power fallback and full original 0x90E0 pin/clock/calibration setup.
Six startup scenes distinguish successful, delayed and never-completing
calibration/dummy initialization. Original setup returns and enables both
sequences even after four failed attempts; the model rejects conversion
while startup is unhealthy. Two reader scenes recover to a new valid
3450 sample after eight invalid polls and full power reinitialization.
Four averaging fault scenes show stock 0x2C678 includes stale results;
all ten failures still mark an average of 3200 ready in the fixture.
Fifteen negative guards reject unspecified/unsupported operations.
Normal and optimized complete reports match. This does not change the
stock image or fix its behavior on hardware.
That recovery stage retains its explicit ADC-A start boundary at 0x1982C.
The [unified ADC A/B stage](firmware/ADC_DUAL_SEQUENCE_REPLAY.md) now
executes original full setup and six-channel scans in the same strict
power/startup lifecycle as B. Twelve scans check delayed/last-poll data
and independent moving averages; eighteen startup scans check trimmed
calibration. Six warm timeout scenes check each channel position, aborted
remaining channels, original retained-raw/uninverted values and fresh
recovery, interleaved with valid B zero/3450. Original trigger mapping
and RAM-queued GIP press/release fields also pass; no host delivery or
authentication is inferred. Twenty negative guards reject unsupported data,
inputs, widths, overlaps, exhaustion and unpowered operations.
The new integrated scenes include a stock/disabled pair, stereo, L/R and
A-/B-timeout audio paths, original foreground iterations, PCM preservation,
independent PWM/idle/isolation and Sensa frame checks. Five active runs
cover 1275 iterations, 635 A scans, 3809 valid A reads, 49 valid B reads,
22,249 checked IRQ returns and 105 independent frames. Normal/optimized
complete reports match. Calibration wait 0x1FF70 still executes without
advancing concurrent peripheral time. Simultaneous sequence priority,
physical ADC timing/voltage/pin behavior, continuous USB/DMA arrivals during
waits, independent stop, full boot and safe flash/recovery remain open.
No hardware firmware was written.

Current desktop verification (2026-11-27): main `5ff0450`, CI
`45876635460`, passes 585 contracts, WPF and the package job. The release
job is skipped; this is not a published release or physical validation.
Older missing-ChromaEffect failures below are historical source outcomes.

Published firmware-source commit `665e70c` also passes Windows CI
`46085319751`: 585 contracts, WPF and the package job pass. The release
job is skipped; no published release or physical firmware proof follows.

Published firmware-source commit `7dd139a` also passes Windows CI
`46083705379`: 585 contracts, WPF and the package job pass. The release
job is skipped; no published release or physical firmware proof follows.

Published firmware-source commit `ac476f3` also passes Windows CI
`46033025127`: 585 contracts, WPF and the package job pass. The release
job is skipped; no published release or physical firmware proof follows.

The firmware suite result is separate from desktop packaging: Windows
CI run `44956123630` on `b14f072` has 573 passing contracts but fails WPF
compilation on the pre-existing missing `ChromaEffect` type (also seen
on parent `04f9ab3`). The local working tree builds with 575 contracts
but includes separate, uncommitted RGB/shortcut additions; that is not
a clean published-main build. See E-20261122-05. A newer check on
`ecc493b`, Windows CI `45021366034`, has 575 passing contracts and the
same missing `ChromaEffect` WPF failure (E-20261122-32).
Package CI `45028884831` on `32809ef` has 575 passing contracts and
the same CS0246 `ChromaEffect` failure (E-20261122-62). The newer
prepublication check `45031220312` on `6e519805` has 576 passing
contracts but still fails that missing type at `MainWindow.xaml.cs:2859`.
Firmware 21/21 is an independent offline result; these failed desktop
runs did not produce a Windows package.

Published firmware-source commit `4e934a4`, CI `45072940309`, also has
576 passing contracts but fails the existing `ChromaEffect` reference
at `MainWindow.xaml.cs:2885` (E-20261123-92). No Windows package was
produced; this is separate from the 22/22 offline firmware result.

Published firmware-source commit `0dd1ab7`, CI `45033468390`, also has
576 passing contracts but fails the existing `ChromaEffect` reference
at `MainWindow.xaml.cs:2884` (E-20261122-72). No Windows package was
produced; this is separate from the 21/21 offline firmware result.
Bootloader acceptance, recovery, wireless audio, pin-level PWM and integration timing
remain open. [Firmware replay and next steps](firmware/AUDIO_HAPTICS_REPLAY.md).

## What the app does

- **RGB (Chroma Lighting) tab & top-right Battery badge (2026-11-21):**
  - **RGB tab (`WolverineLightingAndBattery.cs`, `ChromaEffect`):** Razer Synapse / Controller Setup for Xbox style Chroma RGB lighting tab decoded from `Dev2FW` Class `0x0F` handler (`0x2F700`) and validator (`0x2B4F4`). Supports quick Off/On toggle (`0x00`), brightness slider (`0F/04 [profile, 0x05, 0..255]`), and hardware effects (`0F/02`): **Kapalı (`0x00`)**, **Sabit / Static (`0x08`)**, **Spektrum Döngüsü (`0x03`)**, **Dalga / Wave (`0x04`, left↔right direction)**, **Nefes Alma / Breathing (`0x02`, 1-color / 2-color / random)**, **Tepkisel / Reactive (`0x05`, fast/medium/slow)**, and **Yıldız Işığı / Starlight (`0x07`, 1-color / 2-color / random + speed)**, plus preset Chroma swatches, RGB sliders and optional EEPROM profile persistence (`profile 1`). Works in both PC mode (HID Feature Report 4) and Xbox mode (GIP `0x0F`).
  - **Battery badge (top-right header):** queries Razer Class `0x07` (`0x17370`) `07/80` (battery `raw * 255 / 100` → `0..100%`) and `07/84` (charging state `0/1`) via HID feature report 4 in PC mode, with `XInputGetBatteryInformation` fallback in Xbox/XInput mode, refreshed every 5 s.
- **Main page since 2026-11-15: "Oyuna görünen kol" selector + feature matrix** (replaces the three mode cards; `GameOutput.cs`, `OutputMatrix`, contract-tested). Outputs: **Kolun kendisi** (no virtual pad), **Xbox 360** (HIDMaestro `xbox-360-wired`, XUSB), **Xbox Series** (`xbox-series-xs-bt`), **PS4** (`dualshock-4-v2`), **DualSense** (the virtual DualSense bridge). Rows: Sesten avuç titreşimi (every output), Oyunun HD haptiği (DualSense only; excludes the sound row), Oyunun rumble titreşimi, Oyunun tetik titreşimi (Series, DualSense; "oyun" on Kolun kendisi = the game drives the pad itself). A row the output lacks switches to the closest output that has it (triggers: Series from the Xbox side, DualSense from PS4; HD haptics: DualSense). `OutputMatrix.Plan` maps the choice onto the internal pipelines below: Kolun kendisi = Normal (no rows) or audio; Xbox 360 / Series / PS4 = audio mode with the strict virtual pad (`XboxAudioGameBridge`: pad read through XInput, game rumble → body over XInput, Series triggers → Sensa frame, sound → palms through the Sensa engine, GameInputSvc never allowed; PC mode too); DualSense = native. Old settings migrate once (`FromLegacy`). Tray and the ☰ combo cycle outputs. Elevation goes through the UAC-free task. Not yet tested in a game: Xbox 360 / PS4 outputs (input, rumble decoding: the Xbox 360 profile's rumble format is only logged and guessed).
- Internal pipelines (the former modes):


| Pipeline (internal, was the mode) | Source | Wolverine in PC mode (1532:0A4C) | Wolverine in Xbox mode (1532:0A3F) |
| --- | --- | --- | --- |
| Normal | game's own Xbox rumble | game → body motors | game → body + trigger motors |
| Sesten titreşim | PC audio (WASAPI loopback) | Sensa palms, stereo | Sensa palms over GIP, stereo (setting "Xbox modunda Sensa", default on; off: body motors, left motor ← left channel) |
| Oyunun DualSense titreşimi | game's DualSense output via the virtual DualSense (HIDMaestro) or a USB DualSense | haptic PCM → Sensa palms; trigger effects → body motors | haptic PCM → Sensa palms and trigger effects → trigger motors, in one Sensa frame over GIP (setting; off: body motors + GameInput impulse) |

- **Main page (2026-11-14):** each mode's options sit under the mode cards.
  - Sesten titreşim: level slider (10–300 %, 100 = the sound's own level; Sensa feature gain and body-motor gain, live), "Standart (DSX gibi)", the game's own Xbox rumble/LT-RT (virtual Xbox pad), and an "advanced" expander (frequency filter, auto level, hum suppression, bass/treble split).
  - Oyunun DualSense titreşimi: palm haptic source **Oyunun HD haptiği / Oyun sesinden / Kapalı** with a level slider (game HD → native gain; sound → the same audio level), plus trigger effects and the game's rumble. "Oyun sesinden" is for games with DualSense triggers but no HD haptics: the virtual DualSense keeps the triggers and rumble, and the palms follow the PC sound through the same Sensa extractor. The bridge then pushes no game PCM and no palm trigger synth (one producer). A USB DualSense endpoint is used only for the game-HD source.
  - The native mode now auto-starts with any source on. Before, it auto-started only with HD PCM on, so the common "triggers, no HD" setup waited for a manual click.
  - Settings no longer has the Native gain / audio sensitivity presets (now the sliders).
  - Layout (second pass): the status card (what runs now, level meters) sits above the mode card and stays visible; the mode options scroll. The native mode is labelled "DualSense oyunları". The connections panel names what each part does, and its "DualSense köprüsü" line follows the bridge live (before, it kept saying "kapalı" while the bridge ran, and "Xbox modunda kullanılmaz" for Sensa although Xbox-mode Sensa works). The tray menu can switch the native palm haptic source per game.
  - Fixed: in Xbox mode with "Xbox modunda Sensa", any frequency filter other than "full band" silently moved audio mode from the palms to the body motors. The filter now applies on the palm path too.
  - Audio status shows the captured playback device and the Bluetooth sync delay.
  - **"Hangi ses titreşsin?"** (audio mode and the native "Oyun sesinden" source, one setting, default all system sound): **Yalnız öndeki uygulama (oyun)** captures only the foreground app and its child processes with Windows process loopback (`VAD\Process_Loopback`, Windows 10 2004+; `ForegroundAppCapture`). Background YouTube, Discord or music do not vibrate. The target follows the foreground every 0.5 s; RW3's own window and shell surfaces (Start, taskbar, alt-tab) keep the previous target. No volume compensation on this path (the app stream is taken before the endpoint volume). Probe 2026-11-14: a 0.08-amplitude 200 Hz tone played by the probe process was captured at peak 0.0802, silence before it 0.0000. Not yet felt in a game.
  - **First-start guide** (2026-11-14): one question with three answers, shown once (`GuideDone`), reopened from Settings → Temel. "Her oyunda titreşim (önerilen)" = audio mode, Standart, 100 %, all system sound; "DualSense destekli oyunlar" = native mode, game HD + triggers + rumble; "Uygulama karışmasın" = Normal. "Windows açılınca başlat" is pre-ticked. Controller: D-pad up/down, Ⓐ. Used once by the user (chose native).
  - **Settings split for non-tinkerers:** Temel keeps only output strength, controller mode (PC ↔ Xbox), autostart and the guide. Everything else (trigger feel, Xbox-mode Sensa, big-motor ceiling, PC palm trigger feel, Xbox mix sliders, XInput slot, Sensa method, transport, transient records, sync delay) is in Gelişmiş, which scrolls. Test: the channel lab is a collapsed "Geliştirici araçları" section; motor test, calibration and examples use plain wording. The "Razer Synapse" link is gone (the app does not need Synapse).
  - **Melodi modu** (2026-11-14; audio panel checkbox, or Gelişmiş → Sensa çıkış yöntemi → Melodi; `SensaOutputMethod.Melody`, `MelodyAnalyzer`): per hand, band 1 = bass (30–150 Hz, real pitch, 85 ms window) and band 2 = melody (fundamental 110–1000 Hz by harmonic sum, octave-folded into 150–400 Hz, newest 43 ms), from the RAW audio (the extractor's 550 Hz limit is bypassed), with a pitch per point (4 per 33 ms frame; the firmware keeps per-point pitch, E-20261114-22). Bass + melody strengths share the limiter/level of the standard path, sum ≤ 1. A melody alone counts as sound. The frequency filter is forced to full band while melody is on. Built on: Synapse vs RW3 on the same song (E-20261114-21) and the palm pitch/glide/chord/frame-rate measurement (E-20261114-23: exact pitch, 2 usable bands, ~33 frames/s over the dongle). Live check (E-20261114-24): 87 % of melody notes correct vs 39 % with Standard. Not yet felt with real music.
    - Default output method since 2026-11-14. Tonality gate (melody band only for a clear pitch: peak/mean 4 → 10), melody emphasis ×1.5, hit blend (bass band ≥ standard envelope per point, so booms punch while the melody keeps playing). Xbox mode: 2 points per band, each with its own pitch. Xbox A/B (E-20261114-27): 100 % notes vs 50 % Standard.
  - **Xbox-mode Sensa and controller focus (E-20261114-25, -30).** GIP `SendMessage` (the Razer channel to the palms) follows Windows' controller focus: a process with no window never reaches the controller, and an app in front that opens the Xbox pad itself (Opera, Chrome, the ChatGPT desktop app: Chromium gamepad support) takes the output, as already seen for GameInput (Opera). It is not "foreground only": a visible RW3-like window behind Notepad worked. **In games it works:** the user played Banishers in Xbox mode with "DualSense oyunları" and felt palms (Sensa) and trigger-motor vibration; the game reads the virtual DualSense and the Xbox pad is hidden, so it does not take the focus. Not yet tried: a game that reads the Xbox pad directly through WGI/GameInput while RW3 sends palm frames (audio mode). PC mode (Direct USB HID) is not affected.
    - **Body companion (2026-11-14):** in Xbox mode, audio mode and the native "Oyun sesinden" source also run an XInput body-motor output that plays only while `ControllerFocusGuard` sees a focus-taking app in front (known browsers and Electron apps by name, or `resourcespp.asar` next to the exe; CEF/chrome_elf files are not used because some games ship them). Reason: the user's audio mode used the body motors until the frequency-filter fix moved it to the palms; the body path ignores controller focus, so music kept vibrating with Claude/Codex in front, the palms do not. RW3 itself, games and Notepad keep the palms only (no clash with the game's rumble). Option in both audio panels ("Xbox modu: tarayıcı/Claude öndeyken titreşim rumble ile devam etsin", `XboxRumbleFallback`, default on); off = the vibration stops while such an app is in front, so everything felt is Sensa. The user found the rumble fallback coarser than Sensa (expected: rumble cannot carry pitch).
    - **GDK / Game Pass games (2026-11-14):** Banishers from Game Pass (`NWD-WinGDK-Shipping.exe`) reads the pad through the GameInput service. RW3 allows that service in HidHide (to read and drive the pad in Xbox mode), so the game still sees the hidden Xbox pad, never uses the virtual DualSense (0 output / trigger reports), plays its own impulse triggers and takes the controller focus: palms silent while the game is in front, back when RW3 is clicked. Experimental option in the DualSense panel, "kolu GameInput'tan da gizle" (`XboxStrictHide`, `VirtualDualSenseBridge.StrictHide`): never allows GameInputSvc (takes back RW3's own allow), reads the pad through XInput, drives the body motors through XInput and the trigger motors through the Sensa frame only. Needs a replug (or mode switch) and a game restart, since HidHide applies to new opens. Untested in a game; if the game has no DualSense support it gets no controller.
  - Not yet felt: "Oyun sesinden" in a real game.

- **Sensa without Synapse:** own frame encoder + HID feature report 4 ("Direct USB"), over the cable and the HyperSpeed dongle.
  Synapse/HAR remains an optional legacy backend, used only when Synapse runs and has attached the controller.
- **Virtual DualSense bridge:** needs administrator rights. The game sees a DualSense: buttons/sticks from the Wolverine (XInput in PC mode, GameInput in Xbox mode, live readback check), haptic PCM in, trigger reports in.
  HidHide hides the Wolverine from games while it runs and is restored on stop.
- **Controller mode:** PC → Xbox can be switched from the app (Synapse's command, replayed). Xbox → PC only with ○ + Menü + A (2 s).
- **Trigger motors** exist only in Xbox mode, and work while the game is in front only through GameInput (WGI vibrates only for the foreground window).

## Everyday use

- Start RW3 from the Start menu / shortcut, or `explorer.exe "<path>\RW3ProNativeDSHaptics.exe"`. A start from a packaged app (Claude/Codex) relaunches itself so settings and log stay in one place (`PackageContext`).
- Selecting "Oyunun DualSense titreşimi" starts the virtual bridge by itself. The first time, it offers the UAC-free task once (one UAC prompt); after that the bridge starts without a prompt, on selection and at app start. The only manual control is the start/stop button under that mode; Settings shows status only.
- Native v2 demos (Wolverine) follow the setup: through the running bridge (the bridge stays up), else Sensa in PC mode, else the body motors in Xbox mode. In Xbox mode the body motors give strength only: no 30-400 Hz pitch and weak left/right. The demos' intended feel needs PC mode (Sensa); the app says so after a demo.
- Testing without a game: Test → "Oyun simülasyonu" sends game-like haptic PCM + trigger reports and reports each stage (bridge, Sensa / Xbox motors, mic).
- **General calibration** (Test → "Genel kalibrasyon testi", since 2026-11-11). A blind, controller-driven test in PC or Xbox mode:
  - Trials: shuffled locate trials (one or two actuators, varied strength / pitch / texture, 3 changing choices including mixes and "nothing", plus a fixed 4th "Orta / yerini ayıramadım" since 2026-11-14; each stimulus twice) and "1 or 2: stronger / better" pairs in both orders.
  - Controls: X/A/B answer, Y = Orta (locate trials only), View (⧉) replays, Menu stops; keys 1/2/3/4, R, Esc.
  - Output: raw data and a summary (accuracy per actuator with "orta ×N", ORTA share, sent → chosen, consistency, threshold, pair verdicts) in `%LOCALAPPDATA%\RW3ProNativeDSHaptics\calibration\`.
  - Stops the running mode while it runs.
- Settings and log: `%LOCALAPPDATA%\RW3ProNativeDSHaptics\` (`settings.json`, `logs\rw3.log`).

## Verified on hardware (webcam mic, medians)

- Direct USB = Synapse path within ~1 dB.
- Hybrid places mixed 80+190 Hz correctly.
- Dongle works (Direct USB).
- Xbox-mode trigger motors: GameInput from the background +27/+19 dB; WGI in the background 0 dB.
- PC mode trigger motors: unreachable.
  - XInput has no trigger channel; WGI gives 0 dB.
  - GameInput's trigger channels (2026-11-09) give +12.5 dB at a separate 222 Hz actuator (body motors: 76 / 62 Hz), but the user felt it outside the triggers.
  - Repeated 2026-11-10 (2 runs, mic): left and right trigger channels are identical at every strength (+5.5 / +6.8 / +12 dB at 25 / 50 / 100 %, peak 68–100 Hz) and the pitch does not rise with strength as the real trigger motors do (130–234 Hz in Xbox mode). GameInput emulates trigger rumble on the body motors in PC mode; it does not reach the trigger motors.
  - Firmware 2.04.00 (read from Razer's updater without running it, 2026-11-10): the haptics MCU has two Sensa descriptors. In PC mode it reports 2 body parts (palms 216/116, 3 bands, 4 points, 30–400 Hz); otherwise (Xbox mode) 4 body parts (palms with 2 bands / 2 points, 20–300 Hz, plus 249 and 149, 0–300 Hz, resonance 20 Hz, probably the trigger motors). Selected by one config flag at boot. The descriptor is read with Razer class 0x16 cmd 0x90 (length) / 0x91 (offset, 0x4D-byte chunks). So the firmware supports palms + triggers as one haptic stream in Xbox mode, but no Windows route to that stream is known.
- Sensa in Xbox mode: **in the app since 2026-11-11 (dba5a26), setting on by default; not yet felt through a game or the simulator** (docs/research/XBOX_MODE_HAPTICS_ROUTE.md).
  - "Oyunun DualSense titreşimi" in Xbox mode: the game's haptic PCM → palms. Trigger effects → trigger motors, both in the Sensa frame over GIP and on GameInput's impulse channels (the calibration rated both together better, E-20261111-07). The game's normal rumble stays on the body motors (GameInput). Setting off = previous routing.
  - Trigger strength: values above 0 start at 16/63 (felt threshold), and clicks last 160 ms (E-20261111-06). Resistance runs at 85 % (user: 45 % too weak).
  - Weapon and bow effects (all modes): the vibration rises from 40 % at the zone start to 100 % at the break point, then a strong click, then nothing, like a stiffening trigger that breaks. No physical resistance: the Wolverine's triggers only vibrate.
  - The same setting also sends Xbox-mode Native v2 demos and the Wolverine motor test to the palms through Sensa (2026-11-11).
  - One Sensa frame drives **palms and trigger motors together**: body parts 216/116 = left/right palm, 249/149 = left/right trigger motor. Confirmed by a blind hand-held test (E-20261111-04).
  - The route: Razer packets tunnelled in GIP message 0x0F, answers in 0x10. Our app can send them through `Windows.Gaming.Input.Custom` (interface `{AAE22FB7-…}`).
  - Read-only so far: in Xbox mode the controller answers Sensa class 0x16 and serves a 4-body-part descriptor (palms + 249/149, probably the triggers; 2 bands / 2 points).
  - The 4-body-part frame layout (docs/SENSA_USB_PROTOCOL.md) works on the controller: palms play the sent pitch, and the trigger motors follow strength.
  - The controller exposes only a GIP device: no Razer HID, and Razer's haptics service sees no device.
  - GIP vendor messaging (`GipGameControllerProvider.SendMessage`) is reported not to work and Razer's GIP commands are undocumented, so we did not probe the firmware blindly.
  - USB captures, read-only:
    - PC → Xbox switch (2026-11-08, re-read 2026-11-10): Synapse sends only reads, then the switch command. No Sensa-off command.
    - After re-enumeration only the Windows GIP driver talks to the controller (ack, power, auth, LED, rumble stop), nothing Razer-specific.
    - Synapse does not show the controller in Xbox mode at all (user, 2026-11-10). The 2026-11-10 capture has no Xbox-mode traffic from it.
  - Vibration strength command (PC mode, captured 2026-11-10, not yet sent by RW3): class 0x0C cmd 0x06, arguments [03, channel, percent].
    - Synapse writes channels 5, 6, 7 and 8 together from one slider (100/83/67/50/33/17/0 %).
    - Channel map (mic, PC mode, 2026-11-10, 2 repeats). Setting a channel to 0 % cut the matching actuators by 15-19 dB, down to the room floor; the other channels changed nothing (within ±1 dB):
      - Channel 5 = left side: big body motor and the left Sensa palm.
      - Channel 6 = right side: small body motor and the right Sensa palm.
      - Channels 7 and 8: no effect on the body motors or Sensa. Most likely the trigger motors, which are unreachable in PC mode.
    - All channels were restored to 100 % afterwards.
  - The controller's GIP headset audio (endpoint "Kulaklıklar (Xbox Controller)") does not drive Sensa. Tested 2026-11-10: tones at 40–1000 Hz, with and without a headset in the 3.5 mm jack. The audio reached the headset, but the controller did not vibrate (felt by the user, and no tone peak at the mic without a headset).
- Virtual DualSense input matched the Xbox pad in every readback report.
- Body motor ticks (2026-11-10, mic, 2-8 kHz spikes in 5 ms bins, XInput; TestResults/body-motor-ticks-20261110):
  - The source is the big (heavy, left) body motor at full power. In Xbox mode, steady 1.0 gave 24-29 ticks/s with the high band +15..17 dB, in all 3 runs. The same motor in PC mode at 1.0 ticked in 1 of 2 runs (6.5/s).
  - At 0.35-0.8: sporadic, not repeatable (0-4/s, once 8.7/s at 0.8).
  - The small motor, low levels (start threshold), the update rate (re-sent at 20 Hz or 200 Hz) and audio-like envelopes: no ticks.
  - Capping at 0.75-0.8 costs about 3-4 dB of the big motor's maximum (overall mic level -20 vs -23.4 dBFS).
  - Ceiling setting (BodyMotorLimit, default off since the hand test): up to 0.6 the command is unchanged, above it approaches 0.75.
    - Mic, controller on the desk: 1.0 gave 22-30 ticks/s; 0.74 gave 10-15/s.
    - In a second sweep, 0.45-0.7 also ticked irregularly (0-14/s), although the first sweep had them nearly clean.
    - The motor ticks reliably only at full power. In the middle the ticks are position-dependent, probably the pad touching the desk.
    - Held in the hand, next to the mic (3 runs × 1.0/0.74/0.65/0.55/0.45; the vibration reached the mic at -30..-40 dBFS against a -44 dBFS room): 1 tick in total, none at full power.
    - The ticks therefore come from the pad rattling against the surface it lies on, not from the motor itself. The ceiling only matters while the pad lies on a table, so it is off by default.
  - Why the user hears it only in Xbox mode: there, audio mode drives the body motors with gain 2.5, which often reaches 1.0. In PC mode the audio goes to Sensa instead.
- Sensa overdrive check (2026-11-10, mic, both palms, steady drive):
  - Sweep: 30/50/80/120/190/300 Hz at strengths 16/32/48/63. Strength 63 is the top of the encoder scale, the same maximum Synapse/HAR uses.
  - No tick or bottoming anywhere: the 1.5-6 kHz band stayed within ±2.4 dB of the room.
  - The top end compresses gently instead: at 120-300 Hz, 48 → 63 adds 0 to +5 dB.
  - Below ~50 Hz little reaches the mic at any strength.
  - So RW3 cannot push Sensa past its range. An extra limiter is not needed for steady signals.
- Trigger motor frequency (Xbox mode, GameInput, 2026-11-10, mic; 4 good runs, 2 orientations). The pitch follows the strength:
  - Left trigger: ~130 Hz at 25 %, ~176 Hz at 50 %, ~234 Hz at 100 %.
  - Right trigger: ~108 / ~150 / ~234 Hz.
  - So the right motor turns slower at low and mid strength, and the two are equal at full.
  - Level differences flipped with the pad's orientation, so they are mic placement. Averaged, the levels are equal except the right trigger at 25 % (~2.5 dB weaker).
  - Body motors: big 76 Hz, small 188 Hz.
  - A DualSense trigger frequency cannot be reproduced: only strength is commandable.
- Xbox-mode audio option (2026-11-10): bass (< 120 Hz) → heavy motor, treble (> 300 Hz) → light motor. Mono, no left/right. Selectable and effective only while the controller is detected in Xbox mode. Unit-tested, not yet felt.

## Open / not verified

- Xbox-mode trigger effects in the bridge after the GameInput input fix: software-checked, not yet felt by the user.
- Xbox audio stereo fix (0346115): unit-tested, not yet felt by the user.
- Transient records (2026-11-10): Direct USB now writes the decoded record (strength, 24, time, tail 100) for the first hit of a frame instead of folding it into the points. This is a setting, on by default.
  Mic A/B (TestResults/transient-records-20261110, Standard, 5 repeats per scene, medians):
  - Repeated hits: same peak (+24.3 dB) and same -6 dB width (80 ms). Better contrast between hits (p90/p50 +17.9 vs +15.6 dB).
  - Character: energy moves from 120 Hz (×21 vs ×27) to 190 Hz (×5.3 vs ×2.0), a crisper click.
  - First hit after silence: ~4.5 dB weaker and ~10 ms later.
  - Mixed and stereo scenes: unchanged.
  - Not a clear mic win, so the feel decides (Ayarlar → "vuruşları darbe kaydı olarak gönder").
  - Fixed 2026-11-11 (firmware decoder, emulated): the old record layout shifted the right hand 3 bits whenever the left hand had a hit. The A/B above used that layout and should be repeated.
- Spectral history: multi-resolution since 2026-11-09. Components >= 60 Hz come from a 42.7 ms window, the bass from 85.3 ms. The 240-case audit passes 240/240 (max 0.36 Hz error); the plain 2048 window failed 55 cases. Hybrid enters multiband after 21.3 ms + 2 steady frames. Mic comparison 2026-11-09, Direct USB, 45 cases (TestResults/multires-physical-20261109), medians:
  - Mixed 80+190 Hz, Hybrid: 80 Hz ×13.6, 190 Hz ×10.9, wrong band ×2.2.
  - Multiband: onset in the first 300 ms +4.1 dB (old run ≈0); short hits +9.8 dB (old run +6 dB).
  - Hits under Hybrid unchanged versus Standard.
  - No regression found.
- Mode orchestration is in `HapticOrchestrator` (decisions in `HapticModePolicy`, contract-tested).
  `MainWindow.xaml.cs` is still large (~1,860 lines). The next split candidates are the test/simulator page and the settings page.
- **Audio mode "Standart (DSX gibi)"** (2026-11-14, default on, also for existing settings): the audio becomes vibration as it is, with no gain, compression, auto level or hum stage. A soft noise gate on the < 250 Hz band (threshold ≈ -30 dBFS, opens in 1 ms, releases over 80 ms, expander below) stops the vibration in near-silence.
  - Why: the user reported audio mode "vibrated all the time and never stopped" while playing. Their settings had auto level + hum suppression on: auto level lifts quiet content up to ×4 toward -20 dBFS, and hum suppression only lowers a steady bed to 40 %.
  - DSX reference: its saved audio-to-haptics profile (DSX 3.1.7, user's install) has only per-motor intensity (0–10) and speaker/headset volume, no threshold or level stage.
  - Contract-tested (unity above the gate, -43 dB tone -19.7 dB, a hit after near-silence passes at 0.99, auto/hum ignored while on). Not yet felt in a game.
  - Auto level and hum suppression remain as options; they apply only with Standart off.
- Audio mode level shaping (2026-11-10, both off by default; mode screen → "Seviye"). Not yet felt or mic-tested. Contract tests:
  - Auto level: follows the haptic band (< 250 Hz) over ~3.5 s toward -20 dBFS, gain ×0.5..×4. It turns down in 0.4 s and up in 3.5 s, and holds the gain in silence. A 12 dB input spread leaves 1.7 dB.
  - Hum suppression: a steady sound fades to 40 % over a few seconds (-6.4 dB on an 8 s drone). A hit 1.5× above the drone baseline opens it fully in 2 ms.
  - On-screen sound tests bypass both.
  - Mic, 2026-11-10 (TestResults/level-shaper-20261110). Real shaper → SensaAudioSink → Direct USB path. Scene: 60 Hz drone + 120 Hz hit every 0.5 s, at source -12/-6/0 dB, 2 repeats, medians:
    - Auto level works. The spread across the three volumes fell from 6.4 dB (off) to 1.4 dB. Hit contrast is unchanged (+15.9 vs +15.4 dB).
    - Auto + hum at -6/0 dB gave the best hit contrast: +18..23 dB, against +15..20 off.
    - At -12 dB, hum lowered the contrast: hum alone +6.5/+8.5 dB, auto+hum +11/+11.5, against +13/+14 off. Not explained yet.
    - The repeats differ by up to 5 dB, so the hum result is only indicative.
- YouTube seek recovery (audio mode) unresolved; see the dated reports.
- GameInput output dropouts (2026-11-10): explained. They happen while another GameInput/gamepad app (e.g. Opera) is in the foreground: GameInput then gives output focus to that window. The user has seen this before. Not a probe or app bug. It matters for the bridge only if a browser is in front during play.

## Code map

- `src/RW3ProNativeDSHaptics/`: the app.
- `src/RW3Pro.Lab/`: audit/measurement code used only by tests and the hardware probe.
- `tests/RW3Pro.ContractTests/`: device-free checks; every check runs and failures are listed at the end.
- `tests/RW3Pro.HardwareProbe/`: hardware measurements (mic, USB, Sensa frames).

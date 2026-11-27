# Original GPIO latch and calibrated busy-wait replay

2026-11-23. Offline NXP v2.04.00, pinned `Dev2FW.bin` and the unchanged
simulator-only DSP module. No hardware access, firmware upload or driver change.

## Original execution and explicit fixtures

The preceding [RGB/SPI replay](RGB_SPI_REPLAY.md) stopped at the byte write
`0xD7F8 -> 0x4008C028`. The new replay declares an explicit P1_8 output
latch, initially zero. After the original high-input filter accepts P3_17,
the stock code writes one to that latch and calls the original routine at
`0xD620` with argument 200. Both supplied P0_23 levels are covered.
Short-high and later-rise scenes exercise the original filter rather than
calling its output function directly. Physical pin names/roles, direction,
pin mux and electrical behavior remain unknown; this is a byte-latch model.
Existing input-only GPIO fixtures remain strict and unchanged by default.

The delay routine is a calibrated CPU busy loop, **not** an MRT-counter wait.
Its argument is not proof of milliseconds. The fixture supplies a 96 MHz
`SystemCoreClock` value at `0x200000A8`. An independent arithmetic oracle
checks the outer iterations and the inner multiplier/shift calculation.
Argument 200 executes 9,611 foreground instructions; its PC/size trace hash
is `fa30045a236b3cd48ede762c9bb58b2d03e53b898ac08f362c9af22a83a547e9`.

Only this busy wait and its intervening handlers advance the new instruction
clock. Explicit budgets of 1, 3 or 7 cycles per observed instruction drive
the existing MRT, motor timers and WWDT; they are **supplied sensitivity
conditions, not measured Cortex-M4 instruction timing**. The original loop
trace must remain identical across those conditions. IRQ/FP setup restarts
are not charged twice. Other foreground time, USB receive calls, DMA
completions and SPI clocks retain their earlier boundary-supplied schedule.
This is not a continuously cycle-accurate whole-board scheduler.

The mask test retains MRT level/coalescing behavior. Releasing PRIMASK
delivers pending levels, not one interrupt per missed expiration. Therefore
the stock counter still cannot serve as an independent elapsed-time safety
clock during masking. The existing watchdog limitations remain open.

## Validation and controls

The [public validation](validation/20261123-gpio-delay.json) records stage
provenance explicitly: four stages executed afresh, nineteen reused after
checking pinned inputs, baseline tool bytes, transitive local import
dependencies and previous report hashes. The affected old foreground,
I2C and RGB stages must reproduce their earlier reports exactly. The
standard suite now includes all 23 stages for a full rerun. Reused results
are not described as 23 fresh executions.

The complete new stage is also executed under optimized Python and compared
with the normal run. Guards use explicit exceptions, not removable asserts.
The focused GPIO controls check declared latch read/write, independent
input preservation and write tracing, plus seven invalid operations.
Delay controls cover arguments 0, 1, 200 and 1000, invalid budgets,
zero/changed core clock, deliberately skipped original code and an
exhausted instruction limit. Corruption is confined to emulator memory.

Six active cases cover stereo, each mono side, the other P0_23 level,
short-high and later-rise input. Each runs actual stock foreground Sensa
dispatch, SPI/I2S IRQs and the compiled publisher, checks its emitted frames
independently, and drains to a separate stock-idle PWM reference. Mono
cases require opposite palm and both trigger outputs to stay idle.

## Falling-input boundary and next work

With P3_17 lowered at iteration 70, both P0_23 fixtures stop after 89
completed iterations at PC `0x10BB6`, reading `0x40088800`. The original
driver context selects FLEXCOMM2 I2C (`0x40088000`); the original caller
at `0x21AE8` targets slave address `0x1A` with two-byte register/value
commands. This is a different controller/peer from the earlier FLEXCOMM3
`0x6B` fixture. Its physical chip identity and responses are not established.
No invented ACK or callback is supplied. The latch has only its high write
at this point: **full release/power-down has not completed**.

The later [FLEXCOMM2 extension](I2C_RELEASE_REPLAY.md) now executes
original initialization, IRQ16 and the five raw writes through the low
latch write in its explicit ACK fixture. This original stage retains
its own unmapped-I2C controls unchanged. Full-audio NACK recovery then
stops at 0x9FB2/0x400A000C; physical peer and timing remain unknown.

The original next-step record was to model this controller from its initialization/driver state,
retain NACK/timeout/IRQ controls and follow the original falling path to
its low write. Confirm the board peer separately before treating register
acceptance as hardware evidence. SPI error propagation, continuous event
timing, full boot, independent stop safety, resident bootloader acceptance
and recovery remain open. Whole-device equivalence and a flashable custom
image remain unproven.

## Reproduce

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_power_gpio.py <Dev2FW.bin> <audio_haptics.bin>
python -O -B tools/firmware/replay_nxp_power_gpio.py <Dev2FW.bin> <audio_haptics.bin>
```

Use locally extracted, hash-pinned images and the existing reproducible
module build. Raw stock binaries and captures are not distributed.

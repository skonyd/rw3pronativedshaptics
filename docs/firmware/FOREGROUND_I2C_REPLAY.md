# Foreground I²C integration

Status: **prototype, bounded offline fixture**, 2026-11-22. This extends
[foreground/GPIO replay](FOREGROUND_LOOP_REPLAY.md); no hardware access,
firmware flashing or new loadable image.

## Missing initialization and original execution

The earlier three alternate raw-gate states stopped at the FLEXCOMM3
configuration read. Mapping that peripheral alone is insufficient:
the audio fixture had not run the stock completion-callback registration.
Transfers entered the real IRQ17 handler, but their wrapper's wait flag
remained set after the SDK transaction completed.

`replay_nxp_loop_i2c.py` now executes the original registration at
`0x10D70` with the original `0x117F5` callback. Stock startup writes this
same callback into its driver context. The host does not copy opaque
startup RAM, write completion flags or replace an original I²C routine.
Original SDK handle creation enables IRQ17; its restored vector handles
the modeled level source through memory-backed Cortex-M4/FP exceptions.
Suspended core and FP state is checked at every return. IRQ17 priority
`0x20` is an explicit inherited lab choice, not a measured board priority.

The peer remains **explicit synthetic data**, at address `0x6B`, identity
register `0x0B`. No physical chip identity is inferred from that address.
Accepted identities initialize nine registers with the same values as
the earlier startup replay. A successfully configured peer is not
initialized again in the following supplied loop iterations.

## Bounded comparisons

Seven stock/inactive-candidate scenario pairs exercise the actual loop:

| Scenario | Iterations per variant | Original behavior |
| --- | ---: | --- |
| Identity `0x53`, `0x50`, `0x57` | 8 each | One identity read, nine configuration writes, 51 I²C IRQ entries |
| Identity `0x4F`, `0x58` | 8 each | Three identity attempts per iteration, no configuration writes, 144 I²C IRQ entries |
| Address NACK | 1 | Three unsuccessful attempts, six I²C IRQ entries, original timeout cleanup |
| Bus IRQ suppressed | 1 | No I²C IRQ entry, three original timeouts |

That is 84 complete iterations across both variants and 3,546 checked
interrupt returns. Each pair compares 81,192 stock RAM bytes, persistent
registers, PWM duty values, command/data transactions and watchdog feed
counts. Every completed iteration reaches its own original Sensa
dispatcher. This stage supplies no PCM and tests the candidate inactive;
active audio remains separately covered by the earlier raw-gate fixture.

Timeout tests supply one logical 125 µs clock quantum per observed
busy-wait visit. MRT, motor timers and WWDT advance together. Original MRT and motor
vectors execute; the host does not decrement the timeout byte. WWDT
counts without reaching its warning or reset boundary in these cases. Each
fault scenario takes 240 supplied quanta across three ten-tick waits.
This is **not measured instruction time, I²C wire timing or latency**.
Successful transfers receive immediate register-state transitions and
do not need those artificial wait-clock events.

## Strict boundary and negative controls

The new wrapper reuses the earlier START/CONTINUE/STOP peer model and
adds a strict master-register subset. It guards the entire mapped
FLEXCOMM3 page, including IRQ accesses. Unsupported widths, offsets,
slave aliases, slave mode, interrupt selections and wide data fail.
Missing identity data fails explicitly; it never becomes a zero reply.
Without callback registration and with a frozen wait clock, the bounded
run fails while the original wait flag is still set. This control would
catch a fixture that silently invented completion.

The new completed-state tests set raw fields `0x200019C0=1` and
`0x2000A538=0`.
The previously covered `(1,1)` state remains in the earlier stage. The
two `(0,0)` / `(0,1)` cases now complete their exercised I²C work and
stop at **PC `0x20672`, read `0x4009AE00`**. They are not full-loop passes.
That address is FLEXCOMM9 FIFOCFG. The failing original transfer uses
the stock SPI SDK routines (its base-instance lookup at 0x202BC resolves
FLEXCOMM9). The FIFO/DMA state and actual peer still need integration;
the already modeled runtime radio SPI path uses separate FLEXCOMM5.
The raw flag meanings remain unverified.

Register layout reference: NXP's
[LPC54605 header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h),
I²C master registers at FLEXCOMM base + `0x800` and FLEXCOMM9 base
`0x4009A000`. This model does not implement electrical arbitration,
clock stretching, peripheral error injection, sleep/reset effects or
all I²C registers merely because their addresses are mapped.

## Validation

[The public summary](validation/20261122-loop-i2c.json) records 21/21
passing stages, unchanged preceding 20 reports and identical normal/
optimized new-stage results. A tool connection/process interruption
stopped the first execution after 17 stages. Those completed reports
were checked against the previous validation; pinned inputs and tool
sources were checked before the last four stages resumed. This is
recorded as execution recovery, not a single uninterrupted run.

The later [RGB/SPI extension](RGB_SPI_REPLAY.md) executes the original
FLEXCOMM9 initializer, FIFO descriptors and callbacks in selected GPIO
states. The missing-SPI controls in this older stage are retained as
original fixture boundaries. GPIO output/delay, real pin meanings and
full production startup remain open in the extension too.

## Reproduce and next step

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_loop_i2c.py <Dev2FW.bin> <audio_haptics.bin>
```

The compiled DSP module remains byte-identical (`f2c5f153...`). The suite
includes this as a separate stage, retaining the old fixtures and their
reported unsupported boundaries. Next connect the actual FLEXCOMM9 SPI
driver/FIFO/DMA/peer state, extend active audio to the new gate state, identify
runtime flags and close whole-board event scheduling. Full boot, USB
receive IRQ delivery, independent elapsed-time audio safety, physical
PWM, image placement, bootloader acceptance and recovery remain open.

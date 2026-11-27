# Original FLEXCOMM2 falling-input release replay

2026-11-27. Offline NXP v2.04.00 and the unchanged simulator-only compiled
DSP module. No controller access, flashing, driver installation or SDK update.

## What now executes

The [GPIO/busy-wait stage](GPIO_BUSYWAIT_REPLAY.md) retained an unmapped
read at `0x10BB6 / 0x40088800` after P3_17 fell. A separate extension
now executes the original FLEXCOMM2 driver and IRQ16 to complete that
selected branch with a synthetic write-only peer at address `0x1A`.
The earlier stage keeps its original boundary controls unchanged.

Original driver table `0x34180` supplies Initialize `0x10AC8`, PowerControl
`0x10AE0` and MasterTransmit `0x10B8C`. The fixture calls Initialize with
the original wrapper callback `0x117E1`, then PowerControl(FULL). Original
code writes clock-enable `0x40000224=0x2000`, reset-set `0x40000124=0x2000`,
reset-clear `0x40000144=0x2000`, and PSELID `0x40088FF8=3`.
The replay checks that complete write sequence and selected mode. PSELID's
I2C-present bit is an explicit fixture; pin mux, bus speed and physical
clock/board state are not validated by it.

PowerControl's zero return alone was insufficient: without a modeled
capability read, it could return zero without selecting I2C. The new
guard requires actual selection, and a missing-capability control fails.
No driver completion flag or opaque initialized context is copied in by
the host. The original master handle setup, interrupt vector, driver
callback mapping and application callback execute in the emulator.
The parent's steady-state RAM/prologue and other peripheral fixtures
remain; this is not a completed reset-to-main boot.
The stock callback adapter is shared with FLEXCOMM3; status telemetry
is filtered by the actual FLEXCOMM2 peripheral base, so the other bus's
successful startup/configuration calls cannot inflate its result.

The independent byte oracle expects five writes after a filtered fall:

| Address | Bytes | Register/value interpretation of the original packing |
| --- | --- | --- |
| `0x1A` | `69 C0` | `0x34 / 0x1C0` |
| `0x1A` | `6B C0` | `0x35 / 0x1C0` |
| `0x1A` | `02 00` | `0x01 / 0` |
| `0x1A` | `04 00` | `0x02 / 0` |
| `0x1A` | `06 00` | `0x03 / 0` |

The peer only accepts bounded raw two-byte writes. It supplies no read
data and invents no codec register behavior or physical chip identity.
The original caller performs its delay and writes P1_8 low. This follows
the selected software release sequence, not proof of board power-down.

## Controls and preserved failures

Fifteen register/protocol controls reject reserved/unknown reads, implicit
data responses, wrong widths, slave mode, unknown interrupts, wide data,
incorrect/locked peripheral selection, missing START, repeated START and
overlong writes. Capability, pending/set/clear aliases and raw byte
retention have separate positive checks.

Direct original-wrapper tests distinguish bus transfer from application
completion. Address NACK yields original driver status `0xA33` and callback
value `16`, followed by retries/timeouts. Suppressing IRQ16 cannot invent
a completion. Initializing with a null wrapper callback can transmit all
bytes three times while the wrapper never acknowledges success. Deferring
IRQs for 88 supplied wait quanta permits the original retry to recover.
Frozen-clock and PRIMASK controls hit a bounded instruction stop while
the original busy flag and timeout remain pending; that stop is not a
firmware abort or motor safety mechanism.

With active audio and all five address writes rejected, the original path
still writes GPIO low, then reaches an unmodeled read at
`0x9FB2 / 0x400A000C`. The test uses a declared five-million-instruction
budget to reach this boundary; a smaller exploratory budget stopped during
IRQ work first. The larger budget is not a timing claim. The full-loop
recovery case is explicitly **not accepted as a completed run**. GPIO low
alone does not prove successful I2C configuration or error recovery.
The existing [NXP register header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h)
identifies `0x400A0000` as ADC0 and offset `0x0C` as `SEQ_CTRL[1]`
(sequence B); the following original code sets its START bit 26.
The earlier `AdcModel` in `replay_nxp_analog.py` models sequence A at
offset `0x08`, so plugging it in unchanged would miss this boundary.
A second all-ACK fixture also reaches the same ADC channel-8 boundary
after 199 complete iterations in a requested 255-iteration run. Both
high/low cycles and all ten I2C writes finish first. Thus NACK is not
required to reach it. These runs still do not isolate elapsed-counter
effects from the changed foreground state; the ADC visit is not labeled
as a NACK-specific error handler.

The later [ADC-B extension](ADC_B_FOREGROUND_REPLAY.md) follows
these two retained boundaries with explicit samples/readiness and the
original configuration, reader and averaging code. Selected longer
and NACK fixtures now return. This original stage keeps its own
unsupported-ADC contracts; missing-conversion full recovery remains open.

## Validation and timing limits

[Public validation](validation/20261127-release-i2c.json) records one new
stage executed with both normal and optimized Python. The other 23
reports are reused after checking all six inputs, prior tool bytes,
transitive imports, report hashes and runtime versions. No old stage
imports the changed runner or this new module. These are not 24 fresh
runs; the standard suite includes all 24 for a complete reproduction.

The candidate-disabled pair compares 81,192 shared RAM bytes, persistent
registers, PWM, PCM and transactions after real original-loop execution.
Seven bounded active scenes cover stereo, independent L/R, both P0_23
levels, short-low filtering, two high/low cycles within 159 iterations,
and delayed IRQ delivery. The longer repeated-cycle failure is retained
separately and is not hidden by the shorter positive fixture.
They require actual foreground Sensa dispatch, independent runtime frame
checks, mono opposite-palm/trigger isolation and return to stock-idle PWM.
The stock-idle trace from the inactive pair is reused for those scenes;
it is not reconstructed from candidate output.

Wait-loop visits supply logical 125us timer/motor/WWDT quanta. They do
not deliver concurrent USB/DMA arrivals during a long foreground stall.
D620 retains its separate supplied instruction budget. Neither mechanism
is continuous board timing. Extend the `0x400A000C` path, actual event
arrival scheduling and independent elapsed-time stop before claiming full
error recovery. Physical peer identification, complete boot, radio/USB
timing, bootloader acceptance and safe hardware restoration remain open.
Whole-device equivalence and a flashable custom image remain unproven.

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_release_i2c.py <Dev2FW.bin> <audio_haptics.bin>
python -O -B tools/firmware/replay_nxp_release_i2c.py <Dev2FW.bin> <audio_haptics.bin>
```

Use locally extracted, hash-pinned inputs. Stock firmware and captures are
not distributed in Git.

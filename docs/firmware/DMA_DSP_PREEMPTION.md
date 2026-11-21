# NXP DMA and DSP preemption replay

Status: **prototype, offline only**, 2026-11-22. This extends the
[bounded Cortex-M4 IRQ/FP model](CORTEX_M_IRQ.md). It does not access a
controller or create a flashable image.

```powershell
python -B tools/firmware/replay_nxp_dma_preemption.py <Dev2FW.bin> <audio_haptics.bin>
```

## Execution path

The stock DMA0 vector (IRQ1) executes the original driver at `0xC478`,
its I²S completion callback and application TX callback. The candidate's
compiled TX tap runs inside that IRQ. Stock CTIMER3 (IRQ13) then preempts
DMA, and CTIMER2 (IRQ36) preempts the FP-using CTIMER3 routine. The
original handlers return through memory-backed exception frames to the
interrupted DSP worker.

The test priorities are deliberately supplied as DMA `0xC0`, CTIMER3
`0x80`, CTIMER2 `0x40`. They exercise three nesting levels and are **not**
claimed to be the stock startup priorities. Event arrival is injected
at instruction boundaries, not derived from physical bus clocks.

Foreground `execute` now stops at an eligible external IRQ, services
it, then resumes the same instruction. Its instruction budget includes
handler execution. `Replay.call` can opt into this execution model;
existing direct-call tests remain separate references. Level sources
are sampled during IRQ selection. An acknowledged source deasserts;
an uncleared level can re-pend and eventually exhaust the bounded
instruction budget instead of falsely completing.

`CheckedInterrupts` saves register values **only for assertions**.
There is no `context_save`/`context_restore` or host register restoration
in the new test. Every return checks general registers, SP/LR/PC/xPSR,
CONTROL and the caller-owned FP state. S16–S31 must always survive;
the low FP registers/FPSCR must survive when the interrupted context
has FPCA set. The model's architectural unstacking restores the actual
stack words. Injected changes to stacked R0 and S0 must fail these
assertions.

## DMA status boundary

`nxp_dma_status.py` covers channel interrupt enables, A/B/error status,
write-one acknowledgements and IRQ output. Descriptor transfer and
FIFO progress are supplied by the existing continuous-DMA fixture.
It is not a complete DMA peripheral model.

The [NXP LPC54605 header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h)
defines IRQ1, the 30 channels, offsets and summary masks. NXP's
[UM10912 rev. 1.7, chapter 12, sections 12.6.8–12.6.12](https://community.nxp.com/pwmxy87654/attachments/pwmxy87654/lpc/58795/1/lpc546_series.pdf)
documents status acknowledgement and enable aliases. Errors contribute
to IRQ output independently of the A/B interrupt-enable mask.
Global status uses bit 1 for enabled A/B and bit 2 for error status.

An important unresolved boundary: the manual defines `INTENCLR` as
write-only, yet stock code reads it in read/modify/write sequences at
`0x12AA4` and `0x12FB4`. No physical readback value is established.
The model therefore requires an **explicit fixture value**. Tests
supply `0`, `0x80` and `0xFFFFFFFF`; the read count and value are reported.
Success under these inputs is not proof of the actual readback or of
all other DMA channels remaining enabled. In particular, an all-ones
read can change enable state on channels outside this TX exercise.
In three separate modeled setup probes, supplied readbacks `0` and
`0x80` leave INTENSET at `0x6000` (channels 13/14); `0xFFFFFFFF` leaves
`0x2000` (channel 13 only). Each setup reads INTENCLR four times. This
is an observed dependency on an unknown fixture input, not a physical
register measurement. The TX path passing all three does not validate
the separate channel 14 path.

## Checks

Seven register checks use original Thumb load/store fixtures to verify
enable aliases, bank-isolated W1C, zero-write behavior, retained masked
status, error IRQ output, summary bits and explicit unknown inputs.

Six continuous cases compare architectural IRQ delivery with the
retained direct-call path: stock/candidate variants for left-only,
right-only and mixed tones with an input pause. Each plays 96 original
192-byte PCM blocks across three descriptor addresses and drains to
silence. The pause causes a repeatable eight-tick PCM silence gap.
Comparisons cover every PWM snapshot and DSP level, 81,192 initialized
stock RAM bytes and the PCM delivery result. Candidate output must
actually change the expected palm(s), preserve opposite/trigger
outputs and clear levels at drain; two equally silent paths cannot
pass that positive control.

Twelve worker cases inject DMA at five compiled-instruction positions
or immediately after mailbox release. They include eager/lazy FP
preservation, PRIMASK, BASEPRI and disabled NVIC delivery, and the two
additional undefined-read inputs. A busy mailbox must keep the first
block while audio TX continues. After release, a subsequent original
DMA completion must publish the expected next block. DSP state/levels
must match uninterrupted compiled-code references, source PCM must be
unchanged, and a missing next block must clear private state/output.
Each nested run checks the order `1,13,36,36,13,1`.
The one-slot tap intentionally skips a new haptic copy while busy.
Unchanged audio playback and a preserved in-flight block do not prove
zero haptic input loss. Sustainable worker timing still needs an
independently validated event schedule and worst-case execution budget.

Other guards cover a spurious IRQ with no completion, an error that
retains the queued I²S buffer, success/rearm after that error, a held
IRQ level exhausting the budget, and detection of deliberately
corrupted core/FP stack words. The original handler also acknowledges
an error when the channel's normal A/B interrupts are disabled, without
retiring that buffer.

## Remaining work

- Resolve the physical write-only-register readback and independently
  observe interrupt priorities, arrival times, FIFO and descriptor pacing.
- Candidate band frames are still assembled by the harness. The compiled
  module contains DSP/tap/worker/reset code, not the complete autonomous
  Sensa publisher or a validated allocation in a loadable firmware image.
- General system exceptions/fault handling, full board startup, the real
  second I²C peer and radio clock acquisition remain unverified.
- Bootloader acceptance, hardware backup/recovery and physical actuator
  behavior remain separate prerequisites. No flashing is performed.

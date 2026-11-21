# Cortex-M4 external IRQ replay

Status: **prototype, offline only**. This extends the register-driven
[startup replay](AUDIO_HAPTICS_REPLAY.md#register-driven-startup-i²c-peers).
The earlier direct vector-call path remains as a separate regression.

```powershell
python -B tools/firmware/replay_nxp_exceptions.py <Dev2FW.bin> <audio_haptics.bin>
```

## Architecture boundary

`cortex_m_irq.py` models basic external interrupt frames. Entry writes
R0–R3, R12, LR, PC and xPSR to the selected stack, observes CCR alignment,
fetches VTOR and sets IPSR/EXC_RETURN. The original handler executes to
its exception return. A Unicorn trap transfers control to the model,
which restores the frame from memory. It does not restore a saved
Unicorn context. MSP, PSP and nested Handler returns are covered.

NVIC enable/pending/active state, software triggering, priority grouping
and mask eligibility select the next interrupt. Higher group priority
can preempt an active handler; subpriority orders pending requests.
The model preserves APSR flags on entry, clears interrupted IT state,
retains handler changes to PRIMASK/BASEPRI and clears FAULTMASK on return.

Architecture reference: [Arm Cortex-M4 Generic User Guide, sections 2.3
and 4.2–4.3](https://documentation-service.arm.com/static/5f2ac4ab60a93e65927bbdbf).
The [NXP LPC54605 header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h)
specifies 57 external interrupts and three priority bits. This model
masks the unimplemented priority bits accordingly.

## Checks

The synthetic instruction fixtures are our own code. They run on the
same Unicorn CPU and use actual load/store, MRS/MSR, CPSID/CPSIE and
exception-return instructions. The helper flushes translated blocks
when replacing its tiny MMIO accessor, so the test executes the new
instruction bytes.

Twenty-two checks cover:

- MSP/PSP with four- and eight-byte stack alignment and exact frame words.
- A handler modifying stacked R0: the returned R0 follows memory.
- A deliberately clobbered R4: it remains visibly clobbered, exposing a
  mistake the old whole-context restore would conceal.
- IPSR, preserved entry flags and the active interrupt bitmap.
- PRIMASK, FAULTMASK, BASEPRI, pending retention and unmasking.
- Priority zero with BASEPRI, enable/pending aliases, priority-bit masking
  and AIRCR key validation/readback.
- Higher-priority nesting, equal-priority deferral, same-group subpriority
  deferral and the IRQ-number tie break.
- A handler leaving interrupts masked, unmasking within a handler and
  automatic FAULTMASK clearing at return.

Eight controls reject corrupted stack state, unbalanced stack, odd
stacked PC, the wrong return stack, an extended FP frame, FP instructions
inside a handler, an endless handler and the unsupported subpriority-only
BASEPRI configuration. Unknown cases fail rather than falling back to a
plain function return.

The same 12 startup scenarios run with both stock and candidate hooks,
using the new frames for the original FLEXCOMM3, FLEXCOMM8 and MRT0 code.
At every supplied interrupt, all general registers, SP, LR, PC and xPSR
must return unchanged. The final comparison also checks 81,192 initialized
stock RAM bytes, SP, peripheral/system-control writes, wait order and
mask state. Entry/return accounting must balance. Peripheral replies
remain the explicitly synthetic inputs of the startup test.
The [2026-11-21 validation](validation/20261121-exceptions.json) records
14/14 suite stages and 1,003 balanced entries/returns in each firmware
variant, with interrupted-register checks on every return.

## Remaining limits

- Peripheral event arrivals and timer pulses are still supplied by the
  harness; this is not a bus-clock or physical interrupt-latency proof.
- The basic model rejects FP frames/instructions. The bounded FP extension
  below covers selected eager/lazy paths; system exceptions, fault
  escalation and MPU behavior remain open.
- Nonzero BASEPRI values whose implemented bits are all subpriority
  are rejected pending further architectural validation.
- Late arrival, cycle-accurate tail chaining, sleep/wake and interrupted
  multi-transfer instructions are not modeled. Deferred requests run
  using a normal return and re-entry.
- The complete startup, actual second I²C peer, RF clock acquisition,
  board wiring, bootloader acceptance and recovery remain open.

No device access or firmware flashing is performed by this replay.

## FP context continuation (2026-11-22)

`cortex_m_fpu.py` adds an opt-in `CortexMFpuInterrupts` model. The basic
model above retains its explicit FP rejection tests. Run the new stage:

```powershell
python -B tools/firmware/replay_nxp_fpu.py <Dev2FW.bin> <audio_haptics.bin>
```

An active FP caller receives a 104-byte frame, plus alignment padding.
R0–xPSR occupy offsets 0–28; S0–S15 occupy 32–92, FPSCR is at 96 and
offset 100 is reserved. The reserved word is left untouched. Extended
EXC_RETURN values are `FFFFFFE1/E9/ED`. S16–S31 are **not** automatically
restored; the stock compiler-generated handler must preserve them.

With lazy preservation, only the basic words are initially written.
FPCAR points to offset 32 and FPCCR.LSPACT records the reservation.
The first executed FP instruction saves the caller's low FP registers
before changing them. A nested FP handler can flush an older thread
reservation even when its immediate parent uses a basic frame. If no
handler uses FP, return cancels the reservation without reading or
writing those FP slots. A new automatic context initializes FPSCR from
the bounded FPDSCR fields. Foreground execution with this model must
use `execute`, which handles the same bounded translation restarts as
IRQ service.

The architectural frame layout was checked visually against figure 2
of [Arm DAI0298A, Cortex-M4(F) Lazy Stacking and Context Switching](https://documentation-service.arm.com/static/5f0352c0cafe527e86f5bc66).
The [Arm generic user guide](https://documentation-service.arm.com/static/5f2ac4ab60a93e65927bbdbf)
defines FPCCR, FPCAR, FPDSCR and CPACR. QEMU's original
[FP context translator](https://github.com/qemu/qemu/blob/master/target/arm/tcg/translate-vfp.c)
and [exception implementation](https://github.com/qemu/qemu/blob/master/target/arm/tcg/m_helper.c)
were used as an additional implementation cross-check; no QEMU code is
copied into this model.

### Corrected Unicorn CPU selection

Unicorn 2.1.4 forcibly chooses Cortex-M33 when `UC_MODE_MCLASS` is set,
even after `ctl_set_cpu_model(CORTEX_M4)`. This was reproduced locally
and confirmed in its [CPU initialization source](https://github.com/unicorn-engine/unicorn/blob/2.1.4/qemu/target/arm/cpu.c#L2113).
Our shared NXP audio `Replay` and core fixtures now select Thumb mode,
explicitly select M4, and assert the resulting CPU model. Previous
validation reports predate this correction; their CPU setting alone
did not establish an M4 run. Other standalone replay helpers are not
claimed to have changed CPU models by this increment.

Explicit M4 reports a prefetch-abort trap at EXC_RETURN in this Unicorn
version. The external IRQ model handles it only with an active frame
and an exact expected return token. A non-return prefetch abort still
fails. CONTROL.SPSEL restoration now follows restoration of the
returning mode, because M4 ignores that write while in Handler mode.
First-FP context creation also restarts translation before executing
the instruction; otherwise Unicorn's previously translated context
initialization could overwrite the modeled FPDSCR value.

### Validation and bounds

Sixteen original instruction-fixture checks cover eager/lazy MSP and
PSP frames, both incoming stack alignments, untouched lazy slots,
memory-backed FP return, visible S16 corruption, two nesting patterns,
foreground/handler context creation and control-register readback.
Twelve controls reject invalid return tokens and frames, disabled or
reserved CPACR access, unprivileged access denial, unsupported policies,
manual FPCA changes and FPU-control changes during lazy preservation.

The original CTIMER2 (IRQ36) and CTIMER3 (IRQ13) vectors also run with
real exception-return instructions. Idle, left transient and right
transient scenes each use both eager and lazy preservation: 792 total
entries/returns. Every return checks general registers, SP/LR/PC/xPSR,
all 32 FP registers and FPSCR. Each scene compares 112 duty/depth snapshots and
81,192 stock RAM bytes against the retained direct-call baseline.
The scene includes eight suppressed refills, duty hold at underrun,
recovery and opposite-output isolation. Events remain harness supplied;
the tests do not measure physical interrupt latency or PWM timing.

The new model deliberately requires STKALIGN for FP frames, ASPEN for
executed FP instructions, and disabled configurable faults/debug
monitor for lazy reservation. CPACR failures stop the replay rather
than emulate NOCP escalation. Manual lazy-state/control manipulation,
system exceptions, MPU faults, cycle timing and full board startup
remain open. This is a bounded FP model, not complete Cortex-M4 or
whole-controller equivalence.

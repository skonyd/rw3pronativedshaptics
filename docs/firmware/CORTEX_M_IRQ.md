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
- FP/lazy stacking, system exceptions, fault escalation and MPU behavior
  remain open. Executed FP instructions in these handlers fail clearly.
- Nonzero BASEPRI values whose implemented bits are all subpriority
  are rejected pending further architectural validation.
- Late arrival, cycle-accurate tail chaining, sleep/wake and interrupted
  multi-transfer instructions are not modeled. Deferred requests run
  using a normal return and re-entry.
- The complete startup, actual second I²C peer, RF clock acquisition,
  board wiring, bootloader acceptance and recovery remain open.

No device access or firmware flashing is performed by this replay.

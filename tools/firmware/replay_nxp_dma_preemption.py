"""Replay stock DMA IRQs and preempt the compiled DSP without context restore."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.arm_const import (
    UC_ARM_REG_BASEPRI, UC_ARM_REG_CONTROL, UC_ARM_REG_FPSCR, UC_ARM_REG_LR,
    UC_ARM_REG_PC, UC_ARM_REG_PRIMASK, UC_ARM_REG_SP, UC_ARM_REG_XPSR,
)

from cortex_m_fpu import ASPEN, LSPEN, CortexMFpuInterrupts
from cortex_m_irq import VTOR
from nxp_dma_status import DMA, DMA_IRQ, TX_CHANNEL, DmaInterruptStatus
from replay_nxp_exceptions import GPRS, fixture, read_mmio, write_mmio
from replay_nxp_fpu import fp_state, seed_fp
from simulate_audio_haptics import (
    CTIMER2, CTIMER3, DSP, LEVELS, MAIN_DISPATCH_CALL, FOREGROUND, RETURN,
    STATE, STOCK_SHA256, TAP_BUFFER, TAP_READY, TX_TAIL, USB_RING, WORKER,
    check_continuous_dma, configure_stock_dma, initialize_sensa_gain_tables,
    initialize_stock_sensa_pwm, initialize_stock_sensa_timers,
    install_simulated_tx_tap, read_sensa_pwm, redirect_simulated_branch,
    tone, verify, xbox_frame,
)


CORE = (*GPRS, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_XPSR, UC_ARM_REG_CONTROL)
BIT = 1 << TX_CHANNEL


class CheckedInterrupts(CortexMFpuInterrupts):
    """Snapshots are assertions only. No CPU register/context is restored here."""
    def __init__(self, cpu):
        super().__init__(cpu)
        self.suspended, self.checked_returns = [], 0

    def enter(self, irq):
        self.suspended.append((tuple(self.cpu.reg_read(reg) for reg in CORE), fp_state(self.cpu)))
        super().enter(irq)

    def exception_return(self, cpu, number, data):
        core, fp = self.suspended[-1]
        super().exception_return(cpu, number, data)
        verify(tuple(cpu.reg_read(reg) for reg in CORE) == core, "IRQ corrupted suspended core state")
        observed = fp_state(cpu)
        verify(observed == fp if core[-1] & 4 else observed[16:32] == fp[16:32],
               "IRQ corrupted suspended FP/callee state")
        self.suspended.pop()
        self.checked_returns += 1


def prepare(image, dsp, *, candidate=False, architectural=True, lazy=True, clear_readback=0):
    replay, registers = initialize_stock_sensa_pwm(image, dsp)
    initialize_stock_sensa_timers(replay)
    cpu = replay.cpu
    interrupts = CheckedInterrupts(cpu)
    interrupts.fpccr = ASPEN | (LSPEN if lazy else 0)
    replay.execution_model = interrupts
    dma = DmaInterruptStatus(cpu, interrupts, clear_readback=clear_readback)
    configure_stock_dma(replay)
    initialize_sensa_gain_tables(replay, 100)
    for address, value in ((0x2000077C, 1), (0x20004520, 2), (0x200044ED, 1)):
        cpu.mem_write(address, bytes([value]))
    if candidate:
        install_simulated_tx_tap(replay)
        redirect_simulated_branch(cpu, MAIN_DISPATCH_CALL, FOREGROUND, "fbf79dff", link=True)
    cpu.mem_write(VTOR, struct.pack("<I", 0x8000))
    # Explicit test priorities, not an assertion about stock startup policy.
    interrupts.enabled |= (1 << DMA_IRQ) | (1 << 13) | (1 << 36)
    interrupts.priorities[DMA_IRQ], interrupts.priorities[13], interrupts.priorities[36] = 0xC0, 0x80, 0x40
    verify(dma.enabled & BIT, "Stock I2S setup did not enable the TX DMA interrupt")
    cpu.reg_write(UC_ARM_REG_CONTROL, 4)
    seed_fp(cpu)
    cpu.reg_write(UC_ARM_REG_FPSCR, 0)  # Match the direct-call reference's arithmetic environment.
    if not architectural:
        replay.execution_model = None
    return replay, registers, interrupts, dma


def acknowledge(replay, interrupts, dma, status, bit, architectural):
    dma.signal(a=bit if status == 0x58 else 0, b=bit if status == 0x60 else 0)
    if architectural:
        verify(interrupts.service() == 1, "DMA completion did not produce one architectural IRQ")
    else:
        replay.call(0xC478, RETURN)
    verify(not dma.asserted() and not any(dma.flags.values()), "Stock DMA handler left its source asserted")


def continuous_case(image, dsp, blocks, *, architectural, candidate, lazy=True, arrivals=None):
    replay, registers, interrupts, dma = prepare(image, dsp, architectural=architectural, candidate=candidate, lazy=lazy)
    cpu = replay.cpu
    pwm, levels = [], []

    def on_block(tick, source, pcm):
        if candidate:
            cpu.mem_write(TAP_BUFFER + 192, struct.pack("<I", 1))
            replay.call(WORKER, RETURN)
            value = bytes(cpu.mem_read(LEVELS, 4))
            levels.append(value.hex())
            bodies = [([(a, a, pitch) for a, pitch in zip(value[i:i + 2], (17, 55)) if a], [])
                      for i in (0, 2)] + [([], []), ([], [])]
            cpu.mem_write(0x20000B34, xbox_frame(bodies) + bytes(64))
            cpu.mem_write(0x20000B2C, b"\x01")
            replay.call(MAIN_DISPATCH_CALL, MAIN_DISPATCH_CALL + 4)
        for irq, timer, handler in ((36, CTIMER2, 0xB438),) * 4 + ((13, CTIMER3, 0xB49C),):
            cpu.mem_write(timer, struct.pack("<I", 1))
            if architectural:
                interrupts.pend(irq)
                verify(interrupts.service() == 1, "Motor timer did not return once")
            else:
                replay.call(handler, RETURN)
            pwm.append(read_sensa_pwm(cpu, registers))
        verify(bytes(cpu.mem_read(source, 192)) == pcm, "Candidate changed playing PCM")

    result = check_continuous_dma(image, dsp, blocks, arrival_ticks=arrivals, replay=replay,
                                  on_block=on_block, tail_ticks=64,
                                  irq_dispatch=lambda status, bit: acknowledge(replay, interrupts, dma, status, bit, architectural))
    # Exclude private DSP/mailbox areas and stacks, compare all initialized stock RAM.
    state = bytes(cpu.mem_read(0x20000000, 81192))
    return result, pwm, levels, state, {"checked_returns": interrupts.checked_returns,
                                      "dma_acknowledgements": len(dma.acknowledgements),
                                      "intenclr_fixture_readback": hex(dma.clear_readback),
                                      "undefined_intenclr_reads": dma.undefined_reads}


def worker_memory(cpu):
    return bytes(cpu.mem_read(STATE, 32)), bytes(cpu.mem_read(LEVELS, 4))


def check_worker(image, dsp, *, target=100, mask=None, lazy=True, clear_readback=0):
    first, following, next_block = tone(0, ((83,), ())), tone(1, ((), (190,))), tone(2, ((), (190,)))
    reference, _, _, _ = prepare(image, dsp, candidate=True)
    reference.cpu.mem_write(TAP_BUFFER, first + struct.pack("<3I", 1, 1, 0x20025000))
    reference.call(WORKER, RETURN)
    expected_first = worker_memory(reference.cpu)
    expected_following = following if target == "release" or mask else next_block
    reference.cpu.mem_write(TAP_BUFFER, expected_following + struct.pack("<3I", 1, 1, USB_RING))
    reference.call(WORKER, RETURN)
    expected_second = worker_memory(reference.cpu)

    replay, registers, interrupts, dma = prepare(image, dsp, candidate=True, lazy=lazy, clear_readback=clear_readback)
    cpu = replay.cpu
    cpu.mem_write(0x20000A59, b"\x01")
    cpu.mem_write(0x20008F92, b"\x01")
    for sequence, pcm in enumerate((following, next_block), 1):
        replay.receive_usb(bytes((0x60, 0x20, sequence, 0xC0, 0x81, 0)) + pcm)
    cpu.mem_write(TAP_BUFFER, first + struct.pack("<3I", 1, 1, 0x20025000))
    fired, nested, fast, count, fp_count = False, False, False, 0, 0

    def signal():
        nonlocal fired
        if not fired:
            fired = True
            dma.signal(b=BIT)

    def observe(machine, pc, size, data):
        nonlocal count, nested, fast, fp_count
        if not interrupts.active and DSP <= pc < DSP + 0x1000:
            count += 1
            if count == target:
                signal()
        if interrupts.active == [DMA_IRQ] and pc == TX_TAIL:
            nested = True
            machine.mem_write(CTIMER3, struct.pack("<I", 1))
            interrupts.pend(13)
        if interrupts.active == [DMA_IRQ, 13] and size == 4 and not interrupts.context_restart:
            a, b = struct.unpack("<HH", machine.mem_read(pc, 4))
            if a & 0xEC00 == 0xEC00 and b & 0xF00 in (0xA00, 0xB00):
                fp_count += 1
                if fp_count == 8:
                    fast = True
                    machine.mem_write(CTIMER2, struct.pack("<I", 1))
                    interrupts.pend(36)

    def release(machine, access, address, size, value, data):
        if target == "release" and value == 0:
            signal()

    code_hook = cpu.hook_add(UC_HOOK_CODE, observe)
    release_hook = cpu.hook_add(UC_HOOK_MEM_WRITE, release, begin=TAP_READY, end=TAP_READY + 3)
    if mask == "primask":
        cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
    elif mask == "basepri":
        cpu.reg_write(UC_ARM_REG_BASEPRI, 0xA0)
    elif mask == "nvic":
        interrupts.enabled &= ~(1 << DMA_IRQ)
    replay.call(WORKER, RETURN, instruction_limit=100000)
    verify(fired and worker_memory(cpu) == expected_first, "Preempted worker differs from uninterrupted DSP")
    if mask:
        verify(interrupts.checked_returns == 0 and dma.asserted(), "Masked DMA did not remain pending")
        cpu.reg_write(UC_ARM_REG_PRIMASK, 0)
        cpu.reg_write(UC_ARM_REG_BASEPRI, 0)
        interrupts.enabled |= 1 << DMA_IRQ
        verify(interrupts.service() == 3, "Unmasked DMA did not resume its nested motor IRQs")
    verify(nested and fast and interrupts.checked_returns == 3, "DMA/motor nesting did not execute exactly once")
    verify(not dma.asserted(), "Nested DMA did not acknowledge its peripheral source")
    trace = list(interrupts.trace)
    verify([e["irq"] for e in trace] == [1, 13, 36, 36, 13, 1], "Wrong actual DMA/motor preemption order")
    cpu.hook_del(code_hook)
    cpu.hook_del(release_hook)
    if target != "release" and not mask:
        verify(cpu.mem_read(TAP_READY, 4) == bytes(4) and bytes(cpu.mem_read(TAP_BUFFER, 192)) == first,
               "DMA producer overwrote the busy mailbox")
        dma.signal(a=BIT)
        verify(interrupts.service() == 1, "Next DMA did not publish after release")
    verify(bytes(cpu.mem_read(TAP_BUFFER, 192)) == expected_following and cpu.mem_read(TAP_READY, 4) == struct.pack("<I", 1),
           "Mailbox did not publish the expected post-release PCM")
    verify(bytes(cpu.mem_read(USB_RING, 384)) == following + next_block, "DMA/tap changed source PCM")
    replay.call(WORKER, RETURN)
    verify(worker_memory(cpu) == expected_second and cpu.mem_read(TAP_READY, 4) == bytes(4), "Second worker block mismatch")
    replay.call(WORKER, RETURN)
    verify(worker_memory(cpu) == (bytes(32), bytes(4)), "Missing next block did not clear DSP state/output")
    return {"target": target, "mask": mask, "policy": "lazy" if lazy else "eager",
            "intenclr_fixture_readback": hex(clear_readback), "undefined_intenclr_reads": dma.undefined_reads,
            "checked_returns": interrupts.checked_returns, "nesting": trace,
            "first_levels": list(expected_first[1]), "second_levels": list(expected_second[1]),
            "busy_mailbox_preserved": target != "release" and not mask, "source_pcm_unchanged": True}


def check_error_and_guards(image, dsp):
    replay, _, interrupts, dma = prepare(image, dsp)
    cpu = replay.cpu
    handle, = struct.unpack("<I", cpu.mem_read(0x2000AB1C + TX_CHANNEL * 4, 4))
    private, = struct.unpack("<I", cpu.mem_read(handle + 4, 4))
    i2s_handle, = struct.unpack("<I", cpu.mem_read(private + 4, 4))
    before = bytes(cpu.mem_read(i2s_handle + 0x14, 0x22))
    interrupts.pend(DMA_IRQ)
    verify(interrupts.service() == 1 and interrupts.service() == 0 and not dma.acknowledgements,
           "Spurious DMA invented a completion or stayed pending")
    dma.signal(error=BIT)
    verify(interrupts.service() == 1 and not dma.asserted(), "DMA error was not acknowledged")
    verify(bytes(cpu.mem_read(i2s_handle + 0x14, 0x22)) == before, "Error retired the queued I2S buffer")
    dma.signal(b=BIT)
    verify(interrupts.service() == 1 and not dma.asserted(), "Success after error did not complete")
    verify(bytes(cpu.mem_read(i2s_handle + 0x14, 0x22)) != before, "Success did not retire/rearm I2S")
    verify([offset for offset, value in dma.acknowledgements] == [0x40, 0x60], "Wrong error/success W1C acknowledgements")
    retained = bytes(cpu.mem_read(i2s_handle + 0x14, 0x22))
    write_mmio(cpu, DMA + 0x50, BIT)
    dma.signal(error=BIT)
    verify(interrupts.service() == 1 and not dma.asserted() and dma.acknowledgements[-1] == (0x40, BIT),
           "Stock failed to acknowledge error with channel interrupts disabled")
    verify(bytes(cpu.mem_read(i2s_handle + 0x14, 0x22)) == retained, "Masked-channel error retired its buffer")
    cpu2, model = fixture("7047", model=CortexMFpuInterrupts)
    model.level_sources[17] = lambda: True
    try:
        model.service(instruction_limit=50)
    except RuntimeError as error:
        verify("budget exhausted" in str(error), "Unexpected held-level failure")
    else:
        raise RuntimeError("Unacknowledged external level falsely reached quiescence")
    injected = []
    for offset, expected in ((0, "IRQ corrupted suspended core"), (32, "IRQ corrupted suspended FP")):
        r, _, nvic, peripheral = prepare(image, dsp, lazy=False)
        done = []

        def corrupt(machine, pc, size, data):
            if nvic.active and not done:
                address = nvic.frames[-1][2] + offset
                value, = struct.unpack("<I", machine.mem_read(address, 4))
                machine.mem_write(address, struct.pack("<I", value ^ 1))
                done.append(True)

        r.cpu.hook_add(UC_HOOK_CODE, corrupt)
        nvic.pend(DMA_IRQ)
        try:
            nvic.service()
        except RuntimeError as error:
            verify(str(error).startswith(expected), f"Unexpected corruption failure: {error}")
        else:
            raise RuntimeError("Injected stack corruption was not detected")
        injected.append("stacked_r0" if offset == 0 else "stacked_s0")
    return {"spurious_irq_no_completion": True, "error_retains_queue": True,
            "success_after_error_rearms": True, "uncleared_level_budget_rejected": True,
            "error_with_channel_interrupt_disabled_acknowledged": True,
            "injected_corruption_rejected": injected}


def check_registers():
    cpu, interrupts = fixture("7047", model=CortexMFpuInterrupts)
    cpu.mem_map(DMA, 4096)
    dma = DmaInterruptStatus(cpu, interrupts, clear_readback=0x80)
    other = 1 << 12
    write_mmio(cpu, DMA + 0x48, BIT)
    write_mmio(cpu, DMA + 0x48, other)
    write_mmio(cpu, DMA + 0x48, 0)
    verify(read_mmio(cpu, DMA + 0x48) == BIT | other, "INTENSET is not a write-one-to-set alias")
    dma.signal(a=BIT, b=other)
    verify(read_mmio(cpu, DMA + 4) == 2 and dma.asserted(), "Wrong DMA interrupt summary")
    write_mmio(cpu, DMA + 0x58, 0)
    verify(read_mmio(cpu, DMA + 0x58) == BIT, "Zero write cleared INTA")
    write_mmio(cpu, DMA + 0x58, BIT)
    verify(read_mmio(cpu, DMA + 0x58) == 0 and read_mmio(cpu, DMA + 0x60) == other, "W1C changed the other DMA status bank")
    write_mmio(cpu, DMA + 0x50, BIT | other)
    verify(not dma.asserted() and read_mmio(cpu, DMA + 4) == 0 and read_mmio(cpu, DMA + 0x60) == other,
           "Disabled interrupt lost its status or remained asserted")
    dma.signal(error=BIT)
    verify(dma.asserted() and read_mmio(cpu, DMA + 4) == 4, "INTEN incorrectly masked a DMA error")
    write_mmio(cpu, DMA + 0x40, BIT)
    verify(not dma.asserted(), "DMA error W1C failed")
    write_mmio(cpu, DMA + 0x48, other)
    verify(dma.asserted(), "Re-enabling retained DMA status did not assert IRQ")
    write_mmio(cpu, DMA + 0x60, other)
    verify(not dma.asserted() and read_mmio(cpu, DMA + 4) == 0, "INTB W1C did not deassert IRQ")
    verify(read_mmio(cpu, DMA + 0x50) == 0x80 and dma.undefined_reads == 1, "Unknown WO readback did not use its explicit fixture")
    for action in (lambda: dma.signal(a=1 << 30), lambda: DmaInterruptStatus(cpu, interrupts, clear_readback=None)):
        try:
            action()
        except RuntimeError:
            pass
        else:
            raise RuntimeError("Invalid DMA fixture input was accepted")
    return ["enable_set_clear_aliases", "w1c_zero_no_effect_and_bank_isolation",
            "retained_disabled_status_reasserts_on_enable", "error_not_gated_by_intenset",
            "summary_bits_1_and_2", "explicit_write_only_readback", "invalid_fixture_inputs_rejected"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
    args = parser.parse_args()
    image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Expected pinned Dev2FW 2.04.00")
    register_checks = check_registers()
    continuous = {}
    for name, parts, arrivals in (("left", ((83,), ()), None), ("right", ((), (190,)), None),
                                   ("mixed_pause", ((80, 190), (110, 230)), [i + (24 if i >= 48 else 0) for i in range(96)])):
        blocks = [tone(i, parts) for i in range(96)]
        stock_trace = None
        for candidate in (False, True):
            print(f"Continuous DMA: {name}, candidate={candidate}", file=sys.stderr, flush=True)
            reference = continuous_case(image, dsp, blocks, architectural=False, candidate=candidate, arrivals=arrivals)
            observed = continuous_case(image, dsp, blocks, architectural=True, candidate=candidate, arrivals=arrivals)
            verify(reference[:4] == observed[:4], f"Architectural DMA/PWM/DSP state mismatch: {name}/{candidate}")
            if not candidate:
                stock_trace = observed
            else:
                verify(observed[0] == stock_trace[0], "Candidate changed the PCM delivery result")
                active = (0,) if name == "left" else ((1,) if name == "right" else (0, 1))
                for channel in active:
                    verify(any(a[channel] != b[channel] for a, b in zip(observed[1], stock_trace[1])),
                           "Candidate PWM positive control is silent")
                verify(all(a[channel] == b[channel] for a, b in zip(observed[1], stock_trace[1])
                           for channel in range(4) if channel not in active), "Candidate PWM leaked to another output")
                verify(observed[2][-1] == "00000000", "Candidate retained levels after stream drain")
            continuous[f"{name}_{'candidate' if candidate else 'stock'}"] = {**observed[0], **observed[-1],
                                                                           "pwm_snapshots": len(observed[1]), "stock_ram_bytes_compared": len(observed[3])}
            if candidate:
                continuous[f"{name}_candidate"].update(pwm_positive_control=True, opposite_outputs_unchanged=True,
                                                       drained_levels_zero=True)
    print("DSP worker preemption and guard cases", file=sys.stderr, flush=True)
    workers = [check_worker(image, dsp, target=target) for target in (1, 20, 100, 500, 1000, "release")]
    workers.append(check_worker(image, dsp, target=100, lazy=False))
    workers.extend(check_worker(image, dsp, target=100, mask=mask) for mask in ("primask", "basepri", "nvic"))
    workers.extend(check_worker(image, dsp, clear_readback=value) for value in (0x80, 0xFFFFFFFF))
    guards = check_error_and_guards(image, dsp)
    print(json.dumps({"scope": "instruction-boundary DMA/DSP/motor preemption with supplied descriptor events",
                      "continuous_cases": continuous, "worker_cases": workers, "guards": guards,
                      "dma_register_checks": register_checks,
                      "whole_device_equivalence_proven": False, "flashable_candidate_ready": False,
                      "unverified": ["INTENCLR is write-only: its real readback is unknown; 0, 0x80 and 0xFFFFFFFF are explicit test inputs",
                                     "Instruction/event boundaries are supplied, not physical bus or interrupt timing",
                                     "Candidate Sensa frames are assembled by the harness, not a complete on-device frame publisher"],
                      "input_sha256": {"stock": hashlib.sha256(image).hexdigest(), "dsp": hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == "__main__":
    main()

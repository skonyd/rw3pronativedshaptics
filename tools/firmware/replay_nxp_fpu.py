"""Check bounded FP exception contexts and original Sensa timer IRQs offline."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn.arm_const import (
    UC_ARM_REG_CONTROL, UC_ARM_REG_FPSCR, UC_ARM_REG_LR, UC_ARM_REG_MSP,
    UC_ARM_REG_PC, UC_ARM_REG_PSP, UC_ARM_REG_SP, UC_ARM_REG_XPSR,
)

from cortex_m_fpu import ASPEN, CPACR, FPCCR, FPCAR, FPDSCR, LSPACT, LSPEN, S_REGISTERS, CortexMFpuInterrupts
from cortex_m_irq import VTOR
from replay_nxp_exceptions import GPRS, fixture, read_mmio, stirring_handler, write_mmio
from simulate_audio_haptics import (
    CTIMER2, CTIMER3, RETURN, STOCK_SHA256, initialize_stock_sensa_pwm,
    initialize_stock_sensa_timers, read_sensa_pwm, stock_timer_irq, verify, xbox_frame,
)


def fp_state(cpu):
    return tuple(cpu.reg_read(reg) for reg in (*S_REGISTERS, UC_ARM_REG_FPSCR))


def seed_fp(cpu):
    for index, register in enumerate(S_REGISTERS):
        cpu.reg_write(register, 0x3E800001 + index * 0x10101)
    cpu.reg_write(UC_ARM_REG_FPSCR, 0xA0400011)


def fp_fixture(code="00ee100a7047", *, lazy=True, psp=False, offset=4, active=True):
    # Original VMOV S0,R0; BX LR, unless a specific fixture is supplied.
    cpu, nvic = fixture(code, model=CortexMFpuInterrupts)
    cpu.mem_write(CPACR, struct.pack("<I", 0xF00000))
    cpu.mem_write(0x20001000, bytes([0xA5]) * 0x2100)
    nvic.fpccr = ASPEN | (LSPEN if lazy else 0)
    seed_fp(cpu)
    cpu.reg_write(UC_ARM_REG_MSP, 0x20003000 + offset)
    cpu.reg_write(UC_ARM_REG_PSP, 0x20002000 + offset)
    cpu.reg_write(UC_ARM_REG_CONTROL, (2 if psp else 0) | (4 if active else 0))
    return cpu, nvic


def check_contracts():
    checks, negative = [], []
    for lazy in (False, True):
        for psp in (False, True):
            for offset in (0, 4):
                cpu, nvic = fp_fixture(lazy=lazy, psp=psp, offset=offset)
                before, stack = fp_state(cpu), cpu.reg_read(UC_ARM_REG_SP)
                nvic.pend(17)
                verify(nvic.service() == 1 and fp_state(cpu) == before, "Extended frame lost caller FP state")
                address = int(nvic.trace[0]["frame"], 16)
                verify(address == (stack - 104) & ~7, "Wrong extended frame size/alignment")
                values = struct.unpack("<17I", cpu.mem_read(address + 32, 68))
                verify(values == (*before[:16], before[-1]), "Wrong S0-S15/FPSCR frame offsets")
                verify(cpu.mem_read(address + 100, 4) == bytes([0xA5]) * 4, "Reserved FP word was overwritten")
                verify(cpu.reg_read(UC_ARM_REG_SP) == stack and cpu.reg_read(UC_ARM_REG_CONTROL) == (6 if psp else 4),
                       "Extended return lost stack selection/FPCA")
                verify(nvic.trace[0]["return"] == ("0xffffffed" if psp else "0xffffffe9"), "Wrong extended EXC_RETURN")
                checks.append(f"{'lazy' if lazy else 'eager'}_{'psp' if psp else 'msp'}_offset_{offset}")

    cpu, nvic = fp_fixture("7047")
    before = fp_state(cpu)
    nvic.pend(17)
    nvic.service()
    address = int(nvic.trace[0]["frame"], 16)
    verify(cpu.mem_read(address + 32, 72) == bytes([0xA5]) * 72 and fp_state(cpu) == before,
           "Unused lazy frame wrote or reloaded the reserved FP area")
    verify([item["event"] for item in nvic.fp_trace] == ["lazy_reserved", "lazy_discarded"] and not nvic.fpccr & LSPACT,
           "Unused lazy preservation was not canceled on return")
    verify(nvic.fpcar == address + 32, "FPCAR does not point to S0")
    checks.append("lazy_unused_area_untouched")

    # Eager return must honor the real frame, rather than a hidden Python copy.
    cpu, nvic = fp_fixture("5a2008907047", lazy=False)
    nvic.pend(17)
    nvic.service()
    verify(cpu.reg_read(S_REGISTERS[0]) == 0x5A, "FP return ignored modified stacked S0")
    checks.append("return_uses_stacked_fp_memory")
    cpu, nvic = fp_fixture("08ee100a7047")  # Deliberately clobber callee-saved S16.
    nvic.pend(17)
    nvic.service()
    verify(cpu.reg_read(S_REGISTERS[16]) == 0xABC00000, "Hidden full-context restore concealed corrupt S16")
    checks.append("s16_corruption_remains_visible")

    # A non-FP outer handler can leave the thread frame lazy until a nested
    # handler needs FP. An FP outer handler instead owns a second FP context.
    nested = {}
    for name, prefix in (("nested_basic_flushes_older_lazy", ""), ("nested_fp_contexts", "00ee100a")):
        cpu, nvic = fp_fixture(stirring_handler(prefix=prefix))
        cpu.mem_write(0x2100, bytes.fromhex("01ee100a7047"))  # VMOV S2,R0.
        nvic.priorities[17], nvic.priorities[9] = 0x80, 0x40
        before = fp_state(cpu)
        nvic.pend(17)
        verify(nvic.service() == 2 and fp_state(cpu) == before, "Nested FP return lost thread state")
        verify([item["irq"] for item in nvic.trace] == [17, 9, 9, 17], "Nested FP preemption order changed")
        verify(nvic.trace[1]["return"] == ("0xfffffff1" if not prefix else "0xffffffe1"), "Wrong nested FP frame type")
        verify(nvic.lazy_frame is None and not nvic.fpccr & LSPACT, "Nested return left stale lazy state")
        nested[name] = {"interrupts": nvic.trace, "fp": nvic.fp_trace}
        checks.append(name)

    # Initial FP use creates a context, initializes FPSCR from FPDSCR, and
    # activates automatic preservation even outside interrupt delivery.
    cpu, nvic = fp_fixture(active=False)
    nvic.fpdscr = 0x01400000
    cpu.mem_write(0x600, bytes.fromhex("00ee100a7047"))
    cpu.reg_write(UC_ARM_REG_LR, 0x701)
    nvic.execute(0x601, 0x700, instruction_limit=8)
    verify(cpu.reg_read(UC_ARM_REG_CONTROL) == 4 and cpu.reg_read(UC_ARM_REG_FPSCR) == 0x01400000,
           "First foreground FP use failed to create/default its context")
    checks.append("foreground_fp_context_from_fpdscr")
    cpu, nvic = fp_fixture(active=False)
    nvic.pend(17)
    nvic.service()
    verify(nvic.trace[0]["return"] == "0xfffffff9" and not cpu.reg_read(UC_ARM_REG_CONTROL) & 4,
           "Inactive caller incorrectly received an extended frame or handler FPCA")
    checks.append("first_handler_fp_basic_caller_frame")
    cpu, nvic = fp_fixture(active=False)
    write_mmio(cpu, FPDSCR, 0xFFFFFFFF)
    verify(read_mmio(cpu, FPDSCR) == 0x07C00000, "FPDSCR retained reserved bits")
    write_mmio(cpu, FPCAR, 0x20001237)
    verify(read_mmio(cpu, FPCAR) == 0x20001230 and read_mmio(cpu, FPCCR) == ASPEN | LSPEN, "FPU control readback failed")
    checks.append("fp_control_register_readback")

    for name, setup, prefix in (
        ("disabled_coprocessor", lambda c, n: c.mem_write(CPACR, bytes(4)), "FPU access requires"),
        ("reserved_cpacr", lambda c, n: c.mem_write(CPACR, struct.pack("<I", 0xA00000)), "FPU access requires"),
        ("unprivileged_denied", lambda c, n: (c.mem_write(CPACR, struct.pack("<I", 0x500000)), c.reg_write(UC_ARM_REG_CONTROL, 5)), "FPU access requires"),
        ("unaligned_fp_policy", lambda c, n: c.mem_write(0xE000ED14, bytes(4)), "FP frames without STKALIGN"),
        ("enabled_faults", lambda c, n: c.mem_write(0xE000ED24, struct.pack("<I", 0x10000)), "Lazy stacking with enabled"),
        ("aspen_disabled", lambda c, n: setattr(n, "fpccr", 0), "FP execution with ASPEN disabled"),
    ):
        cpu, nvic = fp_fixture()
        setup(cpu, nvic)
        nvic.pend(17)
        try:
            nvic.service()
        except RuntimeError as error:
            verify(str(error).startswith(prefix), f"Unexpected {name} failure: {error}")
        else:
            raise RuntimeError(f"Unsupported FP case {name} was accepted")
        negative.append(name)
    for name, code, prefix in (
        ("extended_wrong_return", "00480047f9ffffff", "Invalid EXC_RETURN"),
        ("extended_unbalanced_stack", "81b07047", "Handler returned with an unbalanced"),
        ("extended_corrupt_xpsr", "002007907047", "Invalid stacked execution state"),
        ("manual_fpca_during_lazy", "042080f3148800ee100a7047", "Manual FPCA change overlaps"),
        ("nonreturn_prefetch_abort", "00480047000000f0", "Invalid EXC_RETURN"),
    ):
        cpu, nvic = fp_fixture(code)
        nvic.pend(17)
        try:
            nvic.service(instruction_limit=100)
        except RuntimeError as error:
            verify(str(error).startswith(prefix), f"Unexpected {name} failure: {error}")
        else:
            raise RuntimeError(f"Invalid FP return case {name} was accepted")
        negative.append(name)
    cpu, nvic = fp_fixture("7047")
    nvic.pend(17)
    nvic.enter(17)
    try:
        write_mmio(cpu, FPCCR, ASPEN)
    except RuntimeError as error:
        verify(str(error).startswith("FPU control change during lazy"), "Unexpected lazy control-write error")
    else:
        raise RuntimeError("FPU controls changed while a lazy frame was pending")
    negative.append("control_change_during_lazy")
    return {"checks": checks, "negative_controls": negative, "nested_traces": nested}


def timer_trace(image, dsp, side, mode):
    replay, registers = initialize_stock_sensa_pwm(image, dsp)
    initialize_stock_sensa_timers(replay)
    cpu = replay.cpu
    bodies = [([], []) for _ in range(4)]
    if side is not None:
        bodies[side] = ([], [(35, 24, 0)])
    cpu.mem_write(0x20000B34, xbox_frame(bodies) + bytes(64))
    cpu.mem_write(0x20000B2C, b"\x01")
    replay.call(0x2B538, RETURN)
    for address, value in ((0x2000077C, 1), (0x20004520, 2), (0x200044ED, 1)):
        cpu.mem_write(address, bytes([value]))
    nvic = None
    if mode != "direct":
        nvic = CortexMFpuInterrupts(cpu)
        nvic.fpccr = ASPEN | (LSPEN if mode == "lazy" else 0)
        nvic.enabled = (1 << 36) | (1 << 13)
        nvic.priorities[36], nvic.priorities[13] = 0x40, 0x80
        cpu.mem_write(VTOR, struct.pack("<I", 0x8000))
        cpu.reg_write(UC_ARM_REG_MSP, 0x20027004)
        cpu.reg_write(UC_ARM_REG_PC, RETURN | 1)
        cpu.reg_write(UC_ARM_REG_XPSR, 0xA1000000)
        cpu.reg_write(UC_ARM_REG_CONTROL, 4)
        seed_fp(cpu)

    def tick(timer, handler, irq):
        if nvic is None:
            stock_timer_irq(replay, timer, handler)
        else:
            before = tuple(cpu.reg_read(reg) for reg in (*GPRS, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_XPSR))
            fp_before = fp_state(cpu)
            cpu.mem_write(timer, struct.pack("<I", 1))
            nvic.pend(irq)
            verify(nvic.service() == 1, "Stock Sensa IRQ did not return once")
            verify(tuple(cpu.reg_read(reg) for reg in (*GPRS, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_XPSR)) == before,
                   "Stock Sensa IRQ corrupted interrupted core state")
            verify(fp_state(cpu) == fp_before, "Stock Sensa IRQ corrupted interrupted FP state")

    trace = []
    for index in range(1, 113):
        tick(CTIMER2, 0xB438, 36)
        trace.append({"duty": read_sensa_pwm(cpu, registers), "depth": tuple(cpu.mem_read(0x2000162C, 2))})
        if index % 4 == 0 and not 40 < index <= 72:
            tick(CTIMER3, 0xB49C, 13)
    state = bytes(cpu.mem_read(0x20000000, 81192))
    return trace, state, ({"entries": len(nvic.trace) // 2,
                          "lazy_saves": sum(item["event"] == "lazy_saved" for item in nvic.fp_trace)} if nvic else {})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
    args = parser.parse_args()
    image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Expected pinned Dev2FW 2.04.00")
    contracts = check_contracts()
    reports = {}
    baselines = {}
    for side in (None, 0, 1):
        baseline, state, _ = timer_trace(image, dsp, side, "direct")
        baselines[side] = baseline
        for mode in ("eager", "lazy"):
            trace, observed, counters = timer_trace(image, dsp, side, mode)
            verify(trace == baseline and observed == state, f"Stock Sensa state/PWM differs using {mode}, side {side}")
            reports[f"{mode}_{'idle' if side is None else side}"] = {**counters, "stock_ram_bytes_compared": len(state), "ticks": len(trace)}
    for side in (0, 1):
        verify(any(row["duty"][side] != idle["duty"][side] for row, idle in zip(baselines[side], baselines[None])),
               "Stock transient positive control produced no duty change")
        verify(all(row["duty"][index] == idle["duty"][index] for row, idle in zip(baselines[side], baselines[None])
                   for index in range(4) if index != side), "Stock timer output leaked to another motor")
        verify(len({row["duty"] for row in baselines[side][55:72]}) == 1, "Stock underrun did not hold last duty")
    print(json.dumps({"scope": "offline FP contexts and supplied stock Sensa CTIMER2/3 events",
                      "contracts": contracts, "stock_timer_cases": reports, "whole_device_equivalence_proven": False,
                      "input_sha256": {"stock": hashlib.sha256(image).hexdigest(), "dsp": hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == "__main__":
    main()

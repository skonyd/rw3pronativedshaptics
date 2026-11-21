"""Check basic Cortex-M IRQ frames/masks/preemption and stock startup."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_ARM, UC_MODE_MCLASS, UC_MODE_THUMB
from unicorn.arm_const import (
    UC_ARM_REG_BASEPRI, UC_ARM_REG_CONTROL, UC_ARM_REG_FAULTMASK, UC_ARM_REG_IPSR,
    UC_ARM_REG_LR, UC_ARM_REG_MSP, UC_ARM_REG_PC, UC_ARM_REG_PRIMASK, UC_ARM_REG_PSP,
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_R4,
    UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7, UC_ARM_REG_R8, UC_ARM_REG_R9,
    UC_ARM_REG_R10, UC_ARM_REG_R11, UC_ARM_REG_R12, UC_ARM_REG_SP, UC_ARM_REG_XPSR,
)

from cortex_m_irq import AIRCR, CCR, CortexMInterrupts, IABR, ICPR, IPR, ISPR, VTOR
from replay_nxp_startup import run_startup, startup_scenarios
from simulate_audio_haptics import CANDIDATE_BOOT, DSP, STOCK_SHA256, verify


GPRS = (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_R4,
        UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7, UC_ARM_REG_R8, UC_ARM_REG_R9,
        UC_ARM_REG_R10, UC_ARM_REG_R11, UC_ARM_REG_R12)


def fixture(code="99207047"):
    cpu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
    cpu.mem_map(0, 0x4000)
    cpu.mem_map(0x20000000, 0x4000)
    cpu.mem_map(0xE000E000, 4096)
    nvic = CortexMInterrupts(cpu)
    cpu.mem_write(VTOR, struct.pack("<I", 0x1000))
    for irq, entry in ((17, 0x2001), (9, 0x2101)):
        cpu.mem_write(0x1000 + (irq + 16) * 4, struct.pack("<I", entry))
    cpu.mem_write(0x2000, bytes.fromhex(code))
    cpu.mem_write(0x2100, bytes.fromhex("55207047"))
    cpu.reg_write(UC_ARM_REG_MSP, 0x20003004)
    cpu.reg_write(UC_ARM_REG_PC, 0x101)
    cpu.reg_write(UC_ARM_REG_XPSR, 0xA1000000)
    cpu.reg_write(UC_ARM_REG_LR, 0x12345679)
    for index, register in enumerate(GPRS):
        cpu.reg_write(register, 0xABC00000 + index)
    nvic.enabled = (1 << 9) | (1 << 17)
    return cpu, nvic


def write_mmio(cpu, address, value, *, size=4):
    # Original test instructions: STR/STRB r1,[r0]; BX LR.
    cpu.mem_write(0x300, bytes.fromhex("01707047" if size == 1 else "01607047"))
    cpu.ctl_flush_tb()
    cpu.reg_write(UC_ARM_REG_R0, address)
    cpu.reg_write(UC_ARM_REG_R1, value)
    cpu.reg_write(UC_ARM_REG_LR, 0x501)
    cpu.emu_start(0x301, 0x500, count=8)
    verify(cpu.reg_read(UC_ARM_REG_PC) == 0x500, "Synthetic MMIO writer failed")


def read_mmio(cpu, address):
    cpu.mem_write(0x300, bytes.fromhex("01687047"))
    cpu.ctl_flush_tb()
    cpu.reg_write(UC_ARM_REG_R0, address)
    cpu.reg_write(UC_ARM_REG_LR, 0x501)
    cpu.emu_start(0x301, 0x500, count=8)
    return cpu.reg_read(UC_ARM_REG_R1)


def stirring_handler(prefix="", suffix=""):
    """Original Thumb fixture: prefix; pend IRQ9 via STIR; suffix; BX LR."""
    code = bytearray.fromhex(prefix + "0920")
    ldr = len(code)
    code += bytes.fromhex("00490860" + suffix + "7047")
    while len(code) % 4:
        code += bytes.fromhex("00bf")
    offset = (len(code) - ((ldr + 4) & ~3)) // 4
    struct.pack_into("<H", code, ldr, 0x4900 | offset)
    code += struct.pack("<I", 0xE000EF00)
    return code.hex()


def check_contracts():
    checks = []
    for psp in (False, True):
        for aligned in (False, True):
            cpu, nvic = fixture()
            cpu.mem_write(CCR, struct.pack("<I", 0x200 if aligned else 0))
            if psp:
                cpu.reg_write(UC_ARM_REG_PSP, 0x20002004)
                cpu.reg_write(UC_ARM_REG_CONTROL, 2)
            before = tuple(cpu.reg_read(reg) for reg in (*GPRS, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_XPSR))
            nvic.pend(17)
            verify(nvic.service() == 1, "Basic IRQ was not serviced once")
            verify(before == tuple(cpu.reg_read(reg) for reg in (*GPRS, UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_XPSR)),
                   "Basic IRQ failed to preserve the interrupted context")
            entry = nvic.trace[0]
            address = int(entry["frame"], 16)
            frame = struct.unpack("<8I", cpu.mem_read(address, 32))
            verify(frame[:5] == (*before[:4], before[12]) and frame[5:7] == (before[14], before[15]) and
                   bool(frame[7] & 0x200) == aligned, "Incorrect architectural stack frame contents")
            verify(entry["return"] == ("0xfffffffd" if psp else "0xfffffff9"), "Wrong thread return stack")
            checks.append(f"{'psp' if psp else 'msp'}_{'8' if aligned else '4'}_byte_alignment")

    cpu, nvic = fixture("5a2000907047")  # Modify stacked R0, then return.
    nvic.pend(17)
    nvic.service()
    verify(cpu.reg_read(UC_ARM_REG_R0) == 0x5A, "Return restored a hidden context instead of stacked R0")
    checks.append("return_uses_memory_frame")
    cpu, nvic = fixture("99247047")  # Deliberately violate callee-save convention.
    nvic.pend(17)
    nvic.service()
    verify(cpu.reg_read(UC_ARM_REG_R4) == 0x99, "Hidden context restore concealed handler register corruption")
    checks.append("callee_register_corruption_is_visible")

    # MRS R0,IPSR; STR R0,[R4]; BX LR.
    cpu, nvic = fixture("eff3058020607047")
    cpu.reg_write(UC_ARM_REG_R4, 0x20001000)
    nvic.pend(17)
    nvic.service()
    verify(cpu.mem_read(0x20001000, 4) == struct.pack("<I", 33), "Handler did not observe its IPSR")
    checks.append("ipsr_in_handler")
    cpu, nvic = fixture("eff3008020607047")  # MRS R0,APSR; STR R0,[R4]; BX LR.
    cpu.reg_write(UC_ARM_REG_R4, 0x20001000)
    nvic.pend(17)
    nvic.service()
    verify(cpu.mem_read(0x20001000, 4) == struct.pack("<I", 0xA0000000), "Exception entry lost interrupted APSR flags")
    checks.append("apsr_preserved_on_entry")
    cpu, nvic = fixture("086820607047")  # LDR R0,[R1]; STR R0,[R4]; BX LR.
    cpu.reg_write(UC_ARM_REG_R1, IABR)
    cpu.reg_write(UC_ARM_REG_R4, 0x20001000)
    nvic.pend(17)
    nvic.service()
    verify(cpu.mem_read(0x20001000, 4) == struct.pack("<I", 1 << 17), "NVIC active read disagrees with handler")
    checks.append("active_bitmap_in_handler")

    for register in (UC_ARM_REG_PRIMASK, UC_ARM_REG_FAULTMASK, UC_ARM_REG_BASEPRI):
        cpu, nvic = fixture()
        nvic.priorities[17] = 0x80
        cpu.reg_write(register, 0x80 if register == UC_ARM_REG_BASEPRI else 1)
        nvic.pend(17)
        verify(nvic.service() == 0 and nvic.pending == 1 << 17, "Masked IRQ lost pending state")
        cpu.reg_write(register, 0)
        verify(nvic.service() == 1 and nvic.pending == 0, "Unmasked pending IRQ did not run")
        checks.append(f"mask_register_{register}")
    cpu, nvic = fixture()
    cpu.reg_write(UC_ARM_REG_BASEPRI, 0x80)
    nvic.pend(17)
    verify(nvic.service() == 1 and cpu.reg_read(UC_ARM_REG_BASEPRI) == 0x80,
           "BASEPRI masked priority zero or was incorrectly restored on return")
    checks.append("priority_zero_survives_basepri")

    cpu, nvic = fixture()
    write_mmio(cpu, 0xE000E180, 1 << 17)
    write_mmio(cpu, ISPR, 1 << 17)
    verify(nvic.service() == 0 and read_mmio(cpu, ISPR) == 1 << 17, "Disabled pending alias failed")
    write_mmio(cpu, 0xE000E100, 1 << 17)
    verify(nvic.service() == 1, "Set-enable did not release the pending IRQ")
    write_mmio(cpu, ISPR, 1 << 17)
    write_mmio(cpu, ICPR, 1 << 17)
    verify(nvic.service() == 0 and read_mmio(cpu, ICPR) == 0, "Clear-pending alias failed")
    write_mmio(cpu, IPR + 17, 0xFF, size=1)
    verify(nvic.priorities[17] == 0xE0, "Unimplemented priority bits were retained")
    checks.append("nvic_aliases_and_three_priority_bits")
    write_mmio(cpu, AIRCR, 0x12340500)
    verify(read_mmio(cpu, AIRCR) == 0xFA050000, "Invalid AIRCR write key changed priority grouping")
    write_mmio(cpu, AIRCR, 0x05FA0500)
    verify(read_mmio(cpu, AIRCR) == 0xFA050500, "Valid AIRCR grouping or read key failed")
    checks.append("aircr_write_key_and_readback")

    preemptions = {}
    # Pend IRQ9 via STIR while IRQ17 executes. Lower numeric priority wins.
    code = stirring_handler(suffix="4422")
    for name, priority, group, expected in (
        ("higher_priority_preempts", 0x40, 0, [17, 9, 9, 17]),
        ("equal_priority_waits", 0x80, 0, [17, 17, 9, 9]),
        ("same_group_subpriority_waits", 0x40, 5, [17, 17, 9, 9]),
    ):
        cpu, nvic = fixture(code)
        nvic.priorities[17], nvic.priorities[9] = (0x60 if group else 0x80), priority
        write_mmio(cpu, AIRCR, 0x05FA0000 | group << 8)
        cpu.reg_write(UC_ARM_REG_PC, 0x101)
        nvic.pend(17)
        verify(nvic.service() == 2, "Unexpected nested/deferred IRQ count")
        order = [event["irq"] for event in nvic.trace]
        verify(order == expected, f"Wrong preemption order in {name}: {order}")
        if name == "higher_priority_preempts":
            verify(nvic.trace[1]["return"] == "0xfffffff1", "Nested exception returned to thread mode")
        preemptions[name] = nvic.trace
        checks.append(name)
    cpu, nvic = fixture()
    nvic.priorities[9] = nvic.priorities[17] = 0x80
    nvic.pend(17)
    nvic.pend(9)
    verify(nvic.service() == 2 and nvic.trace[0]["irq"] == 9, "Equal pending priorities did not use IRQ-number ordering")
    checks.append("pending_priority_tie")

    cpu, nvic = fixture(stirring_handler(prefix="72b6"))  # CPSID i.
    nvic.priorities[17], nvic.priorities[9] = 0x80, 0x40
    nvic.pend(17)
    verify(nvic.service() == 1 and nvic.pending == 1 << 9 and cpu.reg_read(UC_ARM_REG_PRIMASK) == 1,
           "Exception return incorrectly rolled back the handler's PRIMASK change")
    cpu.reg_write(UC_ARM_REG_PRIMASK, 0)
    verify(nvic.service() == 1, "Interrupt masked inside its peer did not resume after unmask")
    checks.append("handler_mask_change_survives_return")
    cpu, nvic = fixture(stirring_handler(prefix="72b6", suffix="62b6"))  # CPSIE i.
    nvic.priorities[17], nvic.priorities[9] = 0x80, 0x40
    nvic.pend(17)
    verify(nvic.service() == 2 and [event["irq"] for event in nvic.trace] == [17, 9, 9, 17],
           "A newly unmasked higher-priority interrupt did not preempt")
    checks.append("unmask_inside_handler_preempts")
    cpu, nvic = fixture("012080f313887047")  # MSR FAULTMASK,R0.
    nvic.pend(17)
    nvic.service()
    verify(cpu.reg_read(UC_ARM_REG_FAULTMASK) == 0, "External exception return did not clear FAULTMASK")
    checks.append("faultmask_clears_on_return")

    negative = []
    for name, code, control, prefix in (
        ("invalid_stack_state", "002007907047", 0, "Invalid stacked execution state"),
        ("unbalanced_stack", "81b07047", 0, "Handler returned with an unbalanced"),
        ("odd_stacked_pc", "032006907047", 0, "Unaligned stacked PC"),
        ("wrong_return_stack", "00480047fdffffff", 0, "Invalid EXC_RETURN"),
        ("unsupported_fp_frame", "7047", 4, "Floating-point exception frames"),
        ("unsupported_handler_fp", "00ee100a7047", 0, "FP instruction in handler"),
        ("instruction_budget", "fee7", 0, "IRQ instruction budget exhausted"),
    ):
        cpu, nvic = fixture(code)
        cpu.reg_write(UC_ARM_REG_CONTROL, control)
        nvic.pend(17)
        try:
            nvic.service(instruction_limit=100)
        except RuntimeError as error:
            verify(str(error).startswith(prefix), f"Unexpected failure in {name}: {error}")
        else:
            raise RuntimeError(f"Unsafe exception case {name} was accepted")
        negative.append(name)
    cpu, nvic = fixture()
    nvic.priority_group = 7
    cpu.reg_write(UC_ARM_REG_BASEPRI, 0x20)
    nvic.pend(17)
    try:
        nvic.service()
    except RuntimeError as error:
        verify(str(error).startswith("BASEPRI confined to subpriority"), "Unexpected grouped mask failure")
    else:
        raise RuntimeError("Unsupported grouped BASEPRI setting was silently accepted")
    negative.append("unsupported_subpriority_only_basepri")
    return {"checks": checks, "negative_controls": negative, "preemption_traces": preemptions}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
    args = parser.parse_args()
    image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Expected unmodified Dev2FW 2.04.00")
    verify(len(dsp) > CANDIDATE_BOOT - DSP, "Rebuild the DSP reset module")
    contracts = check_contracts()
    reports = {}
    for name, options in startup_scenarios():
        stock, stock_state = run_startup(image, dsp, interrupt_factory=CortexMInterrupts, **options)
        candidate, candidate_state = run_startup(image, dsp, candidate=True, interrupt_factory=CortexMInterrupts, **options)
        verify(stock == candidate and stock_state == candidate_state, f"Exception-frame startup mismatch in {name}")
        reports[name] = stock
    print(json.dumps({"scope": "modeled basic external IRQ entry/return and explicit peripheral events",
                      "contracts": contracts, "stock_candidate_match": True, "startup_cases": reports,
                      "input_sha256": {"stock": hashlib.sha256(image).hexdigest(), "dsp": hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == "__main__":
    main()

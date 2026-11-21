"""Bounded ARMv7E-M FP context extension; no fault/MPU or cycle simulation.

Frame offsets follow Arm DAI0298A figure 2: core frame first, then S0-S15,
FPSCR and one reserved word. S16-S31 remain the handler's responsibility.
"""
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE
from unicorn import arm_const
from unicorn.arm_const import UC_ARM_REG_CONTROL, UC_ARM_REG_IPSR, UC_ARM_REG_FPSCR

from cortex_m_irq import CCR, CortexMInterrupts
from simulate_audio_haptics import verify


CPACR, FPCCR, FPCAR, FPDSCR = 0xE000ED88, 0xE000EF34, 0xE000EF38, 0xE000EF3C
ASPEN, LSPEN, LSPACT = 1 << 31, 1 << 30, 1
S_REGISTERS = tuple(getattr(arm_const, f"UC_ARM_REG_S{i}") for i in range(32))


class CortexMFpuInterrupts(CortexMInterrupts):
    def __init__(self, cpu):
        super().__init__(cpu)
        self.fpccr, self.fpcar, self.fpdscr = ASPEN | LSPEN, 0, 0
        self.lazy_frame = None
        self.fp_trace = []
        self.total_instructions = 0
        for address in (FPCCR, FPCAR, FPDSCR):
            cpu.hook_add(UC_HOOK_MEM_READ, self.read_fp, begin=address, end=address + 3)
            cpu.hook_add(UC_HOOK_MEM_WRITE, self.write_fp, begin=address, end=address + 3)

    def read_fp(self, cpu, access, address, size, value, data):
        verify(size == 4 and address in (FPCCR, FPCAR, FPDSCR), "Unmodeled FPU control read")
        cpu.mem_write(address, struct.pack("<I", {FPCCR: self.fpccr, FPCAR: self.fpcar, FPDSCR: self.fpdscr}[address]))

    def write_fp(self, cpu, access, address, size, value, data):
        verify(size == 4 and address in (FPCCR, FPCAR, FPDSCR), "Unmodeled FPU control write")
        verify(self.lazy_frame is None, "FPU control change during lazy preservation is unsupported")
        if address == FPCCR:
            # Software manipulation of LSPACT/ready flags is deliberately excluded.
            verify(value & ~(ASPEN | LSPEN) == 0, "Manual FPCCR status writes are unsupported")
            self.fpccr = value
        elif address == FPCAR:
            self.fpcar = value & ~7
        else:
            self.fpdscr = value & 0x07C00000

    def access_check(self, control, ipsr):
        cpacr, = struct.unpack("<I", self.cpu.mem_read(CPACR, 4))
        cp10, cp11 = (cpacr >> 20) & 3, (cpacr >> 22) & 3
        privileged = bool(ipsr) or not control & 1
        verify(cp10 == cp11 and (cp10 == 3 or cp10 == 1 and privileged),
               "FPU access requires unmodeled NOCP fault handling")

    def frame_size(self, control):
        if not control & 4:
            return 32
        verify(self.lazy_frame is None, "Active FP context overlaps pending lazy state")
        self.access_check(control, self.cpu.reg_read(UC_ARM_REG_IPSR))
        ccr, = struct.unpack("<I", self.cpu.mem_read(CCR, 4))
        verify(ccr & 0x200, "FP frames without STKALIGN are outside this replay")
        return 104

    def save_fp(self, address):
        values = [self.cpu.reg_read(reg) for reg in (*S_REGISTERS[:16], UC_ARM_REG_FPSCR)]
        self.cpu.mem_write(address + 32, struct.pack("<17I", *values))
        # The reserved word at +100 is not written or interpreted.

    def stack_extension(self, address, control, ipsr):
        if not control & 4:
            return
        if self.fpccr & LSPEN:
            verify(self.fpccr & ASPEN, "LSPEN without ASPEN is outside this replay")
            shcsr, = struct.unpack("<I", self.cpu.mem_read(0xE000ED24, 4))
            demcr, = struct.unpack("<I", self.cpu.mem_read(0xE000EDFC, 4))
            verify(not shcsr & 0x70000 and not demcr & 0x10000,
                   "Lazy stacking with enabled configurable faults/debug monitor is unsupported")
            self.lazy_frame = address
            self.fpcar = (address + 32) & ~7
            # In this bounded external-IRQ model HardFault can preempt; the
            # configurable faults/debug monitor are disabled (checked above).
            self.fpccr = (self.fpccr & ~0xFF) | LSPACT | 0x10 | (0 if ipsr else 8) | (2 if not ipsr and control & 1 else 0)
            event = "lazy_reserved"
        else:
            self.save_fp(address)
            event = "eager_saved"
        self.fp_trace.append({"event": event, "frame": hex(address)})

    def instruction_context(self, cpu, pc, size):
        self.total_instructions += 1
        if size != 4:
            return
        first, second = struct.unpack("<HH", cpu.mem_read(pc, 4))
        if not (first & 0xEC00 == 0xEC00 and second & 0xF00 in (0xA00, 0xB00)):
            return
        verify(self.fpccr & ASPEN, "FP execution with ASPEN disabled is outside this replay")
        control, ipsr = cpu.reg_read(UC_ARM_REG_CONTROL), cpu.reg_read(UC_ARM_REG_IPSR)
        self.access_check(control, ipsr)
        if self.lazy_frame is not None:
            verify(not control & 4, "Manual FPCA change overlaps pending lazy state")
            self.access_check(1 if self.fpccr & 2 else 0, 0)
            address = self.lazy_frame
            self.save_fp(address)
            self.lazy_frame = None
            self.fpccr &= ~LSPACT
            self.fp_trace.append({"event": "lazy_saved", "frame": hex(address), "pc": hex(pc)})
        if self.fpccr & ASPEN and not control & 4:
            cpu.reg_write(UC_ARM_REG_FPSCR, self.fpdscr)
            cpu.reg_write(UC_ARM_REG_CONTROL, control | 4)
            self.fp_trace.append({"event": "context_created", "pc": hex(pc)})
            # Unicorn translates its own FP context initialization before the
            # code hook. Restart this instruction with updated FPCA, otherwise
            # the already translated block would overwrite our FPDSCR default.
            self.context_restart = True
            cpu.emu_stop()

    def execute(self, start, end, instruction_limit=20000):
        """Foreground execution with bounded restarts after FP context creation."""
        verify(not self.servicing and not self.active, "Foreground execution during IRQ service")
        initial = self.total_instructions
        pc = start
        while True:
            remaining = instruction_limit - (self.total_instructions - initial)
            verify(remaining > 0, "Foreground FP instruction budget exhausted")
            self.context_restart = False
            self.cpu.emu_start(pc | 1, end, count=remaining)
            if not self.context_restart:
                verify(self.cpu.reg_read(arm_const.UC_ARM_REG_PC) == end, "Foreground FP execution did not reach its endpoint")
                return
            pc = self.cpu.reg_read(arm_const.UC_ARM_REG_PC)

    def unstack_extension(self, address, expected):
        if expected & 0x10:
            return 32
        if self.lazy_frame == address:
            self.lazy_frame = None
            self.fpccr &= ~LSPACT
            event = "lazy_discarded"
        else:
            verify(self.lazy_frame is None, "Extended return overlaps another lazy frame")
            stacked_xpsr, = struct.unpack("<I", self.cpu.mem_read(address + 28, 4))
            self.access_check(self.cpu.reg_read(UC_ARM_REG_CONTROL), stacked_xpsr & 0x1FF)
            values = struct.unpack("<17I", self.cpu.mem_read(address + 32, 68))
            for register, value in zip((*S_REGISTERS[:16], UC_ARM_REG_FPSCR), values):
                self.cpu.reg_write(register, value)
            event = "fp_restored"
        self.fp_trace.append({"event": event, "frame": hex(address)})
        return 104

    def return_control(self, control, expected):
        return control & ~4 | (0 if expected & 0x10 else 4)

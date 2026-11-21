"""Bounded Cortex-M4 basic-frame external IRQ model for Unicorn replays.

Architectural reference: ARM DUI0553A sections 2.3 and 4.2/4.3.
No cycle timing, system exceptions, fault escalation or FP/lazy stacking.
"""
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_INTR, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE
from unicorn.arm_const import (
    UC_ARM_REG_BASEPRI, UC_ARM_REG_CONTROL, UC_ARM_REG_FAULTMASK, UC_ARM_REG_IPSR,
    UC_ARM_REG_LR, UC_ARM_REG_MSP, UC_ARM_REG_PC, UC_ARM_REG_PRIMASK, UC_ARM_REG_PSP,
    UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_R12,
    UC_ARM_REG_XPSR,
)

from replay_nxp_startup import InterruptEnableModel
from simulate_audio_haptics import verify


FRAME_REGISTERS = (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3,
                   UC_ARM_REG_R12, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_XPSR)
IRQ_COUNT, PRIORITY_MASK = 57, 0xE0  # LPC54605.h: 3 implemented priority bits.
ISPR, ICPR, IABR, IPR = 0xE000E200, 0xE000E280, 0xE000E300, 0xE000E400
ICSR, VTOR, AIRCR, CCR, STIR = 0xE000ED04, 0xE000ED08, 0xE000ED0C, 0xE000ED14, 0xE000EF00


class CortexMInterrupts(InterruptEnableModel):
    def __init__(self, cpu):
        super().__init__(cpu)
        self.cpu, self.pending, self.active, self.priority_group = cpu, 0, [], 0
        self.priorities = bytearray(64)
        self.trace, self.frames, self.servicing, self.preempt = [], [], False, None
        self.instructions = 0
        cpu.mem_write(CCR, struct.pack("<I", 0x200))
        cpu.hook_add(UC_HOOK_MEM_READ, self.read_nvic, begin=ISPR, end=IPR + 63)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write_nvic, begin=ISPR, end=IPR + 63)
        for address in (ICSR, AIRCR):
            cpu.hook_add(UC_HOOK_MEM_READ, self.read_control, begin=address, end=address + 3)
            cpu.hook_add(UC_HOOK_MEM_WRITE, self.write_control, begin=address, end=address + 3)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.trigger, begin=STIR, end=STIR + 3)
        cpu.hook_add(UC_HOOK_CODE, self.before_instruction)
        cpu.hook_add(UC_HOOK_INTR, self.exception_return)

    def pend(self, irq):
        verify(0 <= irq < IRQ_COUNT, f"Unsupported external IRQ {irq}")
        self.pending |= 1 << irq

    def group_priority(self, priority):
        return priority >> (self.priority_group + 1)

    def allows(self, cpu, irq, *, ignore_primask=False, ignore_active=False):
        if not self.enabled & (1 << irq) or cpu.reg_read(UC_ARM_REG_FAULTMASK):
            return False
        if not ignore_primask and cpu.reg_read(UC_ARM_REG_PRIMASK):
            return False
        priority = self.priorities[irq]
        base = cpu.reg_read(UC_ARM_REG_BASEPRI) & PRIORITY_MASK
        verify(not base or self.group_priority(base) > 0,
               "BASEPRI confined to subpriority bits is outside this model")
        if base and self.group_priority(priority) >= self.group_priority(base):
            return False
        return not self.active or ignore_active or self.group_priority(priority) < self.group_priority(self.priorities[self.active[-1]])

    def choose(self, *, ignore_primask=False, ignore_active=False):
        eligible = [irq for irq in range(IRQ_COUNT) if self.pending & (1 << irq) and
                    self.allows(self.cpu, irq, ignore_primask=ignore_primask, ignore_active=ignore_active)]
        return min(eligible, key=lambda irq: (self.priorities[irq], irq)) if eligible else None

    def read_nvic(self, cpu, access, address, size, value, data):
        if IPR <= address and address + size <= IPR + 64:
            cpu.mem_write(address, bytes(self.priorities[address - IPR:address - IPR + size]))
            return
        for base, bits in ((ISPR, self.pending), (ICPR, self.pending),
                           (IABR, sum(1 << irq for irq in self.active))):
            if base <= address < base + 32:
                verify(size == 4 and address % 4 == 0, "Unmodeled NVIC bitmap read")
                cpu.mem_write(address, struct.pack("<I", bits >> ((address - base) * 8) & 0xFFFFFFFF))
                return

    def write_nvic(self, cpu, access, address, size, value, data):
        if IPR <= address and address + size <= IPR + 64:
            self.priorities[address - IPR:address - IPR + size] = bytes(byte & PRIORITY_MASK for byte in value.to_bytes(size, "little"))
            return
        for base in (ISPR, ICPR):
            if base <= address < base + 32:
                verify(size == 4 and address % 4 == 0, "Unmodeled NVIC bitmap write")
                bits = value << ((address - base) * 8)
                verify(bits >> IRQ_COUNT == 0, "Pending write includes an unsupported IRQ")
                self.pending = self.pending | bits if base == ISPR else self.pending & ~bits

    def read_control(self, cpu, access, address, size, value, data):
        verify(size == 4 and address in (ICSR, AIRCR), "Unmodeled SCB access")
        if address == AIRCR:
            value = 0xFA050000 | self.priority_group << 8
        else:
            irq = self.choose(ignore_primask=True, ignore_active=True)
            value = cpu.reg_read(UC_ARM_REG_IPSR) | (int(len(self.active) <= 1) << 11)
            value |= int(bool(self.pending)) << 22
            if irq is not None:
                value |= (irq + 16) << 12
        cpu.mem_write(address, struct.pack("<I", value))

    def write_control(self, cpu, access, address, size, value, data):
        verify(size == 4 and address in (ICSR, AIRCR), "Unmodeled SCB access")
        if address == ICSR:
            verify(value == 0, "System exception requests are not implemented")
        elif value >> 16 == 0x5FA:
            verify(value & 7 == 0, "Reset/debug AIRCR operations are not implemented")
            self.priority_group = value >> 8 & 7

    def trigger(self, cpu, access, address, size, value, data):
        verify(address == STIR and size == 4 and value < IRQ_COUNT, "Unmodeled software IRQ trigger")
        self.pend(value)

    def enter(self, irq):
        cpu = self.cpu
        verify(self.pending & (1 << irq) and self.allows(cpu, irq), "Attempt to enter a masked/nonpending IRQ")
        ipsr, control = cpu.reg_read(UC_ARM_REG_IPSR), cpu.reg_read(UC_ARM_REG_CONTROL)
        verify(ipsr == (self.active[-1] + 16 if self.active else 0), "IPSR and active IRQ stack disagree")
        verify(not control & 4, "Floating-point exception frames are not implemented")
        use_psp = not ipsr and bool(control & 2)
        stack_register = UC_ARM_REG_PSP if use_psp else UC_ARM_REG_MSP
        stack = cpu.reg_read(stack_register)
        verify(stack % 4 == 0, "Exception stack is not word-aligned")
        ccr, = struct.unpack("<I", cpu.mem_read(CCR, 4))
        padding = 4 if ccr & 0x200 and stack & 4 else 0
        frame = [cpu.reg_read(register) for register in FRAME_REGISTERS]
        verify(frame[-1] & 0x1000000, "Interrupted context is not Thumb state")
        frame[-2] &= ~1
        frame[-1] = frame[-1] & ~0x200 | (0x200 if padding else 0)
        address = stack - 32 - padding
        vtor, = struct.unpack("<I", cpu.mem_read(VTOR, 4))
        vector, = struct.unpack("<I", cpu.mem_read(vtor + (irq + 16) * 4, 4))
        verify(vector & 1 and vector < 0xFFFFFF00, "Invalid Thumb IRQ vector")
        cpu.mem_write(address, struct.pack("<8I", *frame))
        cpu.reg_write(stack_register, address)
        self.pending &= ~(1 << irq)
        exc_return = 0xFFFFFFF1 if ipsr else (0xFFFFFFFD if use_psp else 0xFFFFFFF9)
        self.frames.append((irq, stack_register, address, exc_return))
        self.active.append(irq)
        cpu.reg_write(UC_ARM_REG_CONTROL, control & ~2)
        cpu.reg_write(UC_ARM_REG_PC, vector)
        # Exception entry preserves APSR flags and clears interrupted IT state.
        cpu.reg_write(UC_ARM_REG_XPSR, frame[-1] & 0xF80F0000 | 0x1000000 | (irq + 16))
        cpu.reg_write(UC_ARM_REG_LR, exc_return)
        self.trace.append({"event": "entry", "irq": irq, "frame": hex(address), "return": hex(exc_return)})

    def exception_return(self, cpu, number, data):
        verify(self.servicing and number == 8 and self.frames, f"Unmodeled Unicorn exception {number}")
        irq, stack_register, address, expected = self.frames[-1]
        returned = cpu.reg_read(UC_ARM_REG_PC) | 1
        verify(returned == expected, f"Invalid EXC_RETURN {returned:#x}; expected {expected:#x}")
        verify(cpu.reg_read(stack_register) == address, "Handler returned with an unbalanced exception stack")
        frame = struct.unpack("<8I", cpu.mem_read(address, 32))
        parent = self.active[-2] + 16 if len(self.active) > 1 else 0
        verify(frame[7] & 0x1000000 and frame[7] & 0x1FF == parent, "Invalid stacked execution state")
        verify(frame[6] % 2 == 0, "Unaligned stacked PC")
        for register, value in zip(FRAME_REGISTERS[:-2], frame[:-2]):
            cpu.reg_write(register, value)
        cpu.reg_write(stack_register, address + 32 + (4 if frame[7] & 0x200 else 0))
        control = cpu.reg_read(UC_ARM_REG_CONTROL) & ~2
        if not parent and expected & 4:
            control |= 2
        cpu.reg_write(UC_ARM_REG_CONTROL, control)
        cpu.reg_write(UC_ARM_REG_FAULTMASK, 0)
        cpu.reg_write(UC_ARM_REG_PC, frame[6] | 1)
        cpu.reg_write(UC_ARM_REG_XPSR, frame[7] & ~0x200)
        self.frames.pop()
        self.active.pop()
        self.trace.append({"event": "return", "irq": irq, "pc": hex(frame[6])})
        cpu.emu_stop()

    def before_instruction(self, cpu, pc, size, data):
        if not self.servicing:
            return
        self.instructions += 1
        if size == 4:
            first, second = struct.unpack("<HH", cpu.mem_read(pc, 4))
            verify(not (first & 0xEC00 == 0xEC00 and second & 0xF00 in (0xA00, 0xB00)),
                   "FP instruction in handler requires an unimplemented FP frame model")
        irq = self.choose()
        if irq is not None:
            self.preempt = irq
            cpu.emu_stop()

    def service(self, instruction_limit=20000):
        verify(not self.servicing and not self.active, "Nested service API call")
        self.instructions = 0
        self.servicing = True
        entries = 0
        try:
            while self.active or self.choose() is not None:
                irq = self.choose()
                if irq is not None:
                    self.enter(irq)
                    entries += 1
                remaining = instruction_limit - self.instructions
                verify(remaining > 0, "IRQ instruction budget exhausted")
                self.preempt = None
                self.cpu.emu_start(self.cpu.reg_read(UC_ARM_REG_PC) | 1, 0, count=remaining)
                verify(self.preempt is not None or not self.active or
                       self.trace[-1]["event"] == "return", "IRQ instruction budget exhausted")
            return entries
        finally:
            self.servicing = False

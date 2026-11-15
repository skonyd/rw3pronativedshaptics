"""Run the stock controller Timer0 vector with a modeled B91 clock."""
import struct

from unicorn import UC_HOOK_MEM_WRITE
from unicorn.riscv_const import (
    UC_RISCV_REG_MCAUSE, UC_RISCV_REG_MEPC, UC_RISCV_REG_MSTATUS, UC_RISCV_REG_RA, UC_RISCV_REG_S0,
    UC_RISCV_REG_SP, UC_RISCV_REG_X0,
)

from replay_telink_rf import RX_STATE
from replay_telink_startup import require
from replay_telink_usb import run_until


TIMER0_VECTOR = 0x199C
RADIO_CLOCK_HANDLER, RADIO_COUNTER, PLAYOUT_COUNTER = 0x11C8C, RX_STATE + 6, RX_STATE + 0x18
READ_MXSTATUS, WRITE_MXSTATUS = 0x19F4, 0x1A10
RESUME_PC, PLIC_COMPLETION = 0x500, 0xE4200004
SYSTEM_TICK, PCLK_SCALE = 0x80140200, 0x2A0
TIMER_CONTROL, TIMER_PENDING, TIMER_COMPARE = 0x80140140, 0x80140143, 0x80140144
SYSTEM_MHZ, CLOCK_MHZ = 16, 24
PERIOD_TICKS, INITIAL_TICK = SYSTEM_MHZ * 5000, 1_000_000


class ReceiveTimer:
    def __init__(self, cpu, stack, radio_counter):
        self.cpu = cpu
        self.stack = stack
        self.tick = INITIAL_TICK
        cpu.mem_map(PLIC_COMPLETION & ~0xFFF, 0x1000)
        cpu.mem_write(PCLK_SCALE, bytes([CLOCK_MHZ]))
        # Begin in a synchronized phase of the stock 5 ms audio clock.
        cpu.mem_write(RX_STATE + 0xB0, struct.pack("<I", self.tick - SYSTEM_MHZ * 4750))
        cpu.mem_write(RADIO_COUNTER, struct.pack("<H", radio_counter))
        cpu.mem_write(RX_STATE + 0x14, struct.pack("<I", self.tick - PERIOD_TICKS))
        cpu.mem_write(TIMER_CONTROL, b"\x04")

    def advance_radio_clock(self):
        """Let the stock system-timer handler publish the RF counter."""
        cpu = self.cpu
        before = cpu.mem_read(RADIO_COUNTER, 2)
        cpu.mem_write(SYSTEM_TICK, struct.pack("<I", self.tick & 0xFFFFFFFF))
        cpu.reg_write(UC_RISCV_REG_SP, self.stack)
        cpu.reg_write(UC_RISCV_REG_RA, RESUME_PC)
        run_until(cpu, RADIO_CLOCK_HANDLER, RESUME_PC, vendor_budget=16_384)
        require(cpu.mem_read(PLAYOUT_COUNTER, 2) == before and cpu.mem_read(RADIO_COUNTER, 2) != before,
                "Stock system-timer handler did not publish and advance its RF counter")
        return struct.unpack("<H", before)[0]

    def interrupt(self, late_ticks=0):
        cpu = self.cpu
        require(cpu.mem_read(TIMER_CONTROL, 1)[0] & 4, "Stock Timer0 is disabled")
        now = self.tick + late_ticks
        cpu.mem_write(SYSTEM_TICK, struct.pack("<I", now & 0xFFFFFFFF))
        cpu.mem_write(TIMER_PENDING, b"\x01")
        cpu.mem_write(PLIC_COMPLETION, bytes(4))
        cpu.reg_write(UC_RISCV_REG_SP, self.stack)
        registers = [cpu.reg_read(UC_RISCV_REG_X0 + index) for index in range(32)]
        cpu.reg_write(UC_RISCV_REG_MEPC, RESUME_PC)
        cpu.reg_write(UC_RISCV_REG_MSTATUS, 0x7880)  # FS, MPIE, MPP=M after modeled trap entry.
        cpu.reg_write(UC_RISCV_REG_MCAUSE, 0x8000000B)

        # Unicorn runs the stock save, call, PLIC completion, restore and
        # mret. Supply the Andes mxstatus read and observe its restored value.
        require(cpu.mem_read(READ_MXSTATUS, 4) == bytes.fromhex("7324407c")
                and cpu.mem_read(WRITE_MXSTATUS, 4) == bytes.fromhex("7310447c"),
                "Unexpected Timer0 vector CSR instructions")
        mxstatus = 0x123
        run_until(cpu, TIMER0_VECTOR, READ_MXSTATUS, vendor_budget=128)
        cpu.reg_write(UC_RISCV_REG_S0, mxstatus)
        acknowledgements = []
        hook = cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                            acknowledgements.append((size, value)), begin=TIMER_PENDING, end=TIMER_PENDING)
        try:
            run_until(cpu, READ_MXSTATUS + 4, WRITE_MXSTATUS, vendor_budget=250_000)
        finally:
            cpu.hook_del(hook)
        require(cpu.reg_read(UC_RISCV_REG_S0) == mxstatus, "Timer0 lost its saved mxstatus")
        run_until(cpu, WRITE_MXSTATUS + 4, RESUME_PC, vendor_budget=128)
        require(registers == [cpu.reg_read(UC_RISCV_REG_X0 + index) for index in range(32)],
                "Stock Timer0 vector changed the interrupted integer registers")
        require(cpu.mem_read(PLIC_COMPLETION, 4) == struct.pack("<I", 4)
                and (1, 1) in acknowledgements,
                "Stock Timer0 vector did not acknowledge the timer and PLIC")
        cpu.mem_write(TIMER_PENDING, bytes(1))
        compare, = struct.unpack("<I", cpu.mem_read(TIMER_COMPARE, 4))
        require(0 < compare and compare * SYSTEM_MHZ % CLOCK_MHZ == 0, "Unexpected Timer0 compare")
        self.tick = now + compare * SYSTEM_MHZ // CLOCK_MHZ
        return compare

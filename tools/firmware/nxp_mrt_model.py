"""Bounded LPC54605 MRT register/counter model driven by supplied clock cycles.

NXP LPC54605.h / fsl_mrt.h: four 24-bit channels; IVALUE clocks per
interval, TIMER starts at IVALUE-1, W1C flags and write-only LOAD.
This models logical repeat/one-shot behavior, not physical bus timing.
"""
from dataclasses import dataclass
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

from simulate_audio_haptics import verify


MRT, MRT_IRQ, VALUE_MASK = 0x4000D000, 9, 0xFFFFFF


@dataclass
class Channel:
    interval: int = 0
    remaining: int = 0
    control: int = 0
    flag: bool = False
    expirations: int = 0
    coalesced: int = 0
    acknowledgements: int = 0


class MrtModel:
    def __init__(self, cpu, interrupts, clock_hz):
        verify(isinstance(clock_hz, int) and clock_hz > 0, "MRT requires an explicit clock")
        verify(MRT_IRQ not in interrupts.level_sources, "MRT source already attached")
        self.cpu, self.interrupts, self.clock_hz = cpu, interrupts, clock_hz
        self.channels, self.cycles = [Channel() for _ in range(4)], 0
        interrupts.level_sources[MRT_IRQ] = self.asserted
        for first, last in ((0, 0x3F), (0xF0, 0xFB)):
            cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=MRT + first, end=MRT + last)
            cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=MRT + first, end=MRT + last)

    def asserted(self):
        return any(ch.flag and ch.control & 1 for ch in self.channels)

    def advance(self, cycles):
        verify(isinstance(cycles, int) and cycles >= 0, "MRT cycles must be a nonnegative integer")
        self.cycles += cycles
        for ch in self.channels:
            if not ch.remaining or not cycles:
                continue
            if cycles < ch.remaining:
                ch.remaining -= cycles
                continue
            leftover = cycles - ch.remaining
            expires = 1
            if ch.control & 6 == 0 and ch.interval:
                expires += leftover // ch.interval
                ch.remaining = ch.interval - leftover % ch.interval
            else:
                ch.remaining = 0
            ch.expirations += expires
            ch.coalesced += expires - int(not ch.flag)
            ch.flag = True

    def read(self, cpu, access, address, size, value, data):
        offset = address - MRT
        verify(size == 4 and offset % 4 == 0, "Unmodeled MRT access width")
        if offset < 0x40:
            ch, register = self.channels[offset // 16], offset % 16
            values = {0: ch.interval, 4: ch.remaining - 1 if ch.remaining else VALUE_MASK,
                      8: ch.control, 12: int(ch.flag) | (2 if ch.remaining else 0)}
            value = values[register]
        elif offset == 0xF0:
            value = 4 | 24 << 4  # hardware-status mode, four 24-bit channels
        elif offset == 0xF4:
            value = next((i for i, ch in enumerate(self.channels) if not ch.remaining), 15) << 4
        elif offset == 0xF8:
            value = sum(int(ch.flag) << i for i, ch in enumerate(self.channels))
        else:
            raise RuntimeError("Unmodeled MRT register read")
        cpu.mem_write(address, struct.pack('<I', value))

    def write(self, cpu, access, address, size, value, data):
        offset = address - MRT
        verify(size == 4 and offset % 4 == 0, "Unmodeled MRT access width")
        if offset < 0x40:
            ch, register = self.channels[offset // 16], offset % 16
            if register == 0:
                verify(value & ~(VALUE_MASK | 0x80000000) == 0, "Reserved MRT interval bits")
                ch.interval = value & VALUE_MASK
                if value & 0x80000000 or not ch.remaining or ch.control & 6 == 2:
                    ch.remaining = ch.interval
            elif register == 8:
                verify(value & ~7 == 0 and value & 6 in (0, 2), "MRT stall/reserved mode is not modeled")
                ch.control = value
            elif register == 12:
                verify(value & ~3 == 0, "MRT multitask/status manipulation is not modeled")
                if value & 1:
                    ch.flag = False
                    ch.acknowledgements += 1
            else:
                raise RuntimeError("Write to read-only MRT TIMER")
        elif offset == 0xF0:
            verify(value & ~0x1FF == 0, "MRT multitask mode is not modeled")
        elif offset == 0xF8:
            verify(value & ~15 == 0, "Reserved MRT global flags")
            for i, ch in enumerate(self.channels):
                if value & (1 << i):
                    ch.flag = False
                    ch.acknowledgements += 1
        else:
            raise RuntimeError("Unmodeled/read-only MRT write")

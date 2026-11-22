"""Steady MR0 clock/IRQ model for the stock-initialized Sensa CTIMERs.

Only enabled periodic reset-on-MR0 operation is covered. Reconfiguration
fails explicitly; this is not a general capture/PWM/cycle-accurate model.
"""
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

from simulate_audio_haptics import verify


class MatchTimer:
    def __init__(self, cpu, interrupts, base, irq, clock_hz):
        self.base, self.irq, self.cpu = base, irq, cpu
        word = lambda offset: struct.unpack('<I', cpu.mem_read(base + offset, 4))[0]
        verify(word(4) & 3 == 1 and word(0x14) & 7 == 3 and word(0x70) & 3 == 0,
               'CTIMER requires initialized periodic MR0 mode')
        self.prescale = word(0x0C) + 1
        self.period = self.prescale * (word(0x18) + 1)
        self.clock_hz, self.phase = clock_hz, word(8) * self.prescale
        verify(0 <= self.phase < self.period and self.period > 0 and word(0x10) == 0,
               'Unexpected initial CTIMER phase/prescaler counter')
        self.flag = False
        self.expirations = self.coalesced = self.acknowledgements = 0
        self.w1c_writes = 0
        verify(irq not in interrupts.level_sources, 'CTIMER level already attached')
        interrupts.level_sources[irq] = lambda: self.flag
        for offset in (0, 8):
            cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=base + offset, end=base + offset + 3)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=base, end=base + 0xFFF)

    @property
    def remaining(self):
        return self.period - self.phase

    def advance(self, cycles):
        verify(isinstance(cycles, int) and cycles >= 0, 'Invalid CTIMER cycle advance')
        expirations, self.phase = divmod(self.phase + cycles, self.period)
        if expirations:
            self.expirations += expirations
            self.coalesced += expirations - int(not self.flag)
            self.flag = True

    def read(self, cpu, access, address, size, value, data):
        verify(size == 4 and address in (self.base, self.base + 8), 'Unmodeled CTIMER counter read')
        value = int(self.flag) if address == self.base else self.phase // self.prescale
        cpu.mem_write(address, struct.pack('<I', value))

    def write(self, cpu, access, address, size, value, data):
        verify(size == 4 and address == self.base and value & ~1 == 0,
               'CTIMER reconfiguration/capture is outside the steady clock model')
        if value & 1:
            self.w1c_writes += 1
            self.acknowledgements += int(self.flag)
            self.flag = False

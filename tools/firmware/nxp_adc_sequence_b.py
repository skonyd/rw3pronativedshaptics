"""Strict ADC-B channel-8 fixture; readiness is supplied, not electrical timing."""
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE
from simulate_audio_haptics import verify

ADC = 0x400A0000
CONTROL, DATA8 = ADC + 12, ADC + 0x40
START, ENABLE, VALID = 1 << 26, 1 << 31, 1 << 31
CONFIG = 0x50040100


class AdcSequenceB:
    def __init__(self, cpu, samples, *, ready_after=0):
        verify(isinstance(samples, (list, tuple)) and 0 < len(samples) <= 4096 and
               all(type(x) is int and 0 <= x <= 4095 for x in samples), 'Explicit 12-bit ADC samples required')
        delays = [ready_after] * len(samples) if ready_after is None or type(ready_after) is int else ready_after
        verify(isinstance(delays, (list, tuple)) and len(delays) == len(samples) and
               all(x is None or type(x) is int and 0 <= x <= 8 for x in delays), 'Invalid explicit ADC readiness fixture')
        verify(not any(a <= ADC <= b for a, b, _ in cpu.mem_regions()), 'ADC page already owned by another model')
        cpu.mem_map(ADC, 4096)
        self.samples, self.delays = tuple(samples), tuple(delays)
        self.control, self.index, self.pending, self.attempts, self.latch = 0, 0, False, 0, 0
        self.starts, self.reads, self.writes = [], [], []
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=ADC, end=ADC + 4095)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=ADC, end=ADC + 4095)

    @staticmethod
    def validate(access, address, size):
        verify(size == 4 and (access == UC_MEM_READ and address in (CONTROL, DATA8) or
                             access == UC_MEM_WRITE and address == CONTROL), 'Unmodeled ADC-B register/access')

    def write(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        verify(value in (CONFIG, CONFIG | ENABLE, CONFIG | ENABLE | START), 'Unsupported ADC-B sequence configuration')
        if value & START:
            verify(not self.pending and self.index < len(self.samples), 'Pending/exhausted ADC conversion fixture')
            self.pending, self.attempts = True, 0
            self.starts.append(self.index)
        else:
            verify(not self.pending, 'ADC reconfiguration with pending conversion is outside fixture')
        self.control = value & ~START
        self.writes.append(value)

    def read(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        if address == CONTROL:
            cpu.mem_write(address, struct.pack('<I', self.control)); return
        verify(self.starts, 'ADC data read without an explicit conversion')
        valid = False
        if self.pending:
            delay = self.delays[self.index]
            valid = delay is not None and self.attempts >= delay
            self.attempts += 1
            if valid:
                self.latch = 8 << 26 | self.samples[self.index] << 4
                self.index += 1; self.pending = False
        word = self.latch | (VALID if valid else 0)
        self.reads.append((len(self.starts) - 1, valid, word))
        cpu.mem_write(address, struct.pack('<I', word))

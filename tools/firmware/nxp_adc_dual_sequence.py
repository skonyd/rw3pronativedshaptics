"""Strict A/B ADC fixture sharing the original power/startup lifecycle."""
import struct

from unicorn import UC_MEM_READ, UC_MEM_WRITE
from nxp_adc_recovery import RecoveringAdc, ADC, SEQA, STARTUP, POWER_SET, ADC_POWER
from nxp_adc_sequence_b import CONTROL, START, VALID
from simulate_audio_haptics import verify

CHANNELS = (5, 9, 3, 4, 6, 7)
DATA = {ADC + 0x20 + 4 * channel: channel for channel in CHANNELS}
A_CONFIG = 0xC00402F8


class DualSequenceAdc(RecoveringAdc):
    def __init__(self, cpu, samples, *, scans, scan_ready_after=0, **options):
        verify(isinstance(scans, (list, tuple)) and 0 < len(scans) <= 4096 and
               all(isinstance(row, dict) and set(row) == set(CHANNELS) and
                   all(type(k) is int and type(v) is int and 0 <= v <= 4095 for k, v in row.items())
                   for row in scans), 'Explicit six-channel 12-bit ADC scans required')
        if scan_ready_after is None or type(scan_ready_after) is int:
            delays = [dict.fromkeys(CHANNELS, scan_ready_after) for _ in scans]
        else: delays = scan_ready_after
        verify(isinstance(delays, (list, tuple)) and len(delays) == len(scans) and
               all(isinstance(row, dict) and set(row) == set(CHANNELS) and
                   all(type(k) is int and (v is None or type(v) is int and 0 <= v <= 8)
                       for k, v in row.items()) for row in delays), 'Invalid explicit ADC-A readiness fixture')
        self.scans, self.scan_delays = [dict(row) for row in scans], [dict(row) for row in delays]
        self.a_started, self.a_reads, self.a_writes, self.a_aborted = [], [], [], []
        self.a_pending, self.a_attempts = set(), {}
        self.a_latches = {ch: ch << 26 for ch in CHANNELS}  # Explicit empty reset payloads; VALID clear.
        super().__init__(cpu, samples, **options)

    @staticmethod
    def validate(access, address, size):
        if access == UC_MEM_READ and address in DATA:
            verify(size == 4, 'Unsupported ADC-A data width'); return
        RecoveringAdc.validate(access, address, size)

    def power_write(self, cpu, access, address, size, value, data):
        super().power_write(cpu, access, address, size, value, data)
        if address == POWER_SET and self.a_pending:
            self.a_aborted.append((self.a_started[-1], sorted(self.a_pending)))
            self.a_pending.clear()

    def write(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        if address == SEQA and value & START:
            verify(value == A_CONFIG | START and self.registers[SEQA] == A_CONFIG,
                   'Unsupported ADC-A sequence configuration')
            verify(not self.power & ADC_POWER and self.registers[STARTUP] & 1 and
                   not self.calibration_required and not self.busy, 'ADC-A conversion before successful startup/calibration')
            verify(not self.pending and not self.a_pending, 'Overlapping ADC sequences are outside fixture')
            verify(len(self.a_started) < len(self.scans), 'Exhausted ADC-A scan fixture')
            index = len(self.a_started)
            self.a_started.append(index)
            self.a_pending, self.a_attempts = set(CHANNELS), dict.fromkeys(CHANNELS, 0)
            self.registers[SEQA] = value & ~START
            self.a_writes.append(value)
            return
        if address == CONTROL and value & START:
            verify(not self.a_pending, 'Overlapping ADC sequences are outside fixture')
        if address == SEQA:
            verify(not self.a_pending, 'ADC-A reconfiguration with pending scan')
            self.a_writes.append(value)
        super().write(cpu, access, address, size, value, data)

    def read(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        if address not in DATA:
            super().read(cpu, access, address, size, value, data); return
        verify(self.a_started, 'ADC-A data read before explicit scan')
        channel, index, valid = DATA[address], self.a_started[-1], False
        if channel in self.a_pending:
            after = self.scan_delays[index][channel]
            valid = after is not None and self.a_attempts[channel] >= after
            self.a_attempts[channel] += 1
            if valid:
                self.a_latches[channel] = channel << 26 | self.scans[index][channel] << 4
                self.a_pending.remove(channel)
        word = self.a_latches[channel] | (VALID if valid else 0)
        self.a_reads.append((index, channel, valid, word))
        cpu.mem_write(address, struct.pack('<I', word))

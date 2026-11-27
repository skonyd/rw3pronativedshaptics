"""Run original ADC power fallback and complete stock startup/calibration offline."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE
from unicorn.arm_const import UC_ARM_REG_IPSR, UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_PC
from nxp_adc_recovery import (RecoveringAdc, ADC, CTRL, INSEL, SEQA, STARTUP, CALIB,
                             POWER, POWER_SET, POWER_CLEAR, ADC_POWER)
from nxp_adc_sequence_b import CONTROL, DATA8, CONFIG, ENABLE, START
from replay_nxp_release_i2c import ReleaseLab
from replay_nxp_foreground_adc import AdcForegroundLab, RESULT, READY
from replay_nxp_loop import PERSISTENT
from replay_nxp_power_gpio import validate_delay
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from simulate_audio_haptics import RETURN, STOCK_SHA256, verify

PINS = (0x4000107C, 0x400011D4, 0x4000103C, 0x40001040, 0x40001080, 0x40001100, 0x40001104)
SYSTEM_READS = {0x40000284: 2, 0x40000380: 0}
SYSTEM_WRITES = {0x40000220: (0x8000000,), 0x400002A4: (1,),
                 0x40000380: (0,), 0x40000394: (0x20000000, 0x40000000, 0x5F, 0x2F)}


class AdcRecoveryLab(ReleaseLab):
    def __init__(self, image, dsp, *, samples=(3300,), ready_after=0, **options):
        adc_options = {name: options.pop(name) for name in
                       ('calibration_required', 'startup_enabled', 'calibration_after', 'dummy_after') if name in options}
        super().__init__(image, dsp, **options)
        self.adc = RecoveringAdc(self.cpu, samples, ready_after=ready_after, **adc_options)
        # Base audio fixture already maps the pin page for original I2S setup.
        verify(any(a <= 0x40001000 and b >= 0x40001FFF for a, b, _ in self.cpu.mem_regions()),
               'Expected original audio IOCON mapping missing')
        self.pin_initial = {address: 0xFFFF0000 | (i << 9) | 0x13F for i, address in enumerate(PINS)}
        for address, value in self.pin_initial.items(): self.cpu.mem_write(address, struct.pack('<I', value))
        for address, value in SYSTEM_READS.items(): self.cpu.mem_write(address, struct.pack('<I', value))
        self.cpu.mem_write(0x2000ACB0, struct.pack('<I', 96000000))
        self.init_writes, self.calibration_returns = [], []
        self.calibration_waits = []
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.observe_init, begin=0x40000000, end=0x40001FFF)
        self.cpu.hook_add(UC_HOOK_CODE, self.observe_calibration, begin=0x8FBC, end=0x8FBC)
        self.cpu.hook_add(UC_HOOK_CODE, self.observe_wait, begin=0x1FF70, end=0x1FF70)

    call_adc = AdcForegroundLab.call_adc

    def call_average(self, *, instruction_limit=5000000):
        # Ten complete timeout+power-init paths exceed the earlier healthy
        # reader's 1M budget. Keep a bounded explicit limit, not an early pass.
        self.in_foreground = self.delay_clock.enabled = True
        try: self.replay.call(0x2C678, RETURN, instruction_limit=instruction_limit)
        finally: self.in_foreground = self.delay_clock.enabled = False

    def observe_init(self, cpu, access, address, size, value, data):
        if address in PINS or address in SYSTEM_WRITES: self.init_writes.append((address, value))

    def observe_calibration(self, cpu, pc, size, data):
        self.calibration_returns.append(cpu.reg_read(UC_ARM_REG_R0))

    def observe_wait(self, cpu, pc, size, data):
        self.calibration_waits.append((cpu.reg_read(UC_ARM_REG_R0), cpu.reg_read(UC_ARM_REG_R1)))

    def check_mmio(self, cpu, access, address, size, value, data):
        if self.in_foreground and not cpu.reg_read(UC_ARM_REG_IPSR) and hasattr(self, 'adc'):
            if ADC <= address < ADC + 4096:
                self.adc.validate(access, address, size); return
            if address in PINS:
                verify(size == 4 and access in (UC_MEM_READ, UC_MEM_WRITE), 'Unsupported ADC pin access')
                if access == UC_MEM_WRITE:
                    verify(value == self.pin_initial[address] & 0xFFFFFEC0, 'Original ADC pin mask changed')
                return
            if access == UC_MEM_READ and address in SYSTEM_READS:
                verify(size == 4 and read_word(cpu, address) == SYSTEM_READS[address], 'ADC clock fixture changed')
                return
            if access == UC_MEM_WRITE and address in SYSTEM_WRITES:
                verify(size == 4 and value in SYSTEM_WRITES[address], 'Unsupported ADC clock write')
                return
            if access == UC_MEM_WRITE and address in (POWER_SET, POWER_CLEAR):
                verify(size == 4, 'Unsupported ADC power width'); return
            if access == UC_MEM_READ and address == POWER:
                verify(size == 4, 'Unsupported ADC power width'); return
        super().check_mmio(cpu, access, address, size, value, data)

    def initialize_adc(self):
        persistent = [self.cpu.reg_read(r) for r in PERSISTENT]
        self.in_foreground = True
        try: self.replay.call(0x90E0, RETURN, instruction_limit=1000000)
        finally: self.in_foreground = False
        verify([self.cpu.reg_read(r) for r in PERSISTENT] == persistent,
               'ADC initialization changed caller persistent registers/stack')


def read_word(cpu, address):
    return struct.unpack('<I', cpu.mem_read(address, 4))[0]


def check_setup(lab):
    verify(lab.adc.control == CONFIG | ENABLE and lab.adc.registers[SEQA] == 0xC00402F8 and
           lab.adc.registers[CTRL] == 0x665F and lab.adc.registers[INSEL] == 0 and
           lab.cpu.mem_read(READY + 1, 1) == b'\1', 'Original ADC init/configuration did not complete')
    verify(lab.init_writes[:7] == [(a, lab.pin_initial[a] & 0xFFFFFEC0) for a in PINS],
           'Original seven-pin setup changed')
    verify(lab.init_writes[7:] == [(0x40000380, 0), (0x40000394, 0x20000000),
            (0x40000394, 0x40000000), (0x40000394, 0x5F), (0x40000220, 0x8000000),
            (0x400002A4, 1), (0x40000220, 0x8000000), (0x40000380, 0),
            (0x40000394, 0x20000000), (0x40000394, 0x40000000),
            (0x40000394, 0x2F), (0x40000220, 0x8000000)], 'ADC clock setup order changed')


def startup_cases(image, dsp):
    reports = {}
    for name, options, expected in (
            ('calibration', {}, [1]),
            ('dummy', {'calibration_required': False}, [1]),
            ('retry_calibration', {'calibration_after': (1, 0)}, [0, 1]),
            ('retry_dummy', {'calibration_required': False, 'dummy_after': (1, 0)}, [0, 1]),
            ('calibration_never', {'calibration_after': (None,) * 4}, [0] * 4),
            ('dummy_never', {'calibration_required': False, 'dummy_after': (None,) * 4}, [0] * 4)):
        print('ADC startup:', name, file=sys.stderr, flush=True)
        lab = AdcRecoveryLab(image, dsp, **options)
        feeds = lab.wdt.feeds
        lab.initialize_adc()
        check_setup(lab)
        verify(lab.calibration_returns == expected and lab.wdt.feeds - feeds == len(expected),
               'Original calibration result/retry count changed')
        reports[name] = {'calibration_returns': lab.calibration_returns,
                         'requests': lab.adc.requests, 'status_reads': lab.adc.status_reads,
                         'power_writes': lab.adc.power_writes, 'clock_and_pin_writes': lab.init_writes,
                         'original_wait_arguments': lab.calibration_waits,
                         'setup_returned': True, 'healthy': bool(expected[-1]),
                         'dummy_requests_while_calibration_required': lab.adc.dummy_while_calibration_required,
                         'watchdog_feeds': lab.wdt.feeds - feeds}
    return reports


def recovery_cases(image, dsp):
    reports = {}
    for delay in (None, 8):
        lab = AdcRecoveryLab(image, dsp, samples=[3300, 3450], ready_after=[delay, 0])
        lab.initialize_adc()
        lab.cpu.mem_write(RESULT, struct.pack('<I', 3200))
        initial_writes = len(lab.adc.power_writes)
        initial_feeds, initial_calibrations = lab.wdt.feeds, len(lab.calibration_returns)
        lab.call_adc()
        verify(read_word(lab.cpu, RESULT) == 3200 and lab.cpu.mem_read(READY, 1) == b'\0' and
               lab.adc.aborted == [0] and lab.adc.index == 1 and not lab.adc.pending,
               'Failed ADC conversion falsely accepted/lost fallback state')
        power = lab.adc.power_writes[initial_writes:]
        verify(power == [(POWER_SET, ADC_POWER)] + [(POWER_CLEAR, v) for v in
               (0x80000, ADC_POWER, 0x200, 0x800000, 0x40)] and
               lab.calibration_returns[initial_calibrations:] == [1] and
               lab.wdt.feeds - initial_feeds == 10, 'Original full fallback power/feed/calibration sequence changed')
        verify(lab.adc.control == CONFIG | ENABLE and lab.adc.registers[SEQA] == 0xC00402F8,
               'Fallback failed to restore original two sequences')
        invalid_reads = len(lab.adc.reads)
        lab.call_adc()
        verify(invalid_reads == 8 and lab.adc.reads[-1][1] and
               read_word(lab.cpu, RESULT) == 3450 and lab.cpu.mem_read(READY, 1) == b'\1',
               'Post-reset real sample did not recover independently of stale result')
        for record in lab.delay_clock.records: validate_delay(record)
        reports[str(delay)] = {'invalid_reads': invalid_reads, 'aborted_conversions': lab.adc.aborted,
                              'power_writes': power, 'retained_result_on_failure': 3200,
                              'recovered_result': read_word(lab.cpu, RESULT),
                              'fallback_calibration_returns': lab.calibration_returns[initial_calibrations:],
                              'checked_irq_returns': lab.irq.checked_returns,
                              'delay_arguments': [r['argument'] for r in lab.delay_clock.records]}
    return reports


def averaging_faults(image, dsp):
    reports = {}
    for name, missing in (('first', {0}), ('middle', {4}), ('last', {9}), ('all', set(range(10)))):
        print('ADC averaging fault:', name, file=sys.stderr, flush=True)
        values = [3100 + i * 35 for i in range(10)]
        lab = AdcRecoveryLab(image, dsp, samples=values, ready_after=[None if i in missing else 0 for i in range(10)],
                             dummy_after=(0,) * 16)
        lab.initialize_adc()
        lab.cpu.mem_write(RESULT, struct.pack('<I', 3200))
        lab.call_average()
        previous, expected = 3200, []
        for i, sample in enumerate(values):
            if i not in missing: previous = sample
            expected.append(previous)
        actual = list(struct.unpack('<10I', lab.cpu.mem_read(0x20008A50, 40)))
        mean = sum(expected) // 10
        verify(actual == expected and struct.unpack('<H', lab.cpu.mem_read(0x20000EF0, 2))[0] == mean and
               lab.cpu.mem_read(0x20000EE8, 1)[0] == (mean - 3056) * 100 // 467 and
               lab.adc.aborted == sorted(missing) and lab.adc.index == 10 and
               sum(r[1] for r in lab.adc.reads) == 10 - len(missing) and
               lab.cpu.mem_read(0x20000F1E, 2) == b'\1\1',
               'Original averaging stale-value behavior differs from independent oracle')
        for record in lab.delay_clock.records: validate_delay(record)
        reports[name] = {'missing_positions': sorted(missing), 'raw_samples': values,
                         'averaged_samples': actual, 'mean': mean, 'valid_conversions': 10 - len(missing),
                         'stale_values_included_by_stock': len(missing),
                         'last_read_ready': lab.cpu.mem_read(READY, 1)[0],
                         'original_average_marked_ready': lab.cpu.mem_read(0x20000F1E, 2).hex(),
                         'checked_irq_returns': lab.irq.checked_returns}
    return reports


def guards(image, dsp):
    rejected = []
    def expect(name, action, message):
        try: action()
        except RuntimeError as error:
            verify(str(error) == message, f'Wrong rejection for {name}: {error}')
            rejected.append(name)
        else: raise RuntimeError(f'ADC fault silently accepted: {name}')
    for name, access, address, size in (
            ('sequence_a_data', UC_MEM_READ, ADC + 0x34, 4),
            ('sequence_a_start_register', UC_MEM_WRITE, ADC + 0x10, 4),
            ('calibration_byte', UC_MEM_WRITE, CALIB, 1), ('unknown_register', UC_MEM_READ, ADC + 0x74, 4)):
        expect(name, lambda a=access, b=address, c=size: RecoveringAdc.validate(a, b, c),
               'Unmodeled recovery ADC register/access')
    for name, options in (('negative_schedule', {'calibration_after': (-1,)}),
                          ('boolean_schedule', {'dummy_after': (True,)}),
                          ('empty_schedule', {'dummy_after': ()}),
                          ('missing_flag', {'calibration_required': 1})):
        expect(name, lambda o=options: RecoveringAdc(fixture()[0], [1], **o),
               'Explicit ADC startup/calibration flags required' if name == 'missing_flag' else
               'Invalid ADC calibration/dummy status-read schedule')
    for name, address, value, message in (
            ('unsupported_power', POWER_SET, 1, 'Unsupported recovery power write'),
            ('startup_unpowered', STARTUP, 1, 'Unsupported/unpowered ADC startup'),
            ('conversion_uninitialized', CONTROL, CONFIG | ENABLE | START, 'ADC conversion before successful startup/calibration'),
            ('sequence_a_start', SEQA, 0xC40402F8, 'Unsupported recovery ADC configuration')):
        cpu = fixture()[0]
        cpu.mem_map(0x40000000, 4096)
        adc = RecoveringAdc(cpu, [1])
        expect(name, lambda a=address, v=value: write_mmio(cpu, a, v), message)
    lab = AdcRecoveryLab(image, dsp, startup_enabled=False)
    expect('enable_failure', lab.initialize_adc, 'ADC initialization request without enabled powered ADC')
    for name, options in (('calibration_unhealthy', {'calibration_after': (None,) * 4}),
                          ('dummy_unhealthy', {'calibration_required': False, 'dummy_after': (None,) * 4})):
        lab = AdcRecoveryLab(image, dsp, **options)
        lab.initialize_adc()
        verify(lab.cpu.mem_read(READY + 1, 1) == b'\1' and lab.calibration_returns == [0] * 4,
               'Unhealthy initialization was not exercised')
        expect(name, lab.call_adc, 'ADC conversion before successful startup/calibration')
    return {'rejected': rejected, 'failed_setup_not_treated_as_healthy': True}


def next_scan_boundary(image, dsp):
    lab = AdcRecoveryLab(image, dsp)
    lab.initialize_adc()
    lab.cpu.mem_write(0x20000CC6, b'\1')  # Original SCAN_DUE, not conversion completion.
    lab.in_foreground = True
    try: lab.replay.call(0x197F8, RETURN)
    except RuntimeError as error:
        verify(str(error) == 'Unsupported recovery ADC configuration', 'Unexpected next ADC-A boundary')
    else: raise RuntimeError('ADC-A scan silently succeeded without supplied channels')
    finally: lab.in_foreground = False
    return {'entry': '0x197F8', 'pc': hex(lab.cpu.reg_read(UC_ARM_REG_PC)),
            'unmodeled_sequence': 'A', 'accepted_as_complete_foreground': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    report = {'startup': startup_cases(image, dsp), 'recovery': recovery_cases(image, dsp),
              'averaging_faults': averaging_faults(image, dsp), 'guards': guards(image, dsp),
              'next_scan_boundary': next_scan_boundary(image, dsp),
              'scope': 'Original ADC power fallback/startup/calibration and averaging with supplied read-count statuses',
              'calibration_busy_wait_advances_peripheral_time': False,
              'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
              'physical_adc_clock_or_voltage_scale_proven': False,
              'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}
    print(json.dumps(report, indent=2))


if __name__ == '__main__': main()

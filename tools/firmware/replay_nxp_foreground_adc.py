"""Connect original ADC-B reads to the stock foreground with supplied samples."""
import argparse
from functools import partial
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn import UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE
from unicorn.arm_const import (UC_ARM_REG_IPSR, UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6,
                              UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11, UC_ARM_REG_PC)
from nxp_adc_sequence_b import ADC, CONTROL, DATA8, CONFIG, ENABLE, START, VALID, AdcSequenceB
from replay_nxp_release_i2c import ReleaseLab
from replay_nxp_loop import PERSISTENT, check_isolated_trace
from replay_nxp_clock import check_motor_trace
from replay_nxp_rgb_spi import audio_stream, tone
from replay_nxp_power_gpio import validate_delay
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from simulate_audio_haptics import RETURN, STOCK_SHA256, verify
from nxp_gpio_inputs import GPIO

RESULT, READY, SAMPLE_BUFFER = 0x20000DAC, 0x20000DA8, 0x200056BC


class AdcForegroundLab(ReleaseLab):
    def __init__(self, image, dsp, *, samples, ready_after=0, **options):
        super().__init__(image, dsp, **options)
        self.adc = AdcSequenceB(self.cpu, samples, ready_after=ready_after)
        # Same configuration bytes as 0x90E0's sequence-B setup. Execute
        # its original setter and enable fragment, not full ADC calibration.
        self.cpu.mem_write(0x20025400, struct.pack('<3I', 0x100, 0, 0x01010001))
        self.replay.call(0x93AC, RETURN, ADC, 0x20025400)
        r6 = self.cpu.reg_read(UC_ARM_REG_R6)
        self.cpu.reg_write(UC_ARM_REG_R6, ADC + 4)
        self.replay.call(0x924E, 0x9256)
        self.cpu.reg_write(UC_ARM_REG_R6, r6)
        verify(self.adc.control == CONFIG | ENABLE, 'Original ADC-B configuration/enable changed')
        self.gpio_events = []
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.observe_gpio, begin=GPIO + 40, end=GPIO + 40)

    def observe_gpio(self, cpu, access, address, size, value, data):
        self.gpio_events.append((cpu.reg_read(UC_ARM_REG_PC), self.iterations, value))

    def check_mmio(self, cpu, access, address, size, value, data):
        if self.in_foreground and not cpu.reg_read(UC_ARM_REG_IPSR) and ADC <= address < ADC + 4096:
            AdcSequenceB.validate(access, address, size); return
        super().check_mmio(cpu, access, address, size, value, data)

    def call_adc(self, *, end=RETURN, instruction_limit=1000000):
        self.in_foreground = self.delay_clock.enabled = True
        try: self.replay.call(0x9FA4, end, 8, instruction_limit=instruction_limit)
        finally: self.in_foreground = self.delay_clock.enabled = False

    def call_average(self):
        self.in_foreground = self.delay_clock.enabled = True
        try: self.replay.call(0x2C678, RETURN, instruction_limit=1000000)
        finally: self.in_foreground = self.delay_clock.enabled = False


def reader_cases(image, dsp):
    report = {}
    for sample, delay in ((0, 0), (1, 0), (3056, 0), (3300, 2), (4095, 7), (3300, 8), (3300, None)):
        lab = AdcForegroundLab(image, dsp, samples=[sample], ready_after=delay)
        lab.cpu.mem_write(RESULT, struct.pack('<I', 0x12345678))
        lab.cpu.mem_write(SAMPLE_BUFFER, b'\xA5' * 16)
        missing = delay is None or delay == 8
        feeds = lab.wdt.feeds
        lab.call_adc(end=0xA022 if missing else RETURN)
        polls = 8 if missing else delay + 1
        verify(len(lab.adc.reads) == polls and sum(x[1] for x in lab.adc.reads) == (0 if missing else 1),
               'Original ADC polling boundary/validity changed')
        value, = struct.unpack('<I', lab.cpu.mem_read(RESULT, 4))
        verify(value == (0x12345678 if missing else sample) and
               lab.cpu.mem_read(READY, 1) == bytes([not missing]), 'Missing ADC data became a successful zero/value')
        verify([r['argument'] for r in lab.delay_clock.records] == [150] + [200] * (8 if missing else delay),
               'Original ADC delay/retry sequence bypassed')
        verify(lab.wdt.feeds - feeds == (8 if missing else delay), 'Original retry watchdog feeds changed')
        for r in lab.delay_clock.records: validate_delay(r)
        if not missing:
            result = lab.cpu.mem_read(SAMPLE_BUFFER, 16)
            verify(struct.unpack_from('<I', result)[0] == sample and result[4:6] == b'\0\0' and
                   result[6:8] == b'\xA5\xA5' and struct.unpack_from('<I', result, 8)[0] == 8 and
                   result[12:] == b'\0\xA5\xA5\xA5', 'Stock ADC result metadata or padding changed')
            verify(read_mmio(lab.cpu, DATA8) == 8 << 26 | sample << 4 and not lab.adc.pending,
                   'Consumed DATA_VALID was incorrectly repeated')
        report[f'{sample}_delay_{delay}'] = {'sample': sample, 'readiness_after_invalid_reads': delay,
            'polls': polls, 'result': value, 'success': not missing,
            'checkpoint': 'fallback-before-reinitialization' if missing else 'returned',
            'checked_irq_returns': lab.irq.checked_returns,
            'delay_arguments': [r['argument'] for r in lab.delay_clock.records],
            'watchdog_feeds_during_retry': lab.wdt.feeds - feeds}
    return report


def clamp_sample(value):
    return 3523 if value >= 3524 else 3056 if value < 3056 else value


def average_cases(image, dsp):
    lab = AdcForegroundLab(image, dsp, samples=[0])
    lab.cpu.mem_write(0x20008A4D, b'\x01')
    lab.cpu.mem_write(0x20000F20, b'\x01')  # Original 0x20000ED4 + 0x4C gate: reuse average, no fresh sampling.
    digest = hashlib.sha256()
    for mean in range(3056, 3524):
        lab.cpu.mem_write(0x20008A50, struct.pack('<10I', *([mean] * 10)))
        lab.call_average()
        raw, = struct.unpack('<H', lab.cpu.mem_read(0x20000EF0, 2))
        percent = lab.cpu.mem_read(0x20000EE8, 1)[0]
        verify(raw == mean and percent == (mean - 3056) * 100 // 467,
               'Original average/percentage disagrees with independent integer division')
        digest.update(struct.pack('<IB', raw, percent))
    verify(not lab.adc.starts, 'Average-only gate unexpectedly sampled ADC')
    first = [0, 1, 3040, 3055, 3056, 3100, 3300, 3523, 3524, 4095]
    later = [3072, 3200, 3350, 3450, 3500]
    lab = AdcForegroundLab(image, dsp, samples=first + later)
    windows = []
    for expected_samples, count in (([clamp_sample(x) for x in first], 10),
                                    ([clamp_sample(x) for x in later + first[5:]], 15)):
        lab.call_average()
        values = list(struct.unpack('<10I', lab.cpu.mem_read(0x20008A50, 40)))
        mean = sum(expected_samples) // 10
        verify(values == expected_samples and lab.adc.index == count and
               struct.unpack('<H', lab.cpu.mem_read(0x20000EF0, 2))[0] == mean and
               lab.cpu.mem_read(0x20000EE8, 1)[0] == (mean - 3056) * 100 // 467,
               'Original cold-ten/warm-five clamping and ring average changed')
        windows.append({'conversions_total': count, 'window': values, 'mean': mean,
                        'percent': (mean - 3056) * 100 // 467})
    return {'mean_cases': 468, 'percent_oracle': 'floor((mean - 3056) * 100 / 467)',
            'maximum_percent_in_clamped_fixture': 100, 'case_sha256': digest.hexdigest(),
            'cold_and_warm_windows': windows, 'physical_voltage_scale_proven': False}


def clamp_cases(image, dsp):
    lab = AdcForegroundLab(image, dsp, samples=[0])
    for register, value in ((UC_ARM_REG_R4, 3524), (UC_ARM_REG_R5, 0x20008A50),
                            (UC_ARM_REG_R6, 0x20008A4C), (UC_ARM_REG_R9, 0x20000B74),
                            (UC_ARM_REG_R10, 3523), (UC_ARM_REG_R11, 3056)):
        lab.cpu.reg_write(register, value)
    digest = hashlib.sha256()
    for sample in range(4096):
        lab.cpu.mem_write(RESULT, struct.pack('<I', sample))
        lab.cpu.mem_write(0x20008A4C, b'\0')
        lab.cpu.ctl_flush_tb()
        lab.replay.call(0x2C75A, 0x2C74C)
        actual, = struct.unpack('<I', lab.cpu.mem_read(0x20008A50, 4))
        verify(actual == clamp_sample(sample) and lab.cpu.mem_read(0x20008A4C, 1) == b'\x01',
               'Original clamp fragment disagrees with independent 12-bit range oracle')
        digest.update(struct.pack('<II', sample, actual))
    verify(not lab.adc.starts, 'Clamp-only fragment unexpectedly requested conversion')
    return {'raw_sample_cases': 4096, 'case_sha256': digest.hexdigest(),
            'scope': 'Original 0x2C75A -> 0x2C74C clamp fragment, explicit caller registers',
            'valid_output_range': [3056, 3523]}


def register_contracts():
    def fresh(samples=(3300,), delay=0):
        cpu, _ = fixture(); model = AdcSequenceB(cpu, samples, ready_after=delay)
        write_mmio(cpu, CONTROL, CONFIG); write_mmio(cpu, CONTROL, CONFIG | ENABLE)
        return cpu, model
    cpu, model = fresh()
    write_mmio(cpu, CONTROL, CONFIG | ENABLE | START)
    verify(read_mmio(cpu, CONTROL) == CONFIG | ENABLE, 'ADC START did not self-clear')
    verify(read_mmio(cpu, DATA8) == VALID | 8 << 26 | 3300 << 4 and
           read_mmio(cpu, DATA8) == 8 << 26 | 3300 << 4, 'ADC valid/read-clear/latch behavior changed')
    rejected = []
    for name, action in (
            ('sequence_a', lambda c: read_mmio(c, ADC + 8)),
            ('unmodeled_control', lambda c: read_mmio(c, ADC)),
            ('other_channel', lambda c: read_mmio(c, DATA8 - 4)),
            ('data_write', lambda c: write_mmio(c, DATA8, 1)),
            ('byte_access', lambda c: write_mmio(c, CONTROL, 1, size=1)),
            ('unknown_config', lambda c: write_mmio(c, CONTROL, 0)),
            ('disabled_start', lambda c: write_mmio(c, CONTROL, CONFIG | START)),
            ('data_without_conversion', lambda c: read_mmio(c, DATA8))):
        c, _ = fresh()
        try: action(c)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported ADC access accepted: ' + name)
    for name in ('pending_restart', 'pending_reconfiguration', 'exhausted_source'):
        c, m = fresh(delay=None if name.startswith('pending') else 0)
        write_mmio(c, CONTROL, CONFIG | ENABLE | START)
        if name == 'exhausted_source': read_mmio(c, DATA8)
        try: write_mmio(c, CONTROL, CONFIG | ENABLE if name == 'pending_reconfiguration' else CONFIG | ENABLE | START)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('ADC pending/exhausted state silently accepted')
    for name, samples, delay in (('missing_samples', [], 0), ('nan_sample', [float('nan')], 0),
                                ('boolean_sample', [True], 0), ('negative_sample', [-1], 0),
                                ('wide_sample', [4096], 0), ('missing_delay', [1, 2], [0]),
                                ('negative_delay', [1], -1), ('boolean_delay', [1], False)):
        try: AdcSequenceB(fixture()[0], samples, ready_after=delay)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unspecified/invalid ADC fixture silently accepted')
    return {'checks': ['original_config_values', 'self_clearing_start', 'explicit_valid_result',
                       'clear_valid_on_read', 'retain_last_payload'], 'rejected': rejected}


def recovery_boundary(image, dsp):
    lab = AdcForegroundLab(image, dsp, samples=[3300], ready_after=None)
    lab.cpu.mem_write(RESULT, struct.pack('<I', 0x12345678))
    lab.call_adc(end=0xA022)
    lab.in_foreground = lab.delay_clock.enabled = True
    try: lab.irq.execute(0xA022, RETURN, instruction_limit=1000000)
    except RuntimeError as error:
        verify(str(error) == 'Unmodeled foreground MMIO at 0x40000620, width 4' and
               lab.cpu.reg_read(UC_ARM_REG_PC) == 0xA034 and lab.cpu.mem_read(READY, 1) == b'\0' and
               lab.cpu.mem_read(RESULT, 4) == struct.pack('<I', 0x12345678), 'Unexpected ADC fallback/power boundary')
    else: raise RuntimeError('Unmodeled ADC reinitialization silently completed')
    finally: lab.in_foreground = lab.delay_clock.enabled = False
    return {'pc': '0xA034', 'unmodeled_write': '0x40000620', 'value': '0x400',
            'invalid_polls': len(lab.adc.reads), 'previous_result_retained': True,
            'accepted_as_full_recovery': False}


def stream(image, dsp, *, candidate=True, enabled=True, length=192, side=None, ack=True, delayed=False):
    samples = [3300] * 128
    if delayed: samples[:10] = [0, 3056, 3100, 3200, 3300, 3400, 3523, 3524, 4095, 3150]
    changes = ((70, 0), (120, 1), (170, 0)) if ack else ((70, 0),)
    lab = AdcForegroundLab(image, dsp, candidate=candidate, samples=samples,
                           ready_after=2 if delayed else 0, audio_enabled=enabled,
                           ack=ack, pin_changes=changes)
    if not ack: lab.run = partial(lab.run, instruction_limit=5000000)
    frequencies = ((80, 190), ()) if side == 0 else ((), (110, 230)) if side == 1 else ((80, 190), (110, 230))
    blocks = [tone(i, frequencies) for i in range(length)]
    _, result, pwm, combined = audio_stream(image, dsp, blocks, candidate=candidate, lab=lab)
    verify(lab.iterations == length + 63 == lab.calls[0x2B538] and not lab.adc.pending and
           not lab.armed and not lab.shifted and not lab.irq.active and not lab.irq.frames,
           'ADC/foreground/audio stream did not complete and drain')
    verify(lab.adc.index == 10 and len(lab.adc.starts) == 10 and sum(x[1] for x in lab.adc.reads) == 10,
           'Original cold ADC sample count changed')
    verify(len(lab.adc.reads) == (30 if delayed else 10), 'Integrated ADC readiness/poll count changed')
    for record in lab.delay_clock.records: validate_delay(record)
    return lab, {**result, 'iterations': lab.iterations, 'source_blocks': length,
                  'checked_irq_returns': lab.irq.checked_returns, 'adc_conversions': lab.adc.index,
                  'adc_reads': len(lab.adc.reads), 'gpio_events': lab.gpio_events,
                  'source_pcm_preserved': True, 'combined_spi_i2s_batches': combined,
                  'runtime_sensa_frame_checks': len(lab.observed_decodes),
                  'i2c_callbacks': lab.release_callbacks,
                  'adc_mean': struct.unpack('<H', lab.cpu.mem_read(0x20000EF0, 2))[0],
                  'calculated_percent': lab.cpu.mem_read(0x20000EE8, 1)[0]}, pwm


def inactive_pairs(image, dsp):
    reports, references = {}, {}
    for name, length, ack in (('ack_255', 192, True), ('nack_159', 96, False)):
        variants, results = [], []
        for candidate in (False, True):
            print('Inactive ADC pair:', name, candidate, file=sys.stderr, flush=True)
            lab, result, pwm = stream(image, dsp, candidate=candidate, enabled=False, length=length, ack=ack)
            variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                             tuple(lab.cpu.reg_read(r) for r in PERSISTENT), pwm,
                             lab.release_bus.commands, lab.release_bus.frames,
                             lab.adc.reads, lab.gpio_events))
            results.append(result)
            if not candidate: references[length + 63] = pwm
        verify(variants[0] == variants[1] and results[0] == results[1], 'Disabled candidate changed ADC/stock state')
        reports[name] = {**results[0], 'compared_ram_bytes': 0x13D28, 'stock_inactive_candidate_match': True}
    return reports, references


def active_cases(image, dsp, references):
    print('Stock idle reference: 575', file=sys.stderr, flush=True)
    _, _, references[575] = stream(image, dsp, candidate=False, enabled=False, length=512)
    reports = {}
    for name, length, side, ack, delayed in (
            ('stereo_575', 512, None, True, False),
            ('left_255', 192, 0, True, False), ('right_255', 192, 1, True, False),
            ('delayed_variable_adc_255', 192, None, True, True),
            ('nack_audio_159', 96, None, False, False)):
        print('Active ADC stream:', name, file=sys.stderr, flush=True)
        lab, result, pwm = stream(image, dsp, length=length, side=side, ack=ack, delayed=delayed)
        reference = references[len(pwm)]
        if side is None: check_motor_trace(pwm, reference)
        else: check_isolated_trace(pwm, reference, side)
        verify(lab.values()[10] == 1 and len(lab.observed_decodes) == lab.values()[5] and lab.values()[1] == 0,
               'ADC path skipped runtime frame checks or audio stop')
        if ack:
            verify(lab.gpio.writes == [(40, 1), (40, 0)] * 2 and lab.release_callbacks == [1] * 10,
                   'Long ACK release sequence changed')
        else:
            verify(lab.gpio.writes[:2] == [(40, 1), (40, 0)] and lab.release_callbacks == [16] * 15,
                   'Original NACK path failed before GPIO release/retry boundary')
        reports[name] = {**result, 'active_and_drained_pwm_match_independent_idle_expectations': True,
                         'opposite_palm_and_triggers_idle_checked': side is not None,
                         'all_gpio_writes_retained': True}
    return reports


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    report = {'registers': register_contracts(), 'readers': reader_cases(image, dsp),
              'clamps': clamp_cases(image, dsp), 'averages': average_cases(image, dsp),
              'fallback_boundary': recovery_boundary(image, dsp)}
    report['inactive_pairs'], references = inactive_pairs(image, dsp)
    report['active'] = active_cases(image, dsp, references)
    report.update({'scope': 'Original ADC-B reader/averager in a supplied conversion/readiness fixture',
                   'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                   'physical_adc_clock_or_voltage_scale_proven': False,
                   'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}})
    print(json.dumps(report, indent=2))


if __name__ == '__main__': main()

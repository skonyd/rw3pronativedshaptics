"""Original six-channel scans and channel-8 reads in one strict ADC lifecycle."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE
from unicorn.arm_const import UC_ARM_REG_PC
from nxp_adc_dual_sequence import DualSequenceAdc, CHANNELS, DATA, A_CONFIG
from nxp_adc_recovery import ADC, SEQA
from replay_nxp_adc_recovery import AdcRecoveryLab, PINS, SYSTEM_READS, read_word
from replay_nxp_release_i2c import ReleaseLab
from replay_nxp_analog import (SCAN_DUE, ADC_ENABLED, FOREGROUND_SCAN, AVERAGED_VALUES,
                              AVERAGE_READY, STARTUP_SCAN, STARTUP_VALUES, fixture_values,
                              TRIGGER_CALIBRATION, TRIGGER_VALUES, TRIGGER_REPORT_MAXIMUM,
                              GIP_INPUT_HEADER, GIP_INPUT_READY, INPUT_CHANGED, GIP_INPUT_PENDING,
                              GIP_INPUT_BUFFER, GIP_INPUT_BUILD, GIP_INPUT_TRANSLATE)
from replay_nxp_foreground_adc import RESULT, READY
from replay_nxp_exceptions import read_mmio, write_mmio
from nxp_adc_sequence_b import CONTROL, CONFIG, ENABLE, START, VALID
from nxp_adc_recovery import POWER_SET, ADC_POWER
from replay_nxp_power_gpio import validate_delay
from replay_nxp_rgb_spi import audio_stream, tone
from replay_nxp_loop import PERSISTENT, check_isolated_trace
from replay_nxp_clock import check_motor_trace
from simulate_audio_haptics import RETURN, STOCK_SHA256, verify


class DualAdcLab(AdcRecoveryLab):
    def __init__(self, image, dsp, *, scans, samples=(3300,) * 128, scan_ready_after=0, ready_after=0, **options):
        # Keep all preceding stages byte-identical: the new lifecycle model
        # owns the ADC page from the start instead of replacing live hooks.
        adc_options = {name: options.pop(name) for name in
                       ('calibration_required', 'startup_enabled', 'calibration_after', 'dummy_after') if name in options}
        ReleaseLab.__init__(self, image, dsp, **options)
        self.adc = DualSequenceAdc(self.cpu, samples, scans=scans, scan_ready_after=scan_ready_after,
                                   ready_after=ready_after, **adc_options)
        verify(any(a <= 0x40001000 and b >= 0x40001FFF for a, b, _ in self.cpu.mem_regions()),
               'Expected original audio IOCON mapping missing')
        self.pin_initial = {address: 0xFFFF0000 | (i << 9) | 0x13F for i, address in enumerate(PINS)}
        for address, value in self.pin_initial.items(): self.cpu.mem_write(address, struct.pack('<I', value))
        for address, value in SYSTEM_READS.items(): self.cpu.mem_write(address, struct.pack('<I', value))
        self.cpu.mem_write(0x2000ACB0, struct.pack('<I', 96000000))
        self.init_writes, self.calibration_returns, self.calibration_waits = [], [], []
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.observe_init, begin=0x40000000, end=0x40001FFF)
        self.cpu.hook_add(UC_HOOK_CODE, self.observe_calibration, begin=0x8FBC, end=0x8FBC)
        self.cpu.hook_add(UC_HOOK_CODE, self.observe_wait, begin=0x1FF70, end=0x1FF70)

    def call_scan(self, *, startup=False, end=RETURN, instruction_limit=5000000):
        self.in_foreground = self.delay_clock.enabled = True
        try: self.replay.call(STARTUP_SCAN if startup else FOREGROUND_SCAN, end, instruction_limit=instruction_limit)
        finally: self.in_foreground = self.delay_clock.enabled = False
        verify(self.delay_clock.active is None, 'Analog scan returned inside a busy wait')


def transformed(scan):
    # Independent read-order/inversion oracle from the original result stores.
    return [4096 - scan[ch] if ch in (3, 9) else scan[ch] for ch in CHANNELS]


def words(cpu, address, count=6):
    return list(struct.unpack('<' + 'I' * count, cpu.mem_read(address, 4 * count)))


def scan_cases(image, dsp):
    scans = [{ch: (i * 197 + ch * 113) % 4096 for ch in CHANNELS} for i in range(12)]
    scans[0] = dict(zip(CHANNELS, (0, 4095, 1, 4094, 2048, 3072)))
    delays = [dict.fromkeys(CHANNELS, 0) for _ in scans]
    delays[3][3], delays[8][7] = 2, 7
    lab = DualAdcLab(image, dsp, scans=scans, scan_ready_after=delays)
    lab.initialize_adc()
    lab.cpu.mem_write(SCAN_DUE, b'\0')
    lab.call_scan()
    lab.cpu.mem_write(ADC_ENABLED, b'\0'); lab.cpu.mem_write(SCAN_DUE, b'\1')
    lab.call_scan()
    verify(not lab.adc.a_started and not lab.adc.a_reads, 'Scan ran while disabled/not due')
    lab.cpu.mem_write(ADC_ENABLED, b'\1')
    averages = []
    for i in range(len(scans)):
        lab.cpu.mem_write(SCAN_DUE, b'\1')
        lab.call_scan()
        verify(lab.cpu.mem_read(SCAN_DUE, 1) == b'\0' and not lab.adc.a_pending and
               lab.cpu.mem_read(AVERAGE_READY, 1)[0] == int(i >= 9), 'Original scan/average gate failed')
        if i >= 9:
            expected = [sum(row[col] for row in map(transformed, scans[i - 9:i + 1])) // 10 for col in range(6)]
            verify(words(lab.cpu, AVERAGED_VALUES) == expected, 'Unified ADC moving average/inversion mismatch')
            averages.append(expected)
    expected_reads = [(i, ch, good) for i in range(len(scans)) for ch in CHANNELS
                      for good in [False] * delays[i][ch] + [True]]
    verify([(i, ch, good) for i, ch, good, _ in lab.adc.a_reads] == expected_reads and
           lab.adc.a_writes.count(A_CONFIG | (1 << 26)) == len(scans), 'ADC-A read order/delayed validity changed')
    for record in lab.delay_clock.records: validate_delay(record)
    return {'scans': len(scans), 'valid_reads': 6 * len(scans), 'invalid_reads': 9,
            'averages': averages, 'disabled_and_not_due_skipped': True,
            'original_start_self_clears': lab.adc.registers[SEQA] == A_CONFIG,
            'checked_irq_returns': lab.irq.checked_returns}


def startup_scan(image, dsp):
    scans = fixture_values(18)
    lab = DualAdcLab(image, dsp, scans=scans, scan_ready_after=0)
    lab.initialize_adc(); lab.call_scan(startup=True)
    expected = [sum(sorted(row[col] for row in map(transformed, scans))[1:-1]) // 16 for col in range(6)]
    verify(words(lab.cpu, STARTUP_VALUES) == expected and len(lab.adc.a_started) == 18 and
           len(lab.adc.a_reads) == 108 and not lab.adc.a_pending, 'Unified startup trimmed calibration mismatch')
    return {'scans': 18, 'valid_reads': 108, 'trimmed_average': expected,
            'checked_irq_returns': lab.irq.checked_returns}


def fault_cases(image, dsp):
    reports = {}
    for failed in CHANNELS:
        print('Unified ADC: missing channel', failed, file=sys.stderr, flush=True)
        scans = [{ch: 1100 + i * 211 + ch * 43 for ch in CHANNELS} for i in range(3)]
        delays = [dict.fromkeys(CHANNELS, 0) for _ in scans]
        delays[1][failed] = None
        position = CHANNELS.index(failed)
        lab = DualAdcLab(image, dsp, scans=scans, scan_ready_after=delays, samples=[0, 3450],
                         dummy_after=(0,) * (7 - position))
        lab.initialize_adc()
        lab.cpu.mem_write(SCAN_DUE, b'\1'); lab.call_scan()
        verify([read_word(lab.cpu, 0x20000BA4 + 40 * c) for c in range(6)] == transformed(scans[0]),
               'Priming scan did not establish independent channel payloads')
        lab.cpu.mem_write(SCAN_DUE, b'\1'); lab.call_scan()
        # 0x8FE0 always writes the channel DAT payload, even with VALID clear.
        # A timeout powers down, aborting the other pending A channels. The
        # original success-only inversion is skipped for all invalid reads.
        expected = transformed(scans[1])[:position] + [scans[0][ch] for ch in CHANNELS[position:]]
        actual = [read_word(lab.cpu, 0x20000BA8 + 40 * c) for c in range(6)]
        verify(actual == expected and lab.adc.a_aborted == [(1, sorted(CHANNELS[position:]))] and
               len(lab.adc.power_writes) == 5 + 6 * (6 - position) and
               lab.cpu.mem_read(READY, 1) == b'\0' and not lab.adc.a_pending,
               'Original A-timeout abort/stale payload propagation differs from oracle')
        lab.call_adc()
        verify(read_word(lab.cpu, RESULT) == 0 and lab.cpu.mem_read(READY, 1) == b'\1',
               'Valid B zero lost after A power recovery')
        lab.cpu.mem_write(SCAN_DUE, b'\1'); lab.call_scan()
        recovered = [read_word(lab.cpu, 0x20000BAC + 40 * c) for c in range(6)]
        verify(recovered == transformed(scans[2]) and lab.cpu.mem_read(READY, 1) == b'\1' and
               len(lab.adc.a_started) == 3 and not lab.adc.a_pending, 'Next A scan did not recover fresh channels')
        lab.call_adc()
        verify(read_word(lab.cpu, RESULT) == 3450 and lab.adc.index == 2 and not lab.adc.pending,
               'A/B channels aliased or next B result failed')
        for record in lab.delay_clock.records: validate_delay(record)
        reports[str(failed)] = {'failed_channel': failed, 'valid_channels_before_failure': position,
                               'stale_raw_payloads_in_original_ring': actual,
                               'recovered_scan': recovered, 'aborted': lab.adc.a_aborted,
                               'fallback_initializations': 6 - position, 'valid_b_results': [0, 3450],
                               'checked_irq_returns': lab.irq.checked_returns}
    return reports


def trigger_reports(image, dsp):
    scans = [{**dict.fromkeys(CHANNELS, 2048), 6: a, 7: b}
             for a, b in ((2525, 2200), (3000, 3000)) for _ in range(10)]
    lab = DualAdcLab(image, dsp, scans=scans, samples=[1234, 3450])
    lab.initialize_adc()
    for address in TRIGGER_CALIBRATION: lab.cpu.mem_write(address, struct.pack('<II', 3000, 1000))
    lab.cpu.mem_write(TRIGGER_REPORT_MAXIMUM, struct.pack('<HH', 1023, 1023))
    lab.cpu.mem_write(GIP_INPUT_HEADER, b'\x20\0\0')
    lab.cpu.mem_write(GIP_INPUT_READY, b'\1')  # Explicit negotiated queue state, not USB authentication.
    reports = []
    for phase, expected in enumerate(((458, 808), (0, 0))):
        lab.call_adc()
        verify(read_word(lab.cpu, RESULT) == (1234, 3450)[phase], 'Interleaved B result corrupted')
        for _ in range(10):
            lab.cpu.mem_write(SCAN_DUE, b'\1'); lab.call_scan()
        verify(tuple(words(lab.cpu, TRIGGER_VALUES, 2)) == expected, 'Original unified trigger mapping failed')
        lab.replay.call(GIP_INPUT_TRANSLATE, RETURN)
        lab.replay.call(GIP_INPUT_BUILD, RETURN)
        packet = bytes(lab.cpu.mem_read(GIP_INPUT_BUFFER, 36))
        verify(lab.cpu.mem_read(GIP_INPUT_PENDING, 1) == b'\x24' and
               packet[:4] == bytes((0x20, 0, 1, 0x20)) and
               struct.unpack_from('<HH', packet, 6) == expected, 'Original queued GIP report lost analog triggers')
        reports.append({'internal_trigger_values': list(expected), 'queued_report_hex': packet.hex(),
                        'b_result': read_word(lab.cpu, RESULT)})
        lab.cpu.mem_write(INPUT_CHANGED, b'\0'); lab.cpu.mem_write(GIP_INPUT_PENDING, b'\0')
    verify(len(lab.adc.a_reads) == 120 and lab.adc.index == 2, 'Unified report fixture skipped A/B input')
    return {'phases': reports, 'valid_a_reads': 120,
            'send_sequence_not_advanced_without_usb_send': True,
            'usb_host_delivery_or_authentication_proven': False}


def guards(image, dsp):
    rejected = []
    scan = dict.fromkeys(CHANNELS, 2048)
    def expect(name, action, message):
        try: action()
        except RuntimeError as error:
            verify(str(error) == message, f'Wrong rejection for {name}: {error}'); rejected.append(name)
        else: raise RuntimeError(f'Unsupported ADC operation silently accepted: {name}')
    for name, scans, delays in (
            ('empty_scans', [], 0), ('missing_channel', [{5: 1}], 0),
            ('extra_channel8', [{**scan, 8: 1}], 0), ('boolean_sample', [{**scan, 5: True}], 0),
            ('negative_sample', [{**scan, 6: -1}], 0), ('wide_sample', [{**scan, 7: 4096}], 0),
            ('nonfinite_sample', [{**scan, 9: float('nan')}], 0),
            ('missing_delay', [scan], []), ('boolean_delay', [scan], False),
            ('wide_delay', [scan], 9), ('missing_delay_channel', [scan], [{5: 0}])):
        expect(name, lambda s=scans, d=delays: DualAdcLab(image, dsp, scans=s, scan_ready_after=d),
               'Invalid explicit ADC-A readiness fixture' if name.endswith('delay') or name == 'missing_delay_channel'
               else 'Explicit six-channel 12-bit ADC scans required')
    lab = DualAdcLab(image, dsp, scans=[scan]); lab.initialize_adc()
    expect('unstarted_a_data', lambda: read_mmio(lab.cpu, ADC + 0x34), 'ADC-A data read before explicit scan')
    expect('byte_data', lambda: lab.adc.validate(UC_MEM_READ, ADC + 0x34, 1), 'Unsupported ADC-A data width')
    expect('unknown_channel', lambda: read_mmio(lab.cpu, ADC + 0x20), 'Unmodeled recovery ADC register/access')
    write_mmio(lab.cpu, SEQA, A_CONFIG | START)
    expect('overlap_a', lambda: write_mmio(lab.cpu, SEQA, A_CONFIG | START), 'Overlapping ADC sequences are outside fixture')
    expect('overlap_b', lambda: write_mmio(lab.cpu, CONTROL, CONFIG | ENABLE | START), 'Overlapping ADC sequences are outside fixture')
    expect('reconfigure_pending', lambda: write_mmio(lab.cpu, SEQA, A_CONFIG), 'ADC-A reconfiguration with pending scan')
    for ch in CHANNELS:
        value = read_mmio(lab.cpu, ADC + 0x20 + 4 * ch)
        verify(value == VALID | ch << 26 | 2048 << 4 and
               read_mmio(lab.cpu, ADC + 0x20 + 4 * ch) == value & ~VALID, 'ADC-A metadata/read-clear changed')
    expect('exhausted_scans', lambda: write_mmio(lab.cpu, SEQA, A_CONFIG | START), 'Exhausted ADC-A scan fixture')
    write_mmio(lab.cpu, CONTROL, CONFIG | ENABLE | START)
    expect('a_during_b', lambda: write_mmio(lab.cpu, SEQA, A_CONFIG | START), 'Overlapping ADC sequences are outside fixture')
    write_mmio(lab.cpu, POWER_SET, ADC_POWER)
    expect('unpowered_a', lambda: write_mmio(lab.cpu, SEQA, A_CONFIG | START), 'ADC-A conversion before successful startup/calibration')
    return {'rejected': rejected, 'data_metadata_and_read_clear_checked_for_all_six_channels': True}


def stream(image, dsp, *, candidate=True, enabled=True, side=None, fault=None):
    scans = [{ch: 1500 + ch * 83 + (i % 17) * 7 for ch in CHANNELS} for i in range(512)]
    delays = [dict.fromkeys(CHANNELS, 0) for _ in scans]
    if fault == 'a': delays[9][7] = None
    b_delays = [0] * 128
    if fault == 'b': b_delays[0] = None
    lab = DualAdcLab(image, dsp, scans=scans, scan_ready_after=delays, ready_after=b_delays,
                     pin_changes=((70, 0), (120, 1), (170, 0)), candidate=candidate, audio_enabled=enabled)
    lab.initialize_adc()
    lab.cpu.mem_write(RESULT, struct.pack('<I', 3200))
    frequencies = ((80, 190), ()) if side == 0 else ((), (110, 230)) if side == 1 else ((80, 190), (110, 230))
    _, result, pwm, combined = audio_stream(image, dsp, [tone(i, frequencies) for i in range(192)],
                                           candidate=candidate, lab=lab)
    verify(lab.iterations == 255 == lab.calls[0x2B538] and not lab.adc.a_pending and not lab.adc.pending and
           not lab.armed and not lab.shifted and not lab.irq.active and not lab.irq.frames,
           'Unified ADC foreground/audio did not complete and drain')
    verify(len(lab.adc.a_started) > 10 and len(lab.adc.starts) == 10 and lab.adc.index == 10 and
           lab.adc.aborted == ([0] if fault == 'b' else []) and
           len(lab.adc.a_aborted) == int(fault == 'a') and
           lab.cpu.mem_read(AVERAGE_READY, 1) == b'\1', 'Integrated scan/average/recovery path was bypassed')
    b_mean = struct.unpack('<H', lab.cpu.mem_read(0x20000EF0, 2))[0]
    verify(b_mean == (3290 if fault == 'b' else 3300), 'Integrated B average/stale-result semantics changed')
    for record in lab.delay_clock.records: validate_delay(record)
    report = {**result, 'iterations': lab.iterations, 'adc_a_scans': len(lab.adc.a_started),
              'adc_a_valid_reads': sum(r[2] for r in lab.adc.a_reads),
              'adc_a_invalid_reads': sum(not r[2] for r in lab.adc.a_reads),
              'adc_a_aborted': lab.adc.a_aborted, 'adc_b_valid_reads': sum(r[1] for r in lab.adc.reads),
              'adc_b_aborted': lab.adc.aborted, 'checked_irq_returns': lab.irq.checked_returns,
              'adc_b_mean': b_mean,
              'combined_spi_i2s_batches': combined, 'runtime_sensa_frame_checks': len(lab.observed_decodes),
              'original_calibration_returns': lab.calibration_returns, 'averaged_channel_values': words(lab.cpu, AVERAGED_VALUES),
              'source_pcm_preserved': True, 'fault_fixture': fault}
    return lab, report, pwm


def integrated_cases(image, dsp):
    variants, reports = [], []
    for candidate in (False, True):
        print('Unified ADC: disabled pair', candidate, file=sys.stderr, flush=True)
        lab, report, idle = stream(image, dsp, candidate=candidate, enabled=False)
        variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                         tuple(lab.cpu.reg_read(r) for r in PERSISTENT), idle,
                         lab.release_bus.commands, lab.release_bus.frames, lab.adc.a_reads,
                         lab.adc.reads, lab.adc.power_writes))
        reports.append(report)
    verify(variants[0] == variants[1] and reports[0] == reports[1], 'Unified ADC disabled candidate changed stock behavior')
    results = {'inactive_pair': {**reports[0], 'compared_ram_bytes': 0x13D28,
                                'stock_disabled_candidate_match': True}, 'active': {}}
    for name, side, fault in (('stereo', None, None), ('left', 0, None), ('right', 1, None),
                              ('a_timeout', None, 'a'), ('b_timeout', None, 'b')):
        reference = idle
        if fault:
            print('Unified ADC: fault idle reference', fault, file=sys.stderr, flush=True)
            _, _, reference = stream(image, dsp, candidate=False, enabled=False, fault=fault)
        print('Unified ADC: active', name, file=sys.stderr, flush=True)
        lab, report, pwm = stream(image, dsp, side=side, fault=fault)
        if side is None: check_motor_trace(pwm, reference)
        else: check_isolated_trace(pwm, reference, side)
        verify(lab.values()[10] >= 1 and lab.values()[1] == 0 and
               lab.values()[5] == len(lab.observed_decodes) > 1, 'Unified Sensa runtime checks or final stop missing')
        results['active'][name] = {**report, 'active_and_idle_pwm_checked': True,
                                   'opposite_palm_and_trigger_isolation_checked': side is not None}
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    print('Unified ADC: foreground scans', file=sys.stderr, flush=True)
    report = {'scans': scan_cases(image, dsp)}
    print('Unified ADC: startup scans', file=sys.stderr, flush=True)
    report['startup'] = startup_scan(image, dsp)
    report['faults'] = fault_cases(image, dsp)
    print('Unified ADC: trigger reports', file=sys.stderr, flush=True)
    report['trigger_reports'] = trigger_reports(image, dsp)
    report['guards'] = guards(image, dsp)
    report['integrated'] = integrated_cases(image, dsp)
    report.update({'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                   'physical_adc_timing_or_voltage_scale_proven': False,
                   'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}})
    print(json.dumps(report, indent=2))


if __name__ == '__main__': main()

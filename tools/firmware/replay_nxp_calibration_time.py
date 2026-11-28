"""Original calibration busy waits advance supplied MRT/motor/WWDT clocks."""
import argparse
from functools import wraps
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn.arm_const import UC_ARM_REG_PC, UC_ARM_REG_PRIMASK
from nxp_calibration_clock import ENTRY, LOOP, CalibrationInstructionClock
from replay_nxp_adc_dual import DualAdcLab, CHANNELS
from replay_nxp_rgb_spi import RX, TX, CONTEXT, BUFFER, RECEIVE, audio_stream, tone
from replay_nxp_foreground_adc import RESULT, READY
from replay_nxp_power_gpio import CORE_CLOCK, validate_delay
from replay_nxp_loop import PERSISTENT
from replay_nxp_clock import check_motor_trace
from nxp_wwdt_model import WatchdogReset
from simulate_audio_haptics import RETURN, STOCK_SHA256, verify


def with_clock(method):
    @wraps(method)
    def call(self, *args, **kwargs):
        before = self.cal_clock.enabled
        self.cal_clock.enabled = True
        try: return method(self, *args, **kwargs)
        finally: self.cal_clock.enabled = before
    return call


class CalibrationLab(DualAdcLab):
    def __init__(self, image, dsp, *, budget=3, **options):
        options.setdefault('scans', [dict.fromkeys(CHANNELS, 2048)] * 512)
        super().__init__(image, dsp, budget=budget, **options)
        self.cal_clock = CalibrationInstructionClock(self, budget)

    initialize_adc = with_clock(DualAdcLab.initialize_adc)
    call_adc = with_clock(DualAdcLab.call_adc)
    call_scan = with_clock(DualAdcLab.call_scan)
    call_average = with_clock(DualAdcLab.call_average)
    run = with_clock(DualAdcLab.run)

    @with_clock
    def call_wait(self, duration, clock_argument=180000000, *, instruction_limit=1000000):
        verify(type(duration) is int and 0 <= duration <= 0xFFFFFFFF and
               type(clock_argument) is int and 0 <= clock_argument <= 0xFFFFFFFF, 'Invalid explicit wait arguments')
        self.replay.call(ENTRY, RETURN, duration, clock_argument, instruction_limit=instruction_limit)
        if self.cal_clock.active:
            verify(self.cpu.reg_read(UC_ARM_REG_PC) == RETURN, 'Calibration wait did not return')
            self.cal_clock.finish()


def validate_record(r):
    loops = r['foreground_counts'].get(hex(LOOP), 0)
    expected = r['argument'] * r['clock_argument'] // 4000000 if r['argument'] else 0
    verify(loops == expected and sum(r['foreground_counts'].values()) == r['foreground_instructions'],
           'Original calibration division/loop count mismatch')
    verify(r['bus_cycles'] == (r['foreground_instructions'] + r['handler_instructions']) *
           r['cycles_per_observed_instruction'], 'Calibration instruction time omitted/double-charged')
    instructions = r['foreground_instructions'] + r['handler_instructions']
    for prefix, frequency in (('bus', r['bus_clock_hz']), ('wdclk', r['wdclk_hz'])):
        verify(r[prefix + '_cycles'] * r['core_clock_hz'] + r[prefix + '_fraction_end'] -
               r[prefix + '_fraction_start'] == instructions * r['cycles_per_observed_instruction'] * frequency,
               'Calibration fractional clock conservation failed')


def direct_cases(image, dsp):
    reports = {}
    for duration, clock, budget in ((0, 180000000, 3), (1, 4000000, 3), (1, 96000000, 3),
                                    (1, 180000000, 3), (11, 180000000, 3), (121, 180000000, 3),
                                    (1000, 180000000, 1), (1000, 180000000, 3), (1000, 180000000, 7)):
        print('Calibration wait:', duration, clock, budget, file=sys.stderr, flush=True)
        lab = CalibrationLab(image, dsp, budget=budget)
        before = lab.timer.cycles, lab.wdt.cycles, lab.irq.checked_returns
        lab.call_wait(duration, clock)
        r, = lab.cal_clock.records
        validate_record(r)
        verify(lab.timer.cycles - before[0] == r['bus_cycles'] and
               lab.wdt.cycles - before[1] == r['wdclk_cycles'] and not lab.delay_clock.records,
               'Calibration time was not independently advanced')
        reports[f'{duration},{clock},{budget}'] = {**r, 'checked_irq_returns': lab.irq.checked_returns - before[2],
                                                 'mrt_expirations': lab.timer.channels[0].expirations}
    same = [reports[f'1000,180000000,{b}'] for b in (1, 3, 7)]
    verify(len({r['pc_sha256'] for r in same}) == 1 and
           all(r['foreground_counts'] == same[0]['foreground_counts'] for r in same),
           'CPI sensitivity changed original foreground work')
    return reports


def masked_case(image, dsp):
    lab = CalibrationLab(image, dsp)
    lab.cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
    lab.call_wait(1000)
    r, = lab.cal_clock.records
    validate_record(r)
    ch = lab.timer.channels[0]
    verify(r['handler_instructions'] == 0 and ch.expirations > 1 and ch.coalesced == ch.expirations - 1 and
           lab.counter_writes == 0 and lab.irq.checked_returns == 0, 'Masked wait invented serviced timer ticks')
    before = {'expirations': ch.expirations, 'coalesced': ch.coalesced,
              'pending_flag': ch.flag, 'mrt_counter': lab.clock(), 'mrt_phase': lab.phase()}
    lab.cpu.reg_write(UC_ARM_REG_PRIMASK, 0)
    lab.irq.service(instruction_limit=100000)
    verify(ch.acknowledgements == 1 and not ch.flag and lab.clock() == 0 and lab.phase() == 1,
           'Masked MRT loss was hidden by fabricated catch-up ticks')
    return {'wait': r, 'before_unmask': before, 'mrt_phase_after_unmask': lab.phase(),
            'independent_elapsed_time_stop_proven': False}


def mixed_clock_case(image, dsp):
    lab = CalibrationLab(image, dsp)
    before = lab.timer.cycles, lab.wdt.cycles
    lab.call_wait(11)
    phase_after_calibration = lab.delay_clock.wdt_remainder
    lab.call_delay(1)
    a, = lab.cal_clock.records
    b, = lab.delay_clock.records
    validate_record(a); validate_delay(b)
    total = lab.timer.cycles - before[0]
    expected_wdclk, expected_phase = divmod(total * lab.wdt.clock_hz, lab.cal_clock.core_hz)
    separately_rounded = a['wdclk_cycles'] + b['bus_cycles'] * lab.wdt.clock_hz // lab.cal_clock.core_hz
    verify(total == a['bus_cycles'] + b['bus_cycles'] and
           lab.wdt.cycles - before[1] == expected_wdclk and
           lab.delay_clock.wdt_remainder == expected_phase and separately_rounded != expected_wdclk,
           'Mixed helper boundary lost fractional watchdog time or negative control did not discriminate')
    return {'calibration_wait': a, 'd620_wait': b, 'total_bus_cycles': total,
            'wdclk_cycles': lab.wdt.cycles - before[1], 'global_floor_oracle': expected_wdclk,
            'separately_rounded_negative_control': separately_rounded,
            'shared_fraction_after_calibration': phase_after_calibration, 'final_fraction': expected_phase}


def dma_cases(image, dsp):
    reports = {}
    for name, order, offsets, masked in (
            ('rx_then_tx', (RX, TX), (2400, 4800), False),
            ('tx_then_rx', (TX, RX), (2400, 4800), False),
            ('pending_while_masked', (RX, TX), (2400, 4800), True),
            ('timer_overlap', (RX, TX), (12030, 12060), False)):
        print('Calibration DMA:', name, file=sys.stderr, flush=True)
        lab = CalibrationLab(image, dsp)
        size = sum(row[2] for row in lab.plan(TX))
        response = bytes((i * 29 + 7) & 255 for i in range(size))
        sent = lab.exchange(response)
        verify(sent == bytes(110) and lab.cpu.mem_read(RECEIVE, size) == response, 'Original RGB/DMA fixture changed wire bytes')
        def complete(channel):
            verify(channel in lab.shifted and channel in lab.armed, 'Scheduled DMA completion duplicated/unshifted')
            lab.shifted.remove(channel); lab.armed.remove(channel)
            lab.dma.signal(a=1 << channel)
        for i, (channel, offset) in enumerate(zip(order, offsets)):
            lab.cal_clock.schedule(offset, str(channel), lambda ch=channel: complete(ch))
        if masked: lab.cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
        lab.call_wait(121)
        held_callback = lab.callbacks
        if masked:
            verify(lab.callbacks == 0 and lab.dma.asserted(), 'Masked DMA was serviced early')
            lab.cpu.reg_write(UC_ARM_REG_PRIMASK, 0); lab.irq.service(instruction_limit=100000)
        verify(lab.callbacks == 1 and not lab.dma.asserted() and not lab.armed and not lab.shifted and
               lab.cpu.mem_read(CONTEXT, 2) == bytes(2) and lab.cpu.mem_read(CONTEXT + 24, 4) == bytes(4) and
               lab.cpu.mem_read(BUFFER, 1200) == bytes(1200) and not lab.irq.active and not lab.irq.frames,
               'Original scheduled DMA ISR/callback did not complete and drain')
        verify(not lab.cal_clock.events and all(e['inside_calibration_wait'] and
               0 <= e['actual_bus_cycle'] - e['due_bus_cycle'] <= 3 for e in lab.cal_clock.delivered),
               'Supplied DMA event was delivered outside its wait/budget')
        if name == 'timer_overlap':
            verify(lab.cal_clock.delivered[0]['ipsr'] == 25 and lab.cal_clock.delivered[1]['ipsr'] == 17 and
                   any(e['event'] == 'entry' and e.get('irq') == 1 and e.get('return') == '0xfffffff1'
                       for e in lab.irq.trace), 'DMA did not preempt the original MRT handler')
        for r in lab.cal_clock.records: validate_record(r)
        reports[name] = {'events': lab.cal_clock.delivered, 'callbacks_during_wait': held_callback,
                         'final_callbacks': lab.callbacks, 'checked_irq_returns': lab.irq.checked_returns,
                         'irq_trace': lab.irq.trace, 'original_wait': lab.cal_clock.records,
                         'supplied_fifo_bytes': size, 'wire_sha256': hashlib.sha256(sent).hexdigest(),
                         'no_usb_host_arrival_or_dma_bus_timing_claim': True}
    return reports


def initialization_cases(image, dsp):
    reports = {}
    for name, options, expected in (
            ('calibration', {}, [1]),
            ('dummy', {'calibration_required': False}, [1]),
            ('retry', {'calibration_after': (1, 0)}, [0, 1]),
            ('never', {'calibration_after': (None,) * 4}, [0] * 4)):
        print('Timed ADC initialization:', name, file=sys.stderr, flush=True)
        lab = CalibrationLab(image, dsp, **options)
        before = lab.timer.cycles
        lab.initialize_adc()
        verify(lab.calibration_returns == expected and not lab.delay_clock.records and
               lab.timer.cycles - before == sum(r['bus_cycles'] for r in lab.cal_clock.records),
               'Full initialization omitted/double-charged wait time or changed status semantics')
        for r in lab.cal_clock.records: validate_record(r)
        reports[name] = {'calibration_returns': lab.calibration_returns, 'waits': lab.cal_clock.records,
                         'checked_irq_returns': lab.irq.checked_returns, 'mrt_counter': lab.clock(),
                         'mrt_phase': lab.phase(), 'supplied_bus_cycles': lab.timer.cycles - before}
    return reports


def guards(image, dsp):
    rejected = []
    def expect(name, action, message):
        try: action()
        except RuntimeError as e:
            verify(str(e) == message, f'Wrong rejection {name}: {e}'); rejected.append(name)
        else: raise RuntimeError(f'Invalid calibration fixture silently accepted: {name}')
    lab = CalibrationLab(image, dsp)
    for value in (0, -1, True, 17):
        expect(f'budget_{value}', lambda v=value: CalibrationInstructionClock(lab, v),
               'Calibration CPI budget must be explicit and positive')
    expect('negative_duration', lambda: lab.call_wait(-1), 'Invalid explicit wait arguments')
    expect('boolean_clock', lambda: lab.call_wait(1, True), 'Invalid explicit wait arguments')
    expect('negative_event', lambda: lab.cal_clock.schedule(-1, 'bad', lambda: None), 'Invalid supplied calibration event')
    lab.cal_clock.schedule(0, 'once', lambda: None)
    expect('duplicate_event', lambda: lab.cal_clock.schedule(1, 'once', lambda: None), 'Pending calibration event name/boundary is invalid')
    expect('invalid_callback', lambda: lab.cal_clock.schedule(1, 'bad', None), 'Invalid supplied calibration event')
    zero = CalibrationLab(image, dsp); zero.cpu.mem_write(CORE_CLOCK, bytes(4))
    expect('zero_core_clock', lambda: CalibrationInstructionClock(zero, 3), 'Calibration clock requires an explicit nonzero core clock')
    changed = CalibrationLab(image, dsp); changed.cpu.mem_write(CORE_CLOCK, struct.pack('<I', 48000000))
    expect('changed_core_clock', lambda: changed.call_wait(11), 'Core-clock transition outside calibration fixture')
    original = CalibrationLab(image, dsp); original.call_wait(11)
    record = original.cal_clock.records[0]
    shortened = {**record, 'foreground_counts': {**record['foreground_counts'], hex(LOOP): 0}}
    expect('skipped_loop_record', lambda: validate_record(shortened), 'Original calibration division/loop count mismatch')
    expect('double_charged_record', lambda: validate_record({**record, 'bus_cycles': record['bus_cycles'] + 1}),
           'Calibration instruction time omitted/double-charged')
    tiny = CalibrationLab(image, dsp)
    expect('zero_quotient_loop', lambda: tiny.call_wait(1, 1, instruction_limit=4000),
           'Foreground FP execution did not reach its endpoint')
    verify(tiny.cal_clock.active is not None and not tiny.cal_clock.records, 'Incomplete zero-quotient delay falsely returned')
    huge = CalibrationLab(image, dsp)
    expect('original_assert_boundary', lambda: huge.call_wait(0xFFFFFFFF, 0xFFFFFFFF), 'Unmodeled calibration helper call/branch')
    watchdog = CalibrationLab(image, dsp)
    watchdog.wdt.tv, watchdog.wdt.phase, watchdog.wdt.warning = 1, 0, 0
    watchdog.wdt.schedule_warning()
    try: watchdog.call_wait(121)
    except WatchdogReset:
        verify(watchdog.wdt.reset_reason == 'counter_expired' and not watchdog.cal_clock.records,
               'Unexpected watchdog/reset completion')
    else: raise RuntimeError('Watchdog expiry during calibration was silently ignored')
    return {'rejected': rejected, 'watchdog_terminal_reset_retained': True,
            'watchdog_reset_cycle': watchdog.wdt.reset_cycle,
            'original_zero_quotient_can_exceed_instruction_budget': True}


class RecoveryDuringAudio(CalibrationLab):
    def __init__(self, image, dsp, *, enabled=True, **options):
        super().__init__(image, dsp, samples=[3300, 3450], ready_after=[None, 0], audio_enabled=enabled,
                         pin_changes=((70, 0),), **options)
        self.recovery_events = []

    def run(self, **options):
        if self.iterations in (32, 33):
            self.call_adc()
            value, = struct.unpack('<I', self.cpu.mem_read(RESULT, 4))
            self.recovery_events.append({'iteration': self.iterations, 'result': value,
                                         'ready': self.cpu.mem_read(READY, 1)[0],
                                         'calibration_waits': len(self.cal_clock.records)})
        super().run(**options)


def stream(image, dsp, *, candidate=True, enabled=True):
    lab = RecoveryDuringAudio(image, dsp, candidate=candidate, enabled=enabled)
    lab.initialize_adc(); lab.cpu.mem_write(RESULT, struct.pack('<I', 3200))
    _, result, pwm, combined = audio_stream(image, dsp, [tone(i, ((80, 190), (110, 230))) for i in range(64)],
                                           candidate=candidate, lab=lab)
    verify(lab.iterations == 127 == lab.calls[0x2B538] and lab.adc.aborted == [0] and lab.adc.index == 2 and
           not lab.adc.pending and not lab.adc.a_pending and not lab.armed and not lab.shifted and
           not lab.irq.active and not lab.irq.frames and lab.cal_clock.active is None,
           'Timed calibration/audio/ADC stream failed to complete and drain')
    verify(lab.recovery_events == [{'iteration': 32, 'result': 3200, 'ready': 0, 'calibration_waits': 5},
                                   {'iteration': 33, 'result': 3450, 'ready': 1, 'calibration_waits': 5}],
           'Original timed recovery did not distinguish stale from fresh data')
    for r in lab.cal_clock.records: validate_record(r)
    for r in lab.delay_clock.records: validate_delay(r)
    report = {**result, 'iterations': lab.iterations, 'source_pcm_preserved': True,
              'recovery_events': lab.recovery_events, 'adc_a_scans': len(lab.adc.a_started),
              'checked_irq_returns': lab.irq.checked_returns, 'runtime_sensa_frame_checks': len(lab.observed_decodes),
              'combined_spi_i2s_batches': combined, 'original_calibration_waits': lab.cal_clock.records,
              'calibration_bus_cycles': sum(r['bus_cycles'] for r in lab.cal_clock.records),
              'd620_waits': len(lab.delay_clock.records)}
    return lab, report, pwm


def integrated_cases(image, dsp):
    variants, reports = [], []
    for candidate in (False, True):
        print('Timed calibration: stock/disabled', candidate, file=sys.stderr, flush=True)
        lab, report, idle = stream(image, dsp, candidate=candidate, enabled=False)
        variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                         tuple(lab.cpu.reg_read(r) for r in PERSISTENT), idle,
                         lab.adc.reads, lab.adc.a_reads, lab.adc.power_writes,
                         lab.release_bus.frames, lab.cal_clock.records))
        reports.append(report)
    verify(variants[0] == variants[1] and reports[0] == reports[1], 'Timed calibration changed disabled candidate behavior')
    print('Timed calibration: active stereo recovery', file=sys.stderr, flush=True)
    lab, report, pwm = stream(image, dsp)
    check_motor_trace(pwm, idle)
    verify(lab.values()[10] >= 1 and lab.values()[1] == 0 and
           lab.values()[5] == len(lab.observed_decodes) > 1, 'Timed calibration/audio render and stop not checked')
    return {'inactive_pair': {**reports[0], 'compared_shared_ram_bytes': 0x13D28, 'stock_disabled_match': True},
            'active': {**report, 'independent_active_palms_idle_triggers_and_final_drain_checked': True},
            'injection_scope': 'Original B reader called at supplied foreground boundaries 32/33; not autonomous USB arrivals'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    report = {'direct': direct_cases(image, dsp), 'masked': masked_case(image, dsp),
              'mixed_clock': mixed_clock_case(image, dsp),
              'dma': dma_cases(image, dsp), 'initialization': initialization_cases(image, dsp),
              'guards': guards(image, dsp), 'whole_device_equivalence_proven': False,
              'flashable_candidate_ready': False, 'physical_clock_or_latency_proven': False}
    report['integrated'] = integrated_cases(image, dsp)
    report['input_sha256'] = {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}
    print(json.dumps(report, indent=2))


if __name__ == '__main__': main()

"""Drive the compiled publisher's clock through original MRT IRQs, offline."""
import argparse
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.arm_const import UC_ARM_REG_BASEPRI, UC_ARM_REG_IPSR, UC_ARM_REG_PC, UC_ARM_REG_PRIMASK, UC_ARM_REG_R0

from nxp_mrt_model import MRT, MRT_IRQ, VALUE_MASK, MrtModel
from nxp_ctimer_clock import MatchTimer
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from cortex_m_fpu import CortexMFpuInterrupts
from replay_nxp_publication import Lab, CLOCK, MAIN_DISPATCH_CALL, PUBLICATION, RETURN, STOCK_SHA256, captured_window
from replay_nxp_dma_preemption import acknowledge
from simulate_audio_haptics import CTIMER2, CTIMER3, TAP_BUFFER, check_continuous_dma, read_sensa_pwm, tone, verify


PHASE = 0x20013A00


class TimedLab(Lab):
    def __init__(self, image, dsp, *, motors=False):
        super().__init__(image, dsp)
        self.replay.call(0xA9F4, RETURN, 1)
        frequency = self.cpu.reg_read(UC_ARM_REG_R0)
        self.timer = MrtModel(self.cpu, self.irq, frequency)
        self.replay.call(0x22F94, RETURN)
        verify((frequency == 96_000_000 and self.timer.channels[0].interval == 12000), 'Clock replay check failed at condition 30')
        verify((self.timer.channels[0].control == 1 and self.irq.priorities[9] == 0x40), 'Clock replay check failed at condition 31')
        self.counter_writes, self.phase_writes = 0, 0
        self.motors = [MatchTimer(self.cpu, self.irq, base, irq, frequency) for base, irq in
                       ((CTIMER2, 36), (CTIMER3, 13))] if motors else []
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.counter_writer, begin=CLOCK, end=CLOCK + 3)
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.counter_writer, begin=PHASE, end=PHASE + 3)

    def counter_writer(self, cpu, access, address, size, value, data):
        verify((size == 4 and cpu.reg_read(UC_ARM_REG_IPSR) == MRT_IRQ + 16), 'Counter was written outside stock MRT IRQ')
        expected = 0x148E0 if address == CLOCK else 0x148CC
        verify((cpu.reg_read(UC_ARM_REG_PC) == expected), 'Counter writer is not the pinned stock instruction')
        if address == CLOCK: self.counter_writes += 1
        else: self.phase_writes += 1

    def clock(self):
        return struct.unpack('<I', self.cpu.mem_read(CLOCK, 4))[0]

    def phase(self):
        return struct.unpack('<I', self.cpu.mem_read(PHASE, 4))[0]

    def run(self):
        # Intentionally no timestamp argument or host write to CLOCK.
        self.replay.call(MAIN_DISPATCH_CALL, MAIN_DISPATCH_CALL + 4, instruction_limit=150000)

    def send(self):
        self.irq.pend(17)
        verify((self.irq.service() == 1), 'Clock replay check failed at condition 55')

    def advance(self, cycles, *, service=True):
        verify((isinstance(cycles, int) and cycles >= 0), 'Clock replay check failed at condition 58')
        if not service:
            self.timer.advance(cycles)
            for motor in self.motors: motor.advance(cycles)
            return
        while cycles:
            running = [ch.remaining for ch in self.timer.channels if ch.remaining] + [motor.remaining for motor in self.motors]
            amount = min([cycles, *running])
            self.timer.advance(amount)
            for motor in self.motors: motor.advance(amount)
            self.irq.service(instruction_limit=100000)
            cycles -= amount

    def advance_us(self, microseconds, *, service=True):
        cycles = Fraction(microseconds * self.timer.clock_hz, 1_000_000)
        verify((cycles.denominator == 1), 'Clock replay check failed at condition 71')
        self.advance(int(cycles), service=service)


def register_checks():
    cpu, irq = fixture('7047', model=CortexMFpuInterrupts)
    cpu.mem_map(MRT, 4096)
    timer = MrtModel(cpu, irq, 96_000_000)
    read = lambda offset: read_mmio(cpu, MRT + offset)
    write = lambda offset, value: write_mmio(cpu, MRT + offset, value)
    verify((read(0) == 0 and read(4) == VALUE_MASK and read(12) == 0 and read(0xF0) == 0x184), 'Clock replay check failed at condition 81')
    write(8, 1); write(0, 12)
    verify((read(4) == 11 and read(12) == 2), 'Clock replay check failed at condition 83')
    timer.advance(11)
    verify((read(4) == 0 and not timer.asserted()), 'Clock replay check failed at condition 85')
    timer.advance(1)
    verify((read(4) == 11 and read(12) == 3 and timer.asserted()), 'Clock replay check failed at condition 87')
    write(12, 0)
    verify((timer.asserted()), 'Clock replay check failed at condition 89')
    write(12, 1)
    verify((read(12) == 2 and not timer.asserted()), 'Clock replay check failed at condition 91')
    timer.advance(3); write(0, 5)
    verify((read(4) == 8), 'Clock replay check failed at condition 93')
    timer.advance(9)
    verify((read(4) == 4 and timer.asserted()), 'Clock replay check failed at condition 95')
    write(0, 0x80000003)
    verify((read(0) == 3 and read(4) == 2), 'Clock replay check failed at condition 97')  # LOAD reads zero, force reload.
    write(12, 1); timer.advance(30)
    verify((timer.channels[0].expirations == 12 and timer.channels[0].coalesced == 9), 'Clock replay check failed at condition 99')
    write(0xF8, 1)
    verify((not timer.asserted()), 'Clock replay check failed at condition 101')
    write(0, 0x80000000)
    verify((read(4) == VALUE_MASK and read(12) == 0), 'Clock replay check failed at condition 103')
    write(0x18, 3); write(0x10, 7)
    verify((read(0x14) == 6), 'Clock replay check failed at condition 105')
    timer.advance(7)
    verify((read(0x14) == VALUE_MASK and read(0x1C) == 1 and read(0xF8) == 2), 'Clock replay check failed at condition 107')
    write(0xF8, 2)
    verify((read(0xF8) == 0), 'Clock replay check failed at condition 109')
    write(0x18, 0); write(0x10, 4); timer.advance(4)
    verify((read(0x1C) & 1 and not timer.asserted()), 'Clock replay check failed at condition 111')
    write(0x18, 1)
    verify((timer.asserted()), 'Clock replay check failed at condition 113')
    write(0x1C, 1)
    verify((not timer.asserted()), 'Clock replay check failed at condition 115')
    rejected = []
    for name, offset, value in (('stall', 8, 4), ('reserved_mode', 8, 6), ('oversized_interval', 0, 1 << 24),
                                ('multitask', 0xF0, 1 << 31), ('readonly_timer', 4, 0)):
        try: write(offset, value)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported MRT operation accepted: ' + name)
    return {'checks': ['reset_identity', 'interval_minus_one_readback', 'repeat_boundary', 'w1c_zero_noop',
                       'deferred_reload', 'force_reload_and_writeonly_load', 'coalesced_flag', 'force_stop',
                       'oneshot_and_cross_channel_ack', 'retained_disabled_flag'], 'rejected': rejected}


def counter_cases(image, dsp):
    lab = TimedLab(image, dsp)
    lab.advance_us(875)
    verify((lab.phase() == 7 and lab.clock() == 0), 'Clock replay check failed at condition 130')
    lab.advance_us(125)
    verify((lab.clock() == 1 and lab.phase() == 8 and lab.counter_writes == 1), 'Clock replay check failed at condition 132')
    lab.advance_us(19000)
    verify((lab.clock() == 20 and lab.phase() == 160 and lab.timer.channels[0].coalesced == 0), 'Clock replay check failed at condition 134')
    normal = {'mrt_irqs': lab.phase_writes, 'counter_writes': lab.counter_writes, 'counter': lab.clock(),
              'virtual_elapsed_us': 20000, 'checked_returns': lab.irq.checked_returns}
    masked = []
    for mask in ('primask', 'basepri', 'nvic'):
        current = TimedLab(image, dsp)
        if mask == 'primask': current.cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
        elif mask == 'basepri': current.cpu.reg_write(UC_ARM_REG_BASEPRI, 0x40)
        else: current.irq.enabled &= ~(1 << MRT_IRQ)
        current.advance_us(2000)
        verify((current.phase() == 0 and current.clock() == 0 and current.timer.channels[0].expirations == 16), 'Clock replay check failed at condition 144')
        current.cpu.reg_write(UC_ARM_REG_PRIMASK, 0); current.cpu.reg_write(UC_ARM_REG_BASEPRI, 0)
        current.irq.enabled |= 1 << MRT_IRQ
        verify((current.irq.service() == 1 and current.phase() == 1 and current.clock() == 0), 'Clock replay check failed at condition 147')
        verify((current.irq.service() == 0), 'Clock replay check failed at condition 148')
        current.advance_us(875)
        verify((current.phase() == 8 and current.clock() == 1), 'Clock replay check failed at condition 150')
        masked.append({'mask': mask, 'elapsed_us_to_counter_one': 2875, 'coalesced_expirations': 15,
                       'checked_returns': current.irq.checked_returns})
    wrapped = TimedLab(image, dsp)
    # Explicit initial condition only; subsequent writes must be stock ISR stores.
    wrapped.cpu.mem_write(CLOCK, struct.pack('<I', 0xFFFFFFFF))
    wrapped.cpu.mem_write(PHASE, struct.pack('<I', 0xFFFFFFFF))
    wrapped.advance_us(125)
    verify((wrapped.clock() == 0 and wrapped.phase() == 0 and wrapped.counter_writes == 1), 'Clock replay check failed at condition 158')
    return {'normal': normal, 'masked': masked, 'counter_and_phase_wrap': True,
            'nominal_mrt_period_us': 125, 'nominal_counter_period_us_without_loss': 1000}


def motor_register_checks():
    cpu, irq = fixture('7047', model=CortexMFpuInterrupts)
    cpu.mem_map(CTIMER2, 4096)
    for offset, value in ((4, 1), (0x14, 3), (0x18, 23999)):
        cpu.mem_write(CTIMER2 + offset, struct.pack('<I', value))
    timer = MatchTimer(cpu, irq, CTIMER2, 36, 96_000_000)
    timer.advance(23999)
    verify(read_mmio(cpu, CTIMER2 + 8) == 23999 and not timer.flag, 'Early motor match')
    timer.advance(1)
    verify(read_mmio(cpu, CTIMER2 + 8) == 0 and read_mmio(cpu, CTIMER2) == 1, 'Missing motor match/reset')
    write_mmio(cpu, CTIMER2, 0)
    verify(timer.flag, 'Zero W1C cleared motor flag')
    write_mmio(cpu, CTIMER2, 1); write_mmio(cpu, CTIMER2, 1)
    verify(not timer.flag and timer.acknowledgements == 1 and timer.w1c_writes == 2,
           'Redundant W1C invented an acknowledged event')
    timer.advance(24000 * 8)
    verify(timer.expirations == 9 and timer.coalesced == 7 and timer.flag, 'Motor events did not coalesce')
    rejected = []
    for name, offset, value in (('reconfigure', 4, 0), ('other_match', 0, 2)):
        try: write_mmio(cpu, CTIMER2 + offset, value)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported motor operation accepted: ' + name)
    return {'checks': ['match_boundary_and_reset', 'zero_w1c', 'redundant_w1c', 'coalesced_flag'],
            'rejected': rejected}


def publication_cases(image, dsp):
    lab = TimedLab(image, dsp); lab.feed(); lab.run()
    verify((lab.values()[5] == 1), 'Clock replay check failed at condition 165')
    lab.advance_us(249000); lab.run()
    verify((lab.clock() == 249 and lab.values()[1] == 1 and lab.values()[10] == 0), 'Clock replay check failed at condition 167')
    lab.advance_us(1000); lab.run()
    verify((lab.clock() == 250 and lab.values()[1] == 0 and lab.values()[10] == 1), 'Clock replay check failed at condition 169')
    source = {'counter_at_stop': lab.clock(), 'nominal_elapsed_us': 250000,
              'mrt_entries': lab.phase_writes, 'counter_writes': lab.counter_writes,
              'checked_returns': lab.irq.checked_returns}
    cadence = TimedLab(image, dsp); cadence.feed(); cadence.run()
    for _ in range(11):
        cadence.advance_us(1000); cadence.feed(); cadence.run()
    verify((cadence.clock() == 11 and cadence.values()[5] == 1), 'Clock replay check failed at condition 176')
    cadence.advance_us(1000); cadence.run()
    verify((cadence.values()[5] == 2), 'Clock replay check failed at condition 178')
    game = TimedLab(image, dsp); game.packet(); game.send()
    verify((game.values()[3] == 0), 'Clock replay check failed at condition 180')
    game.advance_us(11000); game.send(); game.run()
    verify((game.clock() == 11 and game.values()[3] == 1 and game.values()[2] == 1), 'Clock replay check failed at condition 182')
    # An event batch arrives during the compiled packet copy. The real
    # PRIMASK defers it, and a single hardware flag cannot remember 16 ticks.
    delayed = TimedLab(image, dsp); delayed.feed(); injected = []

    def elapsed(cpu, access, address, size, value, data):
        if not injected:
            verify((cpu.reg_read(UC_ARM_REG_PRIMASK) == 1), 'Clock replay check failed at condition 189')
            injected.append(True); delayed.advance_us(2000, service=False)

    delayed.cpu.hook_add(UC_HOOK_MEM_WRITE, elapsed, begin=0x20000B3C, end=0x20000B3C)
    delayed.run()
    verify((injected and delayed.phase() == 1 and delayed.clock() == 0), 'Clock replay check failed at condition 194')
    verify((delayed.timer.channels[0].coalesced == 15 and delayed.values()[5] == 1), 'Clock replay check failed at condition 195')
    delayed.advance_us(249000); delayed.run()
    verify((delayed.clock() == 249 and delayed.values()[10] == 0), 'Clock replay check failed at condition 197')
    delayed.advance_us(1000); delayed.run()
    verify((delayed.clock() == 250 and delayed.values()[10] == 1), 'Clock replay check failed at condition 199')
    verify((delayed.timer.cycles * 1_000_000 // delayed.timer.clock_hz == 252000), 'Clock replay check failed at condition 200')
    return {'source_stop': source, 'cadence_12_counter_ticks': True, 'original_game_rate_gate_11_ticks': True,
            'masked_copy_coalescence': {'elapsed_us_at_stop': 252000, 'firmware_counter_at_stop': delayed.clock(),
                                      'coalesced_expirations': 15, 'not_a_physical_latency_measurement': True}}


def timed_stream(image, dsp, blocks, arrivals):
    lab = TimedLab(image, dsp, motors=True)
    pwm = []
    for motor, expected in zip(lab.motors, (24000, 96000)):
        verify(motor.period == expected, 'Unexpected register-derived motor clock')

    def dispatch(status, bit):
        lab.advance_us(1000)
        acknowledge(lab.replay, lab.irq, lab.dma, status, bit, True)

    def block(tick, source, pcm):
        if tick == 0: lab.cpu.mem_write(TAP_BUFFER + 192, struct.pack('<I', 1))
        lab.run()
        pwm.append(read_sensa_pwm(lab.cpu, lab.registers))
        verify(lab.cpu.mem_read(source, 192) == pcm, 'Timed compiled path changed PCM')

    result = check_continuous_dma(image, dsp, blocks, replay=lab.replay, arrival_ticks=arrivals,
                                  on_block=block, tail_ticks=64, irq_dispatch=dispatch)
    intervals = (arrivals[-1] if arrivals else len(blocks) - 1) + 64
    verify(lab.clock() == intervals and lab.phase_writes == intervals * 8 and lab.counter_writes == intervals,
           'Timed PCM path lost or invented stock clock increments')
    verify(all(motor.expirations == motor.acknowledgements for motor in lab.motors),
           f'Motor event/ack mismatch: {[(motor.irq, motor.expirations, motor.acknowledgements, motor.coalesced) for motor in lab.motors]}')
    verify(all(motor.coalesced == 0 for motor in lab.motors) and lab.timer.channels[0].coalesced == 0,
           'Unmasked continuous schedule coalesced unexpectedly')
    return lab, result, pwm


def check_motor_trace(pwm, idle):
    verify(len(pwm) == len(idle) and len(pwm) >= 8, 'Different motor trace schedules')
    verify(all(any(row[side] != base[side] for row, base in zip(pwm, idle)) for side in (0, 1)),
           'Timed palm positive control equals the silent-PCM reference')
    verify(all(row[2:] == base[2:] == (0, 0) for row, base in zip(pwm, idle)), 'Timed audio leaked into trigger outputs')
    verify(pwm[-8:] == idle[-8:], 'Timed drain did not return to the silent-PCM reference')


def continuous(image, dsp, blocks=None, *, pause=False, idle_cache=None):
    if blocks is None: blocks = [tone(i, ((80, 190), (110, 230))) for i in range(96)]
    arrivals = [i + (24 if i >= 48 else 0) for i in range(len(blocks))] if pause else None
    if idle_cache is None: idle_cache = {}
    key = tuple(arrivals) if arrivals is not None else tuple(range(len(blocks)))
    if key not in idle_cache:
        _, _, idle_cache[key] = timed_stream(image, dsp, [bytes(192)] * len(blocks), arrivals)
    idle = idle_cache[key]
    lab, result, pwm = timed_stream(image, dsp, blocks, arrivals)
    verify(lab.values()[5] > 1 and lab.values()[10] >= 1 and lab.values()[1] == 0,
           'Timed compiled stream did not render and stop')
    check_motor_trace(pwm, idle)
    rejected = []
    for name, bad in (('silent_output', idle),
                      ('missing_left', [(base[0], *row[1:]) for row, base in zip(pwm, idle)]),
                      ('stuck_tail', pwm[:-1] + [(idle[-1][0] + 1, *idle[-1][1:])])):
        try: check_motor_trace(bad, idle)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Motor trace corruption passed: ' + name)
    return {**result, 'stock_counter': lab.clock(), 'mrt_entries': lab.phase_writes,
            'motor_entries': [motor.acknowledgements for motor in lab.motors], 'checked_returns': lab.irq.checked_returns,
            'motor_w1c_writes': [motor.w1c_writes for motor in lab.motors],
            'compiled_frames': lab.values()[5], 'stop_frames': lab.values()[10],
            'final_eight_motor_snapshots_neutral': True,
            'silent_pcm_reference_final_pwm': idle[-1], 'motor_trace_rejections': rejected,
            'activity_and_stop_compared_to_separate_silent_pcm_stream': True,
            'counter_writes_from_host_after_initialization': 0, 'host_constructed_candidate_frames': 0,
            'scope': 'Virtual timer events from initialized registers; DMA words and CPU execution time remain supplied'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    parser.add_argument('--wired-pcm-trace', type=Path); parser.add_argument('--dongle-pcm-trace', type=Path)
    parser.add_argument('--captured-excerpt-start', type=int, default=3000)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    if bool(args.wired_pcm_trace) != bool(args.dongle_pcm_trace): parser.error('Supply both captured PCM traces')
    verify((hashlib.sha256(image).hexdigest() == STOCK_SHA256), 'Clock replay check failed at condition 210')
    registers = {'mrt': register_checks(), 'motor': motor_register_checks()}
    print('Original MRT counter and mask cases', file=sys.stderr, flush=True)
    counters = counter_cases(image, dsp)
    print('Compiled publication with stock-clock writes only', file=sys.stderr, flush=True)
    publication = publication_cases(image, dsp)
    print('Register-derived MRT/motor clocks with compiled PCM publication', file=sys.stderr, flush=True)
    idle_cache = {}
    streams = {'synthetic_stereo': continuous(image, dsp, idle_cache=idle_cache),
               'synthetic_pause': continuous(image, dsp, pause=True, idle_cache=idle_cache)}
    for name, path in (('wired_capture', args.wired_pcm_trace), ('dongle_capture', args.dongle_pcm_trace)):
        if path is not None:
            print('Timed stream: ' + name, file=sys.stderr, flush=True)
            blocks, provenance = captured_window(path, args.captured_excerpt_start)
            streams[name] = {**continuous(image, dsp, blocks, idle_cache=idle_cache), **provenance}
    print(json.dumps({'scope': 'register-derived virtual MRT clock and original ISR stores, not physical timing',
                      'registers': registers, 'counters': counters, 'publication': publication,
                      'continuous': streams,
                      'counter_write_provenance': 'Only original IRQ9 stores at 0x148E0; no host clock writes after explicit initial state',
                      'clock_hz_from_stock': 96000000, 'stock_mrt_interval': 12000,
                      'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                      'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()

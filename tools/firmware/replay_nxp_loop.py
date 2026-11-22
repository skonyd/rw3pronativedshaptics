"""Run complete foreground iterations in a bounded explicit GPIO/gate fixture."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_INVALID, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE, UcError
from unicorn.arm_const import (UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
                              UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R10, UC_ARM_REG_R11,
                              UC_ARM_REG_SP, UC_ARM_REG_PC, UC_ARM_REG_IPSR)

from nxp_gpio_inputs import GPIO, GpioByteInputs
from nxp_ctimer_clock import MatchTimer
from replay_nxp_watchdog import WatchdogLab, LOOP_HEADER, LAST_LOOP_COUNTER
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from replay_nxp_dma_preemption import acknowledge
from replay_nxp_clock import check_motor_trace
from replay_nxp_publication import captured_window
from simulate_audio_haptics import (CTIMER2, CTIMER3, MAIN_DISPATCH_CALL, TX_TAIL, TAP_BUFFER, RETURN, STOCK_SHA256,
                                   check_continuous_dma, read_sensa_pwm, tone, verify)


# Independent static load/shift map from the pinned original 0x32128 scanner.
# These are GPIO pin numbers and raw bits, NOT physical button names.
PIN_BITS = {74: 30, 73: 29, 93: 24, 92: 23, 86: 27, 85: 26, 88: 25, 87: 28,
            62: 22, 58: 21, 57: 20, 56: 19, 55: 18, 54: 17, 60: 16, 44: 15,
            43: 14, 91: 13, 90: 12, 46: 9, 45: 8, 36: 7, 34: 5, 33: 4, 35: 6,
            9: 3, 38: 2, 112: 1, 37: 0}
RAW_INPUT, RAW_SCAN, RAW_STORED = 0x20000F68, 0x32128, 0x32236
PERSISTENT = (UC_ARM_REG_R4, UC_ARM_REG_R5, UC_ARM_REG_R6, UC_ARM_REG_R7,
              UC_ARM_REG_R8, UC_ARM_REG_R9, UC_ARM_REG_R11, UC_ARM_REG_SP)


class LoopLab(WatchdogLab):
    def __init__(self, image, dsp, *, candidate=True, connection=1, gate=1):
        super().__init__(image, dsp)
        # Restore FLEXCOMM3's real vector. The older class-16 command fixture
        # used IRQ17 for a synthetic shim; it is not a real USB source here.
        self.cpu.mem_write(0x8000 + 33 * 4, image[33 * 4:34 * 4])
        self.irq.enabled &= ~(1 << 17)
        if not candidate:
            for address in (TX_TAIL, MAIN_DISPATCH_CALL, 0x30F36):
                self.cpu.mem_write(address, image[address - 0x8000:address - 0x8000 + 4])
        pins = {pin: 1 for pin in PIN_BITS}; pins[137] = 0
        self.gpio = GpioByteInputs(self.cpu, pins)
        self.motors = [MatchTimer(self.cpu, self.irq, base, irq, self.timer.clock_hz)
                       for base, irq in ((CTIMER2, 36), (CTIMER3, 13))]
        # Explicit prologue-register/steady-state fixture, not completed boot.
        for reg, value in ((UC_ARM_REG_R4, 0x20000AEC), (UC_ARM_REG_R5, 1),
                           (UC_ARM_REG_R6, 0x200019B8), (UC_ARM_REG_R7, 0x20000F68),
                           (UC_ARM_REG_R8, 0), (UC_ARM_REG_R9, 0x200056B8),
                           (UC_ARM_REG_R11, 0x20005100), (UC_ARM_REG_SP, 0x20027000)):
            self.cpu.reg_write(reg, value)
        self.cpu.mem_write(0x200019C0, bytes([connection]))
        self.cpu.mem_write(0x2000A538, bytes([gate]))
        self.cpu.mem_write(LAST_LOOP_COUNTER, struct.pack('<I', 0xFFFFFFFF))
        self.calls, self.iterations = Counter(), 0
        self.in_foreground, self.foreground_mmio = False, Counter()
        self.cpu.hook_add(UC_HOOK_CODE, self.observe_loop)
        self.cpu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, self.check_mmio,
                          begin=0x40000000, end=0x4FFFFFFF)

    def check_mmio(self, cpu, access, address, size, value, data):
        if not self.in_foreground or cpu.reg_read(UC_ARM_REG_IPSR): return
        allowed = (access == UC_MEM_READ and size == 1 and address - GPIO in self.gpio.pins) or (
            access == UC_MEM_WRITE and size == 4 and address == 0x4000C008)
        verify(allowed, f'Unmodeled foreground MMIO at 0x{address:X}, width {size}')
        self.foreground_mmio[(access, address, size)] += 1

    def observe_loop(self, cpu, pc, size, data):
        if pc in (LOOP_HEADER, 0x31DB4, RAW_SCAN, MAIN_DISPATCH_CALL, 0x2B538):
            self.calls[pc] += 1

    def send(self):
        raise RuntimeError('The main-loop fixture does not model a real USB command IRQ')

    def run(self):
        # Split after the first instruction so start != until. Flush cached
        # translations when switching until boundaries; otherwise an already
        # translated interior address can be missed by Unicorn.
        stack = self.cpu.reg_read(UC_ARM_REG_SP)
        self.in_foreground = True
        try:
            self.cpu.ctl_flush_tb()
            self.irq.execute(LOOP_HEADER, LOOP_HEADER + 4, instruction_limit=100)
            self.cpu.ctl_flush_tb()
            self.irq.execute(LOOP_HEADER + 4, LOOP_HEADER, instruction_limit=100000)
        finally:
            self.in_foreground = False
        verify(self.cpu.reg_read(UC_ARM_REG_PC) == LOOP_HEADER and self.cpu.reg_read(UC_ARM_REG_SP) == stack,
               'Foreground iteration did not restore its boundary/stack')
        self.iterations += 1


def gpio_contracts(image, dsp):
    lab = LoopLab(image, dsp)
    initial = {pin: 1 for pin in PIN_BITS}
    scenes = [initial, {pin: 0 for pin in PIN_BITS}]
    scenes += [{**initial, pin: 0} for pin in PIN_BITS]
    seed = 0x91D2E3F4
    for _ in range(32):
        state = {}
        for pin in PIN_BITS:
            seed = (1664525 * seed + 1013904223) & 0xFFFFFFFF
            state[pin] = seed >> 31
        scenes.append(state)
    digest = hashlib.sha256()
    for pins in scenes:
        lab.gpio.pins.update(pins)
        lab.replay.call(RAW_SCAN, RAW_STORED)
        word, = struct.unpack('<I', lab.cpu.mem_read(RAW_INPUT, 4))
        expected = 0xC00 | sum(value << PIN_BITS[pin] for pin, value in pins.items())
        verify(word == expected, 'Raw GPIO pin-to-bit mapping mismatch')
        digest.update(struct.pack('<I', word))
    rejected = []
    lab.cpu.mem_write(0x7000, bytes.fromhex('01787047'))  # Original LDRB/BX test fixture.
    for name, action in (
            ('word_alias', lambda: read_mmio(lab.cpu, GPIO)),
            ('output_write', lambda: write_mmio(lab.cpu, GPIO, 0)),
            ('undefined_pin', lambda: lab.replay.call(0x7000, RETURN, GPIO + 188)),
            ('invalid_level', lambda: GpioByteInputs(fixture()[0], {1: 2}))):
        try: action()
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unmodeled GPIO operation accepted: ' + name)
    return {'raw_scenes': len(scenes), 'mapped_pins': len(PIN_BITS), 'result_sha256': digest.hexdigest(),
            'forced_high_bits': [10, 11], 'rejected': rejected,
            'physical_button_labels_or_debounce_verified': False}


def idle_comparison(image, dsp):
    variants = []
    for candidate in (False, True):
        lab = LoopLab(image, dsp, candidate=candidate)
        lab.cpu.mem_write(0x20009750, struct.pack('<I', 4))
        for _ in range(16):
            lab.advance_us(1000); lab.wdt.advance(800)
            lab.run(); lab.run()  # changed counter then rapid background poll
        verify(lab.calls[RAW_SCAN] == 16 and lab.calls[0x31DB4] == 16 and
               lab.calls[MAIN_DISPATCH_CALL] == 32 and lab.calls[0x2B538] == 32,
               'Original loop task/dispatch cadence mismatch')
        verify(lab.cpu.mem_read(0x20009750, 4) == bytes(4), 'Original countdown task did not finish')
        variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                         tuple(lab.cpu.reg_read(reg) for reg in PERSISTENT),
                         read_sensa_pwm(lab.cpu, lab.registers), lab.wdt.feeds, lab.calls))
    verify(variants[0] == variants[1], 'Inactive candidate changed stock loop state')
    return {'iterations_per_variant': 32, 'stock_ram_compared_bytes': 0x13D28,
            'stock_candidate_persistent_registers_and_pwm_match': True, 'countdown_initial': 4,
            'original_scans_per_variant': 16, 'original_dispatches_per_variant': 32,
            'watchdog_feeds_per_variant': variants[0][3]}


def run_stream(image, dsp, blocks, *, candidate=True, enabled=True):
    lab = LoopLab(image, dsp, candidate=candidate)
    pwm = []

    def dispatch(status, bit):
        lab.advance_us(1000); lab.wdt.advance(800)
        acknowledge(lab.replay, lab.irq, lab.dma, status, bit, True)

    def block(tick, source, pcm):
        if tick == 0: lab.cpu.mem_write(TAP_BUFFER + 192, struct.pack('<I', int(enabled)))
        lab.run()
        verify(lab.cpu.mem_read(source, 192) == pcm, 'Main-loop path changed PCM')
        pwm.append(read_sensa_pwm(lab.cpu, lab.registers))

    result = check_continuous_dma(image, dsp, blocks, replay=lab.replay, tail_ticks=64,
                                  irq_dispatch=dispatch, on_block=block)
    verify(lab.calls[MAIN_DISPATCH_CALL] == lab.iterations == lab.calls[0x2B538], 'Sensa dispatcher was bypassed by host scheduling')
    return lab, result, pwm


def audio_cases(image, dsp, captures):
    blocks = [tone(i, ((80, 190), (110, 230))) for i in range(96)]
    base, base_result, idle = run_stream(image, dsp, blocks, candidate=False, enabled=False)
    off, off_result, off_pwm = run_stream(image, dsp, blocks, enabled=False)
    verify(base_result == off_result and idle == off_pwm and
           base.cpu.mem_read(0x20000000, 0x13D28) == off.cpu.mem_read(0x20000000, 0x13D28),
           'Disabled candidate changed real-loop PCM/PWM/stock RAM')
    report = {'disabled_vs_stock': {'stock_ram_compared_bytes': 0x13D28, 'pwm_and_pcm_match': True}}
    sources = [('synthetic_stereo', blocks, {}, (0, 1)),
               ('left_only', [tone(i, ((80, 190), ())) for i in range(96)], {}, (0,)),
               ('right_only', [tone(i, ((), (110, 230))) for i in range(96)], {}, (1,))]
    for name, path, start in captures:
        pcm, provenance = captured_window(path, start); sources.append((name, pcm, provenance, (0, 1)))
    for name, pcm, provenance, active_sides in sources:
        lab, result, pwm = run_stream(image, dsp, pcm)
        if len(active_sides) == 2: check_motor_trace(pwm, idle)
        else: check_isolated_trace(pwm, idle, active_sides[0])
        verify(lab.values()[5] > 1 and lab.values()[10] == 1 and lab.values()[1] == 0,
               'Full foreground fixture did not publish and stop')
        verify(len(lab.observed_decodes) == lab.values()[5], 'Independent runtime packet oracle was bypassed')
        report[name] = {**result, **provenance, 'main_iterations': lab.iterations,
                       'original_sensa_dispatch_calls': lab.calls[0x2B538], 'gpio_reads': sum(lab.gpio.reads.values()),
                       'compiled_frames': lab.values()[5], 'stop_frames': lab.values()[10],
                       'independent_runtime_packet_checks': len(lab.observed_decodes),
                       'active_palm_sides': active_sides,
                       'checked_irq_returns': lab.irq.checked_returns,
                       'foreground_mmio_guarded': True,
                       'active_and_drained_pwm_compared_with_stock_idle': True,
                       'host_constructed_candidate_frames': 0}
    return report


def check_isolated_trace(pwm, idle, side):
    verify(len(pwm) == len(idle) and len(pwm) >= 8, 'Different isolation trace schedules')
    verify(any(row[side] != base[side] for row, base in zip(pwm, idle)), 'Selected palm equals the silent reference')
    verify(all(row[1 - side] == base[1 - side] for row, base in zip(pwm, idle)), 'Audio changed the opposite palm')
    verify(all(row[2:] == base[2:] == (0, 0) for row, base in zip(pwm, idle)), 'Isolated audio leaked into triggers')
    verify(pwm[-8:] == idle[-8:], 'Isolated audio did not return to idle')


def unsupported_gates(image, dsp):
    cases = []
    for connection, gate in ((0, 0), (0, 1), (1, 0)):
        lab = LoopLab(image, dsp, connection=connection, gate=gate)
        bad = []
        lab.cpu.hook_add(UC_HOOK_MEM_INVALID,
                         lambda c, a, p, s, v, d: bad.append((c.reg_read(UC_ARM_REG_PC), p, s)) or False)
        try: lab.run()
        except UcError:
            verify(bad == [(0x10E5E, 0x40089800, 4)], 'Unexpected unmodeled gate boundary')
        else: raise RuntimeError('Unsupported gate silently completed with no I2C peer')
        cases.append({'connection_flag': connection, 'runtime_gate': gate, 'stopped_at': 'FLEXCOMM3 I2C configuration read',
                      'pc': '0x10E5E', 'address': '0x40089800', 'accepted_as_full_loop': False})
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    parser.add_argument('--wired-pcm-trace', type=Path); parser.add_argument('--dongle-pcm-trace', type=Path)
    parser.add_argument('--captured-excerpt-start', type=int, default=3000)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected foreground stock image')
    if bool(args.wired_pcm_trace) != bool(args.dongle_pcm_trace): parser.error('Supply both PCM traces')
    captures = [] if args.wired_pcm_trace is None else [('wired_capture', args.wired_pcm_trace, args.captured_excerpt_start),
                                                       ('dongle_capture', args.dongle_pcm_trace, args.captured_excerpt_start)]
    print(json.dumps({'scope': 'Complete iterations in supplied connection=1/gate=1 audio fixture; not whole production main/boot',
                     'gpio': gpio_contracts(image, dsp), 'idle': idle_comparison(image, dsp),
                     'audio': audio_cases(image, dsp, captures), 'unsupported_gates': unsupported_gates(image, dsp),
                     'instruction_time_or_physical_gpio_verified': False,
                     'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                     'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()

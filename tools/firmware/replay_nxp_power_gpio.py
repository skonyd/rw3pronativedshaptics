"""Follow the original GPIO latch and calibrated busy-wait branch offline."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_INVALID, UC_MEM_READ, UC_MEM_WRITE, UcError
from unicorn.arm_const import (UC_ARM_REG_IPSR, UC_ARM_REG_LR, UC_ARM_REG_PC,
                              UC_ARM_REG_PRIMASK, UC_ARM_REG_R0, UC_ARM_REG_SP)

from nxp_gpio_inputs import GPIO
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from replay_nxp_loop import check_isolated_trace
from replay_nxp_clock import check_motor_trace
from replay_nxp_rgb_spi import RgbSpiLab, audio_stream, tone
from simulate_audio_haptics import RETURN, STOCK_SHA256, verify


DELAY, DELAY_END, CORE_CLOCK = 0xD620, 0xD660, 0x200000A8


class DelayInstructionClock:
    """Supplied CPI budget during this busy wait and its IRQs, not silicon timing."""
    def __init__(self, lab, budget):
        verify(type(budget) is int and 1 <= budget <= 16, 'Delay instruction budget must be explicit and positive')
        self.lab, self.budget = lab, budget
        self.core_hz, = struct.unpack('<I', lab.cpu.mem_read(CORE_CLOCK, 4))
        verify(self.core_hz > 0, 'Busy-wait replay needs a nonzero explicit core clock')
        self.active, self.enabled = None, False
        self.records = []
        self.bus_remainder = self.wdt_remainder = 0
        lab.cpu.hook_add(UC_HOOK_CODE, self.step)

    def finish(self):
        record = self.active
        verify(record is not None and self.lab.cpu.reg_read(UC_ARM_REG_SP) == record['stack'],
               'Busy wait did not restore its caller stack')
        record['foreground_counts'] = {hex(k): v for k, v in sorted(record.pop('counts').items())}
        record['pc_sha256'] = record.pop('digest').hexdigest()
        record['core_clock_hz'] = self.core_hz
        record['return_r0'] = self.lab.cpu.reg_read(UC_ARM_REG_R0)
        record['cycles_per_observed_instruction'] = self.budget
        self.records.append(record); self.active = None

    def step(self, cpu, pc, size, data):
        lab, irq = self.lab, self.lab.irq
        handler = bool(cpu.reg_read(UC_ARM_REG_IPSR))
        if self.active and not handler and pc == self.active['return_pc'] and cpu.reg_read(UC_ARM_REG_SP) == self.active['stack']:
            self.finish()
        if not self.enabled: return
        # Earlier core hooks can stop before execution for IRQ/FP setup.
        # Do not charge that same instruction again when it restarts.
        if irq.context_restart or (irq.servicing and irq.preempt is not None) or (
                not irq.servicing and irq.foreground_irq): return
        if pc == DELAY and not handler:
            verify(self.active is None, 'Nested busy-wait calls are outside this fixture')
            verify(struct.unpack('<I', cpu.mem_read(CORE_CLOCK, 4))[0] == self.core_hz,
                   'Core-clock transitions are outside the busy-wait fixture')
            self.active = {'argument': cpu.reg_read(UC_ARM_REG_R0), 'return_pc': cpu.reg_read(UC_ARM_REG_LR) & ~1,
                           'stack': cpu.reg_read(UC_ARM_REG_SP), 'counts': Counter(), 'digest': hashlib.sha256(),
                           'foreground_instructions': 0, 'handler_instructions': 0, 'bus_cycles': 0, 'wdclk_cycles': 0}
        if self.active is None: return
        record = self.active
        if handler: record['handler_instructions'] += 1
        else:
            verify(DELAY <= pc <= DELAY_END, 'Unexpected foreground call inside the modeled busy wait')
            record['foreground_instructions'] += 1; record['counts'][pc] += 1
            record['digest'].update(struct.pack('<IH', pc, size))
        self.bus_remainder += self.budget * lab.timer.clock_hz
        cycles, self.bus_remainder = divmod(self.bus_remainder, self.core_hz)
        self.wdt_remainder += self.budget * lab.wdt.clock_hz
        wdclk, self.wdt_remainder = divmod(self.wdt_remainder, self.core_hz)
        lab.timer.advance(cycles)
        for motor in lab.motors: motor.advance(cycles)
        lab.wdt.advance(wdclk)
        record['bus_cycles'] += cycles; record['wdclk_cycles'] += wdclk


class PowerGpioLab(RgbSpiLab):
    def __init__(self, image, dsp, *, budget=3, pin_changes=(), **options):
        super().__init__(image, dsp, extra_pin=1, **options)
        # An explicit output-latch fixture, not measured direction/pin-mux.
        self.gpio.define_outputs({40: 0})  # P1_8, byte alias 0x4008C028.
        self.delay_clock = DelayInstructionClock(self, budget)
        self.pin_changes = dict(pin_changes)
        verify(all(type(tick) is int and tick >= 0 and type(value) is int and value in (0, 1)
                   for tick, value in self.pin_changes.items()), 'Invalid GPIO transition fixture')

    def check_mmio(self, cpu, access, address, size, value, data):
        if self.in_foreground and not cpu.reg_read(UC_ARM_REG_IPSR) and address - GPIO in self.gpio.outputs:
            verify(size == 1 and access in (UC_MEM_READ, UC_MEM_WRITE), 'Unmodeled GPIO output access')
            return
        super().check_mmio(cpu, access, address, size, value, data)

    def run(self, *, instruction_limit=300000):
        if self.iterations in self.pin_changes: self.gpio.pins[113] = self.pin_changes[self.iterations]
        self.delay_clock.enabled = True
        try: super().run(instruction_limit=instruction_limit)
        finally: self.delay_clock.enabled = False
        verify(self.delay_clock.active is None, 'Main loop returned inside a busy wait')

    def call_delay(self, argument):
        self.delay_clock.enabled = True
        try: self.replay.call(DELAY, RETURN, argument, instruction_limit=300000)
        finally: self.delay_clock.enabled = False
        if self.delay_clock.active:
            verify(self.cpu.reg_read(UC_ARM_REG_PC) == RETURN, 'Busy wait did not return')
            self.delay_clock.finish()


def validate_delay(record):
    argument = record['argument']
    counts = record['foreground_counts']
    # Independent loop-count oracle from the pinned routine's arithmetic.
    inner = ((record['core_clock_hz'] >> 6) * 0x02659117) >> 42
    verify(record['return_r0'] == 0 and counts.get('0xd648', 0) == argument and
           counts.get('0xd644', 0) == argument and counts.get('0xd658', 0) == argument * inner and
           counts.get('0xd65a', 0) == argument * inner and counts.get('0xd65c', 0) == argument * inner,
           'Original busy wait was skipped or its loop arithmetic changed')


def gpio_contracts():
    from nxp_gpio_inputs import GpioByteInputs
    cpu, _ = fixture()
    gpio = GpioByteInputs(cpu, {23: 1})
    gpio.define_outputs({40: 0})
    # LDRB test fixture; the shared helper intentionally only reads words.
    def read_byte(address):
        from unicorn.arm_const import UC_ARM_REG_R1
        cpu.mem_write(0x300, bytes.fromhex('01787047')); cpu.ctl_flush_tb()
        cpu.reg_write(UC_ARM_REG_R0, address); cpu.reg_write(UC_ARM_REG_LR, 0x501)
        cpu.emu_start(0x301, 0x500, count=8)
        return cpu.reg_read(UC_ARM_REG_R1)
    verify(read_byte(GPIO + 40) == 0 and read_byte(GPIO + 23) == 1, 'GPIO latch/input fixture reset mismatch')
    write_mmio(cpu, GPIO + 40, 1, size=1)
    verify(read_byte(GPIO + 40) == 1 and read_byte(GPIO + 23) == 1, 'GPIO output altered an independent input')
    write_mmio(cpu, GPIO + 40, 0, size=1)
    verify(read_byte(GPIO + 40) == 0 and gpio.writes == [(40, 1), (40, 0)], 'GPIO latch did not retain/record writes')
    rejected = []
    for name, action in (
            ('undefined_output', lambda: write_mmio(cpu, GPIO + 41, 1, size=1)),
            ('word_write', lambda: write_mmio(cpu, GPIO + 40, 1)),
            ('invalid_level', lambda: write_mmio(cpu, GPIO + 40, 2, size=1)),
            ('overlap_input', lambda: gpio.define_outputs({23: 0})),
            ('redefine_output', lambda: gpio.define_outputs({40: 1})),
            ('invalid_pin', lambda: gpio.define_outputs({192: 0})),
            ('boolean_level', lambda: gpio.define_outputs({41: True}))):
        try: action()
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported GPIO output operation accepted: ' + name)
    verify(gpio.outputs == {40: 0}, 'Rejected GPIO operation changed output state')
    return {'checks': ['explicit_low_latch', 'byte_write_readback', 'separate_input_preserved', 'write_trace'],
            'rejected': rejected, 'direction_pinmux_or_electrical_state_verified': False}


def delay_cases(image, dsp):
    records = []
    for budget, argument in ((1, 0), (1, 1), (1, 200), (3, 1), (3, 200), (3, 1000), (7, 200)):
        lab = PowerGpioLab(image, dsp, budget=budget)
        lab.drain(); lab.call_delay(argument)
        verify(len(lab.delay_clock.records) == 1 and not lab.irq.active, 'Busy-wait fixture did not finish exactly once')
        record = lab.delay_clock.records[0]; validate_delay(record)
        record['checked_irq_returns'] = lab.irq.checked_returns - 2
        records.append(record)
    same = [r['pc_sha256'] for r in records if r['argument'] == 200]
    verify(len(set(same)) == 1 and any(r['handler_instructions'] for r in records),
           'Interrupt scheduling changed the busy-loop trace, or no IRQ ran')
    masked = PowerGpioLab(image, dsp)
    masked.drain(); masked.cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
    before = masked.irq.checked_returns
    masked.call_delay(1000)
    record = masked.delay_clock.records[0]; validate_delay(record)
    verify(masked.irq.checked_returns == before and not record['handler_instructions'] and
           masked.timer.channels[0].coalesced > 0, 'Masked delay delivered IRQs or lost MRT coalescing')
    masked.cpu.reg_write(UC_ARM_REG_PRIMASK, 0); delivered = masked.irq.service()
    verify(delivered > 0 and not masked.irq.active, 'Masked delay did not deliver retained levels after release')
    verify(masked.clock() == 0, 'Coalesced MRT expirations incorrectly became a full elapsed-time counter')
    negative = []
    for budget in (0, -1, True, 1.5, 17):
        try: DelayInstructionClock(None, budget)
        except RuntimeError: negative.append('invalid_budget_' + str(budget))
        else: raise RuntimeError('Invalid instruction budget accepted')
    corrupt = PowerGpioLab(image, dsp)
    corrupt.cpu.mem_write(DELAY, bytes.fromhex('7047'))  # Deliberate BX LR corruption in emulated flash only.
    corrupt.cpu.ctl_flush_tb(); corrupt.call_delay(200)
    try: validate_delay(corrupt.delay_clock.records[0])
    except RuntimeError: negative.append('skipped_original_delay')
    else: raise RuntimeError('An original-delay bypass passed its independent loop checks')
    zero = PowerGpioLab(image, dsp); zero.cpu.mem_write(CORE_CLOCK, bytes(4))
    try: DelayInstructionClock(zero, 3)
    except RuntimeError: negative.append('zero_core_clock')
    else: raise RuntimeError('Uninitialized core clock accepted')
    changed = PowerGpioLab(image, dsp); changed.cpu.mem_write(CORE_CLOCK, struct.pack('<I', 48000000))
    try: changed.call_delay(200)
    except RuntimeError as error:
        verify('Core-clock transitions' in str(error), 'Unexpected clock-transition failure')
        negative.append('unmodeled_clock_transition')
    else: raise RuntimeError('Unmodeled core-clock transition accepted')
    exhausted = PowerGpioLab(image, dsp); exhausted.delay_clock.enabled = True
    try: exhausted.replay.call(DELAY, RETURN, 200, instruction_limit=30)
    except RuntimeError:
        verify(not exhausted.delay_clock.records and exhausted.cpu.reg_read(UC_ARM_REG_PC) != RETURN,
               'Incomplete delay manufactured a complete record')
        negative.append('exhausted_instruction_budget')
    else: raise RuntimeError('A tiny instruction budget unexpectedly completed the original delay')
    return {'profiles': records, 'same_argument_trace_independent_of_cpi_fixture': True,
            'masked': {'record': record, 'mrt_coalesced': masked.timer.channels[0].coalesced,
                       'irqs_after_release': delivered, 'counter_after_release': masked.clock()}, 'rejected': negative,
            'measured_instruction_cycles_or_physical_delay': False}


def foreground_cases(image, dsp):
    report = {}
    stereo = [tone(i, ((80, 190), (110, 230))) for i in range(96)]
    baseline = PowerGpioLab(image, dsp, candidate=False)
    _, _, idle, _ = audio_stream(image, dsp, stereo, candidate=False, lab=baseline)
    for name, other, changes, blocks, side in (
            ('high_stereo', 0, (), stereo, None),
            ('high_other_pin', 1, (), stereo, None),
            ('high_left', 0, (), [tone(i, ((80, 190), ())) for i in range(96)], 0),
            ('high_right', 0, (), [tone(i, ((), (110, 230))) for i in range(96)], 1),
            ('short_high_pulse', 0, ((10, 0),), stereo, None),
            ('later_rise', 0, ((0, 0), (50, 1)), stereo, None)):
        lab = PowerGpioLab(image, dsp, other_pin=other, pin_changes=changes)
        lab, result, pwm, combined = audio_stream(image, dsp, blocks, lab=lab)
        if side is None: check_motor_trace(pwm, idle)
        else: check_isolated_trace(pwm, idle, side)
        pulse = name == 'short_high_pulse'
        verify(lab.gpio.writes == ([] if pulse else [(40, 1)]) and
               len(lab.delay_clock.records) == (0 if pulse else 1), 'Original GPIO debounce/output/delay schedule changed')
        for record in lab.delay_clock.records:
            validate_delay(record)
            verify(record['argument'] == 200, 'Original GPIO branch called a different busy-wait count')
        verify(lab.iterations == lab.calls[0x2B538] == 159 and
               lab.values()[5] == len(lab.observed_decodes) == 9 and lab.values()[10] == 1,
               'GPIO/clock path bypassed normal Sensa dispatch, packet checks or safe stop')
        report[name] = {**result, 'iterations': lab.iterations, 'gpio_writes': lab.gpio.writes,
                        'delay_records': lab.delay_clock.records, 'checked_irq_returns': lab.irq.checked_returns,
                        'runtime_frame_checks': len(lab.observed_decodes), 'combined_spi_i2s_batches': combined,
                        'rgb_callbacks': lab.callbacks, 'extra_rgb_requests': lab.rgb_control_events,
                        'pin_changes': changes, 'P0_23': other, 'drained_to_stock_idle': True,
                        'mono_opposite_palm_and_triggers_isolated': side is not None}
    return report


def falling_boundaries(image, dsp):
    cases = []
    for other in (0, 1):
        lab = PowerGpioLab(image, dsp, other_pin=other, pin_changes=((70, 0),))
        invalid = []
        lab.cpu.hook_add(UC_HOOK_MEM_INVALID, lambda c, a, p, s, v, d:
                         invalid.append((c.reg_read(UC_ARM_REG_PC), p, s)) or False)
        try: audio_stream(image, dsp, [tone(i, ((80, 190), (110, 230))) for i in range(96)], lab=lab)
        except UcError:
            verify(invalid == [(0x10BB6, 0x40088800, 4)] and lab.iterations == 89 and
                   lab.gpio.writes == [(40, 1)] and len(lab.delay_clock.records) == 1,
                   'Unexpected falling-input peripheral boundary')
        else: raise RuntimeError('Unmodeled falling-input I2C path silently completed')
        cases.append({'P0_23': other, 'P3_17_falls_at_iteration': 70, 'completed_iterations': lab.iterations,
                      'pc': '0x10BB6', 'unmodeled_i2c_config': '0x40088800', 'gpio_writes': lab.gpio.writes,
                      'physical_peer_or_full_release_proven': False, 'accepted_as_full_run': False})
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    print(json.dumps({'gpio': gpio_contracts(), 'delay': delay_cases(image, dsp),
                      'foreground': foreground_cases(image, dsp), 'falling_boundaries': falling_boundaries(image, dsp),
                      'scope': 'Explicit GPIO byte latch and original busy-wait instruction budget; not physical timing',
                      'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                      'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()

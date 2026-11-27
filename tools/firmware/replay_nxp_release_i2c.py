"""Replay the original falling-input I2C path with an explicit synthetic sink."""
import argparse
from functools import partial
import hashlib
import json
from pathlib import Path
import struct
import sys

from unicorn import (UC_HOOK_CODE, UC_HOOK_MEM_INVALID, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE,
                     UC_MEM_READ, UC_MEM_WRITE, UcError)
from unicorn.arm_const import UC_ARM_REG_IPSR, UC_ARM_REG_PC, UC_ARM_REG_R0, UC_ARM_REG_R2, UC_ARM_REG_R4

from replay_nxp_power_gpio import PowerGpioLab, validate_delay
from replay_nxp_startup import I2CMasterModel
from replay_nxp_rgb_spi import audio_stream, tone
from replay_nxp_loop import PERSISTENT, check_isolated_trace
from replay_nxp_clock import check_motor_trace
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from unicorn.arm_const import UC_ARM_REG_PRIMASK
from simulate_audio_haptics import RETURN, STOCK_SHA256, TAP_BUFFER, read_sensa_pwm, verify


BASE = 0x40088800
WAIT_PCS = (0x21B36, 0x21B5C, 0x21BAE, 0x21BD4, 0x21C24, 0x21C4C,
            0x146F8, 0x14720, 0x14780, 0x147A8, 0x14808, 0x14830)
# Independent bytes from the original falling-input call arguments and the
# three literal halfwords in 0x14694. No vendor chip identity is inferred.
EXPECTED_FRAMES = (b'\x69\xC0', b'\x6B\xC0', b'\x02\0', b'\x04\0', b'\x06\0')


class ReleaseI2C(I2CMasterModel):
    """Exercised FLEXCOMM2 master subset; ACK sink has no chip identity."""
    READS, WRITES = {0, 4, 8, 0x18, 0x7F8}, {0, 4, 8, 12, 0x20, 0x28, 0x7F8}

    def __init__(self, cpu, *, ack=True, i2c_present=True):
        super().__init__(cpu, BASE, 16, {0x1A: {}} if ack else {})
        self.frames, self.current = [], None
        self.selection = 0
        self.i2c_present = i2c_present
        cpu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, self.guard,
                     begin=BASE - 0x800, end=BASE + 0x7FF)
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=BASE + 0x7F8, end=BASE + 0x7FB)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=BASE + 0x7F8, end=BASE + 0x7FB)

    @classmethod
    def validate(cls, access, address, size):
        offsets = cls.READS if access == UC_MEM_READ else cls.WRITES if access == UC_MEM_WRITE else set()
        verify(size == 4 and address - BASE in offsets, 'Unmodeled FLEXCOMM2 I2C register/access')

    def guard(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)

    def read(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        values = {0: self.configuration, 8: self.enabled, 0x18: self.enabled & self.status,
                  0x7F8: (0x40 if self.i2c_present else 0) | self.selection}
        if address - BASE in values:
            cpu.mem_write(address, struct.pack('<I', values[address - BASE]))
        else: super().read(cpu, access, address, size, value, data)

    def write(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        offset = address - BASE
        if offset == 0x7F8:
            verify(value == 3 and self.i2c_present, 'Only present FLEXCOMM2 I2C selection is modeled')
            self.selection = value
            return
        if offset == 0:
            verify(value in (0, 1), 'Only FLEXCOMM2 master enable is modeled')
            verify(not value or self.selection == 3, 'FLEXCOMM2 I2C was not selected by original power initialization')
        elif offset in (8, 12): verify(value & ~0x03000051 == 0, 'Unsupported FLEXCOMM2 interrupt selection')
        elif offset == 4: verify(value & ~0x030A0050 == 0, 'Unsupported FLEXCOMM2 status clear')
        elif offset == 0x28: verify(0 <= value <= 255, 'Unsupported FLEXCOMM2 wide data')
        if offset == 0x20:
            verify(self.configuration == 1 and self.selection == 3, 'FLEXCOMM2 master was not enabled/selected')
            if value == 2:
                verify(self.current is None, 'Repeated START is outside the write-only sink')
                verify(not self.data & 1, 'Read response is not supplied by the write-only sink')
                self.current = []
            elif value == 1 and not self.receiving:
                verify(self.current is not None and len(self.current) < 2, 'Unstarted/overlong two-byte I2C write')
                self.current.append(self.data)
                self.transmitted.append((self.address, self.data))
                self.tx_count += 1; self.status = 5
                return  # Raw byte sink; do not invent an 8-bit register map.
            elif value == 4:
                if self.current is not None:
                    self.frames.append((self.address, bytes(self.current)))
                    self.current = None
        super().write(cpu, access, address, size, value, data)


class ReleaseLab(PowerGpioLab):
    def __init__(self, image, dsp, *, ack=True, register_callback=True, suppress_release_irq=False,
                 release_wait_clock=True, defer_release_irqs=0, audio_enabled=True, i2c_present=True, **options):
        super().__init__(image, dsp, **options)
        self.cpu.mem_map(BASE - 0x800, 4096)
        self.release_bus = ReleaseI2C(self.cpu, ack=ack, i2c_present=i2c_present)
        verify(type(defer_release_irqs) is int and defer_release_irqs >= 0, 'Invalid release IRQ deferral')
        self.irq.level_sources[16] = lambda: (not suppress_release_irq and
                                            self.release_wait_events >= defer_release_irqs and self.release_bus.pending())
        self.release_wait_clock, self.release_wait_events = release_wait_clock, 0
        self.audio_enabled = audio_enabled
        self.release_callbacks, self.release_driver_status = [], []
        self.cpu.hook_add(UC_HOOK_CODE, self.release_wait)
        self.cpu.hook_add(UC_HOOK_CODE, self.release_callback, begin=0x117E0, end=0x117E0)
        self.cpu.hook_add(UC_HOOK_CODE, self.driver_callback_release, begin=0x14050, end=0x14050)
        self.power_writes, self.initializing_release = [], True
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.observe_power, begin=0x40000000, end=0x4FFFFFFF)
        self.replay.call(0x10AC8, RETURN, 0x117E1 if register_callback else 0)
        self.replay.call(0x10AE0, RETURN, 2)
        self.initializing_release = False
        verify(self.release_bus.selection == 3, 'Original FLEXCOMM2 power initialization did not select I2C')
        verify(self.power_writes == [(0x40000224, 4, 0x2000), (0x40000124, 4, 0x2000),
                                     (0x40000144, 4, 0x2000), (0x40088FF8, 4, 3)],
               'Original FLEXCOMM2 clock/reset/selection write sequence changed')

    def observe_power(self, cpu, access, address, size, value, data):
        if self.initializing_release: self.power_writes.append((address, size, value))

    def run(self, *, instruction_limit=1000000):
        if self.iterations == 0 and not self.audio_enabled:
            self.cpu.mem_write(TAP_BUFFER + 192, bytes(4))
        super().run(instruction_limit=instruction_limit)

    def release_callback(self, cpu, pc, size, data):
        self.release_callbacks.append(cpu.reg_read(UC_ARM_REG_R0))

    def driver_callback_release(self, cpu, pc, size, data):
        # This stock adapter is shared with FLEXCOMM3. Count only the
        # actual FLEXCOMM2 peripheral, including its null-callback control.
        if cpu.reg_read(UC_ARM_REG_R0) == BASE - 0x800:
            self.release_driver_status.append(cpu.reg_read(UC_ARM_REG_R2))

    def check_mmio(self, cpu, access, address, size, value, data):
        if self.in_foreground and not cpu.reg_read(UC_ARM_REG_IPSR) and BASE <= address < BASE + 0x100:
            ReleaseI2C.validate(access, address, size)
            return
        super().check_mmio(cpu, access, address, size, value, data)

    def release_wait(self, cpu, pc, size, data):
        if not self.in_foreground or cpu.reg_read(UC_ARM_REG_IPSR) or not self.release_wait_clock:
            return
        if pc not in WAIT_PCS: return
        if not cpu.mem_read(0x20000AEA, 1)[0]: return
        self.release_wait_events += 1
        self.timer.advance(12000)
        for motor in self.motors: motor.advance(12000)
        self.wdt.advance(100)

    def call_release(self, routine, *arguments, instruction_limit=300000):
        self.in_foreground = True
        try: self.replay.call(routine, RETURN, *arguments, instruction_limit=instruction_limit)
        finally: self.in_foreground = False


def scene(image, dsp, *, candidate=True, blocks=None, **options):
    lab = ReleaseLab(image, dsp, candidate=candidate, **options)
    blocks = blocks if blocks is not None else [tone(i, ((80, 190), (110, 230))) for i in range(96)]
    _, result, pwm, combined = audio_stream(image, dsp, blocks, candidate=candidate, lab=lab)
    verify(not lab.release_bus.enabled and lab.release_bus.current is None and
           not lab.irq.active and not lab.irq.frames, 'Release I2C/IRQ state did not drain')
    verify(lab.calls[0x2B538] == lab.iterations, 'Host bypassed original Sensa foreground dispatch')
    verify(lab.iterations == len(blocks) + 63, 'Original release/audio schedule was truncated')
    for record in lab.delay_clock.records: validate_delay(record)
    return lab, {**result, 'iterations': lab.iterations, 'gpio_writes': lab.gpio.writes,
                 'frames': [{'address': a, 'data_hex': data.hex()} for a, data in lab.release_bus.frames],
                 'callbacks': lab.release_callbacks, 'driver_status': lab.release_driver_status,
                 'unknown_addresses': lab.release_bus.unknown_addresses,
                 'release_wait_events': lab.release_wait_events,
                 'checked_irq_returns': lab.irq.checked_returns, 'combined_spi_i2s_batches': combined,
                 'release_irq_entries': sum(e['event'] == 'entry' and e.get('irq') == 16 for e in lab.irq.trace),
                 'original_power_mmio_writes': lab.power_writes,
                 'delay_arguments': [r['argument'] for r in lab.delay_clock.records],
                 'independent_sensa_frame_checks': len(lab.observed_decodes)}, pwm


def active_cases(image, dsp, *, idle=None):
    reports = {}
    if idle is None: _, _, idle = scene(image, dsp, candidate=False, pin_changes=((70, 0),))
    for name, options, sides, length in (
            ('stereo_release', {}, None, 96), ('other_pin_release', {'other_pin': 1}, None, 96),
            ('left_release', {}, 0, 96), ('right_release', {}, 1, 96),
            ('short_low_pulse', {'pin_changes': ((70, 0), (80, 1))}, None, 96),
            ('repeated_release', {'pin_changes': ((25, 0), (70, 1), (115, 0))}, None, 96),
            ('delayed_irq', {'defer_release_irqs': 8}, None, 96)):
        print('Active release:', name, file=sys.stderr, flush=True)
        options = {'pin_changes': ((70, 0),), **options}
        frequencies = ((80, 190), ()) if sides == 0 else ((), (110, 230)) if sides == 1 else ((80, 190), (110, 230))
        blocks = [tone(i, frequencies) for i in range(length)]
        lab, result, pwm = scene(image, dsp, blocks=blocks, **options)
        reference = idle
        if sides is None: check_motor_trace(pwm, reference)
        else: check_isolated_trace(pwm, reference, sides)
        count = 0 if name == 'short_low_pulse' else 2 if name == 'repeated_release' else 1
        expected_gpio = [(40, 1), (40, 0)] * count if count else [(40, 1)]
        verify(lab.gpio.writes == expected_gpio, 'Original GPIO filter/release sequence changed')
        verify(lab.release_bus.frames == [(0x1A, data) for data in EXPECTED_FRAMES] * count,
               'Original release write sequence differs from independent byte oracle')
        verify(lab.release_callbacks == [1] * (5 * count) and lab.release_driver_status == [0] * (5 * count),
               'Original successful release callback/status mapping changed')
        verify(not lab.release_wait_events if name != 'delayed_irq' else lab.release_wait_events >= 8,
               'Unexpected logical I2C wait schedule')
        verify(len(lab.delay_clock.records) == (1 if count == 0 else 2 * count), 'Original release delay bypassed')
        verify(lab.values()[10] == 1 and len(lab.observed_decodes) == lab.values()[5] and lab.values()[1] == 0,
               'Independent packet checks or audio stop bypassed')
        reports[name] = {**result, 'drained_to_stock_idle': True,
                         'opposite_palm_and_triggers_idle_checked': sides is not None,
                         'pin_changes': options['pin_changes'], 'source_blocks': length,
                         'P0_23': options.get('other_pin', 0),
                         'expected_release_cycles': count, 'physical_peer_verified': False}
    return reports


def register_contracts():
    def fresh(*, select=True):
        cpu, _ = fixture(); cpu.mem_map(BASE - 0x800, 4096)
        bus = ReleaseI2C(cpu)
        if select:
            write_mmio(cpu, BASE + 0x7F8, 3)
            write_mmio(cpu, BASE, 1)
        return cpu, bus
    cpu, bus = fresh(select=False)
    verify(read_mmio(cpu, BASE + 0x7F8) == 0x40, 'Explicit I2C capability fixture changed')
    write_mmio(cpu, BASE + 0x7F8, 3); write_mmio(cpu, BASE, 1)
    verify(read_mmio(cpu, BASE + 0x7F8) == 0x43 and read_mmio(cpu, BASE) == 1, 'I2C selection/master readback failed')
    write_mmio(cpu, BASE + 8, 1)
    verify(bus.pending() and read_mmio(cpu, BASE + 0x18) == 1, 'I2C pending interrupt aliases differ')
    write_mmio(cpu, BASE + 12, 1)
    verify(not bus.pending() and read_mmio(cpu, BASE + 8) == 0, 'I2C clear alias did not disable interrupt')
    write_mmio(cpu, BASE + 0x28, 0x34); write_mmio(cpu, BASE + 0x20, 2)
    for byte in EXPECTED_FRAMES[0]:
        write_mmio(cpu, BASE + 0x28, byte); write_mmio(cpu, BASE + 0x20, 1)
    write_mmio(cpu, BASE + 0x20, 4)
    verify(bus.frames == [(0x1A, EXPECTED_FRAMES[0])] and bus.registers == {} and
           bus.transmitted == [(0x1A, byte) for byte in EXPECTED_FRAMES[0]], 'Raw sink invented a chip register model')
    rejected = []
    for name, action in (
            ('reserved_register', lambda c, b: read_mmio(c, BASE + 0x1C)),
            ('unspecified_data_read', lambda c, b: read_mmio(c, BASE + 0x28)),
            ('byte_access', lambda c, b: write_mmio(c, BASE + 0x28, 1, size=1)),
            ('write_readonly_status', lambda c, b: write_mmio(c, BASE + 0x18, 1)),
            ('slave_mode', lambda c, b: write_mmio(c, BASE, 2)),
            ('unmodeled_interrupt', lambda c, b: write_mmio(c, BASE + 8, 2)),
            ('wide_data', lambda c, b: write_mmio(c, BASE + 0x28, 256)),
            ('wrong_peripheral_selection', lambda c, b: write_mmio(c, BASE + 0x7F8, 2)),
            ('selection_lock', lambda c, b: write_mmio(c, BASE + 0x7F8, 11)),
            ('data_without_start', lambda c, b: write_mmio(c, BASE + 0x20, 1)),
            ('unsupported_master_command', lambda c, b: write_mmio(c, BASE + 0x20, 8))):
        c, b = fresh()
        try: action(c, b)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported release I2C operation accepted: ' + name)
    for name in ('unselected_master', 'read_start', 'repeated_start', 'overlong_write'):
        c, b = fresh(select=name != 'unselected_master')
        try:
            if name == 'unselected_master': write_mmio(c, BASE, 1)
            else:
                write_mmio(c, BASE + 0x28, 0x35 if name == 'read_start' else 0x34)
                write_mmio(c, BASE + 0x20, 2)
                if name == 'repeated_start': write_mmio(c, BASE + 0x20, 2)
                if name == 'overlong_write':
                    for byte in (1, 2, 3):
                        write_mmio(c, BASE + 0x28, byte); write_mmio(c, BASE + 0x20, 1)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported sink sequence accepted: ' + name)
    return {'checks': ['explicit_capability', 'original_selection_value', 'master_enable',
                       'pending_set_and_clear_aliases', 'raw_two_byte_sink_without_invented_chip_registers'],
            'rejected': rejected, 'physical_peer_or_electrical_timing_verified': False}


def blocked_controls(image, dsp):
    rejected = []
    try: ReleaseLab(image, dsp, i2c_present=False)
    except RuntimeError as error:
        verify('did not select I2C' in str(error), 'Unexpected absent-capability failure')
        rejected.append('absent_i2c_capability_cannot_be_success')
    else: raise RuntimeError('Missing I2C capability silently passed original power initialization')
    for name, options in (('frozen_wait_clock', {'ack': False, 'release_wait_clock': False}),
                          ('masked_irqs', {})):
        lab = ReleaseLab(image, dsp, **options)
        if name == 'masked_irqs': lab.cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
        try: lab.call_release(0x21AE8, 0x34, 0x1C0, instruction_limit=30000)
        except RuntimeError:
            verify(lab.cpu.reg_read(UC_ARM_REG_PC) != RETURN and not lab.gpio.writes and
                   lab.cpu.mem_read(0x20000AEA, 1) == b'\x01' and lab.cpu.mem_read(0x20000AE8, 1) == b'\x0A',
                   'Blocked transfer manufactured wrapper completion or elapsed timeout')
            if name == 'masked_irqs':
                verify(not lab.release_callbacks and lab.timer.channels[0].coalesced > 0,
                       'Masked IRQs delivered callbacks or lost original MRT coalescing')
            rejected.append(name + '_cannot_complete')
        else: raise RuntimeError('Blocked I2C state completed without a time/IRQ source')
    return {'rejected': rejected, 'instruction_budget_is_not_an_automatic_transfer_abort': True}


def full_nack_boundary(image, dsp):
    print('Full audio NACK boundary', file=sys.stderr, flush=True)
    lab = ReleaseLab(image, dsp, ack=False, pin_changes=((70, 0),))
    lab.run = partial(lab.run, instruction_limit=5000000)
    invalid = []
    lab.cpu.hook_add(UC_HOOK_MEM_INVALID, lambda c, a, p, s, v, d:
                     invalid.append((c.reg_read(UC_ARM_REG_PC), p, s)) or False)
    try: audio_stream(image, dsp, [tone(i, ((80, 190), (110, 230))) for i in range(96)], lab=lab)
    except UcError:
        verify(invalid == [(0x9FB2, 0x400A000C, 4)] and lab.iterations == 89 and
               lab.gpio.writes == [(40, 1), (40, 0)] and lab.release_callbacks == [16] * 15 and
               lab.release_bus.frames == [(0x1A, b'')] * 15 and len(lab.delay_clock.records) == 2,
               'Unexpected original full-audio NACK/recovery boundary')
    else: raise RuntimeError('Unmodeled post-NACK peripheral silently completed')
    verify(lab.cpu.reg_read(UC_ARM_REG_R4) == 8, 'Unexpected ADC channel in post-NACK path')
    return {'pc': '0x9FB2', 'unmodeled_address': '0x400A000C', 'adc_channel': 8, 'completed_iterations': lab.iterations,
            'gpio_writes': lab.gpio.writes, 'failed_address_attempts': len(lab.release_callbacks),
            'callback_status': 16, 'logical_wait_events': lab.release_wait_events,
            'checked_irq_returns': lab.irq.checked_returns, 'instruction_budget': 5000000,
            'accepted_as_full_run': False, 'physical_error_recovery_verified': False}


def inactive_pair(image, dsp, *, return_reference=False):
    variants, reports = [], []
    for candidate in (False, True):
        print('Inactive release pair:', candidate, file=sys.stderr, flush=True)
        lab, result, pwm = scene(image, dsp, candidate=candidate, audio_enabled=False, pin_changes=((70, 0),))
        verify(lab.gpio.writes == [(40, 1), (40, 0)] and
               lab.release_bus.frames == [(0x1A, data) for data in EXPECTED_FRAMES], 'Inactive release failed')
        variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                         tuple(lab.cpu.reg_read(r) for r in PERSISTENT), read_sensa_pwm(lab.cpu, lab.registers),
                         lab.release_bus.commands, lab.release_callbacks, lab.gpio.writes, result, pwm))
        reports.append({'iterations': lab.iterations, 'checked_irq_returns': lab.irq.checked_returns})
    verify(variants[0] == variants[1] and reports[0] == reports[1], 'Inactive candidate changed release state')
    result = {**reports[0], 'compared_ram_bytes': 0x13D28, 'stock_and_inactive_candidate_match': True}
    return (result, variants[0][-1]) if return_reference else result


def fault_cases(image, dsp):
    reports = {}
    for name, options in (('address_nack', {'ack': False}),
                          ('missing_irq', {'suppress_release_irq': True}),
                          ('missing_callback', {'register_callback': False}),
                          ('late_irq_recovery', {'defer_release_irqs': 88})):
        lab = ReleaseLab(image, dsp, **options)
        lab.call_release(0x21AE8, 0x34, 0x1C0)
        fault = name != 'late_irq_recovery'
        verify(lab.release_wait_events == (240 if fault else 88), 'Original retry/timeout schedule changed')
        if name == 'address_nack':
            verify(lab.release_bus.frames == [(0x1A, b'')] * 3 and lab.release_callbacks == [16] * 3 and
                   lab.release_driver_status == [0xA33] * 3, 'Address NACK became a successful write')
        elif name == 'missing_irq':
            verify(not lab.release_bus.frames and not lab.release_callbacks and not lab.release_driver_status,
                   'Suppressed IRQ manufactured a completion')
        elif name == 'missing_callback':
            verify(lab.release_bus.frames == [(0x1A, EXPECTED_FRAMES[0])] * 3 and
                   not lab.release_callbacks and lab.release_driver_status == [0] * 3,
                   'Missing wrapper callback incorrectly acknowledged success')
        else:
            verify(lab.release_bus.frames[-1] == (0x1A, EXPECTED_FRAMES[0]) and lab.release_callbacks == [1],
                   'Original timeout/retry did not recover after delayed IRQ delivery')
        verify(not lab.release_bus.enabled and lab.release_bus.current is None and not lab.irq.active and
               not lab.gpio.writes, 'Direct fault probe changed GPIO or retained a transfer')
        reports[name] = {'frames': [{'address': a, 'data_hex': b.hex()} for a, b in lab.release_bus.frames],
                         'callbacks': lab.release_callbacks, 'driver_status': lab.release_driver_status,
                         'wait_events': lab.release_wait_events, 'checked_irq_returns': lab.irq.checked_returns,
                         'flags_hex': lab.cpu.mem_read(0x20000AE8, 16).hex(),
                         'wrapper_success_proven': not fault,
                         'wire_completion_alone_is_wrapper_success': False}
    return reports


def long_repeat_boundary(image, dsp):
    print('Long all-ACK repeated release boundary', file=sys.stderr, flush=True)
    changes = ((70, 0), (120, 1), (170, 0))
    lab = ReleaseLab(image, dsp, pin_changes=changes)
    invalid = []
    lab.cpu.hook_add(UC_HOOK_MEM_INVALID, lambda c, a, p, s, v, d:
                     invalid.append((c.reg_read(UC_ARM_REG_PC), p, s)) or False)
    try: audio_stream(image, dsp, [tone(i, ((80, 190), (110, 230))) for i in range(192)], lab=lab)
    except UcError:
        verify(invalid == [(0x9FB2, 0x400A000C, 4)] and lab.iterations == 199 and
               lab.gpio.writes == [(40, 1), (40, 0)] * 2 and
               lab.release_bus.frames == [(0x1A, b) for b in EXPECTED_FRAMES] * 2 and
               lab.release_callbacks == [1] * 10 and not lab.release_wait_events and
               len(lab.delay_clock.records) == 4 and lab.cpu.reg_read(UC_ARM_REG_R4) == 8,
               'Unexpected long repeated-cycle ADC boundary')
    else: raise RuntimeError('Unmodeled long-run ADC sequence B silently completed')
    return {'pc': '0x9FB2', 'unmodeled_address': '0x400A000C', 'adc_channel': 8,
            'completed_iterations': lab.iterations, 'requested_iterations': 255,
            'source_blocks': 192, 'pin_changes': changes, 'gpio_writes': lab.gpio.writes,
            'successful_write_callbacks': len(lab.release_callbacks), 'logical_wait_events': 0,
            'nack_is_required_for_adc_boundary': False, 'accepted_as_full_run': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    registers, blocked, faults = register_contracts(), blocked_controls(image, dsp), fault_cases(image, dsp)
    pair, idle = inactive_pair(image, dsp, return_reference=True)
    print(json.dumps({'registers': registers, 'blocked': blocked,
                      'faults': faults, 'inactive_pair': pair, 'active': active_cases(image, dsp, idle=idle),
                      'stock_idle_reference_reused_from_inactive_pair': True,
                      'full_nack_boundary': full_nack_boundary(image, dsp),
                      'long_repeat_boundary': long_repeat_boundary(image, dsp),
                      'scope': 'Original falling-input FLEXCOMM2 driver/IRQ16; explicit synthetic write-only ACK sink',
                      'physical_i2c_or_instruction_timing_verified': False, 'whole_device_equivalence_proven': False,
                      'flashable_candidate_ready': False, 'input_sha256': {'stock': hashlib.sha256(image).hexdigest(),
                                                                          'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()

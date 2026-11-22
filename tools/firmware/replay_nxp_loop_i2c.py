"""Connect the stock foreground I2C driver to explicit peers and real IRQ17."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from unicorn import (UC_HOOK_CODE, UC_HOOK_MEM_INVALID, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE,
                     UC_MEM_READ, UC_MEM_WRITE, UcError)
from unicorn.arm_const import UC_ARM_REG_IPSR, UC_ARM_REG_PC

from replay_nxp_loop import LoopLab, PERSISTENT
from replay_nxp_exceptions import read_mmio, write_mmio
from replay_nxp_startup import I2CMasterModel, EXPECTED_CONFIG, WAIT_FLAGS
from simulate_audio_haptics import RETURN, STOCK_SHA256, read_sensa_pwm, verify


BASE = 0x40089800


class StrictForegroundI2C(I2CMasterModel):
    """Only the exercised master subset; no implicit values for other registers."""
    READS, WRITES = {0, 4, 8, 0x18, 0x28}, {0, 4, 8, 12, 0x20, 0x28}

    def __init__(self, cpu, base, irq, devices):
        verify(base == BASE and irq == 17, 'This strict subset covers FLEXCOMM3 only')
        super().__init__(cpu, base, irq, devices)
        # Guard the mapped page even in IRQ context and outside the old
        # master's short register hook. Unused aliases must not read as zero.
        cpu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, self.guard,
                     begin=base - 0x800, end=base + 0x7FF)

    def guard(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)

    @classmethod
    def validate(cls, access, address, size):
        offsets = cls.READS if access == UC_MEM_READ else cls.WRITES if access == UC_MEM_WRITE else set()
        verify(size == 4 and address - BASE in offsets, 'Unmodeled foreground I2C register/access')

    def read(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        offset = address - self.base
        values = {0: self.configuration, 8: self.enabled, 0x18: self.enabled & self.status}
        if offset in values:
            cpu.mem_write(address, struct.pack('<I', values[offset]))
        else:
            super().read(cpu, access, address, size, value, data)

    def write(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        offset = address - self.base
        if offset == 0:
            verify(value in (0, 1), 'Only I2C master enable is modeled')
        elif offset in (8, 12):
            verify(value & ~0x03000051 == 0, 'Unmodeled I2C interrupt selection')
        elif offset == 4:
            verify(value & ~0x030A0050 == 0, 'Unmodeled I2C status clear')
        elif offset == 0x28:
            verify(0 <= value <= 255, 'Unmodeled wide I2C data')
        super().write(cpu, access, address, size, value, data)


class I2CLoopLab(LoopLab):
    def __init__(self, image, dsp, *, candidate=True, connection=1, gate=0,
                 identity=0x53, registered=True, suppress_irq=False, wait_clock=True):
        super().__init__(image, dsp, candidate=candidate, connection=connection, gate=gate)
        self.cpu.mem_map(0x40089000, 4096)
        peers = {} if identity is None else {0x6B: {0x0B: identity}}
        self.bus = StrictForegroundI2C(self.cpu, BASE, 17, peers)
        self.irq.level_sources[17] = lambda: not suppress_irq and self.bus.pending()
        self.wait_events, self.wait_clock = 0, wait_clock
        self.cpu.hook_add(UC_HOOK_CODE, self.clock_wait)
        if registered:
            # Execute the stock callback-registration routine, as startup does.
            # No completion flag or opaque startup RAM is copied by the host.
            self.replay.call(0x10D70, RETURN, 0x117F5)
        self.i2c_accesses = Counter()

    def check_mmio(self, cpu, access, address, size, value, data):
        if self.in_foreground and not cpu.reg_read(UC_ARM_REG_IPSR) and BASE <= address < BASE + 0x100:
            StrictForegroundI2C.validate(access, address, size)
            self.i2c_accesses[(access, address, size)] += 1
            return
        super().check_mmio(cpu, access, address, size, value, data)

    def clock_wait(self, cpu, pc, size, data):
        if not self.in_foreground or cpu.reg_read(UC_ARM_REG_IPSR) or pc not in WAIT_FLAGS:
            return
        if not self.wait_clock or not cpu.mem_read(WAIT_FLAGS[pc], 1)[0]:
            return
        # Explicit logical event schedule: one 125us quantum per busy-wait
        # visit. This is NOT measured instruction or electrical bus timing.
        self.wait_events += 1
        self.timer.advance(12000)
        for motor in self.motors:
            motor.advance(12000)
        self.wdt.advance(100)


def compare_case(image, dsp, **options):
    variants, reports = [], []
    iterations = 1 if options.get('identity', 0x53) is None or options.get('suppress_irq', False) else 8
    for candidate in (False, True):
        lab = I2CLoopLab(image, dsp, candidate=candidate, **options)
        for _ in range(iterations):
            lab.advance_us(1000); lab.wdt.advance(800); lab.run(instruction_limit=1000000)
        configured = lab.cpu.mem_read(0x20000F2D, 1)[0]
        expected = options.get('identity', 0x53)
        good = expected is not None and expected & 0xF8 == 0x50 and not options.get('suppress_irq', False)
        verify(configured == int(good), 'Incorrect original foreground configured flag')
        verify(lab.bus.registers == ({(0x6B, k): v for k, v in EXPECTED_CONFIG.items()} if good else {}),
               'Original foreground I2C register configuration mismatch')
        verify(lab.iterations == iterations and not lab.irq.active and not lab.irq.frames,
               'Foreground/IRQ boundary not balanced')
        irq17 = sum(event['event'] == 'entry' and event.get('irq') == 17 for event in lab.irq.trace)
        fault = expected is None or options.get('suppress_irq', False)
        verify(lab.calls[0x2B538] == iterations, 'Original loop did not reach its Sensa dispatcher')
        if fault:
            verify(lab.wait_events == 240 and not lab.bus.registers and not lab.bus.received,
                   'Three original ten-tick I2C timeouts did not complete')
            verify(irq17 == (0 if options.get('suppress_irq', False) else 6), 'Unexpected fault IRQ delivery')
        else:
            verify(not lab.wait_events and len(lab.bus.received) == (1 if good else 24) and
                   irq17 == (51 if good else 144), 'Original I2C success/retry cadence changed')
        reports.append({'iterations': lab.iterations, 'configured': bool(configured),
                        'i2c_irq_entries': irq17, 'checked_irq_returns': lab.irq.checked_returns,
                        'received': lab.bus.received, 'register_writes': sorted(lab.bus.registers.items()),
                        'unknown_addresses': lab.bus.unknown_addresses,
                        'busy_wait_clock_events': lab.wait_events,
                        'sensa_dispatches': lab.calls[0x2B538]})
        variants.append((bytes(lab.cpu.mem_read(0x20000000, 0x13D28)),
                         tuple(lab.cpu.reg_read(r) for r in PERSISTENT),
                         read_sensa_pwm(lab.cpu, lab.registers), lab.bus.commands,
                         lab.bus.transmitted, lab.bus.received, lab.wdt.feeds))
    verify(variants[0] == variants[1] and reports[0] == reports[1], 'Inactive candidate changed foreground I2C behavior')
    return {**reports[0], 'stock_candidate_ram_registers_pwm_transactions_match': True,
            'stock_ram_compared_bytes': 0x13D28}


def boundary_controls(image, dsp):
    rejected = []
    lab = I2CLoopLab(image, dsp)
    for name, action in (
            ('reserved_read', lambda: read_mmio(lab.cpu, BASE + 0x1C)),
            ('slave_alias', lambda: read_mmio(lab.cpu, BASE + 0x40)),
            ('write_readonly_status', lambda: write_mmio(lab.cpu, BASE + 0x18, 1)),
            ('byte_access', lambda: write_mmio(lab.cpu, BASE + 0x28, 1, size=1)),
            ('slave_mode', lambda: write_mmio(lab.cpu, BASE, 2)),
            ('unknown_irq', lambda: write_mmio(lab.cpu, BASE + 8, 2)),
            ('wide_data', lambda: write_mmio(lab.cpu, BASE + 0x28, 256))):
        try: action()
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unsupported I2C operation accepted: ' + name)
    missing = I2CLoopLab(image, dsp)
    missing.bus.devices[0x6B].clear()
    try: missing.run()
    except RuntimeError as error:
        verify(str(error).startswith('Unspecified synthetic I2C response'), 'Unexpected incomplete-peer failure')
        rejected.append('missing_identity_register')
    else: raise RuntimeError('Missing peer identity silently became zero')
    # Without startup registration, real IRQs complete but cannot clear the
    # original wrapper's flag. A frozen wait clock must not manufacture success.
    missing = I2CLoopLab(image, dsp, registered=False, wait_clock=False)
    try: missing.run()
    except RuntimeError as error:
        verify(str(error) == 'Foreground FP execution did not reach its endpoint' and
               missing.iterations == 0 and missing.bus.transmitted == [(0x6B, 0x0B)] and
               missing.cpu.mem_read(0x200093F1, 1) == b'\x01', 'Unexpected missing-callback boundary')
        rejected.append('unregistered_callback_cannot_complete')
    else: raise RuntimeError('Missing callback incorrectly completed the foreground loop')
    boundaries = []
    for gate in (0, 1):
        other = I2CLoopLab(image, dsp, connection=0, gate=gate)
        bad = []
        other.cpu.hook_add(UC_HOOK_MEM_INVALID, lambda c, a, p, s, v, d:
                           bad.append((c.reg_read(UC_ARM_REG_PC), p, s)) or False)
        try: other.run()
        except UcError:
            verify(bad == [(0x20672, 0x4009AE00, 4)], 'Unexpected next foreground peripheral boundary')
        else: raise RuntimeError('Unmodeled next peripheral silently completed')
        verify(other.bus.transmitted and not other.bus.enabled and not other.irq.active,
               'Next-boundary fixture failed before I2C completion')
        boundaries.append({'connection_flag': 0, 'runtime_gate': gate, 'pc': '0x20672',
                           'unmodeled_address': '0x4009AE00', 'accepted_as_full_loop': False})
    return {'rejected': rejected, 'remaining_boundaries': boundaries}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock image')
    cases = {name: compare_case(image, dsp, **options) for name, options in (
        ('configured', {}), ('identity_low', {'identity': 0x50}), ('identity_high', {'identity': 0x57}),
        ('identity_below', {'identity': 0x4F}), ('identity_above', {'identity': 0x58}),
        ('address_nack', {'identity': None}), ('suppressed_irq', {'suppress_irq': True}))}
    print(json.dumps({'scope': 'Original foreground I2C in supplied connection=1/gate=0 state; explicit synthetic peer',
                      'cases': cases, 'controls': boundary_controls(image, dsp),
                      'physical_i2c_or_instruction_timing_verified': False,
                      'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
                      'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()

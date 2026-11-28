"""Supplied instruction-clock budget for the original 0x1FF70 delay helper."""
from collections import Counter
import hashlib
import struct

from unicorn import UC_HOOK_CODE
from unicorn.arm_const import UC_ARM_REG_IPSR, UC_ARM_REG_LR, UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_SP
from replay_nxp_power_gpio import CORE_CLOCK
from simulate_audio_haptics import verify

ENTRY, LOOP = 0x1FF70, 0x1FF98
RANGES = ((0x1FF70, 0x1FFA0), (0x8644, 0x86A6), (0x8B82, 0x8BC0))


class CalibrationInstructionClock:
    """Not silicon cycle timing. Counts the helper, its callees and actual IRQs.

    The existing D620 clock owns that separate helper; overlapping charge
    is rejected. Events supply peripheral states; handlers still execute.
    """
    def __init__(self, lab, budget):
        verify(type(budget) is int and 1 <= budget <= 16, 'Calibration CPI budget must be explicit and positive')
        self.lab, self.budget = lab, budget
        self.core_hz, = struct.unpack('<I', lab.cpu.mem_read(CORE_CLOCK, 4))
        verify(self.core_hz > 0, 'Calibration clock requires an explicit nonzero core clock')
        self.enabled, self.active = False, None
        verify(self.core_hz == lab.delay_clock.core_hz, 'Wait clocks must share the same supplied core clock')
        self.records, self.events, self.delivered = [], [], []
        lab.cpu.hook_add(UC_HOOK_CODE, self.step)

    def schedule(self, offset, name, callback):
        verify(type(offset) is int and offset >= 0 and isinstance(name, str) and name and callable(callback),
               'Invalid supplied calibration event')
        verify(self.active is None and len(self.events) < 32 and
               name not in [e[1] for e in self.events] + [e['name'] for e in self.delivered],
               'Pending calibration event name/boundary is invalid')
        self.events.append((self.lab.timer.cycles + offset, name, callback))
        self.events.sort(key=lambda e: (e[0], e[1]))

    def deliver(self, cpu, pc):
        while self.events and self.events[0][0] <= self.lab.timer.cycles:
            deadline, name, callback = self.events.pop(0)
            self.delivered.append({'name': name, 'due_bus_cycle': deadline,
                                   'actual_bus_cycle': self.lab.timer.cycles,
                                   'pc': hex(pc), 'ipsr': cpu.reg_read(UC_ARM_REG_IPSR),
                                   'inside_calibration_wait': self.active is not None})
            callback()

    def finish(self):
        r = self.active
        verify(r is not None and self.lab.cpu.reg_read(UC_ARM_REG_SP) == r['stack'],
               'Calibration wait did not restore caller stack')
        r['foreground_counts'] = {hex(k): v for k, v in sorted(r.pop('counts').items())}
        r['pc_sha256'] = r.pop('digest').hexdigest()
        r['core_clock_hz'], r['cycles_per_observed_instruction'] = self.core_hz, self.budget
        r['bus_fraction_end'] = self.lab.delay_clock.bus_remainder
        r['wdclk_fraction_end'] = self.lab.delay_clock.wdt_remainder
        self.records.append(r)
        self.active = None

    def step(self, cpu, pc, size, data):
        lab, irq = self.lab, self.lab.irq
        handler = bool(cpu.reg_read(UC_ARM_REG_IPSR))
        if self.active and not handler and pc == self.active['return_pc'] and cpu.reg_read(UC_ARM_REG_SP) == self.active['stack']:
            self.finish()
        if not self.enabled: return
        if irq.context_restart or (irq.servicing and irq.preempt is not None) or (
                not irq.servicing and irq.foreground_irq): return
        self.deliver(cpu, pc)
        if pc == ENTRY and not handler:
            verify(self.active is None and lab.delay_clock.active is None, 'Nested/overlapping calibration clock')
            verify(struct.unpack('<I', cpu.mem_read(CORE_CLOCK, 4))[0] == self.core_hz,
                   'Core-clock transition outside calibration fixture')
            self.active = {'argument': cpu.reg_read(UC_ARM_REG_R0), 'clock_argument': cpu.reg_read(UC_ARM_REG_R1),
                           'return_pc': cpu.reg_read(UC_ARM_REG_LR) & ~1, 'stack': cpu.reg_read(UC_ARM_REG_SP),
                           'counts': Counter(), 'digest': hashlib.sha256(), 'foreground_instructions': 0,
                           'handler_instructions': 0, 'bus_cycles': 0, 'wdclk_cycles': 0,
                           'bus_clock_hz': lab.timer.clock_hz, 'wdclk_hz': lab.wdt.clock_hz,
                           'bus_fraction_start': lab.delay_clock.bus_remainder,
                           'wdclk_fraction_start': lab.delay_clock.wdt_remainder}
        if self.active is None: return
        verify(lab.delay_clock.active is None, 'D620 and calibration time were charged together')
        r = self.active
        if handler: r['handler_instructions'] += 1
        else:
            verify(any(first <= pc < last for first, last in RANGES), 'Unmodeled calibration helper call/branch')
            r['foreground_instructions'] += 1; r['counts'][pc] += 1
            r['digest'].update(struct.pack('<IH', pc, size))
        # Both tracked helpers share one fractional clock phase. Keeping a
        # separate remainder per helper loses a WDCLK edge at their boundary.
        clock = lab.delay_clock
        clock.bus_remainder += self.budget * lab.timer.clock_hz
        cycles, clock.bus_remainder = divmod(clock.bus_remainder, self.core_hz)
        clock.wdt_remainder += self.budget * lab.wdt.clock_hz
        wdclk, clock.wdt_remainder = divmod(clock.wdt_remainder, self.core_hz)
        lab.timer.advance(cycles)
        for motor in lab.motors: motor.advance(cycles)
        lab.wdt.advance(wdclk)
        r['bus_cycles'] += cycles; r['wdclk_cycles'] += wdclk
        self.deliver(cpu, pc)

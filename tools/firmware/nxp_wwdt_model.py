"""Bounded LPC546xx reset-mode WWDT, with an explicitly supplied WDCLK.

UM10912 rev 2.4 ch18: logical counter/feed/flags only. APB/WDCLK
synchronization, oscillator settling, protect/lock, sleep and reset
execution are outside this model. Reset requests are terminal boundaries.
"""
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

from simulate_audio_haptics import verify


WWDT, WDT_IRQ, MASK = 0x4000C000, 0, 0xFFFFFF


class WatchdogReset(RuntimeError):
    pass


class WindowedWatchdog:
    def __init__(self, cpu, interrupts, clock_hz):
        verify(isinstance(clock_hz, int) and clock_hz > 0, 'WWDT requires a nonzero explicit oscillator clock')
        verify(WDT_IRQ not in interrupts.level_sources, 'WWDT IRQ already attached')
        self.cpu, self.clock_hz = cpu, clock_hz
        self.mode, self.timeout, self.window, self.warning = 0, 255, MASK, 0
        self.tv, self.phase, self.cycles = 255, 0, 0
        self.running, self.feed_first = False, False
        self.timeout_flag, self.warning_flag = False, False
        self.warning_due = None
        self.feeds, self.warning_events, self.feed_writes = 0, 0, []
        self.reset_reason, self.reset_cycle = None, None
        interrupts.level_sources[WDT_IRQ] = lambda: self.warning_flag and self.reset_reason is None
        cpu.hook_add(UC_HOOK_CODE, self.before_instruction)
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=WWDT, end=WWDT + 0x1B)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=WWDT, end=WWDT + 0x1B)

    def require_live(self):
        if self.reset_reason is not None:
            raise WatchdogReset(f'WWDT reset boundary: {self.reset_reason} at WDCLK {self.reset_cycle}')

    def before_instruction(self, *_):
        self.require_live()

    def reset(self, reason):
        self.timeout_flag = True
        self.reset_reason, self.reset_cycle = reason, self.cycles
        self.running = False
        self.require_live()  # Never pretend that reset/bootloader/PWM recovery ran.

    def schedule_warning(self):
        # The warning appears on the WDCLK following the compare. No APB
        # read synchronization delay is modeled; TV is the logical counter.
        if self.warning == 0:
            self.warning_due = self.cycles + (self.tv + 1) * 4 - self.phase
        else:
            self.warning_due = self.cycles + max(0, (self.tv - self.warning) * 4 - self.phase) + 1

    def next_event(self):
        if not self.running:
            return None
        # Inclusive down-counter period. Exact synchronization/zero edge
        # is not a physical timing claim (see report).
        expiry = (self.tv + 1) * 4 - self.phase
        warning = self.warning_due - self.cycles if self.warning_due is not None else expiry
        return min(expiry, warning)

    def advance(self, cycles):
        verify(isinstance(cycles, int) and cycles >= 0, 'Invalid watchdog clock advance')
        self.require_live()
        while cycles and self.running:
            amount = min(cycles, self.next_event())
            self.cycles += amount
            cycles -= amount
            counts, self.phase = divmod(self.phase + amount, 4)
            if counts > self.tv:
                self.tv = 0
                self.reset('counter_expired')
            self.tv -= counts
            if self.warning_due is not None and self.cycles >= self.warning_due:
                self.warning_flag = True
                self.warning_events += 1
                self.warning_due = None
        self.cycles += cycles

    def access(self, address, size, *, second_feed=False):
        self.require_live()
        verify(size == 4 and address % 4 == 0, 'WWDT model requires aligned word accesses')
        if self.feed_first and not second_feed:
            self.feed_first = False
            if self.running:
                self.reset('invalid_feed' if address == WWDT + 8 else 'intervening_register_access')

    def read(self, cpu, access, address, size, value, data):
        self.access(address, size)
        values = {0: self.mode | int(self.timeout_flag) * 4 | int(self.warning_flag) * 8,
                  4: self.timeout, 12: self.tv, 20: self.warning, 24: self.window}
        verify(address - WWDT in values, 'Reserved/write-only WWDT register read')
        cpu.mem_write(address, struct.pack('<I', values[address - WWDT]))

    def write(self, cpu, access, address, size, value, data):
        offset = address - WWDT
        second = offset == 8 and value == 0x55 and self.feed_first
        self.access(address, size, second_feed=second)
        if offset == 0:
            verify(value & ~15 == 0, 'WWDT protect/lock/reserved modes are not modeled')
            mode = value & 3 | self.mode  # Enable/reset bits cannot be cleared by software.
            verify(mode in (0, 2, 3), 'WWDT interrupt-only mode is outside the reset-mode fixture')
            self.mode = mode
            if not value & 4: self.timeout_flag = False
            if value & 8 and self.tv != self.warning: self.warning_flag = False
        elif offset == 4:
            verify(value & ~MASK == 0, 'Reserved WWDT timeout bits')
            self.timeout = max(255, value)
        elif offset in (20, 24):
            mask = 1023 if offset == 20 else MASK
            verify(value & ~mask == 0, 'Reserved WWDT compare bits')
            if offset == 20:
                verify(not self.running, 'Live warning reconfiguration is not modeled')
                self.warning = value
            else:
                self.window = value
        elif offset == 8:
            verify(value & ~255 == 0, 'Reserved WWDT feed bits')
            self.feed_writes.append(value)
            if second:
                self.feed_first = False
                if self.running and self.tv > self.window: self.reset('early_window_feed')
                if self.mode & 1:
                    self.running, self.tv, self.phase = True, self.timeout, 0
                    self.feeds += 1
                    self.schedule_warning()
            elif value == 0xAA:
                self.feed_first = True
            elif self.running:
                self.reset('invalid_feed')
        else:
            raise RuntimeError('Reserved/read-only WWDT register write')

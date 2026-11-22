"""Replay stock watchdog setup/feed/IRQ and the main-loop counter gate, offline."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_MEM_WRITE
from unicorn.arm_const import UC_ARM_REG_BASEPRI, UC_ARM_REG_PC, UC_ARM_REG_PRIMASK, UC_ARM_REG_R0

from cortex_m_fpu import CortexMFpuInterrupts
from nxp_wwdt_model import WWDT, WindowedWatchdog, WatchdogReset
from replay_nxp_clock import TimedLab, CLOCK
from replay_nxp_exceptions import fixture, read_mmio, write_mmio
from simulate_audio_haptics import RETURN, STOCK_SHA256, verify


OSCILLATOR, OSCILLATOR_RESET = 0x40000508, 0x140
LAST_LOOP_COUNTER, LOOP_HEADER, LOOP_CHANGED, LOOP_UNCHANGED = 0x2000B340, 0x2F100, 0x2F136, 0x2F0E8


def expect_reset(action, watchdog, reason):
    try: action()
    except WatchdogReset:
        verify(watchdog.reset_reason == reason and watchdog.timeout_flag,
               f'Wrong watchdog reset boundary: {watchdog.reset_reason}, expected {reason}')
    else: raise RuntimeError('Watchdog fault did not request reset: ' + reason)


def register_checks():
    def create(clock_hz=800000):
        cpu, irq = fixture('7047', model=CortexMFpuInterrupts)
        cpu.mem_map(WWDT, 4096)
        return cpu, WindowedWatchdog(cpu, irq, clock_hz)

    cpu, w = create()
    read = lambda offset: read_mmio(cpu, WWDT + offset)
    write = lambda offset, value: write_mmio(cpu, WWDT + offset, value)
    feed = lambda: (write(8, 0xAA), write(8, 0x55))
    verify((read(0), read(4), read(12), read(20), read(24)) == (0, 255, 255, 0, 0xFFFFFF), 'Wrong watchdog reset registers')
    write(4, 1); verify(read(4) == 255, 'Watchdog minimum timeout was not clamped')
    write(8, 0x23); write(0, 3); write(8, 0x23); w.advance(10000)
    verify(not w.running and w.tv == 255 and w.reset_reason is None, 'Enable alone started watchdog')
    feed(); verify(w.running and w.tv == 255 and w.feeds == 1, 'Valid feed did not start watchdog')
    w.advance(3); verify(read(12) == 255, 'Watchdog prescaler expired early')
    w.advance(1); verify(read(12) == 254, 'Watchdog prescaler did not decrement')
    write(0, 0); verify(read(0) & 3 == 3, 'Write-once watchdog mode bits cleared')
    w.timeout_flag = True  # Explicit status-register test seed, not an elapsed reset.
    write(0, 7); verify(read(0) & 4, 'WDTOF write-one incorrectly cleared')
    write(0, 3); verify(not read(0) & 4, 'WDTOF write-zero failed to clear')
    w.warning, w.tv, w.warning_flag = 254, 254, True
    write(0, 11); verify(w.warning_flag, 'Warning clear at equal TV succeeded')
    w.advance(4); write(0, 11); verify(not w.warning_flag, 'Warning W1C below compare failed')
    w.warning = 0
    feed(); w.advance(255 * 4)
    verify(w.tv == 0 and w.reset_reason is None and not w.warning_flag, 'Inclusive logical counter/zero-warning boundary changed')
    expect_reset(lambda: w.advance(4), w, 'counter_expired')
    expect_reset(lambda: w.advance(1), w, 'counter_expired')

    faults = []
    for name, action in (
            ('invalid_feed', lambda c, q: write_mmio(c, WWDT + 8, 0x42)),
            ('intervening_register_access', lambda c, q: (write_mmio(c, WWDT + 8, 0xAA), read_mmio(c, WWDT + 12))),
            ('early_window_feed', lambda c, q: (write_mmio(c, WWDT + 24, 254), write_mmio(c, WWDT + 8, 0xAA), write_mmio(c, WWDT + 8, 0x55)))):
        c, q = create()
        write_mmio(c, WWDT, 3); write_mmio(c, WWDT + 8, 0xAA); write_mmio(c, WWDT + 8, 0x55)
        expect_reset(lambda: action(c, q), q, name); faults.append(name)
    c, q = create()
    for offset, value in ((0, 3), (8, 0xAA), (8, 0x55), (24, 255), (8, 0xAA), (8, 0x55)):
        write_mmio(c, WWDT + offset, value)
    verify(q.feeds == 2 and q.reset_reason is None, 'Feed exactly at window was rejected')

    rejected = []
    for name, action in (
            ('protect', lambda c, q: write_mmio(c, WWDT, 0x13)),
            ('lock', lambda c, q: write_mmio(c, WWDT, 0x23)),
            ('interrupt_only', lambda c, q: write_mmio(c, WWDT, 1)),
            ('readonly_tv', lambda c, q: write_mmio(c, WWDT + 12, 0)),
            ('writeonly_feed', lambda c, q: read_mmio(c, WWDT + 8)),
            ('reserved_timeout', lambda c, q: write_mmio(c, WWDT + 4, 1 << 24)),
            ('byte_access', lambda c, q: write_mmio(c, WWDT, 3, size=1)),
            ('negative_clock_advance', lambda c, q: q.advance(-1)),
            ('zero_oscillator', lambda c, q: create(0))):
        c, q = create()
        try: action(c, q)
        except RuntimeError: rejected.append(name)
        else: raise RuntimeError('Unmodeled watchdog operation accepted: ' + name)
    return {'checks': ['reset_registers', 'minimum_timeout_clamp', 'enable_requires_feed', 'initial_feed_errors_ignored',
                       'feed_load', 'divide_by_four', 'sticky_mode', 'timeout_w0c', 'warning_equal_retained',
                       'warning_below_w1c', 'inclusive_logical_expiry', 'terminal_reset', 'exact_window_feed'],
            'reset_controls': faults, 'rejected': rejected}


class WatchdogLab(TimedLab):
    def __init__(self, image, dsp, *, oscillator_control=OSCILLATOR_RESET):
        super().__init__(image, dsp)
        self.cpu.mem_map(WWDT, 4096)
        # UM10912 WDTOSCCTRL reset fields; oscillator power is an explicit
        # ready fixture. This does not close all board startup/clock gating.
        self.cpu.mem_write(OSCILLATOR, struct.pack('<I', oscillator_control))
        self.replay.call(0xA9F4, RETURN, 10)
        frequency = self.cpu.reg_read(UC_ARM_REG_R0)
        verify(frequency == 800000, 'Unexpected stock nominal watchdog clock')
        self.wdt = WindowedWatchdog(self.cpu, self.irq, frequency)
        self.replay.call(0x29870, RETURN)
        verify((self.wdt.mode, self.wdt.timeout, self.wdt.warning, self.wdt.window, self.wdt.feeds) ==
               (3, 400000, 512, 0xFFFFFF, 2), 'Stock watchdog initializer changed')
        self.refresh_stores = []
        self.cpu.hook_add(UC_HOOK_MEM_WRITE, self.refresh_store, begin=WWDT + 8, end=WWDT + 11)

    def refresh_store(self, cpu, access, address, size, value, data):
        verify(cpu.reg_read(UC_ARM_REG_PC) in (0x29864, 0x29868), 'Not an original watchdog refresh store')
        verify(cpu.reg_read(UC_ARM_REG_PRIMASK) == 1, 'Stock feed sequence was not masked')
        self.refresh_stores.append(value)

    def header(self):
        previous, = struct.unpack('<I', self.cpu.mem_read(LAST_LOOP_COUNTER, 4))
        changed = previous != self.clock()
        endpoint = LOOP_CHANGED if changed else LOOP_UNCHANGED
        self.replay.call(LOOP_HEADER, endpoint)
        verify(self.refresh_stores[-2:] == [0xAA, 0x55], 'Main-loop header did not feed watchdog')
        verify(struct.unpack('<I', self.cpu.mem_read(LAST_LOOP_COUNTER, 4))[0] == self.clock(), 'Loop counter gate did not publish current counter')
        return changed


def loop_header_cases(image, dsp):
    lab = WatchdogLab(image, dsp)
    lab.cpu.mem_write(LAST_LOOP_COUNTER, struct.pack('<I', 0xFFFFFFFF))
    verify(lab.header() and not lab.header(), 'Changed/unchanged stock loop branch mismatch')
    for _ in range(16):
        lab.advance_us(1000); lab.wdt.advance(800)
        verify(lab.header() and not lab.header(), 'Stock loop gate missed counter change')
    verify(lab.clock() == 16 and lab.wdt.feeds == 36 and lab.wdt.reset_reason is None,
           'Loop feeds/counter did not match the supplied schedule')
    verify(lab.cpu.reg_read(UC_ARM_REG_PRIMASK) == 0, 'Header changed caller PRIMASK')
    return {'original_headers': 34, 'changed': 17, 'unchanged': 17,
            'counter_from_original_mrt': lab.clock(), 'mrt_checked_returns': lab.irq.checked_returns,
            'masked_refresh_stores': len(lab.refresh_stores), 'stock_initializer_feeds': 2,
            'scope': 'Original loop header only; stops before tick tasks or the USB background branch'}


def blackouts(image, dsp):
    cases = []
    for mask in ('nvic_disabled', 'primask', 'basepri'):
        lab = WatchdogLab(image, dsp); lab.feed(); lab.run()
        verify(lab.values()[1] == 1, 'No active audio ownership before blackout')
        if mask == 'nvic_disabled': lab.irq.enabled = 0
        elif mask == 'primask': lab.irq.enabled |= 1; lab.cpu.reg_write(UC_ARM_REG_PRIMASK, 1)
        elif mask == 'basepri':
            lab.irq.enabled |= 1; lab.irq.priorities[0] = 0x80; lab.cpu.reg_write(UC_ARM_REG_BASEPRI, 0x40)
        before = bytes(lab.cpu.mem_read(0x20000000, 0x28000))
        lab.timer.advance(lab.wdt.timeout * 4 * 120)  # Explicit fixed 96 MHz : 800 kHz fixture ratio.
        lab.wdt.advance(lab.wdt.timeout * 4)
        verify(lab.irq.service() == 0 and lab.clock() == 0 and lab.wdt.tv == 0,
               'Masked watchdog case unexpectedly serviced an IRQ')
        lab.timer.advance(4 * 120)
        expect_reset(lambda: lab.wdt.advance(4), lab.wdt, 'counter_expired')
        expect_reset(lambda: lab.replay.call(0xA9F4, RETURN, 1), lab.wdt, 'counter_expired')
        verify(lab.cpu.mem_read(0x20000000, 0x28000) == before and lab.values()[1] == 1,
               'Reset boundary silently emulated RAM/firmware recovery')
        cases.append({'mask': mask, 'logical_wdclk_to_reset': lab.wdt.reset_cycle,
                      'mrt_counter': lab.clock(), 'foreground_publisher_calls_during_blackout': 0,
                      'mrt_expirations': lab.timer.channels[0].expirations,
                      'coalesced_mrt_expirations': lab.timer.channels[0].coalesced,
                      'post_reset_core_resume_rejected': True,
                      'reset_requested': True, 'reset_or_pwm_recovery_executed': False})
    return cases


def warning_paths(image, dsp):
    # Explicit NVIC-enabled condition: initializer alone does not establish it.
    lab = WatchdogLab(image, dsp); lab.irq.enabled |= 1
    lab.feed(); lab.run()
    entries = []
    for _ in range(3):
        lab.wdt.advance(lab.wdt.next_event())
        entries.append(lab.irq.service())
        verify(not lab.wdt.warning_flag and lab.wdt.reset_reason is None, 'Stock warning ISR did not clear/reload')
    verify(lab.clock() == 0 and lab.values()[1] == 1 and all(n == 2 for n in entries),
           'Warning-only execution falsely retired audio ownership or missed equality-clear behavior')

    race = WatchdogLab(image, dsp); race.irq.enabled |= 1; race.feed()
    race.wdt.advance((race.wdt.timeout - 513) * 4)
    fired = []

    def warning_during_copy(cpu, access, address, size, value, data):
        if not fired:
            verify(cpu.reg_read(UC_ARM_REG_PRIMASK) == 1, 'Warning injection was not inside masked copy')
            fired.append(True); race.wdt.advance(5)

    race.cpu.hook_add(UC_HOOK_MEM_WRITE, warning_during_copy, begin=0x20000B3C, end=0x20000B3C)
    race.run()
    verify(fired and race.values()[5] == 1 and not race.wdt.warning_flag and race.irq.checked_returns == 2,
           'Masked publication did not preserve/defer the watchdog warning IRQ')
    return {'foreground_stalled_warning_enabled': {'warning_events': lab.wdt.warning_events,
                    'irq_entries_per_warning': entries, 'elapsed_wdclk': lab.wdt.cycles,
                    'mrt_counter': lab.clock(), 'audio_still_owned': bool(lab.values()[1]), 'reset_requested': False,
                    'scope': 'Original warning ISR itself feeds WWDT; actual board NVIC0 enable remains unverified'},
            'warning_during_masked_publication': {'compiled_frames': race.values()[5],
                    'checked_irq_returns': race.irq.checked_returns, 'warning_cleared': not race.wdt.warning_flag},
            'zero_instruction_time_note': 'Clear at TV==WARNINT is retained; immediate refill permits the second ISR to clear it. Two entries are a logical-model result, not measured silicon timing.'}


def original_fault_guards(image, dsp):
    lab = WatchdogLab(image, dsp)
    verify(lab.cpu.mem_read(0x29866, 2) == bytes.fromhex('5522'), 'Unexpected stock second-feed instruction')
    lab.cpu.mem_write(0x29866, bytes.fromhex('5422'))  # Unicorn map only, never a device image.
    lab.cpu.ctl_flush_tb()
    expect_reset(lab.header, lab.wdt, 'invalid_feed')
    try: WatchdogLab(image, dsp, oscillator_control=0)
    except RuntimeError as error:
        verify(str(error) == 'Unexpected stock nominal watchdog clock', 'Wrong missing-clock failure')
    else: raise RuntimeError('Zero oscillator silently accepted by stock setup')
    return ['corrupted_original_second_feed', 'zero_clock_in_original_frequency_query']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stock', type=Path); parser.add_argument('dsp', type=Path)
    args = parser.parse_args(); image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, 'Unexpected stock watchdog image')
    registers = register_checks()
    header = loop_header_cases(image, dsp)
    blackout = blackouts(image, dsp)
    warnings = warning_paths(image, dsp)
    guards = original_fault_guards(image, dsp)
    print(json.dumps({'scope': 'Bounded stock WWDT and main-loop header replay, not complete startup/main/reset',
          'registers': registers, 'loop_header': header, 'blackouts': blackout, 'warnings': warnings,
          'original_code_negative_controls': guards,
          'nominal_oscillator_hz': 800000, 'oscillator_reset_control': OSCILLATOR_RESET,
          'stock_timeout_count': 400000, 'stock_warning_count': 512,
          'timing_policy': 'Inclusive logical down-counter, supplied WDCLK, no APB synchronization/oscillator tolerance or instruction timing',
          'whole_device_equivalence_proven': False, 'flashable_candidate_ready': False,
          'input_sha256': {'stock': hashlib.sha256(image).hexdigest(), 'dsp': hashlib.sha256(dsp).hexdigest()}}, indent=2))


if __name__ == '__main__': main()

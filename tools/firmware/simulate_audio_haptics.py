"""Replay stock NXP I2S TX selection and compare an original ARM DSP in Unicorn."""
import argparse
import hashlib
import json
import math
import re
from fractions import Fraction
from pathlib import Path
import struct
import wave

from unicorn import (
    Uc, UC_ARCH_ARM, UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_MODE_MCLASS, UC_MODE_THUMB,
    UC_PROT_EXEC, UC_PROT_READ,
)
from unicorn.arm_const import (
    UC_ARM_REG_APSR, UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_R0, UC_ARM_REG_R1,
    UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_R4, UC_ARM_REG_SP,
)

from audit_dev2_hex import STOCK_SHA256
from emulate_decoder import DECODER, OUT, decode, xbox_frame

DSP, STATE, LEVELS = 0x100000, 0x20021000, 0x20021100
RING, ZERO, TX = 0x20013A38, 0x200056EC, 0x22FF8
DMA_SUBMIT, RETURN = 0x12F9C, 0xFFF0
USB_RX, USB_RX_ACCOUNTING = 0x28C80, 0x2384C
USB_BUFFER, USB_EVENT, USB_CONTEXT = 0x2000B3C0, 0x20022000, 0x20022100
USB_RING = 0x200057AC
CTIMER2, CTIMER3 = 0x40028000, 0x40048000
TX_TAIL, TAP, TAP_BUFFER = 0x2315C, DSP + 0x800, 0x20024000
WORKER = DSP + 0x900
MAIN_DISPATCH_CALL, FOREGROUND = 0x2F5FA, DSP + 0xA00
BOOT_HANDOFF, CANDIDATE_BOOT, STOCK_MAIN = 0x840C, DSP + 0xB00, 0x2EF68
TAP_ENABLE, TAP_READY, TAP_SOURCE = (TAP_BUFFER + offset for offset in (192, 196, 200))


def verify(condition, message):
    if not condition:
        raise RuntimeError(message)


class Replay:
    def __init__(self, image, dsp=None):
        self.cpu = cpu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
        cpu.mem_map(0, 0x40000)
        cpu.mem_write(0x8000, image)
        cpu.mem_map(0x20000000, 0x28000)
        self.steps = 0
        if dsp is not None:
            verify(0 < len(dsp) <= 4096, "Invalid simulator DSP binary size")
            cpu.mem_map(DSP, 4096)
            cpu.mem_write(DSP, dsp)
            cpu.hook_add(UC_HOOK_CODE, self.count_step, begin=DSP, end=DSP + len(dsp) - 1)

    def count_step(self, *_):
        self.steps += 1

    def call(self, entry, end, *args, instruction_limit=20000):
        cpu = self.cpu
        cpu.reg_write(UC_ARM_REG_SP, 0x20027000)
        cpu.reg_write(UC_ARM_REG_LR, RETURN | 1)
        for reg, value in zip((UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3), args):
            cpu.reg_write(reg, value)
        cpu.emu_start(entry | 1, end, count=instruction_limit)
        verify(cpu.reg_read(UC_ARM_REG_PC) == end, f"Routine 0x{entry:X} did not reach 0x{end:X}")

    def tick(self, pcm, streaming, primed, slot, alternate=False, enabled=True):
        cpu = self.cpu
        cpu.mem_write(RING + slot * 192, pcm)
        cpu.mem_write(0x200057AC + slot * 192, pcm)
        cpu.mem_write(0x20013C78, bytes([slot]))
        cpu.mem_write(0x20000A59, bytes([streaming]))
        cpu.mem_write(0x20000A92, bytes([not alternate]))
        cpu.mem_write(0x20008F92, bytes([primed]))
        cpu.mem_write(0x20008F98, struct.pack("<I", slot * 192))
        cpu.mem_write(0x20008FC4, struct.pack("<II", 0, 1))
        self.call(TX, DMA_SUBMIT, 0, 0)
        address = cpu.reg_read(UC_ARM_REG_R2)
        size = cpu.reg_read(UC_ARM_REG_R3)
        expected = streaming and primed
        ring = 0x200057AC if alternate else RING
        verify(size == 192 and address == (ring + slot * 192 if expected else ZERO),
               f"Unexpected stock DMA transfer: 0x{address:X}, {size} bytes")
        verify(bytes(cpu.mem_read(address, size)) == (pcm if expected else bytes(192)),
               "Stock callback did not preserve the selected PCM bytes")
        if expected and not alternate:
            verify(cpu.mem_read(0x20013C78, 1)[0] == (slot + 1) % 3, "Stock ring index did not advance")
        verify(not any(cpu.mem_read(OUT, 4 * 0x82)), "Stock callback touched Sensa body parts")
        self.steps = 0
        self.call(DSP, RETURN, STATE, address, int(expected and enabled), LEVELS)
        return list(cpu.mem_read(LEVELS, 4)), self.steps

    def receive_usb(self, slot, end=RETURN):
        cpu = self.cpu
        cpu.mem_write(USB_BUFFER, slot)
        cpu.mem_write(USB_EVENT, struct.pack("<II", 0, len(slot)))
        cpu.mem_write(0x20000778, struct.pack("<I", USB_CONTEXT))
        cpu.mem_write(USB_CONTEXT + 0x115, b"\x01")
        cpu.mem_write(0x200021CC, b"\x01")
        self.call(USB_RX, end, 0, 2, USB_EVENT)

    def check_usb_receive(self, slot, offset):
        """Invoke the stock 198-byte receive branch with a synthetic USB event."""
        cpu = self.cpu
        cpu.mem_write(0x20008F9C, struct.pack("<I", offset))
        cpu.mem_write(0x20008FA0, bytes(16))
        self.receive_usb(slot, USB_RX_ACCOUNTING)
        first = min(192, 6144 - offset)
        observed = bytes(cpu.mem_read(USB_RING + offset, first))
        if first < 192:
            observed += bytes(cpu.mem_read(USB_RING, 192 - first))
        verify(observed == slot[6:], "Stock USB receive did not copy the expected PCM bytes")
        written, = struct.unpack("<I", cpu.mem_read(0x20008F9C, 4))
        verify(written == (offset + 192) % 6144, "Stock USB receive did not advance its ring")

    def check_usb_to_i2s(self, slot, offset):
        self.check_usb_receive(slot, offset)
        cpu = self.cpu
        # Configure the already-started stream. Preserve the producer's ring
        # bytes and indices while the real TX routine reads the same slot.
        cpu.mem_write(0x20000A59, b"\x01")
        cpu.mem_write(0x20000A92, b"\x00")
        cpu.mem_write(0x20008F92, b"\x01")
        cpu.mem_write(0x20008F98, struct.pack("<I", offset))
        cpu.mem_write(0x20008FC4, struct.pack("<II", 0, 1))
        self.call(TX, DMA_SUBMIT, 0, 0)
        address = cpu.reg_read(UC_ARM_REG_R2)
        verify(address == USB_RING + offset and cpu.reg_read(UC_ARM_REG_R3) == 192,
               "TX did not select the slot written by USB")
        verify(bytes(cpu.mem_read(address, 192)) == slot[6:], "USB-to-I2S PCM mismatch")
        verify(bytes(cpu.mem_read(0x20008F98, 4)) == struct.pack("<I", (offset + 192) % 6144),
               "I2S read pointer did not advance")


def tone(tick, channel_frequencies, amplitude=0.85):
    return struct.pack("<96h", *(
        round(32767 * amplitude * sum(math.sin(2 * math.pi * hz * n / 48000)
                                     for hz in frequencies) / len(frequencies))
        if frequencies else 0
        for n in range(tick * 48, (tick + 1) * 48)
        for frequencies in channel_frequencies
    ))


def run_scenario(image, dsp, name, frequencies, streaming, primed, amplitude, alternate=False):
    replay = Replay(image, dsp)
    trace, peak_steps = [], 0
    for tick in range(180):
        pcm = tone(tick, frequencies, amplitude) if tick < 100 else bytes(192)
        levels, steps = replay.tick(pcm, streaming and tick < 160, primed, tick % 3, alternate)
        trace.append(levels)
        peak_steps = max(peak_steps, steps)
    peaks = [max(row[column] for row in trace) for column in range(4)]
    verify(all(0 <= x <= 63 for row in trace for x in row), "Strength out of range")
    verify(trace[-1] == [0] * 4 and trace[160] == [0] * 4, f"{name}: stop not silent")
    verify(trace[159] == [0] * 4, f"{name}: digital silence did not decay")
    for channel in range(2):
        active = streaming and primed and frequencies[channel] and amplitude >= 0.1
        verify(bool(any(peaks[channel * 2:channel * 2 + 2])) == bool(active),
               f"{name}: wrong channel or missed activity")
    # Translate DSP strengths into a supported frame and execute the actual stock decoder.
    # The comparison ends at decoded RAM, before synthesis or PWM.
    loudest = max(trace, key=sum)
    bodies = [([(a, a, pitch) for a, pitch in zip(loudest[i:i + 2], (17, 55)) if a], [])
              for i in (0, 2)] + [([], []), ([], [])]
    decoded = decode(image, DECODER["xbox"], xbox_frame(bodies))
    for i, (bands, _) in enumerate(bodies):
        for band, offset in enumerate((17, 33)):
            strength = bands[band][0] if band < len(bands) else 0
            expected = strength * 520 if i < 2 else strength
            verify(decoded[i][offset + 3] == expected and decoded[i][offset + 9] == expected,
                   f"{name}: stock decoder mismatch body {i} band {band}")
    return {"name": name, "stock_tx_touched_sensa_ram": False, "peak_strength": peaks,
            "peak_dsp_instructions_per_tick": peak_steps,
            "first_active_ms": next((i for i, row in enumerate(trace) if any(row)), None),
            "last_active_ms": next((i for i in reversed(range(len(trace))) if any(trace[i])), None)}


def check_stream_loss(image, dsp):
    replay = Replay(image, dsp)
    for tick in range(20):
        pcm = tone(tick, ((80,), (190,)))
        levels, _ = replay.tick(pcm, True, True, tick % 3)
    verify(any(levels[:2]) and any(levels[2:]), "Stream-loss control was not active")
    levels, _ = replay.tick(pcm, False, True, 2)
    verify(levels == [0] * 4, "Stream loss did not clear active output immediately")
    return {"name": "stream_loss", "strength_on_loss": levels}


def check_toggle(image, dsp):
    replay = Replay(image, dsp)
    for tick in range(30):
        enabled = 10 <= tick < 20
        levels, _ = replay.tick(tone(tick, ((80,), (190,))), True, True,
                                tick % 3, enabled=enabled)
        if not enabled:
            verify(levels == [0] * 4, "Disabled DSP affected output")
        elif tick == 19:
            verify(any(levels[:2]) and any(levels[2:]), "Enabled DSP did not produce stereo output")
    return {"name": "runtime_toggle", "pcm_unchanged": True, "strength_after_disable": levels}


def check_stream_queue(image, dsp, records):
    """Drive USB arrival and I2S completion callbacks on shared persistent RAM."""
    replay = Replay(image, dsp)
    cpu = replay.cpu
    cpu.mem_write(0x20000A59, b"\x01")
    # This selects the USB ring, not the short alternate waveform buffer.
    cpu.mem_write(0x20000A92, b"\x00")
    pending = []
    primed_at = None
    played = 0
    for tick, record in enumerate(records[:128]):
        pcm = bytes.fromhex(record["payload_hex"])
        header = bytes((0x60, 0x20, record["sequence"], 0xC0, 0x81, 0))
        replay.receive_usb(header + pcm)
        pending.append(pcm)
        replay.call(TX, DMA_SUBMIT, 0, 0)
        address = cpu.reg_read(UC_ARM_REG_R2)
        verify(cpu.reg_read(UC_ARM_REG_R3) == 192, "Unexpected I2S block size")
        if address == ZERO:
            verify(primed_at is None, "Unexpected gap in a continuous USB stream")
            continue
        if primed_at is None:
            primed_at = tick
        verify(address == USB_RING + (played * 192) % 6144, "Unexpected I2S slot")
        verify(bytes(cpu.mem_read(address, 192)) == pending[played], "USB queue reordered PCM")
        played += 1

    if len(pending) < 17:
        verify(played == 0, "USB queue primed before its stock threshold")
        return {"blocks_received": len(pending), "primed_at_fixture_ms": None}
    verify(primed_at == 16, "USB queue did not prime after 16 blocks of prefill")
    for _ in range(len(pending) - played):
        replay.call(TX, DMA_SUBMIT, 0, 0)
        address = cpu.reg_read(UC_ARM_REG_R2)
        verify(address == USB_RING + (played * 192) % 6144, "Unexpected drain slot")
        verify(bytes(cpu.mem_read(address, 192)) == pending[played], "Drain reordered PCM")
        played += 1
    replay.call(TX, DMA_SUBMIT, 0, 0)
    verify(cpu.reg_read(UC_ARM_REG_R2) == ZERO, "Queue underrun failed to select silence")
    recovery = pending[:17]
    recovery_at = None
    for index, pcm in enumerate(recovery):
        replay.receive_usb(bytes((0x60, 0x20, index + 1, 0xC0, 0x81, 0)) + pcm)
        replay.call(TX, DMA_SUBMIT, 0, 0)
        address = cpu.reg_read(UC_ARM_REG_R2)
        if address != ZERO:
            verify(address == USB_RING + (played * 192) % 6144, "Queue resumed from the wrong slot")
            verify(bytes(cpu.mem_read(address, 192)) == recovery[0], "Queue resumed with stale PCM")
            recovery_at = index
            break
    verify(recovery_at is not None, "Queue did not recover")
    return {"blocks_received": len(pending), "primed_at_fixture_ms": primed_at,
            "blocks_played": played, "underrun_selected_silence": True, "recovery_prefill_blocks": recovery_at}


def check_timer_pwm(image, dsp):
    """Run the stock CTIMER2 callback and capture SCT register writes."""
    replay, match_registers = initialize_stock_sensa_pwm(image, dsp)
    cpu = replay.cpu
    timer = CTIMER2
    match = match_registers[0]
    reload = match + 0x100
    period, = struct.unpack("<I", cpu.mem_read(match - 4, 4))
    cpu.mem_write(0x200044BE, b"\x01")
    writes = []
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                 writes.append((address, value)), begin=timer, end=0x40085FFF)
    observed = []
    for enabled, duty, changed in ((False, 20, False), (True, 20, True), (True, 20, False),
                                   (True, 100, True), (True, 0, True)):
        writes.clear()
        cpu.mem_write(0x200044ED, bytes([enabled]))
        cpu.mem_write(0x20002284, struct.pack("<H", duty))
        replay.call(0x2B6F0, RETURN)
        counts = duty * period // 100
        expected = [(match, counts), (reload, counts)] if changed else []
        verify([(addr, value) for addr, value in writes if addr in (match, reload)] == expected,
               "Unexpected SCT MATCH/MATCHREL writes")
        verify(writes[-1] == (timer, 1), "CTIMER2 did not acknowledge match")
        observed.append({"enabled": enabled, "duty_percent": duty, "match_counts": [v for _, v in expected]})
    return {"name": "stock_timer_pwm_registers", "period_match": period,
            "duty_match_registers": [hex(address) for address in match_registers], "cases": observed}


def check_stock_timer_schedule(image, dsp):
    """Schedule MR0 events from the configured clocks and run stock IRQ handlers."""
    replay, match_registers = initialize_stock_sensa_pwm(image, dsp)
    cpu = replay.cpu

    def read_word(address):
        return read_sensa_pwm(cpu, (address,))[0]

    bootstrap_selector = initialize_stock_sensa_timers(replay)
    verify(bootstrap_selector == 1 and read_word(0x40040020) & 3 == 0,
           "Expected asynchronous APB to switch from FRO 12 MHz to main clock")
    replay.call(0xA9F4, RETURN, 0)
    clocks = (cpu.reg_read(UC_ARM_REG_R0),) * 2
    verify(clocks[0] == 96_000_000, "Unexpected stock system clock")
    period_match = read_word(match_registers[0] - 4)
    carrier = clocks[0] // (2 * period_match)
    verify(carrier == 40_000, "Unexpected stock PWM carrier")
    timers = (CTIMER2, CTIMER3)
    handlers = (0xB438, 0xB49C)
    callbacks = (0x2B6F0, 0x2B718)
    priorities = [cpu.mem_read(0xE000E400 + irq, 1)[0] for irq in (36, 13)]
    verify(priorities == [0x40, 0x80], "Unexpected stock CTIMER interrupt priorities")
    periods = []
    for timer, clock in zip(timers, clocks):
        verify(read_word(timer + 4) & 3 == 1 and read_word(timer + 0x14) & 7 == 3
               and read_word(timer + 0x70) & 3 == 0, "Expected an enabled reset-and-interrupt MR0 timer")
        prescale = read_word(timer + 0x0C) + 1
        counts = read_word(timer + 0x18) + 1
        periods.append(Fraction(prescale * counts * 1_000_000, clock))
    verify(periods == [250, 1000], "Unexpected stock CTIMER periods")

    def acknowledge(machine, access, address, size, value, data):
        # Unicorn MMIO writes are ordinary memory writes. Apply IR's W1C
        # effect immediately before the next executed instruction.
        acknowledgements[address] = read_word(address) & ~value

    def dispatch_instruction(machine, address, size, data):
        for timer, flags in acknowledgements.items():
            machine.mem_write(timer, struct.pack("<I", flags))
        acknowledgements.clear()
        if address in callbacks:
            dispatched.append(address)

    acknowledgements = {}
    dispatched = []
    cpu.hook_add(UC_HOOK_CODE, dispatch_instruction)
    for timer in timers:
        cpu.hook_add(UC_HOOK_MEM_WRITE, acknowledge, begin=timer, end=timer)
        cpu.mem_write(timer, bytes(4))

    # Fixture input: change palm duty on the first fast tick, and seed a
    # countdown updated by the slower stock callback.
    cpu.mem_write(0x200044BE, b"\x01")
    cpu.mem_write(0x200044ED, b"\x01")
    cpu.mem_write(0x20002284, struct.pack("<H", 20))
    countdown = 0x200090AA
    cpu.mem_write(countdown, struct.pack("<H", 24))
    schedule = sorted((period * tick, priorities[index], index) for index, period in enumerate(periods)
                      for tick in range(1, int(24_000 / period) + 1))
    irq_counts = [0, 0]
    duty_at_first_fast_tick = None
    for timestamp, _, index in schedule:
        timer = timers[index]
        cpu.mem_write(timer, struct.pack("<I", 1))
        dispatched.clear()
        replay.call(handlers[index], RETURN)
        verify(dispatched == [callbacks[index]] and read_word(timer) == 0,
               f"Stock timer dispatch or acknowledgement failed at {timestamp} us")
        irq_counts[index] += 1
        if timestamp == periods[0]:
            duty_at_first_fast_tick = read_word(match_registers[0])
        remaining, = struct.unpack("<H", cpu.mem_read(countdown, 2))
        verify(remaining == 24 - irq_counts[1], "Stock slow timer countdown disagrees with dispatch")
    verify(irq_counts == [96, 24] and duty_at_first_fast_tick == 240, "Unexpected stock timing results")
    return {"name": "stock_clock_timer_schedule", "timer_clocks_hz": list(clocks),
            "periods_us": [int(period) for period in periods], "irq_counts_in_24_ms": irq_counts,
            "irq_priorities": priorities,
            "pwm_carrier_hz": carrier, "pwm_period_match": period_match,
            "first_fast_tick_duty_match": duty_at_first_fast_tick}


def initialize_stock_sensa_timers(replay):
    """Run the timer setup in board initialization order, including the clock switch."""
    cpu = replay.cpu
    for page in (0x40001000, 0x40009000, 0x40049000):
        cpu.mem_map(page, 4096)
    replay.call(0x32A64, RETURN, 1)
    replay.call(0x329E4, RETURN, 1)
    selector = cpu.mem_read(0x40040020, 1)[0] & 3
    replay.call(0x130FC, RETURN)
    return selector


def stock_timer_irq(replay, timer, handler):
    replay.cpu.mem_write(timer, struct.pack("<I", 1))
    replay.call(handler, RETURN)


def check_stock_sensa_buffer(image, dsp):
    """Compare stock sample production and consumption at the operational ratio."""
    traces = {}
    for side in (None, 0, 1):
        replay, registers = initialize_stock_sensa_pwm(image, dsp)
        initialize_stock_sensa_timers(replay)
        cpu = replay.cpu
        bodies = [([], []) for _ in range(4)]
        if side is not None:
            bodies[side] = ([], [(35, 24, 0)])
        cpu.mem_write(0x20000B34, xbox_frame(bodies) + bytes(64))
        cpu.mem_write(0x20000B2C, b"\x01")
        replay.call(0x2B538, RETURN)
        for address, value in ((0x2000077C, 1), (0x20004520, 2), (0x200044ED, 1)):
            cpu.mem_write(address, bytes([value]))

        def depths():
            return tuple(cpu.mem_read(0x2000162C, 2))

        duty_trace = []
        for tick in range(1, 113):
            before = depths()
            stock_timer_irq(replay, CTIMER2, 0xB438)
            after = depths()
            expected = before if tick <= 16 else tuple(max(0, depth - 1) for depth in before)
            verify(after == expected, f"Unexpected consumption at tick {tick}")
            duty_trace.append(read_sensa_pwm(cpu, registers))
            # Skip 8 refill opportunities. This drains all 16 slots,
            # exercises empty-buffer hold and then lets stock refill resume.
            if tick % 4 == 0 and not 40 < tick <= 72:
                stock_timer_irq(replay, CTIMER3, 0xB49C)
                verify(depths() == tuple(depth + 4 for depth in after), f"Unexpected refill at tick {tick}")
            if tick in (16, 40):
                verify(depths() == (16, 16), f"Expected steady buffer at tick {tick}")
            if tick in (56, 72):
                verify(depths() == (0, 0), "Suppressed refill did not drain the buffer")
            if tick in (76, 88, 112):
                verify(depths() == (4, 4), "Stock recovery did not stay at the lower refill depth")
        verify(len(set(duty_trace[55:72])) == 1, "Stock PWM did not retain its last duty on underrun")
        traces[side] = duty_trace

    deltas = []
    for side in (0, 1):
        trace = traces[side]
        idle = traces[None]
        verify(all(values[index] == baseline[index] for values, baseline in zip(trace, idle)
                   for index in range(4) if index != side), "Sensa buffer leaked to another output")
        verify(trace[:17] == idle[:17] and any(values[side] != baseline[side]
                                               for values, baseline in zip(trace[17:], idle[17:])),
               "Unexpected stock transient onset")
        verify(any(values[side] != trace[71][side] for values in trace[72:88]),
               "PWM output did not resume after the missed refills")
        deltas.append(max(abs(values[side] - baseline[side]) for values, baseline in zip(trace, idle)))
    return {"name": "stock_sensa_sample_buffer", "startup_depth_per_palm": 16,
            "samples_per_refill": 4, "first_consumption_us": 4250, "missed_refills": 8,
            "post_underrun_depth_after_refill": 4,
            "empty_buffer_retained_last_duty": True, "peak_match_count_deltas": deltas}


def check_dma_boundaries(image, dsp):
    """Exercise stock I2S queue admission and DMA0 interrupt dispatch."""
    replay = Replay(image, dsp)
    cpu = replay.cpu
    i2s, handle, pcm = 0x40097000, 0x20023000, 0x20024000
    statuses = []
    for index in range(5):
        replay.call(0x1278C, RETURN, i2s, handle, pcm + index * 192, 192)
        statuses.append(cpu.reg_read(UC_ARM_REG_R0))
    verify(statuses == [0, 0, 0, 0, 0xA8E], "Unexpected I2S queue admission status")
    expected = [(pcm + index * 192, 192) for index in range(4)]
    verify(list(struct.iter_unpack("<II", cpu.mem_read(handle + 0x14, 32))) == expected,
           "Full I2S queue changed an admitted transfer")
    verify(cpu.mem_read(handle + 0x34, 1)[0] == 0, "I2S queue did not wrap")

    dma, channel, callback = 0x40082000, 13, DSP + 0x800
    bit = 1 << channel
    cpu.mem_map(dma, 4096)
    # The callback is a fixture stub. The actual DMA IRQ vector and driver
    # inspect modeled status bits, acknowledge them, and supply the arguments.
    cpu.mem_write(callback, b"\x70\x47")  # bx lr
    cpu.mem_write(handle, struct.pack("<IIIB", callback | 1, pcm, dma, channel))
    cpu.mem_write(0x2000AB1C + 4 * channel, struct.pack("<I", handle))
    calls = []
    cpu.hook_add(UC_HOOK_CODE, lambda u, *_: calls.append(tuple(
        u.reg_read(reg) for reg in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3)
    )), begin=callback, end=callback)
    writes = []
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                 writes.append((address, value)), begin=dma, end=dma + 4095)
    for complete_a, complete_b, error, expected_calls in (
        (False, False, False, []),
        (True, False, False, [(1, 0)]),
        (False, True, False, [(1, 1)]),
        (False, False, True, [(0, 2)]),
    ):
        calls.clear()
        writes.clear()
        for offset, active in ((0x48, True), (0x58, complete_a), (0x60, complete_b), (0x40, error)):
            cpu.mem_write(dma + offset, struct.pack("<I", bit if active else 0))
        replay.call(0xC478, RETURN)
        verify(calls == [(handle, pcm, *args) for args in expected_calls],
               "Unexpected DMA callback arguments")
        verify(writes == [(dma + offset, bit) for offset, active in
                          ((0x58, complete_a), (0x60, complete_b), (0x40, error)) if active],
               "Unexpected DMA IRQ acknowledgements")
    return {"name": "stock_dma_boundaries", "queue_statuses": statuses,
            "irq_cases": ["no_pending", "complete_a", "complete_b", "error"]}


def check_stock_i2s_completion(image, dsp):
    """Dispatch DMA events through the stock I2S completion handler."""
    dma, channel, i2s = 0x40082000, 13, 0x40097000
    dma_handle, private, i2s_handle, pcm = 0x20023000, 0x20023100, 0x20023200, 0x20024000
    observer = DSP + 0x800
    report = []
    for events in (((192, False),), ((96, False), (96, False)),
                   ((192, True),), ((192, True), (192, False))):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        cpu.mem_map(dma, 4096)
        cpu.mem_write(observer, b"\x70\x47")
        cpu.mem_write(dma_handle, struct.pack("<IIIB", 0x12685, private, dma, channel))
        cpu.mem_write(0x2000AB1C + 4 * channel, struct.pack("<I", dma_handle))
        cpu.mem_write(private, struct.pack("<II", i2s, i2s_handle))
        # State 3 lets the active completion retire without starting another
        # transfer. The application callback is observed at the ABI boundary.
        cpu.mem_write(i2s_handle, struct.pack("<4I", 3, 0, observer | 1, pcm))
        cpu.mem_write(i2s_handle + 0x14, struct.pack("<II", pcm, 192))
        calls = []
        cpu.hook_add(UC_HOOK_CODE, lambda u, *_: calls.append(tuple(
            u.reg_read(reg) for reg in (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3)
        )), begin=observer, end=observer)
        remaining = 192
        for size, error in events:
            descriptor = cpu.mem_read(private + 0xC, 1)[0]
            cpu.mem_write(private + 8 + descriptor * 2, struct.pack("<H", size))
            cpu.mem_write(private + 0xE, b"\x01")
            for offset, enabled in ((0x48, True), (0x58, not error), (0x40, error)):
                cpu.mem_write(dma + offset, struct.pack("<I", (1 << channel) if enabled else 0))
            replay.call(0xC478, RETURN)
            remaining -= 0 if error else size
            pointer, count = struct.unpack("<II", cpu.mem_read(i2s_handle + 0x14, 8))
            verify(count == remaining and pointer == (pcm + 192 - remaining if remaining else 0),
                   "Stock I2S completion advanced the buffer incorrectly")
            verify(cpu.mem_read(i2s_handle + 0x35, 1)[0] == (0 if remaining else 1),
                   "Stock I2S completion advanced the queue incorrectly")
            verify(calls == ([] if remaining else [(i2s, i2s_handle, 0xA8C, pcm)]),
                   "Unexpected I2S application callback")
        report.append({"events": [{"descriptor_bytes": size, "dma_error": error}
                                  for size, error in events],
                       "remaining_bytes": remaining, "application_callbacks": len(calls)})

    # Follow the real application callback through to its next DMA call.
    cpu.mem_write(i2s_handle + 8, struct.pack("<I", TX | 1))
    payload = tone(2, ((80,), (190,)))
    for queue_index, (streaming, expected_address) in enumerate(((False, ZERO), (True, USB_RING)), 1):
        cpu.mem_write(i2s_handle + 0x14 + queue_index * 8, struct.pack("<II", pcm, 192))
        descriptor = cpu.mem_read(private + 0xC, 1)[0]
        cpu.mem_write(private + 8 + descriptor * 2, struct.pack("<H", 192))
        cpu.mem_write(private + 0xE, b"\x01")
        cpu.mem_write(dma + 0x58, struct.pack("<I", 1 << channel))
        cpu.mem_write(0x20000A59, bytes([streaming]))
        if streaming:
            replay.receive_usb(bytes((0x60, 0x20, 1, 0xC0, 0x81, 0)) + payload)
            cpu.mem_write(0x20008F92, b"\x01")
        replay.call(0xC478, DMA_SUBMIT)
        verify(tuple(cpu.reg_read(reg) for reg in
                     (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3)) ==
               (i2s, i2s_handle, expected_address, 192), "Unexpected buffer submitted by stock TX")
        verify(bytes(cpu.mem_read(expected_address, 192)) == (payload if streaming else bytes(192)),
               "Stock completion changed the submitted PCM")
        verify(cpu.mem_read(i2s_handle + 0x35, 1)[0] == queue_index + 1, "Incorrect retired slot")
    return {"name": "stock_i2s_completion", "cases": report, "rearmed_with": ["silence", "usb_pcm"]}


def model_syscon_reset(cpu):
    syscon = 0x40000000
    cpu.mem_map(syscon, 4096)

    def reset_register_alias(machine, access, address, size, value, data):
        target = syscon + 0x100 + (address & 0xF)
        old, = struct.unpack("<I", machine.mem_read(target, 4))
        new = old | value if address < syscon + 0x140 else old & ~value
        machine.mem_write(target, struct.pack("<I", new))

    cpu.hook_add(UC_HOOK_MEM_WRITE, reset_register_alias, begin=syscon + 0x120, end=syscon + 0x14B)


def model_async_syscon_reset(cpu):
    status = 0x40040000

    def async_reset_alias(machine, access, address, size, value, data):
        old, = struct.unpack("<I", machine.mem_read(status, 4))
        new = old | value if address == status + 4 else old & ~value
        machine.mem_write(status, struct.pack("<I", new))

    cpu.hook_add(UC_HOOK_MEM_WRITE, async_reset_alias, begin=status + 4, end=status + 8)


def configure_stock_dma(replay):
    cpu = replay.cpu
    dma, tx, rx = 0x40082000, 0x40097000, 0x40098000
    for page in (dma, tx, rx):
        cpu.mem_map(page, 4096)
    cpu.mem_write(dma + 8, struct.pack("<I", 0x2000B000))
    replay.call(0x13D6C, RETURN)


def initialize_stock_dma(image, dsp):
    replay = Replay(image, dsp)
    replay.cpu.mem_map(0xE000E000, 4096)
    model_syscon_reset(replay.cpu)
    configure_stock_dma(replay)
    return replay


def check_stock_dma_descriptor(image, dsp):
    """Execute the initial descriptors produced by the stock I2S setup."""
    replay = initialize_stock_dma(image, dsp)
    cpu = replay.cpu
    dma, tx = 0x40082000, 0x40097000

    channel = 13
    table, = struct.unpack("<I", cpu.mem_read(dma + 8, 4))
    descriptor = table + channel * 16
    flags, source_end, destination_end, link = struct.unpack("<4I", cpu.mem_read(descriptor, 16))
    channel_config, = struct.unpack("<I", cpu.mem_read(dma + 0x408 + channel * 16, 4))
    verify(flags == channel_config & ~4 and flags & 1 and flags & 2 and flags & 0x30 == 0x20,
           "Unsupported initial DMA transfer or interrupt flags")
    width = 1 << ((flags >> 8) & 3)
    transfers = ((flags >> 16) & 0x3FF) + 1
    verify(width == 4 and transfers == 48 and flags >> 12 & 0xF == 1,
           "Unexpected I2S transfer width, count or increment")
    verify(destination_end == tx + 0xE20 and link and link % 16 == 0,
           "Unexpected DMA FIFO destination or link")
    source_start = source_end - (transfers - 1) * width
    verify(source_start == ZERO, "Initial descriptor did not select the startup zero block")

    # Load a distinctive fixture in the already-addressed buffer to expose
    # incorrect strides or a reversed transfer. The firmware descriptor is untouched.
    payload = tone(2, ((80,), (190,)))
    # DMA reloads the next descriptor before dispatching completion. Snapshot
    # it first, because the stock driver can reuse descriptors in its callback.
    linked = struct.unpack("<4I", cpu.mem_read(link, 16))
    linked_flags, linked_source_end, linked_destination, next_link = linked
    verify(linked_flags & ~0x30 == flags & ~0x30 and linked_flags & 0x30 == 0x10,
           "Unsupported linked DMA transfer")
    verify(linked_destination == destination_end and next_link % 16 == 0 and next_link != link,
           "Invalid DMA reload target")
    second_start = linked_source_end - (transfers - 1) * width
    verify(second_start == ZERO, "Linked startup descriptor did not select silence")
    completions = []
    candidate = Replay(image, dsp)
    candidate_pcm = 0x20024000
    enabled_strengths = []
    for index, (active_flags, start, expected) in enumerate(
            ((flags, source_start, payload), (linked_flags, second_start, bytes(192)))):
        if index == 0:
            cpu.mem_write(start, payload)
        fifo = b"".join(bytes(cpu.mem_read(start + word * width, width)) for word in range(transfers))
        verify(fifo == expected, "DMA FIFO order changed after reload")
        if index == 0:
            candidate.cpu.mem_write(candidate_pcm, fifo)
            candidate.call(DSP, RETURN, STATE, candidate_pcm, 1, LEVELS)
            enabled_strengths = list(candidate.cpu.mem_read(LEVELS, 4))
            verify(any(enabled_strengths[:2]) and any(enabled_strengths[2:]),
                   "Candidate did not generate stereo haptics from DMA PCM")
            candidate.call(DSP, RETURN, STATE, candidate_pcm, 0, LEVELS)
            verify(not any(candidate.cpu.mem_read(LEVELS, 4)), "Candidate disable left active levels")
            verify(bytes(candidate.cpu.mem_read(candidate_pcm, 192)) == fifo,
                   "Candidate changed the stock PCM")
        cpu.mem_write(start, bytes(192))
        status = 0x60 if active_flags & 0x20 else 0x58
        for offset in (0x48, 0x58, 0x60):
            cpu.mem_write(dma + offset, struct.pack("<I", (1 << channel) if offset in (0x48, status) else 0))
        cpu.ctl_flush_tb()
        replay.call(0xC478, DMA_SUBMIT)
        verify(cpu.reg_read(UC_ARM_REG_R2) == ZERO and cpu.reg_read(UC_ARM_REG_R3) == 192,
               "Startup DMA completion did not re-arm silence")
        verify(not any(cpu.mem_read(OUT, 4 * 0x82)), "Stock audio DMA changed Sensa state")
        completions.append("INTB" if status == 0x60 else "INTA")
        if index == 0:
            # Finish the first submit and return through the suspended ISR.
            cpu.ctl_flush_tb()
            cpu.emu_start(DMA_SUBMIT | 1, RETURN, count=20000)
            verify(cpu.reg_read(UC_ARM_REG_PC) == RETURN, "Stock resubmission did not return")
    return {"name": "stock_generated_dma_descriptor", "channel": channel,
            "descriptor": hex(descriptor), "flags": hex(flags), "linked_descriptor": hex(link),
            "fifo_bytes": 2 * len(fifo), "completions": completions, "rearmed_with": "silence",
            "candidate_enabled_strengths": enabled_strengths}


def check_continuous_dma(image, dsp, blocks, arrival_ticks=None, replay=None, on_block=None, tail_ticks=25):
    """Run the stock USB producer and generated DMA descriptor ring together."""
    if replay is None:
        replay = initialize_stock_dma(image, dsp)
    cpu = replay.cpu
    dma, channel_bit = 0x40082000, 1 << 13
    table, = struct.unpack("<I", cpu.mem_read(dma + 8, 4))
    address = table + 13 * 16
    active = struct.unpack("<4I", cpu.mem_read(address, 16))
    cpu.mem_write(0x20000A59, b"\x01")
    played, first_pcm_tick = 0, None
    addresses = set()
    arrivals = list(range(len(blocks))) if arrival_ticks is None else arrival_ticks
    verify(bool(arrivals) and len(arrivals) == len(blocks) and arrivals[0] >= 0 and
           arrivals == sorted(arrivals), "Invalid USB arrival schedule")
    received = 0
    silence_gaps = []
    last_pcm_tick = None
    tail_silence = False
    for tick in range(arrivals[-1] + tail_ticks):
        while received < len(blocks) and arrivals[received] == tick:
            header = bytes((0x60, 0x20, received % 255 + 1, 0xC0, 0x81, 0))
            replay.receive_usb(header + blocks[received])
            received += 1
        flags, source_end, destination, link = active
        verify(flags in (0x2F1223, 0x2F1213) and destination == 0x40097E20,
               f"Unsupported TX descriptor at tick {tick}")
        verify(link and link % 16 == 0 and link != address, "Invalid TX descriptor link")
        addresses.add(address)
        source = source_end - 188
        fifo = b"".join(bytes(cpu.mem_read(source + offset, 4)) for offset in range(0, 192, 4))
        if source == ZERO:
            verify(fifo == bytes(192), "Startup or underrun PCM was not silent")
            tail_silence = played == len(blocks)
        else:
            if first_pcm_tick is None:
                first_pcm_tick = tick
            if last_pcm_tick is not None and tick > last_pcm_tick + 1:
                silence_gaps.append(tick - last_pcm_tick - 1)
            last_pcm_tick = tick
            verify(played < received, "DMA emitted unexpected PCM")
            verify(source == USB_RING + (played * 192) % 6144, "DMA selected an out-of-order slot")
            verify(fifo == blocks[played], "DMA corrupted or reordered PCM")
            played += 1

        address = link
        active = struct.unpack("<4I", cpu.mem_read(address, 16))
        irq_status = 0x60 if flags & 0x20 else 0x58
        for offset in (0x48, 0x58, 0x60):
            cpu.mem_write(dma + offset, struct.pack("<I", channel_bit if offset in (0x48, irq_status) else 0))
        cpu.ctl_flush_tb()
        replay.call(0xC478, RETURN)
        if on_block is not None:
            on_block(tick, source, fifo)

    verify(played == len(blocks) and tail_silence, "DMA queue did not finish and return to silence")
    verify(len(addresses) == 3, "Unexpected descriptor reuse")
    if arrival_ticks is None:
        verify(first_pcm_tick == 18 and not silence_gaps, "Unexpected startup or gap in steady replay")
    return {"name": "stock_continuous_dma", "blocks_played": played, "pcm_bytes": played * 192,
            "first_pcm_fixture_tick": first_pcm_tick, "descriptor_addresses": len(addresses),
            "silence_gaps_ticks": silence_gaps, "underrun_selected_silence": tail_silence}


def check_dma_arrival_variation(image, dsp):
    blocks = [tone(tick, ((83,), (197,))) for tick in range(128)]
    burst = [index // 4 * 4 for index in range(len(blocks))]
    report = []
    for pause in (0, 8, 24, 48):
        arrivals = [tick + (pause if index >= 64 else 0) for index, tick in enumerate(burst)]
        result = check_continuous_dma(image, dsp, blocks, arrivals)
        gaps = result["silence_gaps_ticks"]
        verify(gaps == ([pause] if pause >= 24 else []), f"Unexpected FIFO gap for pause {pause}: {gaps}")
        report.append({"usb_pause_ticks": pause, **{key: value for key, value in result.items() if key != "name"}})
    return {"name": "stock_dma_arrival_variation", "cases": report}


def redirect_simulated_branch(cpu, address, target, original, link=False):
    verify(bytes(cpu.mem_read(address, 4)) == bytes.fromhex(original), "Unexpected stock Thumb branch")
    displacement = target - (address + 4)
    verify(0 <= displacement < 1 << 22 and displacement % 2 == 0, "Unsupported Thumb branch displacement")
    second = 0xF800 if link else 0xB800
    cpu.mem_write(address, struct.pack("<HH", 0xF000 | displacement >> 12,
                                       second | (displacement >> 1 & 0x7FF)))
    cpu.ctl_flush_tb()


def install_simulated_tx_tap(replay):
    # Stock B.W 0x12F9C. Redirect only in Unicorn; the trampoline resumes
    # the original target with the original arguments and LR.
    redirect_simulated_branch(replay.cpu, TX_TAIL, TAP, "eff71ebf")


def check_application_reset(image, dsp):
    """Start at the application vector with dirty RAM, including candidate state."""
    ram, bss_end = 0x20000000, 0x20013D28
    stack, entry = struct.unpack_from("<II", image)
    verify((stack, entry) == (0x20027FF8, 0x8415), "Unexpected application reset vector")
    regions = ((STATE, 32), (LEVELS, 4), (TAP_BUFFER, 204))
    stock_initialized = None
    for candidate in (False, True):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        cpu.mem_map(0xE000E000, 4096)
        model_syscon_reset(cpu)
        # Use both patterns on the same machine instance to test a second reset
        # after RAM and MMIO have already been used by the application.
        if candidate:
            verify(bytes(cpu.mem_read(BOOT_HANDOFF, 4)) == struct.pack("<I", STOCK_MAIN | 1),
                   "Unexpected original scatter-to-main pointer")
            cpu.mem_write(BOOT_HANDOFF, struct.pack("<I", CANDIDATE_BOOT | 1))
        cpu.mem_protect(0x8000, 0x30000, UC_PROT_READ | UC_PROT_EXEC)
        for dirty in (b"\xA5", b"\x5A"):
            cpu.mem_write(ram, dirty * 0x28000)
            cpu.reg_write(UC_ARM_REG_SP, stack)
            cpu.emu_start(entry, STOCK_MAIN, count=200000)
            verify(cpu.reg_read(UC_ARM_REG_PC) == STOCK_MAIN, "Stock application entry was not reached")
            verify(cpu.reg_read(UC_ARM_REG_SP) == stack, "Reset changed the starting application stack")
            verify(read_sensa_pwm(cpu, (0xE000ED08, 0xE000ED88)) == (0x8000, 0xF00000),
                   "Reset did not set VTOR and CPACR")
            verify(bytes(cpu.mem_read(ram + 0xA00, bss_end - ram - 0xA00)) == bytes(bss_end - ram - 0xA00),
                   "Stock zero-init left dirty BSS")
            initialized = bytes(cpu.mem_read(ram, 0xA00))
            if not candidate and dirty == b"\xA5":
                stock_initialized = initialized
            verify(initialized == stock_initialized and b"XGIP10" in initialized,
                   "Reset did not preserve stock initialized data")
            before = bss_end
            for address, size in regions:
                verify(bytes(cpu.mem_read(before, address - before)) == dirty * (address - before),
                       "Reset clobbered RAM outside its allocation")
                expected = bytes(size) if candidate else dirty * size
                verify(bytes(cpu.mem_read(address, size)) == expected, "Candidate state was not reset as expected")
                before = address + size
            verify(bytes(cpu.mem_read(before, stack - before - 16)) == dirty * (stack - before - 16),
                   "Reset clobbered the unused RAM before the stack")
    return {"name": "application_reset_scatter", "entry": hex(entry), "main": hex(STOCK_MAIN),
            "rw_bytes": 0xA00, "bss_bytes": bss_end - ram - 0xA00,
            "candidate_cleared_bytes": sum(size for _, size in regions), "runs": 4}


def check_board_startup_boundary(image, dsp, respond_to_i2c=False):
    """Advance stock timeout handlers while board startup waits for peripherals."""
    ready_registers = (0x40020054, 0x40000584, 0x40000520)
    guards = (STATE, LEVELS, TAP_ENABLE, TAP_READY)
    wait_flags = {0x2B428: 0x20000F64, 0x2A610: 0x2000909C}
    results = {}
    for candidate in (False, True):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        model_syscon_reset(cpu)
        for page in (0xE000E000, 0x40001000, 0x40003000, 0x40004000, 0x40005000, 0x4000B000,
                     0x4000C000, 0x4000D000, 0x40014000, 0x40020000, CTIMER2, 0x40040000,
                     CTIMER3, 0x40082000, 0x40089000, 0x4008C000, 0x4008E000, 0x40097000,
                     0x40098000, 0x40099000, 0x400A0000):
            cpu.mem_map(page, 4096)
        model_async_syscon_reset(cpu)
        if candidate:
            cpu.mem_write(BOOT_HANDOFF, struct.pack("<I", CANDIDATE_BOOT | 1))
            install_simulated_tx_tap(replay)
            redirect_simulated_branch(cpu, MAIN_DISPATCH_CALL, FOREGROUND, "fbf79dff", link=True)
        cpu.mem_protect(0x8000, 0x30000, UC_PROT_READ | UC_PROT_EXEC)
        cpu.mem_write(0x20000000, b"\xA5" * 0x28000)
        # Bootloader-to-application shared word, deliberately unset in this
        # board fixture. The actual resident bootloader is unavailable.
        cpu.mem_write(0x20027FF8, bytes(8))
        for address, value in zip(ready_registers, (0x220, 1, 1)):
            cpu.mem_write(address, struct.pack("<I", value))
        stack, entry = struct.unpack_from("<II", image)
        cpu.reg_write(UC_ARM_REG_SP, stack)
        writes = []
        lowest_stack_write = stack

        def track_stack(machine, access, address, size, value, data):
            nonlocal lowest_stack_write
            lowest_stack_write = min(address, lowest_stack_write)

        cpu.hook_add(UC_HOOK_MEM_WRITE, track_stack, begin=TAP_BUFFER + 204, end=stack - 1)
        cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                     writes.append((address, size, value)), begin=0x40000000, end=0x400FFFFF)

        def stop_at_wait(machine, address, size, data):
            if not respond_to_i2c and machine.mem_read(wait_flags[address], 1)[0]:
                machine.emu_stop()

        for wait in wait_flags:
            cpu.hook_add(UC_HOOK_CODE, stop_at_wait, begin=wait, end=wait)
        irq_vector = read_sensa_pwm(cpu, (0x8000 + (16 + 9) * 4,))[0]
        verify(irq_vector & 1, "MRT0 vector is not a Thumb entry")
        stack_at_wait = None

        def run_vector(vector):
            cpu.reg_write(UC_ARM_REG_SP, stack_at_wait)
            cpu.reg_write(UC_ARM_REG_LR, RETURN | 1)
            cpu.emu_start(vector, RETURN, count=20000)
            verify(cpu.reg_read(UC_ARM_REG_PC) == RETURN, "Startup interrupt handler did not return")

        interrupts = []
        expected_waits = (0x2B428,) if respond_to_i2c else (0x2B428, 0x2B428, 0x2B428, 0x2A610, 0x2A610)
        for expected_wait in expected_waits:
            cpu.emu_start(entry, expected_wait if respond_to_i2c else 0x2DCC0, count=20_000_000)
            wait = cpu.reg_read(UC_ARM_REG_PC)
            verify(wait == expected_wait, f"Unexpected board startup checkpoint 0x{wait:X}")
            entry = wait | 1
            flag = wait_flags[wait]
            verify(cpu.mem_read(flag, 1)[0] == 10, "Unexpected stock timeout counter")
            foreground = cpu.context_save()
            # Reserve the basic exception frame and align the stack. This
            # enters the vector stub but does not emulate NVIC exception entry.
            stack_at_wait = (cpu.reg_read(UC_ARM_REG_SP) - 32) & ~7
            if respond_to_i2c:
                device_byte, commands = respond_to_startup_i2c(cpu, run_vector, foreground)
                break
            ticks = 0
            while cpu.mem_read(flag, 1)[0] and ticks < 80:
                run_vector(irq_vector)
                ticks += 1
            verify(ticks == 80 and not cpu.mem_read(flag, 1)[0], "Stock MRT0 did not expire the transfer")
            cpu.context_restore(foreground)
            cpu.ctl_flush_tb()
            interrupts.append(ticks)
        timer_setup = read_sensa_pwm(cpu, (CTIMER2 + 4, CTIMER2 + 0x18, CTIMER3 + 4, CTIMER3 + 0x18))
        verify(timer_setup == (1, 23999, 1, 95999), "Unexpected board startup timer setup")
        expected_guard = 0 if candidate else 0xA5A5A5A5
        verify(read_sensa_pwm(cpu, guards) == (expected_guard,) * len(guards),
               "Stock board startup modified candidate guard words")
        results[candidate] = (bytes(cpu.mem_read(0x20000000, 0x13D28)), cpu.reg_read(UC_ARM_REG_SP),
                              tuple(writes), lowest_stack_write)
    verify(results[False] == results[True], "Candidate changed stock board startup RAM, stack or MMIO writes")
    if respond_to_i2c:
        return {"name": "stock_board_i2c_reply", "stopped_at": hex(cpu.reg_read(UC_ARM_REG_PC)),
                "synthetic_device_byte": device_byte, "master_commands": commands,
                "stock_mmio_writes_matched": len(writes)}
    return {"name": "stock_board_startup_boundary", "stopped_at": hex(wait),
            "stock_ram_compared_bytes": 0x13D28, "timer_mr0": [23999, 95999],
            "stock_mmio_writes_matched": len(writes), "lowest_observed_stack_write": hex(lowest_stack_write),
            "timeouts_advanced_by_stock_mrt_irq": interrupts}


def respond_to_startup_i2c(cpu, run_vector, foreground):
    """Replay one synthetic register read at the FLEXCOMM3 I2C boundary."""
    stat, control, data = 0x40089804, 0x40089820, 0x40089828
    vector = read_sensa_pwm(cpu, (0x8000 + (16 + 17) * 4,))[0]
    commands = []
    # MSTPENDING plus idle (1), transmit-ready (5), or receive-ready (3).
    # The synthetic identity byte is chosen to take the stock match branch.
    for statuses, resume_at, expected_end in (((1, 5, 5), 0x2B428, 0x2B460),
                                               ((1, 3, 1), 0x2B460, 0x132CC)):
        for status in statuses:
            cpu.mem_write(stat, struct.pack("<I", status))
            if status == 3:
                cpu.mem_write(data, struct.pack("<I", 0x53))
            run_vector(vector)
            commands.append(read_sensa_pwm(cpu, (control, data)))
        verify(cpu.mem_read(0x200093F1, 1)[0] == 0, "I2C interrupt did not complete the phase")
        cpu.context_restore(foreground)
        cpu.mem_write(stat, struct.pack("<I", 1))
        cpu.ctl_flush_tb()
        cpu.emu_start(resume_at | 1, expected_end, count=200000)
        pc = cpu.reg_read(UC_ARM_REG_PC)
        verify(pc == expected_end, f"Stock code did not accept I2C completion: PC=0x{pc:X}")
        foreground = cpu.context_save()
    expected = [(2, 0xD6), (1, 0x0B), (1, 0x0B), (2, 0xD7), (4, 0x53), (4, 0x53)]
    verify(commands == expected, "Unexpected stock I2C address, register or command sequence")
    reply = cpu.mem_read(0x20000ED5, 1)[0]
    verify(reply == 0x53 and cpu.reg_read(UC_ARM_REG_R0) == 1, "Stock caller missed the I2C reply")
    return reply, commands


def check_foreground_worker(image, dsp):
    replay, _ = initialize_stock_sensa_pwm(image, dsp)
    cpu = replay.cpu
    redirect_simulated_branch(cpu, MAIN_DISPATCH_CALL, FOREGROUND, "fbf79dff", link=True)
    pcm = tone(0, ((83,), ()))
    frame = xbox_frame([([], []), ([], [(35, 24, 0)]), ([], []), ([], [])])
    trace = []
    cpu.hook_add(UC_HOOK_CODE, lambda _, address, size, data: trace.append(address),
                 begin=WORKER, end=WORKER)
    cpu.hook_add(UC_HOOK_CODE, lambda _, address, size, data: trace.append(address),
                 begin=0x2B538, end=0x2B538)
    for ready, game_pending, run_worker in ((0, True, False), (1, True, False), (1, False, True)):
        cpu.mem_write(TAP_BUFFER, pcm + struct.pack("<3I", 1, ready, USB_RING))
        cpu.mem_write(0x20000B34, frame + bytes(64))
        cpu.mem_write(0x20000B2C, bytes([game_pending]))
        trace.clear()
        replay.call(MAIN_DISPATCH_CALL, MAIN_DISPATCH_CALL + 4)
        verify(trace == ([WORKER, 0x2B538] if run_worker else [0x2B538]), "Foreground worker order changed")
        verify(cpu.mem_read(0x20000B2C, 1) == bytes(1), "Foreground hook lost the stock pending frame")
        verify(cpu.mem_read(TAP_READY, 4) == struct.pack("<I", ready and not run_worker),
               "Foreground hook did not preserve or release the mailbox as expected")
        verify(not any(cpu.mem_read(LEVELS + 2, 2)), "Foreground worker leaked into the stock frame's palm")
        if run_worker:
            verify(any(cpu.mem_read(LEVELS, 2)), "Foreground worker did not process the published PCM")
    return {"name": "foreground_arm_worker_callsite", "site": hex(MAIN_DISPATCH_CALL),
            "cases": 3, "stock_dispatcher_runs_once": True, "pending_game_frame_has_priority": True}


def check_tx_tap_contract(image, dsp):
    replay = Replay(image, dsp)
    cpu = replay.cpu
    install_simulated_tx_tap(replay)
    source = 0x20023000
    payload = bytes(range(192))
    cpu.mem_write(source, payload)
    sentinel = b"\xA5" * 192
    registers = (UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_R4,
                 UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_APSR)
    for enabled, ready, size in ((False, 0, 192), (True, 0, 191), (True, 1, 192), (True, 0, 192)):
        accepted = enabled and not ready and size == 192
        cpu.mem_write(TAP_BUFFER, sentinel + struct.pack("<3I", enabled, ready, 0))
        values = (0x40097000, 0x20001234, source, size, 0x12345678, 0x20027000, RETURN | 1, 0x98000000)
        for register, value in zip(registers, values):
            cpu.reg_write(register, value)
        cpu.emu_start(TX_TAIL | 1, DMA_SUBMIT, count=1000)
        verify(cpu.reg_read(UC_ARM_REG_PC) == DMA_SUBMIT, "Trampoline did not resume stock enqueue")
        verify(tuple(cpu.reg_read(register) for register in registers) == values,
               "Trampoline changed stock register arguments, stack, return or flags")
        expected = (payload if accepted else sentinel) + struct.pack("<3I", enabled, ready or accepted,
                                                                    source if accepted else 0)
        verify(bytes(cpu.mem_read(TAP_BUFFER, len(expected))) == expected, "Incorrect TX mailbox behavior")
        verify(bytes(cpu.mem_read(source, 192)) == payload, "Trampoline modified its source")
    return {"name": "tx_tap_arm_contract", "cases": 4, "stock_enqueue_preserved": True}


def check_worker_interleaving(image, dsp):
    """Enter the producer during the worker's read and after mailbox release."""
    replay = Replay(image, dsp)
    cpu = replay.cpu
    install_simulated_tx_tap(replay)
    first_source, next_source = 0x20023000, 0x20023400
    first = tone(0, ((83,), ()))
    following = tone(0, ((), (190,)))
    cpu.mem_write(first_source, first)
    cpu.mem_write(next_source, following)
    cpu.mem_write(TAP_BUFFER, first + struct.pack("<3I", 1, 1, first_source))
    cpu.reg_write(UC_ARM_REG_SP, 0x20027000)
    cpu.reg_write(UC_ARM_REG_LR, RETURN | 1)
    cpu.emu_start(WORKER | 1, DSP, count=20000)
    verify(cpu.reg_read(UC_ARM_REG_PC) == DSP, "Worker did not reach its active DSP call")
    worker = cpu.context_save()
    # Run the producer with a separate stack, as if the worker were
    # interrupted immediately before entering audio_haptics_step.
    cpu.reg_write(UC_ARM_REG_SP, 0x20026000)
    cpu.reg_write(UC_ARM_REG_R2, next_source)
    cpu.reg_write(UC_ARM_REG_R3, len(following))
    cpu.emu_start(TX_TAIL | 1, DMA_SUBMIT, count=1000)
    verify(bytes(cpu.mem_read(TAP_BUFFER, 192)) == first, "Producer overwrote the in-flight worker block")
    cpu.context_restore(worker)
    cpu.ctl_flush_tb()
    cpu.emu_start(DSP | 1, RETURN, count=20000)
    pc = cpu.reg_read(UC_ARM_REG_PC)
    verify(pc == RETURN, f"Interrupted worker did not return (PC=0x{pc:X})")
    left_before = bytes(cpu.mem_read(LEVELS, 4))
    verify(any(left_before[:2]) and not any(left_before[2:]), "Worker processed the wrong interrupted block")
    verify(cpu.mem_read(TAP_READY, 4) == bytes(4), "Interrupted worker did not release the mailbox")
    cpu.reg_write(UC_ARM_REG_R2, next_source)
    cpu.reg_write(UC_ARM_REG_R3, len(following))
    cpu.emu_start(TX_TAIL | 1, DMA_SUBMIT, count=1000)
    verify(bytes(cpu.mem_read(TAP_BUFFER, 192)) == following, "Producer did not recover after release")
    replay.call(WORKER, RETURN)
    verify(any(cpu.mem_read(LEVELS + 2, 2)), "Worker missed the next block")
    replay.call(WORKER, RETURN)
    verify(not any(cpu.mem_read(LEVELS, 4)), "No fresh block did not reset worker output")
    return {"name": "tx_worker_mailbox_interleaving", "busy_block_preserved": True,
            "following_block_processed": True, "missing_block_cleared": True}


def check_pcm_to_scheduled_pwm(image, dsp, blocks, active_side=None, disable_tick=104):
    """Exercise the TX submission tap and co-schedule DMA and palm rendering."""
    traces = {}
    for enabled in (False, True):
        replay, registers = initialize_stock_sensa_pwm(image, dsp)
        initialize_stock_sensa_timers(replay)
        configure_stock_dma(replay)
        initialize_sensa_gain_tables(replay, 100)
        cpu = replay.cpu
        if enabled:
            install_simulated_tx_tap(replay)
            redirect_simulated_branch(cpu, MAIN_DISPATCH_CALL, FOREGROUND, "fbf79dff", link=True)
        for address, value in ((0x2000077C, 1), (0x20004520, 2), (0x200044ED, 1)):
            cpu.mem_write(address, bytes([value]))
        submitted = False
        pwm = []
        peak_instructions = 0
        submitted_ticks = []
        captured_blocks = 0

        def submit_frame(bodies):
            cpu.mem_write(0x20000B34, xbox_frame(bodies) + bytes(64))
            cpu.mem_write(0x20000B2C, b"\x01")
            replay.call(MAIN_DISPATCH_CALL, MAIN_DISPATCH_CALL + 4)
            verify(cpu.mem_read(0x20000B2C, 1) == b"\x00", "Stock Sensa dispatcher stayed pending")

        def consume_tap(tick):
            nonlocal submitted, peak_instructions, captured_blocks
            ready, source = struct.unpack("<2I", cpu.mem_read(TAP_READY, 8))
            # The controller starts disabled. Enable the tap after the stock
            # priming window. The disable and source-loss tests leave
            # different amounts of PCM playing when the switch is turned off.
            verify(bool(ready) == (48 <= tick < disable_tick), "TX tap did not follow its runtime switch")
            pcm = bytes(cpu.mem_read(TAP_BUFFER, 192))
            active = bool(ready) and source != ZERO
            if active:
                # DMA plays the first PCM at tick 18; stock TX starts
                # submitting PCM two descriptor iterations earlier.
                index = tick - 16
                verify(source == USB_RING + (index * 192) % 6144 and pcm == blocks[index],
                       f"TX tap copied unexpected PCM at tick {tick}")
                verify(bytes(cpu.mem_read(source, len(pcm))) == pcm, "Tap changed its source")
                captured_blocks += 1
            elif ready:
                verify(pcm == bytes(192), "Underrun did not publish silent PCM")
            replay.steps = 0
            # The no-packet path in the foreground wrapper does no DSP
            # work. The host clears its simulated idle/disabled state.
            if ready:
                replay.call(MAIN_DISPATCH_CALL, MAIN_DISPATCH_CALL + 4)
            else:
                replay.call(WORKER, RETURN)
            verify(cpu.mem_read(TAP_READY, 4) == bytes(4), "Worker did not release the TX mailbox")
            peak_instructions = max(peak_instructions, replay.steps)
            levels = bytes(cpu.mem_read(LEVELS, 4))
            verify(not any(levels) or active, "Inactive DSP retained output")
            if tick % 12 == 0 and (submitted or any(levels)):
                bodies = [([(a, a, pitch) for a, pitch in zip(levels[i:i + 2], (17, 55)) if a], [])
                          for i in (0, 2)] + [([], []), ([], [])]
                submit_frame(bodies)
                submitted = any(levels)
                submitted_ticks.append(tick)

        def process_block(tick, played_source, played_pcm):
            # The game owns Sensa during this window. Audio frame submissions
            # are excluded by the fixture and checked against the stock run.
            if tick == 12:
                submit_frame([([], [(35, 24, 0)]), ([], []), ([], []), ([], [])])
            if tick == 36:
                submit_frame([([], []) for _ in range(4)])
            if enabled:
                consume_tap(tick)
                if tick in (47, disable_tick - 1):
                    cpu.mem_write(TAP_ENABLE, struct.pack("<I", tick == 47))
            for _ in range(4):
                stock_timer_irq(replay, CTIMER2, 0xB438)
                pwm.append(read_sensa_pwm(cpu, registers))
            stock_timer_irq(replay, CTIMER3, 0xB49C)
            verify(bytes(cpu.mem_read(played_source, len(played_pcm))) == played_pcm,
                   "DSP changed the DMA playback buffer")

        dma = check_continuous_dma(image, dsp, blocks, replay=replay, on_block=process_block, tail_ticks=64)
        expected_blocks = min(disable_tick, len(blocks) + 16) - 48 if enabled else 0
        verify(captured_blocks == expected_blocks and cpu.mem_read(TAP_READY, 4) == bytes(4),
               "Incomplete stock TX tap trace")
        traces[enabled] = pwm, dma, submitted_ticks
    stock, stock_dma, stock_submissions = traces[False]
    mixed, mixed_dma, candidate_submissions = traces[True]
    verify(stock_dma == mixed_dma, "Candidate changed stock DMA delivery")
    verify(not stock_submissions and candidate_submissions and min(candidate_submissions) >= 48,
           "Candidate wrote a frame during the stock-only interval")
    verify(stock[:4 * 48] == mixed[:4 * 48] and any(row[0] != stock[0][0] for row in stock[48:144]),
           "Game frame changed during the stock-only interval or never reached PWM")
    verify(stock[-48:] == mixed[-48:], "Candidate duty did not settle after disable/underrun")
    if disable_tick > len(blocks) + 16:
        stopped_at = len(blocks) + 16
        verify(stopped_at <= candidate_submissions[-1] < disable_tick,
               "Source loss did not clear the candidate while still enabled")
    peaks = [max(abs(duty[channel] - reference[channel]) for duty, reference in zip(mixed, stock))
             for channel in range(4)]
    expected = (0, 1) if active_side is None else (active_side,)
    verify(tuple(channel for channel, peak in enumerate(peaks) if peak) == expected,
           f"Unexpected active PWM channels: {peaks}")
    return {"dma_blocks": stock_dma["blocks_played"], "tx_tap_blocks": captured_blocks,
            "pcm_bytes": stock_dma["pcm_bytes"],
            "max_worker_instructions": peak_instructions, "peak_delta_per_output": peaks,
            "stock_game_window_preserved": True, "candidate_submit_ticks": candidate_submissions,
            "returned_to_stock_pwm": True}


def check_scheduled_dsp(image, dsp):
    cases = []
    for side in (0, 1, None):
        frequencies = ((), (190,)) if side == 1 else ((83,), () if side == 0 else (190,))
        blocks = [tone(tick, frequencies) for tick in range(128)]
        cases.append({"input": ("left", "right")[side] if side is not None else "stereo",
                      **check_pcm_to_scheduled_pwm(image, dsp, blocks, side)})
    cases.append({"input": "stereo_source_loss",
                  **check_pcm_to_scheduled_pwm(image, dsp, blocks, disable_tick=168)})
    return {"name": "candidate_scheduled_pcm_to_pwm", "cases": cases}


def initialize_sensa_gain_tables(replay, gain_percent):
    # Four profiles of 11 frequency/gain knots in the calibration input area.
    # Supply controlled profiles, then let the stock routine expand all tables.
    cpu = replay.cpu
    for channel in range(4):
        base = 0x200093FB + 0x2C * channel
        cpu.mem_write(base + 4, struct.pack("<11H", *range(0, 501, 50)))
        cpu.mem_write(base + 0x1A, struct.pack("<11H", *([gain_percent] * 11)))
    replay.call(0x10088, RETURN, 0, instruction_limit=500000)
    for channel in range(4):
        table = struct.unpack("<501f", cpu.mem_read(0x20002404 + 0x7D4 * channel, 501 * 4))
        verify(all(abs(value - gain_percent / 100) < 1e-6 for value in table),
               "Stock gain initializer produced an unexpected table")


def check_stock_profile_calibration(image, dsp):
    """Run the stock profile conversion and frequency-table refresh wrapper."""
    replay = Replay(image, dsp)
    cpu = replay.cpu
    replay.call(0x8EBC, RETURN, 0x37200, 0x20000000, 0xA00)
    cpu.mem_map(0xE000E000, 4096)
    cpu.mem_write(0xE000ED88, struct.pack("<I", 0xF00000))
    profile_base = 0x20001BF5
    # Two distinct little-endian coefficients in the same field of two profiles.
    for index, percent in enumerate((25, 75)):
        cpu.mem_write(profile_base + index * 0x13C + 0x89, struct.pack("<H", percent))
    for channel in range(4):
        base = 0x200093FB + 0x2C * channel
        cpu.mem_write(base + 4, struct.pack("<11H", *range(0, 501, 50)))
        cpu.mem_write(base + 0x1A, struct.pack("<11H", *(20 + 10 * channel + 2 * i for i in range(11))))

    gains = []
    for index, percent in enumerate((25, 75, 25)):
        cpu.mem_write(profile_base + 5, bytes([index % 2]))
        replay.call(0x1EEFC, RETURN, instruction_limit=500000)
        gain, = struct.unpack("<f", cpu.mem_read(0x20000014, 4))
        verify(abs(gain - percent / 100) < 1e-6, "Stock profile conversion used the wrong slot")
        gains.append(gain)
        for channel in range(4):
            values = struct.unpack("<501f", cpu.mem_read(0x20002404 + channel * 0x7D4, 501 * 4))
            # Positive linear knots produce integer percentages in stock code.
            for hz, value in enumerate(values):
                expected = (20 + 10 * channel + hz // 25) / 100
                verify(abs(value - expected) < 1e-6, f"Gain interpolation mismatch at channel {channel}, {hz} Hz")
    return {"name": "stock_profile_calibration", "selected_gains": gains,
            "table_entries_checked_per_refresh": 4 * 501}


def check_eeprom_calibration(image, dsp):
    """Execute the stock EEPROM read and calibration validation branch."""
    address, size = 0x200093FB, 0xDD
    factory = image[0x354CE - 0x8000:0x354CE - 0x8000 + size]
    outcomes = []
    for scenario in ("valid", "bad_checksum", "bad_length", "wrong_version", "erased"):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        cpu.mem_map(0x4010B000, 4096)
        cpu.mem_write(0x200094DA, b"\x01")  # EEPROM controller already initialized
        calibration = bytearray(factory)
        calibration[0x1A] ^= 1  # distinguish valid EEPROM from the factory fallback
        if scenario == "bad_length":
            calibration[0] ^= 1
        if scenario == "wrong_version":
            calibration[3] ^= 1
        cpu.mem_write(address, bytes(calibration))
        replay.call(0x2B6B8, RETURN, address, size - 2)
        struct.pack_into("<H", calibration, size - 2, cpu.reg_read(UC_ARM_REG_R0) & 0xFFFF)
        if scenario == "bad_checksum":
            calibration[0x1A] ^= 2
        if scenario == "erased":
            calibration[:] = b"\xFF" * size
        cpu.mem_write(0x4010B000, bytes(calibration))

        # Enter the isolated calibration branch of the boot loader routine.
        # It reads via 0x31D4C and falls through at 0x2DECA after validation.
        replay.call(0x2DE70, 0x2DECA)
        accepted = scenario == "valid"
        verify(bytes(cpu.mem_read(address, size)) == (bytes(calibration) if accepted else factory),
               f"Unexpected stock calibration result: {scenario}")
        dirty, = struct.unpack("<H", cpu.mem_read(0x200019B8, 2))
        verify(bool(dirty & 0x200) == (not accepted), "Unexpected calibration rewrite flag")
        cpu.mem_map(0xE000E000, 4096)
        cpu.mem_write(0xE000ED88, struct.pack("<I", 0xF00000))
        replay.call(0x10088, RETURN, 0, instruction_limit=500000)
        first_knot_gain, = struct.unpack_from("<H", calibration if accepted else factory, 0x1A)
        first_gain, = struct.unpack("<f", cpu.mem_read(0x20002404, 4))
        verify(abs(first_gain - first_knot_gain / 100) < 1e-6, "Selected calibration did not reach the gain table")
        outcomes.append({"scenario": scenario, "accepted": accepted, "rewrite_pending": not accepted,
                         "first_gain_percent": first_knot_gain})
    return {"name": "stock_eeprom_calibration", "cases": outcomes}


def check_eeprom_user_profile(image, dsp):
    address, size = 0x20001BF5, 0x544
    factory = image[0x34F66 - 0x8000:0x34F66 - 0x8000 + size]
    outcomes = []
    for scenario, stored, accepted in (("valid", 1200, True), ("minimum", 300, True),
                                       ("maximum", 3600, True), ("too_low", 299, True),
                                       ("too_high", 3601, True), ("bad_checksum", 1200, False),
                                       ("bad_length", 1200, False), ("wrong_version", 1200, False),
                                       ("erased", 1200, False)):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        cpu.mem_map(0x40108000, 4096)
        cpu.mem_write(0x200094DA, b"\x01")
        profile = bytearray(factory)
        struct.pack_into("<H", profile, 0x53E, stored)
        if scenario == "bad_length":
            profile[0] ^= 1
        if scenario == "wrong_version":
            profile[3] ^= 1
        cpu.mem_write(address, bytes(profile))
        replay.call(0x2B6B8, RETURN, address, size - 2)
        struct.pack_into("<H", profile, size - 2, cpu.reg_read(UC_ARM_REG_R0) & 0xFFFF)
        if scenario == "bad_checksum":
            profile[-1] ^= 1
        if scenario == "erased":
            profile[:] = b"\xFF" * size
        cpu.mem_write(0x40108400, bytes(profile))
        replay.call(0x2DDDA, 0x2DE70, instruction_limit=200000)
        expected = bytearray(profile if accepted else factory)
        value, = struct.unpack_from("<H", expected, 0x53E)
        bounded = min(3600, max(300, value))
        struct.pack_into("<H", expected, 0x53E, bounded)
        verify(bytes(cpu.mem_read(address, size)) == expected, f"Unexpected stock user profile: {scenario}")
        dirty, = struct.unpack("<H", cpu.mem_read(0x200019B8, 2))
        rewrite = not accepted or bounded != value
        verify(bool(dirty & 0x80) == rewrite, f"Unexpected user profile rewrite flag: {scenario}")
        outcomes.append({"scenario": scenario, "accepted": accepted, "rewrite_pending": rewrite,
                         "loaded_field_0x53e": bounded})
    return {"name": "stock_eeprom_user_profile", "cases": outcomes}


def check_deferred_profile_write(image, dsp):
    """Replay the deferred save, then reboot from each committed page prefix."""
    profile, size = 0x20001BF5, 0x544
    eeprom, offset, page_size = 0x40108000, 0x400, 128
    factory = image[0x34F66 - 0x8000:0x34F66 - 0x8000 + size]
    replay = Replay(image, dsp)
    cpu = replay.cpu
    cpu.mem_map(eeprom, 4096)
    cpu.mem_map(0x40014000, 4096)
    cpu.mem_write(0x200094DA, b"\x01")

    def read_checksum():
        replay.call(0x2B6B8, RETURN, profile, size - 2)
        return struct.pack("<H", cpu.reg_read(UC_ARM_REG_R0) & 0xFFFF)

    cpu.mem_write(profile, factory)
    original = factory[:-2] + read_checksum()
    cpu.mem_write(eeprom + offset, original + b"\xA5" * (page_size - size % page_size))
    changed = bytearray(original)
    changed[0x20] ^= 1  # first page
    struct.pack_into("<H", changed, 0x53E, 1200)  # final page
    cpu.mem_write(profile, bytes(changed))
    changed[-2:] = read_checksum()
    verify(changed[-2:] != original[-2:], "The interruption fixture needs distinct checksums")
    cpu.mem_write(0x200019B8, b"\x80\x00")
    page_attempts = []

    def capture_page_commit(machine, address, width, data):
        page_attempts.append(bytes(machine.mem_read(eeprom + offset, size)))
        machine.mem_write(0x40014FE0, struct.pack("<I", 4))

    # Observe the first status poll after the stock page command.
    cpu.hook_add(UC_HOOK_CODE, capture_page_commit, begin=0xE55C, end=0xE55C)
    # The checksum and save paths share code. Flush cached translations before
    # running the save path with the EEPROM register hook installed.
    cpu.ctl_flush_tb()
    replay.call(0x19620, RETURN, instruction_limit=300000)
    verify(len(page_attempts) == 11 and page_attempts[-1] == changed, "Unexpected deferred page count")
    verify(cpu.mem_read(0x200019B8, 2) == b"\x00\x00", "Successful save stayed pending")
    verify(cpu.mem_read(eeprom + offset + size, 60) == b"\xA5" * 60,
           "Partial final page changed neighboring EEPROM bytes")

    def check_reload(snapshot, expected, rewrite):
        boot = Replay(image, dsp)
        boot.cpu.mem_map(eeprom, 4096)
        boot.cpu.mem_write(0x200094DA, b"\x01")
        boot.cpu.mem_write(eeprom + offset, bytes(snapshot))
        boot.call(0x2DDDA, 0x2DE70, instruction_limit=200000)
        verify(bytes(boot.cpu.mem_read(profile, size)) == expected, "Unexpected profile after simulated reboot")
        pending, = struct.unpack("<H", boot.cpu.mem_read(0x200019B8, 2))
        verify(bool(pending & 0x80) == rewrite, "Wrong recovery write request")

    check_reload(original, original, False)
    check_reload(page_attempts[-1], changed, False)
    for snapshot in page_attempts[:-1]:
        check_reload(snapshot, factory, True)
    # Cut before and after the first changed byte, and inside the final page
    # before the new checksum is stored.
    check_reload(changed[:0x20] + original[0x20:], original, False)
    check_reload(changed[:0x21] + original[0x21:], factory, True)
    check_reload(changed[:0x540] + original[0x540:], factory, True)
    return {"name": "stock_deferred_profile_write", "pages_completed": len(page_attempts),
            "pending_cleared": True, "incomplete_prefixes_rejected": len(page_attempts) - 1,
            "partial_page_cases": 3,
            "old_and_full_images_accepted": True}


def initialize_stock_sensa_pwm(image, dsp):
    replay = Replay(image, dsp)
    cpu = replay.cpu
    replay.call(0x8EBC, RETURN, 0x37200, 0x20000000, 0xA00)
    for page in (0xE000E000, 0x4000D000, 0x40020000, CTIMER2, 0x40040000, CTIMER3, 0x40085000):
        cpu.mem_map(page, 4096)
    cpu.mem_write(0xE000ED88, struct.pack("<I", 0xF00000))
    model_syscon_reset(cpu)
    model_async_syscon_reset(cpu)
    # Supply ready/lock signals without simulating oscillator settling.
    for register, value in ((0x40020054, 0x220), (0x40000584, 1), (0x40000520, 1)):
        cpu.mem_write(register, struct.pack("<I", value))
    replay.call(0x9CA4, RETURN, instruction_limit=100000)
    replay.call(0xA9F4, RETURN, 1)
    pwm_clock = cpu.reg_read(UC_ARM_REG_R0)
    verify(pwm_clock == 96_000_000, "Unexpected clock supplied to the SCT initializer")
    replay.call(0x1F48C, RETURN)
    config, control = struct.unpack("<2I", cpu.mem_read(0x40085000, 8))
    verify(config & 7 == 1 and control & 0x1FF8 == 0x18,
           "Expected unified, center-aligned SCT on the bus clock without prescaling")
    events = struct.unpack("<4I", cpu.mem_read(0x20002270, 16))
    verify(events == (0, 2, 4, 6), "Unexpected stock SCT event mapping")
    match_registers = []
    for output, event in zip((3, 5, 6, 7), events):
        event_mask, control = struct.unpack("<2I", cpu.mem_read(0x40085300 + event * 8, 8))
        verify(event_mask == 1 and control & 0xF == event, "Unexpected SCT period event")
        _, control = struct.unpack("<2I", cpu.mem_read(0x40085308 + event * 8, 8))
        verify(control & 0xF == event + 1, "Unexpected SCT duty event")
        set_mask, clear_mask = struct.unpack("<2I", cpu.mem_read(0x40085500 + output * 8, 8))
        verify(set_mask == 0 and clear_mask == 1 << (event + 1),
               f"Unexpected SCT routing for output {output}")
        period, = struct.unpack("<I", cpu.mem_read(0x40085100 + 4 * event, 4))
        verify(period == pwm_clock // (2 * 40_000), "Unexpected SCT period in operational clock fixture")
        match_registers.append(0x40085100 + 4 * (event + 1))
    verify(read_sensa_pwm(cpu, match_registers) == (120, 120, 0, 0), "Unexpected stock startup duty")
    return replay, match_registers


def read_sensa_pwm(cpu, match_registers):
    return tuple(struct.unpack("<I", cpu.mem_read(register, 4))[0] for register in match_registers)


def check_sensa_pwm(image, dsp, continuous=False, gain_percent=100):
    """Follow stock synthesis into the stock PWM register writer."""
    results = {}
    for side in (None, 0, 1):
        replay, match_registers = initialize_stock_sensa_pwm(image, dsp)
        cpu = replay.cpu
        bodies = [([], []) for _ in range(4)]
        if side is not None:
            if continuous:
                frequencies = [(83,) if channel == side else () for channel in range(2)]
                for tick in range(8):
                    cpu.mem_write(0x20024000, tone(tick, frequencies, 0.35))
                    replay.call(DSP, RETURN, STATE, 0x20024000, 1, LEVELS)
                levels = list(cpu.mem_read(LEVELS, 4))
                verify(not any(levels[(1 - side) * 2:(1 - side) * 2 + 2]), "DSP leaked into the opposite side")
                bands = [(level, level, pitch) for level, pitch in zip(levels[side * 2:side * 2 + 2], (17, 55))
                         if level]
                verify(bool(bands), "DSP did not produce a Sensa band")
                bodies[side] = (bands, [])
            else:
                bodies[side] = ([], [(35, 24, 0)])
        cpu.mem_write(0x20000B34, xbox_frame(bodies) + bytes(64))
        cpu.mem_write(0x20000B2C, b"\x01")
        replay.call(0x2B538, RETURN)
        verify(cpu.mem_read(0x20000B2C, 1) == b"\x00", "Stock dispatcher did not consume the Sensa frame")
        for address, value in ((0x2000077C, 1), (0x20004520, 2), (0x200044ED, 1)):
            cpu.mem_write(address, bytes([value]))
        if continuous:
            initialize_sensa_gain_tables(replay, gain_percent)
        trace = []
        for tick in range(32 if continuous else 16):
            if continuous and tick == 16:
                replay.call(DSP, RETURN, STATE, 0x20024000, 0, LEVELS)
                verify(not any(cpu.mem_read(LEVELS, 4)), "Disabled DSP retained band strength")
                cpu.mem_write(0x20000B34, xbox_frame([([], []) for _ in range(4)]) + bytes(64))
                cpu.mem_write(0x20000B2C, b"\x01")
                replay.call(0x2B538, RETURN)
            replay.call(0x2D210, RETURN)
            replay.call(0x2B6F0, RETURN)
            trace.append(read_sensa_pwm(cpu, match_registers))
        results[side] = trace
    idle = results[None]
    report = []
    for side in (0, 1):
        trace = results[side]
        verify(all(values[1 - side] == baseline[1 - side] for values, baseline in zip(trace, idle)),
               "Sensa synthesis changed the opposite modeled PWM channel")
        verify(all(values[2:] == baseline[2:] for values, baseline in zip(trace, idle)),
               "Palm synthesis changed a trigger PWM channel")
        delta = max(abs(values[side] - baseline[side]) for values, baseline in zip(trace, idle))
        verify(delta > 0, "Sensa synthesis did not reach the modeled PWM registers")
        if continuous:
            verify(trace[-4:] == idle[-4:], "Silent frame did not restore the idle PWM trace")
        report.append({"body_part": (216, 116)[side], "peak_match_count_delta": delta})
    return {"name": "candidate_sensa_band_pwm" if continuous else "stock_sensa_transient_pwm", "cases": report}


def replay_capture(image, dsp, path):
    # The USB capture does not prove the negotiated format or firmware ingress.
    # Use only an externally verified 48 kHz stereo PCM16 stream with 192 B slots.
    records = json.loads(path.read_text(encoding="utf-8"))
    verify(0 < len(records) <= 15000, "Expected at most 15 seconds of PCM")
    replay = Replay(image, dsp)
    trace = []
    previous_sequence = None
    usb_ring_checked = False
    for index, record in enumerate(records):
        sequence = record["sequence"]
        pcm = bytes.fromhex(record["payload_hex"])
        verify(len(pcm) == 192, "Capture contains a non-192-byte payload")
        verify(1 <= sequence <= 255, "Invalid audio sequence")
        verify(previous_sequence is None or sequence == previous_sequence % 255 + 1,
               f"Audio sequence gap at slot {index}")
        previous_sequence = sequence
        if not usb_ring_checked and any(abs(sample) > 100 for sample in struct.unpack("<96h", pcm)):
            header = bytes((0x60, 0x20, sequence, 0xC0, 0x81, 0))
            replay.check_usb_receive(header + pcm, 6144 - 64)
            for offset in (0, 6144 - 192):
                replay.check_usb_to_i2s(header + pcm, offset)
            usb_ring_checked = True
        levels, _ = replay.tick(pcm, True, True, index % 3)
        trace.append({"sequence": sequence, "urb_timestamp_us": record["urb_timestamp_us"],
                      "palm_band_strength": levels})
    result = {"name": "capture_pcm_boundary", "stock_usb_ring_copy_checked": usb_ring_checked,
              "stock_queue": check_stream_queue(image, dsp, records), "blocks": trace}
    if len(records) >= 128:
        result["continuous_dma"] = check_continuous_dma(
            image, dsp, [bytes.fromhex(record["payload_hex"]) for record in records[:128]])
    return result


def check_sensa_trigger_pwm(image, dsp):
    """Trace both Xbox Sensa trigger channels to stock SCT register writes."""
    trace = []
    for side, kind, strength in ((0, "band", 40), (1, "band", 40), (0, "transient", 40),
                                 (1, "transient", 40), (0, "band", 20), (1, "band", 63)):
        for pitch in (0, 60, 127):
            replay, match_registers = initialize_stock_sensa_pwm(image, dsp)
            cpu = replay.cpu
            idle = read_sensa_pwm(cpu, match_registers)
            period_match, = struct.unpack("<I", cpu.mem_read(match_registers[side + 2] - 4, 4))
            verify(period_match == 1200, "Unexpected stock trigger period match")
            cpu.mem_write(0x200044ED, b"\x01")

            bodies = [([], []) for _ in range(4)]
            bodies[side + 2] = ([(strength, strength, pitch)], []) if kind == "band" else (
                [], [(strength, pitch, 0)])
            cpu.mem_write(0x20000B34, xbox_frame(bodies) + bytes(64))
            cpu.mem_write(0x20000B2C, b"\x01")
            replay.call(0x2B538, RETURN)

            def trigger_tick():
                replay.call(0x1B640, RETURN)
                replay.call(0x2B6F0, RETURN)
                return read_sensa_pwm(cpu, match_registers)

            if kind == "band":
                for _ in range(481):
                    replay.call(0x1485C, RETURN)
            else:
                verify(trigger_tick()[side + 2] > 0, "Trigger transient did not start")
            expected = tuple(strength * period_match // 100 if index == side + 2 else value
                             for index, value in enumerate(idle))
            observed = trigger_tick()
            verify(observed == expected, f"Trigger {side}/{kind}/{pitch}: expected {expected}, got {observed}")
            if kind == "transient":
                for _ in range(1016):
                    replay.call(0x1485C, RETURN)
                verify(trigger_tick() == expected, "Trigger transient ended before the stock cutoff")
                replay.call(0x1485C, RETURN)
                verify(trigger_tick() == idle, "Expired trigger transient left PWM active")
            trace.append({"body_part": (249, 149)[side], "kind": kind, "pitch_code": pitch,
                          "strength": strength, "match_count": expected[side + 2]})
    return {"name": "stock_sensa_trigger_pwm", "cases": trace}


def check_stock_sensa_pitch(image, dsp):
    """Validate stock palm oscillator output against its decoded pitch."""
    table = struct.unpack_from("<513f", image, 0x358F8 - 0x8000)
    error = max(abs(value - math.sin(math.tau * index / 512)) for index, value in enumerate(table))
    verify(error < 1e-6, "Stock oscillator lookup does not match one sine period")
    results = []
    for pitch, expected_hz in ((0, 30), (24, 99), (60, 204), (127, 399)):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        replay.call(0x8EBC, RETURN, 0x37200, 0x20000000, 0xA00)
        cpu.mem_map(0xE000E000, 4096)
        cpu.mem_write(0xE000ED88, struct.pack("<I", 0xF00000))
        frame = xbox_frame([([], [(35, pitch, 0)]), ([], []), ([], []), ([], [])])
        cpu.mem_write(0x20000B34, frame + bytes(64))
        cpu.mem_write(0x20000B2C, b"\x01")
        replay.call(0x2B538, RETURN)
        hz, = struct.unpack("<H", cpu.mem_read(OUT + 6, 2))
        verify(hz == expected_hz, "Unexpected stock palm pitch mapping")
        samples = []
        for _ in range(32):
            replay.call(0x227E4, RETURN, 0x2000507C, 0)
            samples.extend(struct.unpack("<4f", cpu.mem_read(0x2000507C, 16)))
        first_positive = next(index for index, value in enumerate(samples) if value > 0)
        # A very small negative LUT/interpolation result at phase zero
        # can follow the initial silent sample.
        start = first_positive - 1
        crossing = next(index for index in range(start + 2, len(samples)) if samples[index] < 0 < samples[index - 1])
        previous, current = samples[crossing - 1:crossing + 1]
        zero = crossing - 1 + previous / (previous - current)
        half_period = zero - start
        # The stock phase formula uses 4000 samples per second. Compare
        # in samples without assuming the actual hardware timer cadence.
        verify(abs(half_period - 2000 / hz) < 0.02,
               f"Stock oscillator at {hz} Hz: first positive {first_positive}, half period {half_period}")
        results.append({"pitch_code": pitch, "decoded_hz": hz, "half_period_samples": round(half_period, 3)})
    return {"name": "stock_sensa_pitch", "sine_table_max_error": error, "cases": results}


def inspect_image_placement(image, dsp):
    """Inventory fill runs and scatter limits without assigning image space."""
    app_base = 0x8000
    scatter = struct.unpack_from("<8I", image, 0x36EA4 - app_base)
    stack, = struct.unpack_from("<I", image)
    verify(scatter == (0x37200, 0x20000000, 0xA00, 0x8EBC, 0x37800, 0x20000A00, 0x13328, 0x33F08),
           "Unexpected stock scatter table")
    runs = list(re.finditer(b"\x00+|\xFF+", image))
    longest = max(runs, key=lambda match: match.end() - match.start())
    offsets = (app_base + longest.start(), app_base + longest.end())
    return {"name": "stock_image_placement_inventory", "app_range": [hex(app_base), hex(app_base + len(image))],
            "scatter_ram_end_exclusive": hex(scatter[5] + scatter[6]), "initial_sp": hex(stack),
            "longest_fill_run_range": [hex(offset) for offset in offsets],
            "longest_fill_run_bytes": offsets[1] - offsets[0], "replay_text_bytes": len(dsp)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
    parser.add_argument("--fixture-wav", type=Path, help="Also save a six-second stereo capture stimulus")
    parser.add_argument("--verified-pcm-trace", type=Path,
                        help="Replay contiguous, independently verified 48 kHz stereo PCM16 slots")
    parser.add_argument("--active-stereo-excerpt", type=int, metavar="START",
                        help="Also run the scheduled PWM test using 128 active stereo slots from START")
    args = parser.parse_args()
    if args.active_stereo_excerpt is not None and (
            args.verified_pcm_trace is None or args.active_stereo_excerpt < 0):
        parser.error("--active-stereo-excerpt needs --verified-pcm-trace and a nonnegative index")
    image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Expected unmodified Dev2FW 2.04.00")
    verify(len(dsp) > CANDIDATE_BOOT - DSP, "Rebuild the simulator module with the TX tap, worker and reset")
    cases = (
        ("left_80", ((80,), ()), True, True, 0.85),
        ("right_190", ((), (190,)), True, True, 0.85),
        ("stereo_multitone", ((80, 190), (110, 320)), True, True, 0.85),
        ("inactive_stream", ((80,), (190,)), False, True, 0.85),
        ("empty_queue", ((80,), (190,)), True, False, 0.85),
        ("near_silence", ((80,), (190,)), True, True, 0.001),
        ("alternate_ring", ((80,), (190,)), True, True, 0.85, True),
    )
    report = [run_scenario(image, dsp, *case) for case in cases]
    report.append(check_stream_loss(image, dsp))
    report.append(check_toggle(image, dsp))
    report.append(check_timer_pwm(image, dsp))
    report.append(check_stock_timer_schedule(image, dsp))
    report.append(check_stock_sensa_buffer(image, dsp))
    report.append(check_dma_boundaries(image, dsp))
    report.append(check_stock_i2s_completion(image, dsp))
    report.append(check_stock_dma_descriptor(image, dsp))
    report.append(check_continuous_dma(image, dsp, [tone(tick, ((80,), (190,))) for tick in range(128)]))
    report.append(check_dma_arrival_variation(image, dsp))
    report.append(check_application_reset(image, dsp))
    report.append(check_board_startup_boundary(image, dsp))
    report.append(check_board_startup_boundary(image, dsp, respond_to_i2c=True))
    report.append(check_tx_tap_contract(image, dsp))
    report.append(check_worker_interleaving(image, dsp))
    report.append(check_foreground_worker(image, dsp))
    report.append(check_scheduled_dsp(image, dsp))
    report.append(check_sensa_pwm(image, dsp))
    report.append(check_stock_profile_calibration(image, dsp))
    report.append(check_eeprom_calibration(image, dsp))
    report.append(check_eeprom_user_profile(image, dsp))
    report.append(check_deferred_profile_write(image, dsp))
    full_gain = check_sensa_pwm(image, dsp, continuous=True)
    half_gain = check_sensa_pwm(image, dsp, continuous=True, gain_percent=50)
    for full, half in zip(full_gain["cases"], half_gain["cases"]):
        verify(half["peak_match_count_delta"] < full["peak_match_count_delta"],
               "Lower stock gain did not reduce PWM deviation")
        full["half_gain_peak_match_count_delta"] = half["peak_match_count_delta"]
    report.append(full_gain)
    report.append(check_sensa_trigger_pwm(image, dsp))
    report.append(check_stock_sensa_pitch(image, dsp))
    report.append(inspect_image_placement(image, dsp))
    if args.verified_pcm_trace:
        capture = replay_capture(image, dsp, args.verified_pcm_trace)
        report.append(capture)
        if args.active_stereo_excerpt is not None:
            start = args.active_stereo_excerpt
            records = json.loads(args.verified_pcm_trace.read_text(encoding="utf-8"))
            blocks = [bytes.fromhex(record["payload_hex"]) for record in records[start:start + 128]]
            verify(len(blocks) == 128, "Active stereo excerpt extends beyond capture")
            report.append({"name": "captured_scheduled_pcm_to_pwm", "excerpt_start_slot": start,
                           **check_pcm_to_scheduled_pwm(image, dsp, blocks)})
    if args.fixture_wav:
        with wave.open(str(args.fixture_wav), "wb") as output:
            output.setparams((2, 2, 48000, 0, "NONE", "not compressed"))
            for frequencies in (((), ()), ((80,), ()), ((), (190,)),
                                ((80, 190), (110, 320)), ((), ()), ((), ())):
                output.writeframes(b"".join(tone(tick, frequencies, 0.35) for tick in range(1000)))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

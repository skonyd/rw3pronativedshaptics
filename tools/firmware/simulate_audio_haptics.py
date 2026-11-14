"""Replay stock NXP I2S TX selection and compare an original ARM DSP in Unicorn."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import wave

from unicorn import Uc, UC_ARCH_ARM, UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_MODE_MCLASS, UC_MODE_THUMB
from unicorn.arm_const import (
    UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_R0, UC_ARM_REG_R1,
    UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_SP,
)

from emulate_decoder import DECODER, OUT, decode, xbox_frame

STOCK_SHA256 = "d19550cf434da3e3ae3c7e8e46f23e5c5f61274a0a462bf81b768c3953a1aee8"
DSP, STATE, LEVELS = 0x100000, 0x20021000, 0x20021100
RING, ZERO, TX = 0x20013A38, 0x200056EC, 0x22FF8
DMA_SUBMIT, RETURN = 0x12F9C, 0xFFF0
USB_RX, USB_RX_ACCOUNTING = 0x28C80, 0x2384C
USB_BUFFER, USB_EVENT, USB_CONTEXT = 0x2000B3C0, 0x20022000, 0x20022100
USB_RING = 0x200057AC


def verify(condition, message):
    if not condition:
        raise RuntimeError(message)


class Replay:
    def __init__(self, image, dsp):
        verify(0 < len(dsp) <= 4096, "Invalid simulator DSP binary size")
        self.cpu = cpu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
        cpu.mem_map(0, 0x40000)
        cpu.mem_write(0x8000, image)
        cpu.mem_map(DSP, 4096)
        cpu.mem_write(DSP, dsp)
        cpu.mem_map(0x20000000, 0x40000)
        self.steps = 0
        cpu.hook_add(UC_HOOK_CODE, self.count_step, begin=DSP, end=DSP + len(dsp) - 1)

    def count_step(self, *_):
        self.steps += 1

    def call(self, entry, end, *args):
        cpu = self.cpu
        cpu.reg_write(UC_ARM_REG_SP, 0x20027000)
        cpu.reg_write(UC_ARM_REG_LR, RETURN | 1)
        for reg, value in zip((UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2, UC_ARM_REG_R3), args):
            cpu.reg_write(reg, value)
        cpu.emu_start(entry | 1, end, count=20000)
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
    replay = Replay(image, dsp)
    cpu = replay.cpu
    timer, sct = 0x40028000, 0x40085000
    cpu.mem_map(timer, 4096)
    cpu.mem_map(sct, 4096)
    cpu.mem_write(0x200044BE, b"\x01")
    for address, value in ((sct, 1), (sct + 0x100, 4500), (sct + 0x30C, 1)):
        cpu.mem_write(address, struct.pack("<I", value))
    writes = []
    cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                 writes.append((address, value)), begin=timer, end=sct + 4095)
    observed = []
    for enabled, duty, changed in ((False, 20, False), (True, 20, True), (True, 20, False),
                                   (True, 100, True), (True, 0, True)):
        writes.clear()
        cpu.mem_write(0x200044ED, bytes([enabled]))
        cpu.mem_write(0x20002284, struct.pack("<H", duty))
        replay.call(0x2B6F0, RETURN)
        expected = [(sct + 0x104, duty * 45), (sct + 0x204, duty * 45)] if changed else []
        verify([(addr, value) for addr, value in writes if addr in (sct + 0x104, sct + 0x204)] == expected,
               "Unexpected SCT MATCH/MATCHREL writes")
        verify(writes[-1] == (timer, 1), "CTIMER2 did not acknowledge match")
        observed.append({"enabled": enabled, "duty_percent": duty, "match_counts": [v for _, v in expected]})
    return {"name": "stock_timer_pwm_registers", "cases": observed}


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


def initialize_stock_dma(image, dsp):
    replay = Replay(image, dsp)
    cpu = replay.cpu
    syscon, dma, tx, rx, nvic = 0x40000000, 0x40082000, 0x40097000, 0x40098000, 0xE000E000
    for page in (syscon, dma, tx, rx, nvic):
        cpu.mem_map(page, 4096)

    def reset_register_alias(machine, access, address, size, value, data):
        target = syscon + 0x100 + (address & 0xF)
        old, = struct.unpack("<I", machine.mem_read(target, 4))
        new = old | value if address < syscon + 0x140 else old & ~value
        machine.mem_write(target, struct.pack("<I", new))

    cpu.hook_add(UC_HOOK_MEM_WRITE, reset_register_alias, begin=syscon + 0x120, end=syscon + 0x14B)
    cpu.mem_write(dma + 8, struct.pack("<I", 0x2000B000))
    replay.call(0x13D6C, RETURN)
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


def check_continuous_dma(image, dsp, blocks, arrival_ticks=None):
    """Run the stock USB producer and generated DMA descriptor ring together."""
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
    for tick in range(arrivals[-1] + 25):
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


def check_sensa_pwm(image, dsp, continuous=False):
    """Follow stock synthesis into the stock PWM register writer."""
    results = {}
    for side in (None, 0, 1):
        replay = Replay(image, dsp)
        cpu = replay.cpu
        replay.call(0x8EBC, RETURN, 0x37200, 0x20000000, 0xA00)
        for page in (0xE000E000, 0x40028000, 0x40085000):
            cpu.mem_map(page, 4096)
        for address, value in ((0xE000ED88, 0xF00000), (0x40085000, 1), (0x40085100, 4500)):
            cpu.mem_write(address, struct.pack("<I", value))
        for channel in range(4):
            event = channel * 2
            cpu.mem_write(0x20002270 + 4 * channel, struct.pack("<I", event))
            cpu.mem_write(0x4008530C + 8 * event, struct.pack("<I", channel + 1))
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
            # The stock oscillator multiplies by a per-palm RAM table.
            # Use a flat fixture until its device-specific initializer is modeled.
            for channel in range(2):
                cpu.mem_write(0x20002404 + 0x7D4 * channel, struct.pack("<501f", *([1.0] * 501)))
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
            trace.append(struct.unpack("<2I", cpu.mem_read(0x40085104, 8)))
        results[side] = trace
    idle = results[None]
    report = []
    for side in (0, 1):
        trace = results[side]
        verify(all(values[1 - side] == baseline[1 - side] for values, baseline in zip(trace, idle)),
               "Sensa synthesis changed the opposite modeled PWM channel")
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
    parser.add_argument("--fixture-wav", type=Path, help="Also save a six-second stereo capture stimulus")
    parser.add_argument("--verified-pcm-trace", type=Path,
                        help="Replay contiguous, independently verified 48 kHz stereo PCM16 slots")
    args = parser.parse_args()
    image, dsp = args.stock.read_bytes(), args.dsp.read_bytes()
    verify(hashlib.sha256(image).hexdigest() == STOCK_SHA256, "Expected unmodified Dev2FW 2.04.00")
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
    report.append(check_dma_boundaries(image, dsp))
    report.append(check_stock_i2s_completion(image, dsp))
    report.append(check_stock_dma_descriptor(image, dsp))
    report.append(check_continuous_dma(image, dsp, [tone(tick, ((80,), (190,))) for tick in range(128)]))
    report.append(check_dma_arrival_variation(image, dsp))
    report.append(check_sensa_pwm(image, dsp))
    report.append(check_sensa_pwm(image, dsp, continuous=True))
    if args.verified_pcm_trace:
        report.append(replay_capture(image, dsp, args.verified_pcm_trace))
    if args.fixture_wav:
        with wave.open(str(args.fixture_wav), "wb") as output:
            output.setparams((2, 2, 48000, 0, "NONE", "not compressed"))
            for frequencies in (((), ()), ((80,), ()), ((), (190,)),
                                ((80, 190), (110, 320)), ((), ()), ((), ())):
                output.writeframes(b"".join(tone(tick, frequencies, 0.35) for tick in range(1000)))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

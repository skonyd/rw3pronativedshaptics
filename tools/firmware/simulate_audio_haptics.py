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
    return {"name": "capture_pcm_boundary", "stock_usb_ring_copy_checked": usb_ring_checked,
            "stock_queue": check_stream_queue(image, dsp, records), "blocks": trace}


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

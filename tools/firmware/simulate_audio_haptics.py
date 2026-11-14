"""Replay stock NXP I2S TX selection and compare an original ARM DSP in Unicorn."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_ARM, UC_HOOK_CODE, UC_MODE_MCLASS, UC_MODE_THUMB
from unicorn.arm_const import (
    UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_R0, UC_ARM_REG_R1,
    UC_ARM_REG_R2, UC_ARM_REG_R3, UC_ARM_REG_SP,
)

from emulate_decoder import DECODER, OUT, decode, xbox_frame

STOCK_SHA256 = "d19550cf434da3e3ae3c7e8e46f23e5c5f61274a0a462bf81b768c3953a1aee8"
DSP, STATE, LEVELS = 0x100000, 0x20021000, 0x20021100
RING, ZERO, TX = 0x20013A38, 0x200056EC, 0x22FF8
DMA_SUBMIT, RETURN = 0x12F9C, 0xFFF0


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock", type=Path)
    parser.add_argument("dsp", type=Path)
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
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

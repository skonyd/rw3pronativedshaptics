"""Transfer stock dongle codec bytes to the stock controller codec offline."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3,
    UC_RISCV_REG_MSTATUS, UC_RISCV_REG_RA, UC_RISCV_REG_S0,
    UC_RISCV_REG_S3, UC_RISCV_REG_S5, UC_RISCV_REG_SP,
)

from replay_telink_radio import DECODE_FRAME, initialize_i2s, resample_to_fifo
from replay_telink_startup import replay_startup, require
from replay_telink_usb import install_andes_hooks, run_until


ENCODER_INIT = 0x26BC4
PAIR_START, PAIR_END = 0x5C6C, 0x5D18
PCM_FRAMES, CODED_BYTES = 240, 35
ENCODED_LENGTH_OFFSET = 0x1C
LEFT_ENCODED, RIGHT_ENCODED = 0x5CB4, 0x5D08
SAMPLE_RATE, AMPLITUDE = 48_000, 20_000
SCENES = (
    ("cold_silence", 0, 0, 2),
    ("left_only", 200, 0, 4),
    ("right_only", 0, 1000, 4),
    ("stereo", 200, 1000, 4),
    ("lose_left", 200, 1000, 1),
    ("recovery", 200, 1000, 4),
    ("swapped_stereo", 1000, 200, 4),
    ("drain", 0, 0, 6),
)


def sine(frequency):
    return [round(AMPLITUDE * math.sin(math.tau * frequency * i / SAMPLE_RATE)) for i in range(PCM_FRAMES)]


def encode_link(cpu):
    cpu.mem_map(0x80140000, 0x1000)
    install_andes_hooks(cpu, ((0x800, 0x29723),))
    cpu.reg_write(UC_RISCV_REG_MSTATUS, cpu.reg_read(UC_RISCV_REG_MSTATUS) | 0x6000)
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    cpu.reg_write(UC_RISCV_REG_RA, 0x700)
    run_until(cpu, ENCODER_INIT, 0x700, vendor_budget=160_000)

    caller_stack, encoded_address = stack - 0x2000, 0xA4600
    pcm_address, mono_address = caller_stack + 0xC0, caller_stack + 0x480
    guard = b"\xA5" * 4
    scenes = {}
    for name, left, right, blocks in SCENES:
        pcm = b"".join(struct.pack("<hh", l, r) for l, r in zip(sine(left), sine(right)))
        packets = []
        for _ in range(blocks):
            cpu.mem_write(pcm_address, pcm)
            cpu.mem_write(encoded_address - 4, b"\xA5" * (CODED_BYTES * 2 + 8))
            cpu.reg_write(UC_RISCV_REG_SP, caller_stack)
            cpu.reg_write(UC_RISCV_REG_S0, pcm_address)
            cpu.reg_write(UC_RISCV_REG_S3, encoded_address)
            cpu.reg_write(UC_RISCV_REG_S5, mono_address)
            # Stock separates the PCM lanes, calls the two encoder
            # contexts and copies each 35-byte result to this buffer.
            for entry, checkpoint in ((PAIR_START, LEFT_ENCODED), (LEFT_ENCODED, RIGHT_ENCODED)):
                run_until(cpu, entry, checkpoint, vendor_budget=160_000)
                length, = struct.unpack("<I", cpu.mem_read(caller_stack + ENCODED_LENGTH_OFFSET, 4))
                require(cpu.reg_read(UC_RISCV_REG_A0) == 0 and length == CODED_BYTES,
                        "Stock dongle encoder failed or returned the wrong packet length")
            run_until(cpu, RIGHT_ENCODED, PAIR_END, vendor_budget=512)
            packet = bytes(cpu.mem_read(encoded_address, CODED_BYTES * 2))
            require(cpu.mem_read(encoded_address - 4, 4) == guard
                    and cpu.mem_read(encoded_address + len(packet), 4) == guard,
                    "Stock encoder output crossed its packet bounds")
            packets.append(packet)
        scenes[name] = packets
    return scenes


def decode_lane(cpu, stack, channel, payload, lost):
    input_address, output_address = 0x98000, 0x98200
    guard = b"\xA5" * 4
    cpu.mem_write(input_address, b"\xA5" * CODED_BYTES if lost else payload)
    cpu.mem_write(output_address - 4, guard * (PCM_FRAMES // 2 + 2))
    for register, value in ((UC_RISCV_REG_SP, stack), (UC_RISCV_REG_RA, 0x500),
                            (UC_RISCV_REG_A0, channel), (UC_RISCV_REG_A1, input_address),
                            (UC_RISCV_REG_A2, output_address), (UC_RISCV_REG_A3, int(lost))):
        cpu.reg_write(register, value)
    run_until(cpu, DECODE_FRAME, 0x500, vendor_budget=200_000)
    end = output_address + PCM_FRAMES * 2
    require(cpu.reg_read(UC_RISCV_REG_A0) == CODED_BYTES and cpu.mem_read(output_address - 4, 4) == guard
            and cpu.mem_read(end, 4) == guard, "Stock controller decoder returned an unexpected buffer result")
    return tuple(sample for sample, in struct.iter_unpack("<h", cpu.mem_read(output_address, end - output_address)))


def tone_level(samples, frequency):
    phase = math.tau * frequency / SAMPLE_RATE
    value = sum(sample * complex(math.cos(phase * i), math.sin(phase * i)) for i, sample in enumerate(samples))
    return round(2 * abs(value) / len(samples), 1)


def check_tones(lanes, frequencies):
    levels = []
    for samples, expected in zip(lanes, frequencies):
        measured = {hz: tone_level(samples, hz) for hz in (200, 1000)}
        for hz, level in measured.items():
            if hz == expected:
                require(18_000 <= level <= 22_000, f"{hz} Hz tone came through at {level} PCM counts")
            else:
                require(level < 250, f"Unexpected {hz} Hz energy: {level} PCM counts")
        if expected == 0:
            require(max(map(abs, samples)) < 250, "Residual signal in a silent lane")
        levels.append(measured)
    return levels


def decode_link(cpu, scenes):
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    caller_stack = stack - 0x1800
    tx, _ = initialize_i2s(cpu)
    fifo = hashlib.sha256()
    frames = 0
    report = {}
    for name, left, right, _ in SCENES:
        packets = scenes[name]
        for packet in packets:
            first = decode_lane(cpu, stack, 0, packet[:CODED_BYTES], name == "lose_left")
            second = decode_lane(cpu, stack, 1, packet[CODED_BYTES:], False)
            interleaved = tuple(zip(first, second))
            fifo_pcm = bytearray()
            for start in range(0, len(interleaved), 40):
                pcm = b"".join(struct.pack("<hh", *frame) for frame in interleaved[start:start + 40])
                resampled, output = resample_to_fifo(cpu, tx, caller_stack, pcm)
                require(all(actual == (value if abs(value) > 1 else 0)
                            for (value,), (actual,) in zip(struct.iter_unpack("<h", resampled),
                                                          struct.iter_unpack("<h", output))),
                        "Decoded PCM took an unexpected path from the stock writer to the FIFO")
                fifo.update(output)
                fifo_pcm.extend(output)
                frames += len(output) // 4
            if name == "cold_silence":
                require(not any(first) and not any(second), "Stock codec introduced audio during cold silence")
        final_fifo = tuple(zip(*struct.iter_unpack("<hh", fifo_pcm)))
        if name == "lose_left":
            require(any(first), "Stock loss concealment did not carry prior left audio")
            levels = [None, *check_tones((second,), (right,))]
            fifo_levels = [None, *check_tones((final_fifo[1],), (right,))]
        else:
            levels = check_tones((first, second), (left, right))
            fifo_levels = check_tones(final_fifo, (left, right))
        report[name] = {"packets": len(packets), "final_decoder_levels": levels,
                        "final_fifo_levels": fifo_levels,
                        "final_peak": [max(map(abs, first)), max(map(abs, second))]}
    return {"scenes": report, "fifo_frames": frames, "fifo_sha256": fifo.hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dongle_fw", type=Path)
    parser.add_argument("controller_radio_fw", type=Path)
    args = parser.parse_args()
    encoded = replay_startup(args.dongle_fw.read_bytes(), "dongle", inspect_application=encode_link)["application"]
    decoded = replay_startup(args.controller_radio_fw.read_bytes(), "radio",
                             inspect_application=lambda cpu: decode_link(cpu, encoded))
    packets = [packet for scene in encoded.values() for packet in scene]
    print(json.dumps({"encoded_pairs": len(packets), "encoded_bytes": sum(map(len, packets)),
                      "encoded_sha256": hashlib.sha256(b"".join(packets)).hexdigest(),
                      "decoder_to_fifo": decoded["application"]}, indent=2))


if __name__ == "__main__":
    main()

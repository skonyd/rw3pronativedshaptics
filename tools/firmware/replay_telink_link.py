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
from replay_telink_usb import (
    EVENT_BYTES, EVENT_RING, RING_SIZE, RING_STATE, initialize_resampler,
    install_andes_hooks, replay_endpoint, run_until,
)
from usb_audio_trace import inspect


ENCODER_INIT = 0x26BC4
PAIR_START, PAIR_END = 0x5C6C, 0x5D18
WORKER_INTERRUPT, PCM_WORKER, WORKER_IRQ_STATUS = 0x3CDC, 0x5958, 0x80140143
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


def encode_captured_worker(cpu, stack, slots):
    """Run the actual PCM worker from its modeled timer invocation."""
    initialize_resampler(cpu, stack)
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_RA, 0x700)
    run_until(cpu, ENCODER_INIT, 0x700, vendor_budget=160_000)
    # The USB handler writes the worker's cursor at 0x530C immediately
    # before publishing the event. Verify both agree with the ring.
    event_address = EVENT_RING + (len(slots) - 1) * EVENT_BYTES
    write_word, = struct.unpack("<I", cpu.mem_read(event_address + 8, 4))
    require(cpu.mem_read(0x880A4, 4) == struct.pack("<I", write_word),
            "USB handler's worker cursor does not match its last published event")
    first_word = write_word - 462
    read_cursor = RING_STATE + 12
    cpu.mem_write(read_cursor, struct.pack("<I", first_word))
    cpu.mem_write(0x90A26, b"\x40")  # Steady-state worker branch using the USB ring.
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_RA, 0x700)
    cpu.mem_write(WORKER_IRQ_STATUS, bytes(1))
    run_until(cpu, WORKER_INTERRUPT, 0x700, vendor_budget=128)
    require(cpu.mem_read(read_cursor, 4) == struct.pack("<I", first_word),
            "Worker interrupt consumed PCM without a pending bit")
    cpu.mem_write(WORKER_IRQ_STATUS, b"\x01")
    run_until(cpu, WORKER_INTERRUPT, PCM_WORKER, vendor_budget=512)
    run_until(cpu, PCM_WORKER, 0x5F54, vendor_budget=4096)
    source = cpu.reg_read(UC_RISCV_REG_A1)
    frames = cpu.reg_read(UC_RISCV_REG_A2)
    cursor, = struct.unpack("<I", cpu.mem_read(read_cursor, 4))
    require(frames == 241 and cursor == first_word + frames, "Worker read an unexpected PCM window")
    pcm = b"".join(data for _, data in slots)
    samples = tuple(sample for sample, in struct.iter_unpack("<h", pcm[first_word * 4:cursor * 4]))
    adjusted = struct.pack(f"<{len(samples)}h", *(sample + (sample < 0) for sample in samples))
    require(len(adjusted) == frames * 4 and cpu.mem_read(source, len(adjusted)) == adjusted,
            "Worker input differs from the recorded USB PCM")
    run_until(cpu, 0x5F54, PAIR_START, vendor_budget=65_536)
    address = cpu.reg_read(UC_RISCV_REG_S3)
    require(address == 0x8E1FD, "Stock worker selected a different encoded-pair destination")
    complete_pair(cpu, cpu.reg_read(UC_RISCV_REG_SP))
    packet = bytes(cpu.mem_read(address, CODED_BYTES * 2))
    run_until(cpu, PAIR_END, 0x700, vendor_budget=4096)
    require(cpu.mem_read(address, CODED_BYTES * 2) == packet, "Stock worker changed the encoded pair at return")
    return packet, {"source_ring_word": first_word % RING_SIZE, "frames_read": frames,
                    "irq_entry": hex(WORKER_INTERRUPT), "no_pending_leaves_cursor_unchanged": True,
                    "ring_wrap": (first_word % RING_SIZE) + frames > RING_SIZE,
                    "packet_sha256": hashlib.sha256(packet).hexdigest()}


def sine(frequency):
    return [round(AMPLITUDE * math.sin(math.tau * frequency * i / SAMPLE_RATE)) for i in range(PCM_FRAMES)]


def complete_pair(cpu, caller_stack):
    # Stock separates the PCM lanes, calls the two encoder contexts
    # and copies each 35-byte result to the caller's buffer.
    for entry, checkpoint in ((PAIR_START, LEFT_ENCODED), (LEFT_ENCODED, RIGHT_ENCODED)):
        run_until(cpu, entry, checkpoint, vendor_budget=160_000)
        length, = struct.unpack("<I", cpu.mem_read(caller_stack + ENCODED_LENGTH_OFFSET, 4))
        require(cpu.reg_read(UC_RISCV_REG_A0) == 0 and length == CODED_BYTES,
                "Stock dongle encoder failed or returned the wrong packet length")
    run_until(cpu, RIGHT_ENCODED, PAIR_END, vendor_budget=512)


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
            complete_pair(cpu, caller_stack)
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


def send_to_fifo(cpu, tx, caller_stack, lanes):
    interleaved = tuple(zip(*lanes))
    fifo_pcm = bytearray()
    for start in range(0, len(interleaved), 40):
        pcm = b"".join(struct.pack("<hh", *frame) for frame in interleaved[start:start + 40])
        resampled, output = resample_to_fifo(cpu, tx, caller_stack, pcm)
        require(all(actual == (value if abs(value) > 1 else 0)
                    for (value,), (actual,) in zip(struct.iter_unpack("<h", resampled),
                                                  struct.iter_unpack("<h", output))),
                "Decoded PCM took an unexpected path from the stock writer to the FIFO")
        fifo_pcm.extend(output)
    return fifo_pcm


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
            fifo_pcm = send_to_fifo(cpu, tx, caller_stack, (first, second))
            fifo.update(fifo_pcm)
            frames += len(fifo_pcm) // 4
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


def replay_capture_worker(dongle_image, radio_image, capture, usb_address):
    slots = []

    def collect(endpoint, slot, pcm):
        if endpoint == 6:
            slots.append((slot, pcm))

    inspect(capture, usb_address, on_audio_slot=collect)
    cases = []
    for active_side, start, count in ((0, 600, 15), (1, 1600, 27)):
        window = slots[start:start + count]
        require(len(window) == count and all(
            b[0][2] == a[0][2] % 255 + 1 for a, b in zip(window, window[1:])),
            "The selected lab capture window has missing USB slots")

        def encode(cpu):
            reports = []
            replay_endpoint(cpu, window, lambda machine, stack:
                            reports.append(encode_captured_worker(machine, stack, window)))
            return reports[0]

        packet, report = replay_startup(dongle_image, "dongle", inspect_application=encode)["application"]

        def decode(cpu):
            stack = cpu.reg_read(UC_RISCV_REG_SP)
            tx, _ = initialize_i2s(cpu)
            lanes = [decode_lane(cpu, stack, channel, packet[offset:offset + CODED_BYTES], False)
                     for channel, offset in enumerate((0, CODED_BYTES))]
            output = send_to_fifo(cpu, tx, stack - 0x1800, lanes)
            return {"decoder_peaks": [max(map(abs, lane)) for lane in lanes],
                    "fifo_peaks": [max(map(abs, lane)) for lane in zip(*struct.iter_unpack("<hh", output))],
                    "fifo_frames": len(output) // 4}

        levels = replay_startup(radio_image, "radio", inspect_application=decode)["application"]
        for peaks in (levels["decoder_peaks"], levels["fifo_peaks"]):
            require(peaks[active_side] > 1000 and peaks[1 - active_side] <= 2,
                    f"Expected only channel {active_side} to carry PCM in this capture window: {peaks}")
        cases.append({"capture_slot": start, "usb_slots": count, **report, **levels})
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dongle_fw", type=Path)
    parser.add_argument("controller_radio_fw", type=Path)
    parser.add_argument("--dongle-usb-trace", type=Path, help="Also replay two active windows of the lab USB capture")
    parser.add_argument("--usb-address", type=int, default=9)
    args = parser.parse_args()
    dongle_image = args.dongle_fw.read_bytes()
    radio_image = args.controller_radio_fw.read_bytes()
    encoded = replay_startup(dongle_image, "dongle", inspect_application=encode_link)["application"]
    decoded = replay_startup(radio_image, "radio", inspect_application=lambda cpu: decode_link(cpu, encoded))
    packets = [packet for scene in encoded.values() for packet in scene]
    report = {"encoded_pairs": len(packets), "encoded_bytes": sum(map(len, packets)),
              "encoded_sha256": hashlib.sha256(b"".join(packets)).hexdigest(),
              "decoder_to_fifo": decoded["application"]}
    if args.dongle_usb_trace is not None:
        report["capture_worker"] = replay_capture_worker(dongle_image, radio_image, args.dongle_usb_trace,
                                                        args.usb_address)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

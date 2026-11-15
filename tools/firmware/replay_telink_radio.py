"""Replay the controller Telink's stock decoder, resampler and I2S output."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE
from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3,
    UC_RISCV_REG_MSTATUS, UC_RISCV_REG_RA, UC_RISCV_REG_SP,
)

from replay_telink_startup import replay_startup, require
from replay_telink_usb import install_andes_hooks, run_until


I2S_SETUP = 0x20029E3C
AFTER_I2S_ENABLE = 0x2002A086
PCM_WRITER = 0xF808
PCM_WRITER_CALL_SETUP, PCM_WRITER_RETURN = 0x12B3C, 0x12B48
STEREO_RESAMPLE_CALL_SETUP = 0x12B2C
RESAMPLER_INIT, RESAMPLER_CONTEXT = 0x20022728, 0x90038
PCM_WRITE_CURSOR = 0x851CC
I2S_FIFO = 0x80120000
DMA_REGISTERS = 0x80100444
DMA_DLM_ALIAS = 0xC0180000
FRAME_BYTES = 4
CALLER_PCM_OFFSET = 0x3E0
DECODER_INIT, DECODE_FRAME = 0x20025618, 0x2002656C
DECODER_CONTEXT_SLOT = 0x85468
# First frames of 200 Hz and 1,000 Hz, 20,000-count synthetic sines
# encoded by liblc3 v1.1.3 at 48 kHz, 5 ms, 35 bytes.
REFERENCE_LC3_TONES = (
    bytes.fromhex("15d19b0c673779a3c5872e7a8735e727fcc45881ff3a70d0a77b4006b4fd0806fdb884"),
    bytes.fromhex("089071b056113aea314bbae7fdacc43d59de8559aaab4b33d4436c70d6e16113d6e934"),
)


def dma_descriptor(cpu, channel, expected):
    address = DMA_REGISTERS + channel * 0x14
    control, source, destination, count, next_link = struct.unpack("<5I", cpu.mem_read(address, 20))
    span = (control, source, destination, count, next_link)
    require(span == expected, f"Unexpected stock I2S DMA channel {channel} registers")
    require(struct.unpack("<5I", cpu.mem_read(next_link - DMA_DLM_ALIAS, 20)) == span,
            f"Stock DMA channel {channel} descriptor does not loop to itself")
    return {"channel": channel, "control": control, "source": source, "destination": destination,
            "frame_words": count, "descriptor": next_link - DMA_DLM_ALIAS, "self_link": True}


def fifo_input(cpu, tx, count):
    """Read DMA words in order and reload the stock self-linked descriptor."""
    source_register = DMA_REGISTERS + tx["channel"] * 0x14 + 4
    pointer, = struct.unpack("<I", cpu.mem_read(source_register, 4))
    limit = tx["source"] + tx["frame_words"] * FRAME_BYTES
    output = bytearray()
    reloads = 0
    require(tx["source"] <= pointer < limit and pointer % FRAME_BYTES == 0, "Invalid DMA source cursor")
    while count:
        words = min(count, (limit - pointer) // FRAME_BYTES)
        size = words * FRAME_BYTES
        output.extend(cpu.mem_read(pointer - DMA_DLM_ALIAS, size))
        pointer += size
        count -= words
        if pointer == limit:
            control, pointer, destination, capacity, link = struct.unpack(
                "<5I", cpu.mem_read(tx["descriptor"], 20))
            require((control, pointer, destination, capacity, link) ==
                    (tx["control"], tx["source"], tx["destination"], tx["frame_words"],
                     tx["descriptor"] + DMA_DLM_ALIAS), "Invalid I2S transmit DMA reload")
            reloads += 1
    cpu.mem_write(source_register, struct.pack("<I", pointer))
    return output, reloads


def resample_to_fifo(cpu, tx, caller_stack, pcm):
    # At this site the two mono inputs have already been processed and
    # interleaved. Stock prepares its in-place 40-frame resampler call.
    address = caller_stack + CALLER_PCM_OFFSET
    cpu.mem_write(address, pcm)
    cpu.reg_write(UC_RISCV_REG_SP, caller_stack)
    run_until(cpu, STEREO_RESAMPLE_CALL_SETUP, PCM_WRITER_CALL_SETUP, vendor_budget=16_384)
    count = cpu.reg_read(UC_RISCV_REG_A0)
    require(count == 40, "Stock controller resampler returned the wrong block size")
    resampled = bytes(cpu.mem_read(address, count * FRAME_BYTES))
    run_until(cpu, PCM_WRITER_CALL_SETUP, PCM_WRITER_RETURN, vendor_budget=2048)
    output, _ = fifo_input(cpu, tx, count)
    return resampled, output


def replay_resampled_pcm(cpu, tx, caller_stack):
    # The stock initializer takes a signed adjustment relative to 1,000,000.
    # Use its nominal value and let stock floating-point code build the table.
    cpu.reg_write(UC_RISCV_REG_MSTATUS, cpu.reg_read(UC_RISCV_REG_MSTATUS) | 0x6000)
    cpu.reg_write(UC_RISCV_REG_A0, 0)
    cpu.reg_write(UC_RISCV_REG_SP, caller_stack)
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    run_until(cpu, RESAMPLER_INIT, 0x500, vendor_budget=250_000)
    require(cpu.mem_read(RESAMPLER_CONTEXT + 0x10, 4) == b"\x01\x00\x00\x00",
            "Stock controller resampler did not initialize")

    cases = [(0, 0), (21_000, 0), (0, 0), (0, -20_000), (0, 0), (0, 0)]
    blocks = []
    for left, right in cases:
        resampled, output = resample_to_fifo(cpu, tx, caller_stack, struct.pack("<hh", left, right) * 40)
        require(output == resampled, "Controller resampler output changed before the modeled FIFO")
        blocks.append(tuple(struct.iter_unpack("<hh", output)))
    silence, left_step, left_tail, right_step, right_tail, drained = blocks
    require(all(left == right == 0 for left, right in silence + drained), "Unexpected resampler output in silence")
    require(all(right == 0 for _, right in left_step + left_tail)
            and all(left == 0 for left, _ in right_step + right_tail), "Stock controller resampler mixed channels")
    for channel, step, tail in ((0, left_step, left_tail), (1, right_step, right_tail)):
        require(abs(step[13][channel]) < 1000 and abs(step[14][channel]) > 18_000
                and abs(tail[13][channel]) > 18_000 and tail[-1][channel] == 0,
                "Unexpected controller resampler step response")
    return {"initializer": hex(RESAMPLER_INIT), "resampler": "0x20022374",
            "call_setup": hex(STEREO_RESAMPLE_CALL_SETUP), "frames": len(cases) * 40,
            "step_crossing_frame_zero_based": 14, "opposite_channel_silent": True, "tail_returns_to_zero": True}


def replay_decoder(cpu, stack, tx, caller_stack):
    """Compare what the board-configured decoder writes and drives to the FIFO."""
    def call(entry, *arguments):
        cpu.reg_write(UC_RISCV_REG_SP, stack)
        cpu.reg_write(UC_RISCV_REG_RA, 0x500)
        for register, value in zip((UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3),
                                   arguments):
            cpu.reg_write(register, value)
        run_until(cpu, entry, 0x500, vendor_budget=200_000)
        return cpu.reg_read(UC_RISCV_REG_A0)

    context, = struct.unpack("<I", cpu.mem_read(DECODER_CONTEXT_SLOT, 4))
    require(context == 0x89438, "Stock board setup did not publish its decoder context")
    channels = []
    for channel in range(2):
        header = context + channel * 0x1554
        decoder, scratch, _, samples = struct.unpack("<4I", cpu.mem_read(header + 4, 16))
        require(decoder == header + 0x14 and scratch != 0 and samples == 240,
                f"Stock decoder channel {channel} has an unexpected layout")
        channels.append({"channel": channel, "state": hex(decoder), "samples": samples})

    input_address, output_address = 0x98000, 0x98200
    sample_bytes = 480
    read_offsets, written_offsets = set(), set()

    def record_input(_, access, address, size, value, data):
        offset = address - input_address
        read_offsets.update(range(offset, offset + size))

    def record_output(_, access, address, size, value, data):
        offset = address - output_address
        written_offsets.update(range(offset, offset + size))

    cpu.hook_add(UC_HOOK_MEM_READ, record_input, begin=input_address, end=output_address - 1)
    cpu.hook_add(UC_HOOK_MEM_WRITE, record_output, begin=output_address - 4, end=output_address + sample_bytes + 3)
    guard = b"\xA5" * 4
    untouched = guard * (sample_bytes // 4)
    cases = []

    def decode(channel, name, payload, flag):
        read_offsets.clear()
        written_offsets.clear()
        pointer = 0
        if payload is not None:
            pointer = input_address
            cpu.mem_write(pointer, payload)
        cpu.mem_write(output_address - 4, guard + untouched + guard)
        status = call(DECODE_FRAME, channel, pointer, output_address, flag)
        expected_writes = set() if payload is None else set(range(sample_bytes))
        require(status == 35 and read_offsets <= set(range(35)) and written_offsets == expected_writes
                and cpu.mem_read(output_address - 4, 4) == guard
                and cpu.mem_read(output_address + sample_bytes, 4) == guard,
                f"Stock decoder channel {channel} changed a buffer boundary in {name}")
        output = bytes(cpu.mem_read(output_address, sample_bytes))
        samples = tuple(sample for sample, in struct.iter_unpack("<h", output)) if expected_writes else ()
        cases.append({"channel": channel, "scenario": name, "return_value": status,
                      "input_bytes_read": len(read_offsets), "output_samples_written": len(samples),
                      "pcm_peak": max(map(abs, samples), default=None)})
        return output, samples

    tones = []
    for channel in range(2):
        output, _ = decode(channel, "null_pointer", None, 0)
        require(output == untouched and not read_offsets, "A null packet changed the decoder output buffer")
        output, _ = decode(channel, "loss_on_start", b"\xFF" * 35, 1)
        require(output == bytes(sample_bytes) and not read_offsets, "Unexpected output from cold packet loss")
        output, _ = decode(channel, "all_zero_packet", bytes(35), 0)
        require(output == bytes(sample_bytes) and read_offsets, "Unexpected output from the all-zero packet")
        frequency = (200, 1000)[channel]
        _, samples = decode(channel, f"reference_lc3_{frequency}_hz", REFERENCE_LC3_TONES[channel], 0)
        require(read_offsets, "Stock decoder did not inspect the synthetic LC3 frame")
        tones.append(samples)
        decode(channel, "loss_after_tone", bytes(35), 1)
        require(not read_offsets, "Loss path read packet bytes")

    # This fixture interleaves the exact decoded PCM and invokes the real
    # downstream stock call for each 40-frame block. RF workers and their
    # buffering remain outside this fixture.
    decoded = tuple(zip(*tones))
    digest = hashlib.sha256()
    fifo_peaks = [0, 0]
    gated_samples = 0
    for offset in range(0, len(decoded), 40):
        pcm = b"".join(struct.pack("<hh", *frame) for frame in decoded[offset:offset + 40])
        resampled, output = resample_to_fifo(cpu, tx, caller_stack, pcm)
        for (source,), (observed,) in zip(struct.iter_unpack("<h", resampled), struct.iter_unpack("<h", output)):
            expected = source if abs(source) > 1 else 0
            require(observed == expected, f"Unexpected PCM change before the modeled FIFO: {source} -> {observed}")
            gated_samples += observed != source
        digest.update(output)
        for left, right in struct.iter_unpack("<hh", output):
            fifo_peaks[0] = max(fifo_peaks[0], abs(left))
            fifo_peaks[1] = max(fifo_peaks[1], abs(right))
    require(any(fifo_peaks), "Both decoder-to-FIFO channels unexpectedly stayed silent")
    return {"initializer": hex(DECODER_INIT), "decoder": hex(DECODE_FRAME),
            "mode_register_value": 0, "channels": channels, "cases": cases,
            "host_interleaved_to_fifo": {"frames": len(decoded), "pcm_peak": fifo_peaks,
                                         "one_count_samples_zeroed": gated_samples,
                                         "sha256": digest.hexdigest()}}


def initialize_i2s(cpu):
    for base in (0x80120000, 0x80140000):
        cpu.mem_map(base, 0x1000)
    install_andes_hooks(cpu, ((0x508, 0x1345B), (0x20009000, 0x20030FFF)))
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    cpu.reg_write(UC_RISCV_REG_MSTATUS, cpu.reg_read(UC_RISCV_REG_MSTATUS) | 0x6000)
    # Run the stock early-boot debug ring setup before peripheral logs.
    run_until(cpu, 0x2002A198, 0x2002A1AC, vendor_budget=128)
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    # Check I2S registers at the point the firmware enables the interface.
    run_until(cpu, I2S_SETUP, AFTER_I2S_ENABLE, vendor_budget=512)
    require(cpu.mem_read(0x80140500, 2) == b"\x05\xC2"
            and cpu.mem_read(0x80140503, 2) == b"\x02\x2A", "Stock I2S configuration did not reach enable")
    require(cpu.mem_read(0x80140334, 2) == b"\x00\x55", "Stock port-C pin functions were not selected")
    tx = dma_descriptor(cpu, 3, (0xA48101, 0xC0213878, I2S_FIFO, 512, 0xC021568C))
    rx = dma_descriptor(cpu, 2, (0xAA2201, I2S_FIFO, 0xC0207D24, 512, 0xC0215678))
    run_until(cpu, AFTER_I2S_ENABLE, 0x500, vendor_budget=400_000)
    return tx, rx


def replay_i2s(cpu):
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    tx, rx = initialize_i2s(cpu)
    ring_start = tx["source"] - DMA_DLM_ALIAS
    ring_frames = tx["frame_words"]
    ring_end = ring_start + ring_frames * FRAME_BYTES
    guard = b"\xA5" * FRAME_BYTES
    cpu.mem_write(ring_start - FRAME_BYTES, guard)
    cpu.mem_write(ring_end, guard)
    require(cpu.mem_read(ring_start, ring_frames * FRAME_BYTES) == bytes(ring_frames * FRAME_BYTES),
            "Stock I2S initialization left stale transmit samples")

    stream = bytearray()
    cursor = 0
    descriptor_reloads = 0
    caller_stack = stack - 0x1800
    blocks = []
    for count, left, right in ((192, 18_000, 0), (160, 0, -22_000), (127, -15_000, 21_000),
                               (48, 13_000, -16_000), (96, 0, 0)):
        # Change every nonzero lane so an ordering error cannot pass as
        # long runs of an identical frame. Each input block is synthetic.
        pcm = b"".join(struct.pack("<hh", left + (i if left else 0), right - (i if right else 0))
                       for i in range(count))
        # The receive path passes its output count and a stereo stack
        # buffer to the real writer. Supply only that call-site state.
        cpu.mem_write(caller_stack + CALLER_PCM_OFFSET, pcm)
        cpu.reg_write(UC_RISCV_REG_A0, count)
        cpu.reg_write(UC_RISCV_REG_SP, caller_stack)
        run_until(cpu, PCM_WRITER_CALL_SETUP, PCM_WRITER_RETURN, vendor_budget=4096)
        written, = struct.unpack("<H", cpu.mem_read(PCM_WRITE_CURSOR, 2))
        require(written == cursor + count, "Stock PCM writer did not advance by one word per stereo frame")

        # The host advances the DMA source using the stock count and
        # self-link. The fixture sink represents FIFO input words.
        derived, reloads = fifo_input(cpu, tx, count)
        require(derived == pcm, "Modeled I2S FIFO input differs from the stock writer's PCM input")
        stream.extend(derived)
        descriptor_reloads += reloads
        blocks.append({"frames": count, "ring_word": cursor % ring_frames, "descriptor_reloads": reloads,
                       "silent_left": left == 0, "silent_right": right == 0})
        cursor += count
    resampled = replay_resampled_pcm(cpu, tx, caller_stack)
    decoder = replay_decoder(cpu, stack, tx, caller_stack)
    require(cpu.mem_read(ring_start - FRAME_BYTES, FRAME_BYTES) == guard
            and cpu.mem_read(ring_end, FRAME_BYTES) == guard, "Stock PCM writer changed a ring guard word")
    require(descriptor_reloads == 1, "Expected one stock I2S transmit descriptor reload")
    return {"setup_entry": hex(I2S_SETUP), "checkpoint_after_enable": hex(AFTER_I2S_ENABLE),
            "board_setup_completed": True,
            "tx_dma": tx, "rx_dma": rx, "pcm_writer": hex(PCM_WRITER),
            "pcm_writer_call_setup": hex(PCM_WRITER_CALL_SETUP), "blocks": blocks,
            "fifo_input_bytes": len(stream), "fifo_input_sha256": hashlib.sha256(stream).hexdigest(),
            "resampler_to_fifo": resampled, "decoder_fixtures": decoder}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("controller_radio_fw", type=Path)
    args = parser.parse_args()
    result = replay_startup(args.controller_radio_fw.read_bytes(), "radio", inspect_application=replay_i2s)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()

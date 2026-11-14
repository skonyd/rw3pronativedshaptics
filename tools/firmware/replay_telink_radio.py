"""Replay the controller Telink's stock I2S setup and PCM writer offline."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn.riscv_const import UC_RISCV_REG_A0, UC_RISCV_REG_MSTATUS, UC_RISCV_REG_RA, UC_RISCV_REG_SP

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

    pcm = caller_stack + CALLER_PCM_OFFSET
    cases = [(0, 0), (21_000, 0), (0, 0), (0, -20_000), (0, 0), (0, 0)]
    blocks = []
    for left, right in cases:
        cpu.mem_write(pcm, struct.pack("<hh", left, right) * 40)
        # At this site the two mono inputs have already been processed
        # and interleaved. Stock prepares the in-place resampler call.
        run_until(cpu, STEREO_RESAMPLE_CALL_SETUP, PCM_WRITER_CALL_SETUP, vendor_budget=16_384)
        count = cpu.reg_read(UC_RISCV_REG_A0)
        require(count == 40, "Stock controller resampler returned the wrong block size")
        result = cpu.mem_read(pcm, count * FRAME_BYTES)
        run_until(cpu, PCM_WRITER_CALL_SETUP, PCM_WRITER_RETURN, vendor_budget=2048)
        output, _ = fifo_input(cpu, tx, count)
        require(output == result, "Controller resampler output changed before the modeled FIFO")
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


def replay_i2s(cpu):
    for base in (0x80120000, 0x80140000):
        cpu.mem_map(base, 0x1000)
    install_andes_hooks(cpu, ((0x508, 0x1345B), (0x20009000, 0x20030FFF)))
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    # Stop immediately after the firmware enables I2S. The rest of the
    # board setup is outside this fixture.
    run_until(cpu, I2S_SETUP, AFTER_I2S_ENABLE, vendor_budget=512)
    require(cpu.mem_read(0x80140500, 2) == b"\x05\xC2"
            and cpu.mem_read(0x80140503, 2) == b"\x02\x2A", "Stock I2S configuration did not reach enable")
    require(cpu.mem_read(0x80140334, 2) == b"\x00\x55", "Stock port-C pin functions were not selected")
    tx = dma_descriptor(cpu, 3, (0xA48101, 0xC0213878, I2S_FIFO, 512, 0xC021568C))
    rx = dma_descriptor(cpu, 2, (0xAA2201, I2S_FIFO, 0xC0207D24, 512, 0xC0215678))
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
    require(cpu.mem_read(ring_start - FRAME_BYTES, FRAME_BYTES) == guard
            and cpu.mem_read(ring_end, FRAME_BYTES) == guard, "Stock PCM writer changed a ring guard word")
    require(descriptor_reloads == 1, "Expected one stock I2S transmit descriptor reload")
    return {"setup_entry": hex(I2S_SETUP), "stop_after_enable": hex(AFTER_I2S_ENABLE),
            "tx_dma": tx, "rx_dma": rx, "pcm_writer": hex(PCM_WRITER),
            "pcm_writer_call_setup": hex(PCM_WRITER_CALL_SETUP), "blocks": blocks,
            "fifo_input_bytes": len(stream), "fifo_input_sha256": hashlib.sha256(stream).hexdigest(),
            "resampler_to_fifo": resampled}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("controller_radio_fw", type=Path)
    args = parser.parse_args()
    result = replay_startup(args.controller_radio_fw.read_bytes(), "radio", inspect_application=replay_i2s)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()

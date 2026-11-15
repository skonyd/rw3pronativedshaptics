"""Check stock I2S clock roles and model the Telink receive DMA boundary."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_MEM_READ
from unicorn.riscv_const import UC_RISCV_REG_A0, UC_RISCV_REG_RA, UC_RISCV_REG_S0, UC_RISCV_REG_SP

from replay_telink_radio import DMA_DLM_ALIAS, DMA_REGISTERS, FRAME_BYTES, I2S_FIFO, initialize_i2s
from replay_telink_startup import replay_startup, require
from replay_telink_usb import run_until
from simulate_audio_haptics import STOCK_SHA256, initialize_stock_dma


TELINK_I2S_CONFIG = 0x80140501
NXP_I2S_CONFIG = {"FLEXCOMM6": 0x40097C00, "FLEXCOMM7": 0x40098C00}
NXP_CLOCK_MODES = ("slave", "WS-synchronized master", "SCK-synchronized master", "master")
RX_READER = 0xE658
RX_READER_BUFFER = 0x92C00
RX_RING_CHECK = 0xE706
RX_BLOCK_WORDS = 242
RX_OUTPUT_SAMPLES = 240


def clock_roles(telink, nxp):
    config = telink.mem_read(TELINK_I2S_CONFIG, 1)[0]
    require(config == 0xC2, "Unexpected controller Telink I2S configuration")
    report = {"telink": {"register": hex(TELINK_I2S_CONFIG), "value": hex(config),
                         "rx_clock": "master" if config & 0x40 else "slave",
                         "tx_clock": "master" if config & 0x80 else "slave",
                         "sample_bits": 16, "format": "classic I2S"}}
    for instance, address in NXP_I2S_CONFIG.items():
        config1, config2 = struct.unpack("<II", nxp.mem_read(address, 8))
        require(config1 & 0xC1 == 1, f"{instance} is not enabled in classic I2S mode")
        report[instance] = {"register": hex(address), "config1": hex(config1), "config2": hex(config2),
                            "clock": NXP_CLOCK_MODES[(config1 >> 4) & 3],
                            "sample_bits": ((config1 >> 16) & 0x1F) + 1,
                            "frame_clocks": (config2 & 0x1FF) + 1,
                            "right_in_high_half": not bool(config1 & 0x100)}
    require(report["FLEXCOMM6"]["clock"] == "master" and report["FLEXCOMM7"]["clock"] == "slave",
            "Unexpected NXP I2S clock roles")
    for instance in NXP_I2S_CONFIG:
        port = report[instance]
        require(port["sample_bits"] == 16 and port["frame_clocks"] == 32 and port["right_in_high_half"],
                f"Unexpected {instance} I2S frame or FIFO layout")
    return report


def inject_fifo_words(cpu, descriptor, pcm):
    """Predict the DMA destination for each externally supplied FIFO word."""
    control = descriptor["control"]
    address = descriptor["destination"]
    capacity = descriptor["frame_words"]
    link = descriptor["descriptor"]
    cursor_register = DMA_REGISTERS + descriptor["channel"] * 0x14 + 8
    cursor, = struct.unpack("<I", cpu.mem_read(cursor_register, 4))
    limit = address + capacity * FRAME_BYTES
    require(descriptor["source"] == I2S_FIFO and address <= cursor < limit and cursor % FRAME_BYTES == 0,
            "Invalid I2S receive DMA cursor or FIFO source")
    require(len(pcm) % FRAME_BYTES == 0, "I2S receive input must contain full FIFO words")
    reloads = 0
    for offset in range(0, len(pcm), FRAME_BYTES):
        cpu.mem_write(cursor - DMA_DLM_ALIAS, pcm[offset:offset + FRAME_BYTES])
        cursor += FRAME_BYTES
        if cursor == limit:
            actual = struct.unpack("<5I", cpu.mem_read(link, 20))
            require(actual == (control, I2S_FIFO, address, capacity, link + DMA_DLM_ALIAS),
                    "Invalid controller I2S receive DMA reload")
            cursor = address
            reloads += 1
    cpu.mem_write(cursor_register, struct.pack("<I", cursor))
    return reloads


def replay_receive(cpu, rx, frames):
    ring = rx["destination"] - DMA_DLM_ALIAS
    capacity = rx["frame_words"]
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    reads = []
    cpu.hook_add(UC_HOOK_MEM_READ, lambda _, access, address, size, value, data:
                 reads.append((address, size)), begin=ring, end=ring + capacity * FRAME_BYTES - 1)
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    cpu.reg_write(UC_RISCV_REG_A0, RX_READER_BUFFER)
    run_until(cpu, RX_READER, RX_RING_CHECK, vendor_budget=2048)
    read_cursor = cpu.reg_read(UC_RISCV_REG_S0) + 0x10
    run_until(cpu, RX_RING_CHECK, 0x500, vendor_budget=2048)
    require(cpu.reg_read(UC_RISCV_REG_A0) == 0 and not reads,
            "Stock I2S receive reader accessed an empty ring")

    # Leave a short tail at the end of the buffer and cross the stock link
    # boundary with an asymmetric stereo input. This is a FIFO fixture.
    start = capacity - 10
    cursor_register = DMA_REGISTERS + rx["channel"] * 0x14 + 8
    cpu.mem_write(cursor_register, struct.pack("<I", rx["destination"] + start * FRAME_BYTES))
    cpu.mem_write(read_cursor, struct.pack("<H", start))
    pcm = b"".join(struct.pack("<hh", *frame) for frame in frames)
    require(inject_fifo_words(cpu, rx, pcm[:-FRAME_BYTES]) == 1, "Expected one modeled I2S receive DMA reload")
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    cpu.reg_write(UC_RISCV_REG_A0, RX_READER_BUFFER)
    run_until(cpu, RX_READER, 0x500, vendor_budget=2048)
    require(cpu.reg_read(UC_RISCV_REG_A0) == 0 and not reads and
            struct.unpack("<H", cpu.mem_read(read_cursor, 2))[0] == start,
            "Stock I2S receive reader consumed an incomplete block")
    require(inject_fifo_words(cpu, rx, pcm[-FRAME_BYTES:]) == 0, "Unexpected extra DMA reload")
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    cpu.reg_write(UC_RISCV_REG_A0, RX_READER_BUFFER)
    run_until(cpu, RX_READER, 0x500, vendor_budget=40_000)
    expected_reads = [(ring + ((start + index) % capacity) * FRAME_BYTES, 2) for index in range(len(frames))]
    require(reads == expected_reads, "Stock I2S reader did not consume the low half of each FIFO word in order")
    require(cpu.reg_read(UC_RISCV_REG_A0) == RX_OUTPUT_SAMPLES,
            "Stock I2S receive reader returned the wrong mono block size")
    require(struct.unpack("<H", cpu.mem_read(read_cursor, 2))[0] == (start + len(frames)) % capacity,
            "Stock I2S receive reader retained the wrong cursor")
    output = bytes(cpu.mem_read(RX_READER_BUFFER, RX_OUTPUT_SAMPLES * 2))
    samples = [sample for sample, in struct.iter_unpack("<h", output)]
    return {"pending_words": len(frames), "start_word": start, "descriptor_reloads": 1,
            "empty_reads": 0, "underfilled_reads": 0, "low_half_words_read": len(reads),
            "mono_samples": len(samples), "mono_peak": max(map(abs, samples)),
            "mono_pcm_sha256": hashlib.sha256(output).hexdigest()}


def replay_i2s(cpu, nxp_cpu, frames):
    _, receive_descriptor = initialize_i2s(cpu)
    return {"clock_roles": clock_roles(cpu, nxp_cpu), "receive": replay_receive(cpu, receive_descriptor, frames)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("controller_radio_fw", type=Path)
    parser.add_argument("controller_nxp_fw", type=Path)
    args = parser.parse_args()
    nxp = args.controller_nxp_fw.read_bytes()
    require(hashlib.sha256(nxp).hexdigest() == STOCK_SHA256, "Unexpected NXP stock image")
    radio = args.controller_radio_fw.read_bytes()
    nxp_cpu = initialize_stock_dma(nxp, None).cpu
    baseline = [(12000 + index, -19000 + index) for index in range(RX_BLOCK_WORDS)]
    fixtures = {"baseline": baseline, "high_half_changed": [(left, -right) for left, right in baseline],
                "low_half_silent": [(0, right) for _, right in baseline]}
    reports = {name: replay_startup(radio, "radio", lambda cpu: replay_i2s(cpu, nxp_cpu, frames))["application"]
               for name, frames in fixtures.items()}
    receive = {name: result["receive"] for name, result in reports.items()}
    require(receive["baseline"]["mono_peak"] > 0 and receive["low_half_silent"]["mono_peak"] == 0,
            "Stock mono output did not isolate the low half-word")
    require(receive["high_half_changed"] == receive["baseline"],
            "Changing only the high half-word affected the stock mono output")
    print(json.dumps({"stock_startup_clock_roles": reports["baseline"]["clock_roles"],
                      "telink_receive_fifo_fixtures": receive}, indent=2))


if __name__ == "__main__":
    main()

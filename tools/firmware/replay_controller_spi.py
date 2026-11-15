"""Replay the stock Telink and NXP SPI control transaction offline."""
import argparse
import functools
import hashlib
import json
import operator
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_WRITE
from unicorn.riscv_const import UC_RISCV_REG_A0, UC_RISCV_REG_RA, UC_RISCV_REG_S1, UC_RISCV_REG_SP

from audit_dev2_hex import STOCK_SHA256
from replay_telink_radio import initialize_i2s
from replay_telink_startup import replay_startup, require
from replay_telink_usb import run_until
from simulate_audio_haptics import RETURN, Replay, model_syscon_reset


TELINK_SPI, TELINK_DMA, PSPI = 0x20029844, 0x80100444, 0x80140040
SPI_FIFO, DMA_RAM_ALIAS = 0x00140048, 0xC0180000
COMMAND_BUFFER, REPLY_BUFFER = 0x85354, 0x957C4
REPLY_QUEUE, REPLY_CHECK = 0x852F0, 0x20028958
NXP_SPI, NXP_DMA, NXP_TABLE = 0x40096000, 0x40082000, 0x2000B000
NXP_RX_CHANNEL, NXP_TX_CHANNEL = 10, 11
NXP_RX_BUFFER, NXP_TX_BUFFER = 0x2000515C, 0x200051CC
NXP_DMA_IRQ, NXP_PACKET = 0xC478, 0x1FFC4
SPI_BYTES = 100


def read_words(cpu, address, count):
    return struct.unpack(f"<{count}I", cpu.mem_read(address, 4 * count))


def telink_transfer(cpu, stack, cold):
    """Observe the stock peripheral writes and return the DMA TX source bytes."""
    transfers = []
    starts = []

    def capture_dma(machine, access, address, size, value, data):
        if value & 1:
            transfers.append(read_words(machine, TELINK_DMA + 4, 3))

    hook = cpu.hook_add(UC_HOOK_MEM_WRITE, capture_dma, begin=TELINK_DMA, end=TELINK_DMA)
    start_hook = cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data: starts.append(value),
                              begin=PSPI + 6, end=PSPI + 6)
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_RA, 0x500)
    cpu.reg_write(UC_RISCV_REG_A0, int(cold))
    try:
        run_until(cpu, TELINK_SPI, 0x500, vendor_budget=8192)
    finally:
        cpu.hook_del(start_hook)
        cpu.hook_del(hook)
    sent = 4 if cold else SPI_BYTES
    expected = [(COMMAND_BUFFER + DMA_RAM_ALIAS, SPI_FIFO, sent // 4)]
    if cold:
        expected.append((SPI_FIFO, REPLY_BUFFER + DMA_RAM_ALIAS, (SPI_BYTES - sent) // 4))
    require(transfers == expected and starts == [0], "Unexpected stock Telink SPI DMA plan")
    spi_counts = cpu.mem_read(PSPI + 3, 2)
    require(spi_counts[0] == sent - 1 and (not cold or spi_counts[1] == SPI_BYTES - sent - 1),
            "Stock PSPI byte counts do not match DMA")
    source = transfers[0][0] - DMA_RAM_ALIAS
    return bytes(cpu.mem_read(source, sent))


def nxp_slave(image):
    replay = Replay(image)
    cpu = replay.cpu
    model_syscon_reset(cpu)
    for page in (0x40001000, NXP_DMA, NXP_SPI, 0xE000E000):
        cpu.mem_map(page, 0x1000)
    cpu.mem_write(NXP_DMA + 8, struct.pack("<I", NXP_TABLE))
    replay.call(0x13B90, RETURN)
    return replay


def exchange_nxp(replay, data, order, *, valid=True):
    """Move bus bytes via the configured descriptors and deliver each DMA completion."""
    cpu = replay.cpu
    rx = read_words(cpu, NXP_TABLE + 16 * NXP_RX_CHANNEL, 4)
    tx = read_words(cpu, NXP_TABLE + 16 * NXP_TX_CHANNEL, 4)
    require(rx == (0x634011, NXP_SPI + 0xE30, NXP_RX_BUFFER + SPI_BYTES - 1, 0)
            and tx == (0x631011, NXP_TX_BUFFER + SPI_BYTES - 1, NXP_SPI + 0xE20, 0),
            "Unexpected stock FLEXCOMM5 SPI descriptors")
    require(len(data) == SPI_BYTES, "SPI bus transfer length differs from stock NXP DMA length")
    response = bytes(cpu.mem_read(tx[1] + 1 - len(data), len(data)))
    cpu.mem_write(rx[2] + 1 - len(data), data)
    cpu.mem_write(NXP_DMA + 0x40, bytes(4))
    cpu.mem_write(NXP_DMA + 0x48, struct.pack("<I", (1 << NXP_RX_CHANNEL) | (1 << NXP_TX_CHANNEL)))
    handled = []
    armed = []
    packet_hook = cpu.hook_add(UC_HOOK_CODE, lambda machine, *_: handled.append(
        bytes(machine.mem_read(NXP_RX_BUFFER, SPI_BYTES))), begin=NXP_PACKET, end=NXP_PACKET)
    rearm_hook = cpu.hook_add(UC_HOOK_CODE, lambda *_: armed.append(True), begin=0x20240, end=0x20240)
    try:
        for index, channel in enumerate(order):
            cpu.mem_write(NXP_DMA + 0x58, struct.pack("<I", 1 << channel))
            replay.call(NXP_DMA_IRQ, RETURN, instruction_limit=50_000)
            if index == 0:
                require(not handled and not armed, "NXP handled the transaction before both DMA completions")
    finally:
        cpu.hook_del(rearm_hook)
        cpu.hook_del(packet_hook)
    require(handled == [data] and len(armed) == int(valid),
            f"Unexpected stock NXP SPI handling, captured {len(handled)} callbacks and {len(armed)} rearms")
    require(cpu.mem_read(0x20005019, 1) == bytes([int(not valid)]), "Unexpected NXP SPI framing error flag")
    return response


def parse_telink_reply(cpu, stack, response, *, valid):
    source, destination, words = read_words(cpu, TELINK_DMA + 4, 3)
    require((source, destination, words) == (SPI_FIFO, REPLY_BUFFER + DMA_RAM_ALIAS, 24),
            "Stock Telink did not arm its 96-byte SPI receive descriptor")
    bus_data = response[4:]
    cpu.mem_write(destination - DMA_RAM_ALIAS, bus_data)
    write_before, read_before = cpu.mem_read(REPLY_QUEUE + 6, 2)
    parsed = []
    targets = []
    parse = cpu.hook_add(UC_HOOK_CODE, lambda machine, *_: parsed.append(
        bytes(machine.mem_read(machine.reg_read(UC_RISCV_REG_S1), 2 + len(bus_data)))),
        begin=REPLY_CHECK, end=REPLY_CHECK)
    branches = [cpu.hook_add(UC_HOOK_CODE, lambda _, pc, *args: targets.append(pc), begin=pc, end=pc)
                for pc in (0x20028C84, 0x20028968)]
    try:
        telink_transfer(cpu, stack, False)
    finally:
        for hook in branches:
            cpu.hook_del(hook)
        cpu.hook_del(parse)
    require(parsed == [struct.pack("<H", len(bus_data)) + bus_data]
            and targets == [0x20028C84 if valid else 0x20028968],
            "Stock Telink SPI reply was not queued or validated as expected")
    require(cpu.mem_read(REPLY_QUEUE + 6, 2) == bytes([(write_before + 1) & 255, (read_before + 1) & 255]),
            "Stock Telink SPI queue did not produce and retire the reply")
    return bus_data


def replay_spi(cpu, nxp_image):
    stack = cpu.reg_read(UC_RISCV_REG_SP) - 0x1800
    initialize_i2s(cpu)
    nxp = nxp_slave(nxp_image)
    cold = telink_transfer(cpu, stack, True)
    require(cold == bytes.fromhex("04123080"), "Wrong stock PSPI cold header")
    first = exchange_nxp(nxp, cold + bytes(96), (NXP_RX_CHANNEL, NXP_TX_CHANNEL))
    require(first == bytes(SPI_BYTES), "NXP SPI cold transaction did not start with empty output")
    normal = telink_transfer(cpu, stack, False)
    require(normal[:6] == bytes.fromhex("04123000bb00"), "Wrong stock PSPI normal header")
    exchange_nxp(nxp, normal, (NXP_TX_CHANNEL, NXP_RX_CHANNEL))
    expected = bytes(nxp.cpu.mem_read(NXP_TX_BUFFER, SPI_BYTES))
    check = functools.reduce(operator.xor, expected[8:98], 0)
    require(expected[4] == 0xAA and expected[98] == check and expected[99] == 1,
            "Stock NXP response did not contain its normal header, checksum and counter")

    # The NXP shifts the previous response while receiving the next command.
    # The stock Telink cold exchange reads the 96 bytes after its four-byte
    # write phase. The dummy MOSI bytes and SPI clocks are modeled here.
    following = telink_transfer(cpu, stack, True)
    response = exchange_nxp(nxp, following + bytes(96), (NXP_RX_CHANNEL, NXP_TX_CHANNEL))
    require(response == expected, "Stock NXP prepared response changed on the SPI bus")
    bus_data = parse_telink_reply(cpu, stack, response, valid=True)

    telink_transfer(cpu, stack, True)
    corrupt_reply = response[:98] + bytes([response[98] ^ 1]) + response[99:]
    parse_telink_reply(cpu, stack, corrupt_reply, valid=False)

    corrupt = bytes([cold[0] ^ 1]) + cold[1:] + bytes(96)
    exchange_nxp(nxp_slave(nxp_image), corrupt, (NXP_TX_CHANNEL, NXP_RX_CHANNEL), valid=False)
    return {"cold_header": cold.hex(), "normal_header": normal[:6].hex(), "nxp_response_header": expected[:8].hex(),
            "nxp_response_checksum": expected[98], "nxp_response_sequence": expected[99],
            "nxp_spi": "FLEXCOMM5", "nxp_dma_channels": [NXP_RX_CHANNEL, NXP_TX_CHANNEL],
            "telink_rx_bytes": len(bus_data), "telink_rx_sha256": hashlib.sha256(bus_data).hexdigest(),
            "invalid_header_rejected": True, "invalid_reply_checksum_rejected": True,
            "both_completion_orders": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("controller_radio_fw", type=Path)
    parser.add_argument("controller_nxp_fw", type=Path)
    args = parser.parse_args()
    nxp = args.controller_nxp_fw.read_bytes()
    require(hashlib.sha256(nxp).hexdigest() == STOCK_SHA256, "Unexpected NXP stock image")
    report = replay_startup(args.controller_radio_fw.read_bytes(), "radio",
                            inspect_application=lambda cpu: replay_spi(cpu, nxp))
    print(json.dumps(report["application"], indent=2))


if __name__ == "__main__":
    main()

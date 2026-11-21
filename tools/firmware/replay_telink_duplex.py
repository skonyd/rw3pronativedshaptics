"""Replay an audio retransmission and the stock controller reply offline."""
import argparse
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE
from unicorn.riscv_const import UC_RISCV_REG_RA, UC_RISCV_REG_S0, UC_RISCV_REG_SP

from replay_telink_radio import initialize_i2s
from replay_telink_rf import (
    AUDIO_BYTES, AUDIO_OFFSET, DMA_RAM_ALIAS, RX_STATE, SLOT_BYTES,
    TX_DMA_SOURCE, TX_SLOTS, TX_STATE, receive_audio, run_receive_irq, select_audio, transmit_audio,
)
from replay_telink_startup import replay_startup, require
from replay_telink_usb import andes_next, install_andes_hooks, run_until


DONGLE_RF_IRQ, IRQ_STATUS, REPLY_BUFFER_INSN = 0x3B08, 0x80170220, 0x3B70
CONTROLLER_REPLY_TX = 0x10A40


def deliver_reply(cpu, reply, pending=1, *, crc_error=False, receive_quality=0):
    """Feed a controller reply through the dongle's stock interrupt routine."""
    # Stock sets s0 to the controller-response DMA buffer with ADDIGP.
    andes_next(cpu, REPLY_BUFFER_INSN)
    buffer = cpu.reg_read(UC_RISCV_REG_S0)
    cpu.mem_write(buffer, struct.pack("<I", reply[5] + 13) + reply[4:] + bytes(9) +
                  bytes([receive_quality, int(crc_error)]))
    writes = []

    def read_status(machine, access, address, size, value, data):
        machine.mem_write(address, struct.pack("<H", pending))

    def clear_status(machine, access, address, size, value, data):
        nonlocal pending
        writes.append(value)
        pending &= ~value

    read_hook = cpu.hook_add(UC_HOOK_MEM_READ, read_status, begin=IRQ_STATUS, end=IRQ_STATUS)
    write_hook = cpu.hook_add(UC_HOOK_MEM_WRITE, clear_status, begin=IRQ_STATUS, end=IRQ_STATUS)
    expected = [pending] if pending else []
    try:
        cpu.reg_write(UC_RISCV_REG_RA, 0x700)
        run_until(cpu, DONGLE_RF_IRQ, 0x700, vendor_budget=8192)
    finally:
        cpu.hook_del(write_hook)
        cpu.hook_del(read_hook)
    require(writes == expected and not pending, "Unexpected stock RF interrupt acknowledgement")


def controller_replies(cpu, original, retry):
    initialize_i2s(cpu)
    stack = cpu.reg_read(UC_RISCV_REG_SP) - 0x1800
    cpu.mem_write(RX_STATE + 0xBC, b"\x01\x01")
    reply_calls = []
    hook = cpu.hook_add(UC_HOOK_CODE, lambda *_: reply_calls.append(True),
                        begin=CONTROLLER_REPLY_TX, end=CONTROLLER_REPLY_TX)

    def rf_reply():
        source, = struct.unpack("<I", cpu.mem_read(TX_DMA_SOURCE, 4))
        address = source - DMA_RAM_ALIAS
        reply = bytes(cpu.mem_read(address, 6))
        require(reply[5] == 0 and struct.unpack_from("<I", reply)[0] == 0x800001,
                "Unexpected stock controller reply format")
        return reply

    try:
        slot = receive_audio(cpu, stack, original)
        require(len(reply_calls) == 1, "Initial packet did not cause one stock reply")
        first = rf_reply()
        before = bytes(cpu.mem_read(slot, SLOT_BYTES))
        require(not run_receive_irq(cpu, stack, retry), "Retry was queued twice")
        require(len(reply_calls) == 2 and cpu.mem_read(slot, SLOT_BYTES) == before,
                "Retry did not cause a fresh reply with an unchanged audio slot")
        retransmission = rf_reply()
        require(retransmission == first, "Controller changed the reply to the same audio packet")
        return retransmission
    finally:
        cpu.hook_del(hook)


def replay_duplex(cpu, radio_image):
    for address in (0x80140000, 0x80170000):
        cpu.mem_map(address, 0x1000)
    install_andes_hooks(cpu, ((0x800, 0x29723),))
    payload = bytes(range(AUDIO_BYTES))
    cpu.mem_write(TX_SLOTS + SLOT_BYTES + AUDIO_OFFSET, payload)
    original = transmit_audio(cpu, 1, payload)
    busy = bytes(cpu.mem_read(TX_STATE + 0x24, 1))
    require(busy == b"\x02", "Stock dongle did not retain the sent audio slot")

    # Advance to the next RF subslot while withholding the first reply.
    cpu.mem_write(TX_STATE + 8, struct.pack("<H", 9))
    retry = select_audio(cpu, 1, payload)
    require(retry[:6] == original[:6] and retry[8:] == original[8:]
            and retry[6:8] == b"\x09\x00", "Stock retry changed more than its send counter")
    reply = replay_startup(radio_image, "radio", inspect_application=lambda radio:
                           controller_replies(radio, original, retry))["application"]
    wrong_sequence = reply[:4] + bytes([reply[4] ^ 2]) + reply[5:]
    for response, pending, crc in ((reply, 0, False), (reply, 2, False),
                                   (reply, 1, True), (wrong_sequence, 1, False)):
        deliver_reply(cpu, response, pending=pending, crc_error=crc)
        require(cpu.mem_read(TX_STATE + 0x24, 1) == busy, "Nonmatching event retired the audio slot")
    deliver_reply(cpu, reply)
    require(cpu.mem_read(TX_STATE + 0x24, 1) == b"\x00", "Stock reply did not retire the audio slot")

    following = bytes(range(AUDIO_BYTES, AUDIO_BYTES * 2))
    cpu.mem_write(TX_SLOTS + 2 * SLOT_BYTES + AUDIO_OFFSET, following)
    cpu.mem_write(TX_STATE + 8, struct.pack("<H", 16))
    next_frame = select_audio(cpu, 2, following)
    require(cpu.mem_read(TX_STATE + 0x24, 1) == b"\x04", "Next transmission did not become pending")
    return {"original_header": original[:AUDIO_OFFSET].hex(), "retry_header": retry[:AUDIO_OFFSET].hex(),
            "controller_reply": reply.hex(), "next_header": next_frame[:AUDIO_OFFSET].hex(),
            "controller_reply_count": 2, "controller_audio_copies": 1,
            "ignored_events": ["no_pending", "local_tx_irq", "crc_error", "wrong_reply_sequence"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dongle_fw", type=Path)
    parser.add_argument("controller_radio_fw", type=Path)
    args = parser.parse_args()
    radio = args.controller_radio_fw.read_bytes()
    report = replay_startup(args.dongle_fw.read_bytes(), "dongle",
                            inspect_application=lambda cpu: replay_duplex(cpu, radio))
    print(json.dumps(report["application"], indent=2))


if __name__ == "__main__":
    main()

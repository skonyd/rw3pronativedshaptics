"""Replay the stock B91 dongle endpoint-6 handler with captured USB slots."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ
from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_GP,
    UC_RISCV_REG_PC, UC_RISCV_REG_RA, UC_RISCV_REG_SP, UC_RISCV_REG_X0,
)

from replay_telink_startup import execute_addigp, field, replay_startup, require
from usb_audio_trace import inspect


USB_DISPATCH, USB_HANDLER, EVENT_CALL = 0x3C60, 0x51B8, 0x5310
ENDPOINT_STATUS = 0x80100841
ENDPOINT_LENGTH, ENDPOINT_STREAM = 0x80100816, 0x80100826
RING_STATE, RING_WORDS, RING_SIZE = 0x90A2C, 0x91254, 1024


def andes_next(cpu, pc):
    """Execute the Andes instructions reached by this handler."""
    word, = struct.unpack("<I", cpu.mem_read(pc, 4))
    opcode = word & 0x707F
    dest = field(word, 7, 5)
    left = cpu.reg_read(UC_RISCV_REG_X0 + field(word, 15, 5))
    right = cpu.reg_read(UC_RISCV_REG_X0 + field(word, 20, 5))
    if word & 0x307F == 0x100B:
        execute_addigp(cpu, word)
        return pc + 4
    if word & 0x307F == 0x200B:
        offset = (field(word, 14, 1) | field(word, 21, 10) << 1 | field(word, 20, 1) << 11 |
                  field(word, 17, 3) << 12 | field(word, 15, 2) << 15) - (field(word, 31, 1) << 17)
        result = cpu.mem_read(cpu.reg_read(UC_RISCV_REG_GP) + offset, 1)[0]
    elif opcode in (0x505B, 0x605B, 0x705B):
        constant = field(word, 20, 5) | field(word, 7, 1) << 5
        if opcode == 0x705B:
            require(constant < 32, "Invalid RV32 bit-test immediate")
            take = field(left, constant, 1) == field(word, 30, 1)
        else:
            constant |= field(word, 30, 1) << 6
            take = (left == constant) == (opcode == 0x505B)
        offset = (field(word, 8, 4) << 1 | field(word, 25, 5) << 5) - (field(word, 31, 1) << 10)
        return pc + offset if take else pc + 4
    elif opcode in (0x205B, 0x305B):
        upper, lower = field(word, 26, 5), field(word, 20, 5)
        if upper == 0:
            result, sign_bit = field(left, 0, 1) << lower, lower
        elif upper < lower:
            result, sign_bit = field(left, 0, lower - upper + 1) << upper, lower
        else:
            result, sign_bit = field(left, lower, upper - lower + 1), upper - lower
        if opcode == 0x305B and result & (1 << sign_bit):
            result -= 1 << (sign_bit + 1)
    elif word & 0xFE00707F == 0x0C00005B:
        result = left + (right << 2)
    elif opcode == 0x402B:
        offset = (field(word, 9, 3) << 2 | field(word, 25, 6) << 5 | field(word, 7, 1) << 11 |
                  field(word, 17, 3) << 12 | field(word, 15, 2) << 15 |
                  field(word, 8, 1) << 17) - (field(word, 31, 1) << 18)
        cpu.mem_write(cpu.reg_read(UC_RISCV_REG_GP) + offset, struct.pack("<I", right))
        return pc + 4
    else:
        raise ValueError(f"Unsupported Andes instruction 0x{word:08X} at 0x{pc:X}")
    cpu.reg_write(UC_RISCV_REG_X0 + dest, result & 0xFFFFFFFF)
    return pc + 4


def run_until(cpu, pc, end, vendor_budget):
    for _ in range(vendor_budget):
        cpu.emu_start(pc, end, count=4000)
        pc = cpu.reg_read(UC_RISCV_REG_PC)
        if pc == end:
            return
        pc = andes_next(cpu, pc)
    raise ValueError(f"Stock routine did not reach 0x{end:X}")


def replay_pcm_split(cpu, source, pcm, stack):
    """Run the stereo split loop of the stock downstream processor."""
    context, left, right = 0xA1000, 0xA2000, 0xA2200
    # The downstream caller normally supplies an initialized context.
    # This fixture exercises only the input loop and its two destinations.
    cpu.mem_write(context + 0xBB8, struct.pack("<II", left, right))
    for address in (left, right):
        cpu.mem_write(address, b"\xA5" * 152)
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    for register, value in ((UC_RISCV_REG_A0, context), (UC_RISCV_REG_A1, source), (UC_RISCV_REG_A2, 48)):
        cpu.reg_write(register, value)
    run_until(cpu, 0x18AB8, 0x18B28, vendor_budget=8)
    for side, address in enumerate((left, right)):
        expected = b"".join(pcm[index + side * 2:index + side * 2 + 2] for index in range(0, len(pcm), 4))
        actual = bytes(cpu.mem_read(address, 152))
        require(actual == b"\xA5" * 54 + expected + b"\xA5" * 2, "Stock stereo input loop changed or mixed channels")
    return {"routine": "0x18ab8", "end_before_processing": "0x18b28", "source_ring_address": hex(source),
            "sample_frames": 48, "separate_channels_preserved": True}


def replay_endpoint(cpu, slots):
    require(bool(slots), "Expected at least one USB audio slot")
    cpu.mem_map(0x80101000, 4096)
    cpu.mem_map(0x80140000, 4096)
    # USB configured, audio enabled, and GIP's six-byte prefix selected.
    cpu.mem_write(0x87F08, b"\x01")
    cpu.mem_write(0x8E0A1, b"\x20")
    cpu.mem_write(0x8E0B0, struct.pack("<I", 1))
    cpu.mem_write(RING_STATE, bytes(20))
    cpu.mem_write(RING_WORDS, b"\xA5" * (4 * RING_SIZE))
    ring_end = RING_WORDS + 4 * RING_SIZE
    adjacent = tuple(bytes(cpu.mem_read(address, 4)) for address in (RING_WORDS - 4, ring_end))

    def stop_for_andes(machine, address, size, data):
        if size == 4 and machine.mem_read(address, 1)[0] & 0x7F in (0x0B, 0x2B, 0x5B):
            machine.emu_stop()

    def read_endpoint(machine, access, address, size, value, data):
        nonlocal position
        require(size == 1 and position < len(slot), "Stock code read past the USB slot")
        machine.mem_write(address, slot[position:position + 1])
        position += 1

    cpu.hook_add(UC_HOOK_CODE, stop_for_andes, begin=0x800, end=0x29723)
    cpu.hook_add(UC_HOOK_MEM_READ, read_endpoint, begin=ENDPOINT_STREAM, end=ENDPOINT_STREAM)
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    returned = 0x700
    slot, position = slots[0][0], 0

    def dispatch(pending):
        nonlocal position
        position = 0
        cpu.mem_write(ENDPOINT_LENGTH, bytes([len(slot)]))
        cpu.mem_write(ENDPOINT_STATUS, bytes([0x40 if pending else 0]))
        cpu.reg_write(UC_RISCV_REG_SP, stack)
        cpu.reg_write(UC_RISCV_REG_RA, returned)
        end = EVENT_CALL if pending else returned
        run_until(cpu, USB_DISPATCH, end, vendor_budget=160)

    dispatch(pending=False)
    require(position == 0 and bytes(cpu.mem_read(RING_STATE + 8, 4)) == bytes(4)
            and bytes(cpu.mem_read(RING_WORDS, 4 * RING_SIZE)) == b"\xA5" * (4 * RING_SIZE),
            "USB dispatcher processed audio without its pending status")
    index = wraps = 0
    digest = hashlib.sha256()
    split = None
    for slot, pcm in slots:
        require(len(slot) == 198 and len(pcm) == 192 and slot[6:] == pcm, "Expected a full GIP USB audio slot")
        dispatch(pending=True)
        require(position == len(slot) and cpu.mem_read(ENDPOINT_LENGTH, 1)[0] == 0,
                "USB handler did not consume and clear the complete slot")
        observed = b"".join(bytes(cpu.mem_read(RING_WORDS + ((index + word) % RING_SIZE) * 4, 4))
                            for word in range(48))
        require(observed == pcm, "USB firmware ring does not match the captured PCM")
        digest.update(observed)
        ring_index = index % RING_SIZE
        if split is None and ring_index + 48 <= RING_SIZE and any(
                abs(left - right) > 1024 for left, right in struct.iter_unpack("<hh", pcm)):
            split = replay_pcm_split(cpu, RING_WORDS + ring_index * 4, pcm, stack)
        next_index = index + 48
        actual, = struct.unpack("<I", cpu.mem_read(RING_STATE + 8, 4))
        require(actual == next_index, "USB firmware write counter did not advance by 48 words")
        wraps += next_index // RING_SIZE - index // RING_SIZE
        index = next_index
    require(adjacent == tuple(bytes(cpu.mem_read(address, 4)) for address in (RING_WORDS - 4, ring_end)),
            "USB firmware ring write modified an adjacent word")
    return {"dispatcher": hex(USB_DISPATCH), "handler": hex(USB_HANDLER),
            "no_pending_leaves_ring_untouched": True, "stop_before_event_call": hex(EVENT_CALL),
            "ring_start": hex(RING_WORDS), "ring_words": RING_SIZE, "slots": len(slots),
            "exact_pcm_bytes": index * 4, "ring_wraps": wraps, "pcm_sha256": digest.hexdigest(),
            "downstream_input_split": split}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dongle_fw", type=Path)
    parser.add_argument("trace", type=Path)
    parser.add_argument("--usb-address", type=int, default=9)
    parser.add_argument("--limit", type=int, help="Run only the first N full slots")
    args = parser.parse_args()
    if args.limit is not None and args.limit < 1:
        parser.error("--limit must be positive")
    slots = []

    def collect(endpoint, slot, pcm):
        if endpoint == 6:
            slots.append((slot, pcm))

    inspect(args.trace, args.usb_address, on_audio_slot=collect)
    require(slots and all(right[0][2] == left[0][2] % 255 + 1 for left, right in zip(slots, slots[1:])),
            "Expected a contiguous captured endpoint-6 audio stream")
    report = replay_startup(args.dongle_fw.read_bytes(), "dongle",
                            inspect_application=lambda cpu: replay_endpoint(cpu, slots[:args.limit]))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

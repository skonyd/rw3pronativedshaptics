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
PCM_CONSUMER, PCM_PROCESS_CALL = 0x5664, 0x5858
ENDPOINT_STATUS = 0x80100841
ENDPOINT_LENGTH, ENDPOINT_STREAM, ENDPOINT_ACK = 0x80100816, 0x80100826, 0x8010082E
RING_STATE, RING_WORDS, RING_SIZE = 0x90A2C, 0x91254, 1024
SYSTEM_TICK = 0x80140200
EVENT_STATE, EVENT_RING = 0x8813C, 0x88154
EVENT_CAPACITY, EVENT_BYTES = 512, 12


def signed_word(value):
    return (value & 0x7FFFFFFF) - (value & 0x80000000)


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
    elif word & 0x307F == 0x300B:
        offset = (field(word, 14, 1) | field(word, 8, 4) << 1 | field(word, 25, 6) << 5 |
                  field(word, 7, 1) << 11 | field(word, 17, 3) << 12 |
                  field(word, 15, 2) << 15) - (field(word, 31, 1) << 17)
        cpu.mem_write(cpu.reg_read(UC_RISCV_REG_GP) + offset, bytes([right & 0xFF]))
        return pc + 4
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
    elif opcode in (0x202B, 0x402B):
        common = (field(word, 17, 3) << 12 | field(word, 15, 2) << 15) - (field(word, 31, 1) << 18)
        if opcode == 0x402B:
            offset = common + (field(word, 9, 3) << 2 | field(word, 25, 6) << 5 |
                               field(word, 7, 1) << 11 | field(word, 8, 1) << 17)
            cpu.mem_write(cpu.reg_read(UC_RISCV_REG_GP) + offset, struct.pack("<I", right))
            return pc + 4
        offset = common + (field(word, 22, 9) << 2 | field(word, 20, 1) << 11 | field(word, 21, 1) << 17)
        result, = struct.unpack("<I", cpu.mem_read(cpu.reg_read(UC_RISCV_REG_GP) + offset, 4))
    elif word & 0xFE00707F == 0xC400107F:  # MADDR32
        result = cpu.reg_read(UC_RISCV_REG_X0 + dest) + left * right
    elif word & 0xFE00707F == 0xC600107F:  # MSUBR32
        result = cpu.reg_read(UC_RISCV_REG_X0 + dest) - left * right
    elif word & 0xFE00707F == 0xF200007F:  # MAXW
        result = max(signed_word(left), signed_word(right))
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


def replay_ring_consumer(cpu, first_word, expected_advance, pcm, stack):
    """Dispatch the stock ring consumer with one modeled caller request."""
    saved = cpu.context_save()
    read_counter = RING_STATE + 12
    previous_read = bytes(cpu.mem_read(read_counter, 4))
    consumer_context = 0x8809C
    previous_context = bytes(cpu.mem_read(consumer_context, 4))
    context, left, right = 0xA1000, 0xA2000, 0xA2200
    output = 0xA2400
    returned = 0x700
    cpu.mem_write(read_counter, struct.pack("<I", first_word))
    cpu.mem_write(consumer_context, struct.pack("<I", context))
    cpu.mem_write(context + 0xBB8, struct.pack("<II", left, right))
    for address in (left, right):
        cpu.mem_write(address, b"\xA5" * 152)
    for register, value in ((UC_RISCV_REG_SP, stack), (UC_RISCV_REG_RA, returned),
                            (UC_RISCV_REG_A0, output), (UC_RISCV_REG_A1, 47)):
        cpu.reg_write(register, value)
    try:
        run_until(cpu, PCM_CONSUMER, PCM_PROCESS_CALL, vendor_budget=256)
        advanced, = struct.unpack("<I", cpu.mem_read(read_counter, 4))
        args = cpu.reg_read(UC_RISCV_REG_A0), cpu.reg_read(UC_RISCV_REG_A2)
        require(signed_word(advanced) == expected_advance and args == (context, 48),
                f"Unexpected stock consumer at {first_word}: read={signed_word(advanced)}, args={args}")
        pointer = cpu.reg_read(UC_RISCV_REG_A1)
        original = struct.unpack("<96h", pcm)
        expected = tuple(value + (value < 0) for value in original)
        require(struct.unpack("<96h", cpu.mem_read(pointer, 192)) == expected,
                "Unexpected stock fixed-point PCM rounding")
        # Execute the real JAL into the downstream processor. In this
        # fixture the processor's context buffers are supplied by the host.
        run_until(cpu, PCM_PROCESS_CALL, 0x18B28, vendor_budget=8)
        for side, address in enumerate((left, right)):
            actual = bytes(cpu.mem_read(address, 152))
            expected_channel = struct.pack("<48h", *expected[side::2])
            require(actual == b"\xA5" * 54 + expected_channel + b"\xA5" * 2,
                    "Stock downstream loop did not preserve its caller's channels")
        source_word = (expected_advance - 48) % RING_SIZE
        return {"source_ring_word": source_word, "read_cursor_after": signed_word(advanced), "frames_read": 48,
                "wrap_within_block": source_word + 48 > RING_SIZE,
                "negative_samples_rounded_toward_zero": sum(value < 0 for value in original)}
    finally:
        cpu.context_restore(saved)
        cpu.mem_write(read_counter, previous_read)
        cpu.mem_write(consumer_context, previous_context)


def replay_endpoint(cpu, slots):
    require(bool(slots), "Expected at least one USB audio slot")
    cpu.mem_map(0x80101000, 4096)
    cpu.mem_map(0x80140000, 4096)
    # USB configured, audio enabled, and GIP's six-byte prefix selected.
    cpu.mem_write(0x87F08, b"\x01")
    cpu.mem_write(0x8E0A1, b"\x20")
    cpu.mem_write(0x8E0B0, struct.pack("<I", 1))
    cpu.mem_write(RING_STATE, bytes(20))
    cpu.mem_write(EVENT_STATE, bytes(24))
    cpu.mem_write(RING_WORDS, b"\xA5" * (4 * RING_SIZE))
    ring_end = RING_WORDS + 4 * RING_SIZE
    adjacent = tuple(bytes(cpu.mem_read(address, 4)) for address in (RING_WORDS - 4, ring_end))

    def stop_for_andes(machine, address, size, data):
        if size == 4 and machine.mem_read(address, 1)[0] & 0x7F in (0x0B, 0x2B, 0x5B, 0x7F):
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
        cpu.mem_write(ENDPOINT_ACK, bytes(1))
        cpu.reg_write(UC_RISCV_REG_SP, stack)
        cpu.reg_write(UC_RISCV_REG_RA, returned)
        run_until(cpu, USB_DISPATCH, returned, vendor_budget=200)

    dispatch(pending=False)
    require(position == 0 and bytes(cpu.mem_read(RING_STATE + 8, 4)) == bytes(4)
            and bytes(cpu.mem_read(RING_WORDS, 4 * RING_SIZE)) == b"\xA5" * (4 * RING_SIZE),
            "USB dispatcher processed audio without its pending status")
    index = wraps = 0
    digest = hashlib.sha256()
    split = None
    consumers = {}
    for tick, (slot, pcm) in enumerate(slots):
        cpu.mem_write(SYSTEM_TICK, struct.pack("<I", tick * 24_000))
        require(len(slot) == 198 and len(pcm) == 192 and slot[6:] == pcm, "Expected a full GIP USB audio slot")
        dispatch(pending=True)
        require(position == len(slot) and cpu.mem_read(ENDPOINT_LENGTH, 1)[0] == 0
                and cpu.mem_read(ENDPOINT_ACK, 1)[0] == 1,
                "USB handler did not consume, clear and acknowledge the complete slot")
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
        event_slot = tick % EVENT_CAPACITY
        event = EVENT_RING + event_slot * EVENT_BYTES
        kind, timestamp, published = struct.unpack("<HII", cpu.mem_read(event + 2, 10))
        event_index, = struct.unpack("<H", cpu.mem_read(EVENT_STATE + 4, 2))
        require(kind == 8 and published == (next_index & 0xFFFF) and event_index == event_slot + 1,
                f"Slot {tick}: stock event {kind}, published {published}/{next_index}, cursor {event_index}")
        require(timestamp == tick * 1000, "Unexpected stock event time under fixture clock")
        if tick == 0:
            # With 48 published frames and no elapsed fixture time,
            # stock repositions to write-462 before reading 48 more.
            consumers["underfilled_poisoned_ring"] = replay_ring_consumer(cpu, 0, -366, b"\xA5" * 192, stack)
            underfilled = RING_WORDS + ((48 - 462) % RING_SIZE) * 4
            cpu.mem_write(underfilled, bytes(192))
            consumers["underfilled_zeroed_ring"] = replay_ring_consumer(cpu, 0, -366, bytes(192), stack)
            cpu.mem_write(underfilled, b"\xA5" * 192)
        if tick >= 9:
            first_word = next_index - 480
            input_pcm = slots[tick - 9][1]
            key = "wrap" if first_word % RING_SIZE + 48 > RING_SIZE else "contiguous"
            if key not in consumers and any(abs(left - right) > 1024 for left, right in
                                            struct.iter_unpack("<hh", input_pcm)):
                consumers[key] = replay_ring_consumer(cpu, first_word, first_word + 48, input_pcm, stack)
        wraps += next_index // RING_SIZE - index // RING_SIZE
        index = next_index
    require(adjacent == tuple(bytes(cpu.mem_read(address, 4)) for address in (RING_WORDS - 4, ring_end)),
            "USB firmware ring write modified an adjacent word")
    return {"dispatcher": hex(USB_DISPATCH), "handler": hex(USB_HANDLER),
            "no_pending_leaves_ring_untouched": True, "event_call": hex(EVENT_CALL),
            "ack_register_value": 1, "event_records_verified": len(slots),
            "ring_start": hex(RING_WORDS), "ring_words": RING_SIZE, "slots": len(slots),
            "exact_pcm_bytes": index * 4, "ring_wraps": wraps, "pcm_sha256": digest.hexdigest(),
            "downstream_input_split": split, "consumer_scenarios": consumers}


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

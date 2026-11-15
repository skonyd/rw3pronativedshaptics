"""Replay the stock B91 dongle endpoint-6 handler with captured USB slots."""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ
from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3, UC_RISCV_REG_GP,
    UC_RISCV_REG_MSTATUS, UC_RISCV_REG_PC, UC_RISCV_REG_RA, UC_RISCV_REG_S1,
    UC_RISCV_REG_SP, UC_RISCV_REG_X0,
)

from replay_telink_startup import execute_addigp, field, replay_startup, require
from usb_audio_trace import inspect


USB_DISPATCH, USB_HANDLER, EVENT_CALL = 0x3C60, 0x51B8, 0x5310
PCM_CONSUMER, PCM_PROCESS_CALL = 0x5664, 0x5858
RESAMPLER, RESAMPLER_ADJUST = 0x18AB8, 0x18A5C
RESAMPLER_CONTEXT_PTR = 0x8809C
ENDPOINT_STATUS = 0x80100841
ENDPOINT_LENGTH, ENDPOINT_STREAM, ENDPOINT_ACK = 0x80100816, 0x80100826, 0x8010082E
RING_STATE, RING_WORDS, RING_SIZE = 0x90A2C, 0x91254, 1024
SYSTEM_TICK = 0x80140200
EVENT_STATE, EVENT_RING = 0x8813C, 0x88154
EVENT_CAPACITY, EVENT_BYTES = 512, 12


def signed_word(value):
    return (value & 0x7FFFFFFF) - (value & 0x80000000)


def signed_half(value):
    return (value & 0x7FFF) - (value & 0x8000)


def saturate(value, bits):
    return min((1 << (bits - 1)) - 1, max(-(1 << (bits - 1)), value))


def shift_saturated(value, shift, bits):
    if shift < 0:
        return value >> min(bits - 1, -shift)
    return saturate(value << shift, bits)


def pack_halves(low, high):
    return (high << 16) | (low & 0xFFFF)


def write_word_pair(cpu, dest, low, high):
    even = dest & ~1
    cpu.reg_write(UC_RISCV_REG_X0 + even, low & 0xFFFFFFFF)
    cpu.reg_write(UC_RISCV_REG_X0 + even + 1, high & 0xFFFFFFFF)


def andes_next(cpu, pc):
    """Execute the Andes register and memory operations reached by this replay."""
    word, = struct.unpack("<I", cpu.mem_read(pc, 4))
    opcode = word & 0x707F
    operation = word & 0xFE00707F
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
    elif word & 0xFE00707F == 0x0A00005B:  # LEA.H
        result = left + (right << 1)
    elif word & 0xFE00707F == 0x0C00005B:  # LEA.W
        result = left + (right << 2)
    elif operation == 0x0E00005B:  # LEA.D
        result = left + (right << 3)
    elif operation == 0x2000005B:  # FFB, little endian
        index = left.to_bytes(4, "little").find(bytes([right & 0xFF]))
        result = index - 4 if index >= 0 else 0
    elif opcode == 0x002B:  # SHGP
        offset = (field(word, 8, 4) << 1 | field(word, 25, 6) << 5 | field(word, 7, 1) << 11 |
                  field(word, 17, 3) << 12 | field(word, 15, 2) << 15) - (field(word, 31, 1) << 17)
        cpu.mem_write(cpu.reg_read(UC_RISCV_REG_GP) + offset, struct.pack("<H", right & 0xFFFF))
        return pc + 4
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
    elif operation in (0xF000007F, 0xF200007F):  # MINW / MAXW
        compare = min if operation == 0xF000007F else max
        result = compare(signed_word(left), signed_word(right))
    elif word & 0xFE00707F == 0x0200007F:  # RSUB16
        lower = (signed_half(left) - signed_half(right)) >> 1
        upper = (signed_half(left >> 16) - signed_half(right >> 16)) >> 1
        result = (upper << 16) | (lower & 0xFFFF)
    elif word & 0xFE00707F == 0x0400107F:  # KADDH
        result = saturate(signed_word(left) + signed_word(right), 16)
    elif operation == 0x0600107F:  # KSUBH
        result = saturate(signed_word(left) - signed_word(right), 16)
    elif operation == 0x0000107F:  # KADDW
        result = saturate(signed_word(left) + signed_word(right), 32)
    elif operation == 0x0200107F:  # KSUBW
        result = saturate(signed_word(left) - signed_word(right), 32)
    elif operation == 0x0A00107F:  # KDMBB
        result = saturate(2 * signed_half(left) * signed_half(right), 32)
    elif word & 0xFE00707F == 0x0C00107F:  # KHMBB
        result = saturate((signed_half(left) * signed_half(right)) >> 15, 16)
    elif word & 0xFE00707F == 0x0E00107F:  # PKBB16
        result = (left << 16) | (right & 0xFFFF)
    elif word & 0xFE00707F == 0x4800107F:  # KMADA
        accumulator = signed_word(cpu.reg_read(UC_RISCV_REG_X0 + dest))
        result = saturate(accumulator + signed_half(left) * signed_half(right)
                          + signed_half(left >> 16) * signed_half(right >> 16), 32)
    elif operation == 0x5A00107F:  # KMABB
        accumulator = signed_word(cpu.reg_read(UC_RISCV_REG_X0 + dest))
        result = saturate(accumulator + signed_half(left) * signed_half(right), 32)
    elif word & 0xFE00707F == 0x8E00107F:  # KMMWB2
        result = saturate((signed_word(left) * signed_half(right)) >> 15, 32)
    elif operation == 0x4400107F:  # SMMWB
        result = (signed_word(left) * signed_half(right)) >> 16
    elif operation == 0x4600107F:  # KMMAWB
        accumulator = signed_word(cpu.reg_read(UC_RISCV_REG_X0 + dest))
        result = saturate(accumulator + ((signed_word(left) * signed_half(right)) >> 16), 32)
    elif operation == 0x6200107F:  # KWMMUL
        result = saturate((signed_word(left) * signed_word(right)) >> 31, 32)
    elif operation == 0xE000107F:  # MULSR64
        product = signed_word(left) * signed_word(right)
        write_word_pair(cpu, dest, product, product >> 32)
        return pc + 4
    elif operation == 0xA000007F:  # SMUL16
        write_word_pair(cpu, dest, signed_half(left) * signed_half(right),
                        signed_half(left >> 16) * signed_half(right >> 16))
        return pc + 4
    elif operation == 0x8C00107F:  # SMALDA
        even = dest & ~1
        accumulator = (cpu.reg_read(UC_RISCV_REG_X0 + even)
                       | cpu.reg_read(UC_RISCV_REG_X0 + even + 1) << 32)
        total = (accumulator + signed_half(left) * signed_half(right)
                 + signed_half(left >> 16) * signed_half(right >> 16))
        write_word_pair(cpu, dest, total, total >> 32)
        return pc + 4
    elif operation in (0x8000007F, 0x8200007F):  # SMIN16 / SMAX16
        compare = min if operation == 0x8000007F else max
        result = pack_halves(compare(signed_half(left), signed_half(right)),
                             compare(signed_half(left >> 16), signed_half(right >> 16)))
    elif word & 0xFFF0707F == 0xAE80007F:  # CLRS16
        low, high = signed_half(left), signed_half(left >> 16)
        result = pack_halves(15 - (low if low >= 0 else ~low).bit_length(),
                             15 - (high if high >= 0 else ~high).bit_length())
    elif word & 0xFFF0707F == 0xAF90007F:  # CLZ32
        result = 32 - left.bit_length()
    elif word & 0xFFF0707F == 0xAF80007F:  # CLRS32
        value = signed_word(left)
        result = 31 - (value if value >= 0 else ~value).bit_length()
    elif word & 0xFFF0707F == 0xAD40007F:  # KABSW
        result = min(abs(signed_word(left)), 0x7FFFFFFF)
    elif word & 0xFFF0707F == 0xAD10007F:  # KABS16
        result = pack_halves(min(abs(signed_half(left)), 32767), min(abs(signed_half(left >> 16)), 32767))
    elif operation in (0x6E00107F, 0x7E00107F):  # KSLRAW / KSLRAW.U
        shift = (right & 31) - (right & 32)
        result = shift_saturated(signed_word(left), shift, 32)
        if operation == 0x7E00107F and shift < 0:
            result += field(left, min(-shift, 31) - 1, 1)
    elif operation in (0x5600007F, 0x6400007F) or word & 0xFF00707F == 0x7500007F:
        # KSLRA16, KSLL16 and KSLLI16
        shift = (right & 15) - (right & 16) if operation == 0x5600007F else right & 15
        if word & 0xFF00707F == 0x7500007F:
            shift = field(word, 20, 4)
        result = pack_halves(shift_saturated(signed_half(left), shift, 16),
                             shift_saturated(signed_half(left >> 16), shift, 16))
    elif word & 0xFF00707F == 0x7000007F:  # SRAI16
        shift = field(word, 20, 4)
        result = pack_halves(signed_half(left) >> shift, signed_half(left >> 16) >> shift)
    elif word & 0xFE00707F == 0xD400107F:  # SRAI.U (RV32)
        right_shift = field(word, 20, 5)
        result = (signed_word(left) * 2 + (1 << right_shift)) >> (right_shift + 1)
    else:
        raise ValueError(f"Unsupported Andes instruction 0x{word:08X} at 0x{pc:X}")
    cpu.reg_write(UC_RISCV_REG_X0 + dest, result & 0xFFFFFFFF)
    return pc + 4


def run_until(cpu, pc, end, vendor_budget):
    # A previously translated block can contain the requested end address.
    # The instruction hook also stops there before an instruction executes.
    cpu.stock_stop_address = end
    for _ in range(vendor_budget):
        cpu.emu_start(pc, end, count=100_000)
        pc = cpu.reg_read(UC_RISCV_REG_PC)
        if pc == end:
            return
        pc = andes_next(cpu, pc)
    raise ValueError(f"Stock routine did not reach 0x{end:X}, stopped near 0x{pc:X}")


def install_andes_hooks(cpu, code_ranges):
    def stop_for_andes(machine, address, size, data):
        if address == machine.stock_stop_address or (
                size == 4 and machine.mem_read(address, 1)[0] & 0x7F in (0x0B, 0x2B, 0x5B, 0x7F)):
            machine.emu_stop()

    for start, end in code_ranges:
        cpu.hook_add(UC_HOOK_CODE, stop_for_andes, begin=start, end=end)


def initialize_resampler(cpu, stack):
    # Execute the original stereo initializer call site. S1 is set to the
    # value loaded earlier in the startup routine at 0x1AD62.
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_S1, 4096)
    cpu.reg_write(UC_RISCV_REG_MSTATUS, cpu.reg_read(UC_RISCV_REG_MSTATUS) | 0x6000)
    run_until(cpu, 0x1AD70, 0x1AD98, vendor_budget=16_384)
    context, = struct.unpack("<I", cpu.mem_read(RESAMPLER_CONTEXT_PTR, 4))
    require(context == 0x93164 and cpu.mem_read(context + 0x10, 4) == b"\x01\x00\x00\x00",
            "Stock stereo resampler context did not initialize")
    return context


def replay_resampler(cpu, stack):
    """Exercise the stock kernel with repeatable inputs and clock adjustments."""
    context = initialize_resampler(cpu, stack)
    source, output, returned = 0xA5000, 0xA5200, 0x700

    def call(entry, a1, a2, a3):
        for register, value in ((UC_RISCV_REG_SP, stack), (UC_RISCV_REG_RA, returned),
                                (UC_RISCV_REG_A0, context), (UC_RISCV_REG_A1, a1),
                                (UC_RISCV_REG_A2, a2), (UC_RISCV_REG_A3, a3)):
            cpu.reg_write(register, value)
        run_until(cpu, entry, returned, vendor_budget=16_384)
        return cpu.reg_read(UC_RISCV_REG_A0)

    def process(pcm):
        capacity = 50 * 4
        guard = b"\xA5" * (capacity + 8)
        cpu.mem_write(source, pcm)
        cpu.mem_write(output - 4, guard)
        count = call(RESAMPLER, source, 48, output)
        tail_size = capacity + 4 - count * 4
        require(47 <= count <= 49 and bytes(cpu.mem_read(output - 4, 4)) == guard[:4]
                and bytes(cpu.mem_read(output + count * 4, tail_size)) == guard[:tail_size],
                "Unexpected output size or write outside the stock resampler buffer")
        return bytes(cpu.mem_read(output, count * 4))

    silence = bytes(192)
    step_left = struct.pack("<hh", 25_000, 0) * 48
    step_right = struct.pack("<hh", 0, 25_000) * 48
    require(process(silence) == silence, "Stock resampler did not start silent")
    left = tuple(struct.iter_unpack("<hh", process(step_left)))
    tail = tuple(struct.iter_unpack("<hh", process(silence)))
    require(process(silence) == silence, "Stock resampler did not return to silence")
    right = tuple(struct.iter_unpack("<hh", process(step_right)))
    require(len(left) == len(tail) == len(right) == 48
            and [(r, l) for l, r in left] == list(right) and all(r == 0 for _, r in left)
            and all(r == 0 for _, r in tail), "Stock resampler mixed channels or changed nominal frame count")
    require(abs(left[13][0]) < 500 and left[14][0] > 24_000 and left[-1][0] in (25_000, 25_001)
            and 24_000 < tail[13][0] and abs(tail[14][0]) < 500 and tail[-1][0] == 0,
            "Unexpected stock resampler step or tail")
    right_tail = tuple(struct.iter_unpack("<hh", process(silence)))
    require([(r, l) for l, r in tail] == list(right_tail) and process(silence) == silence,
            "Stock resampler did not flush the right step")

    drift = {}
    for offset, expected_frames in ((10_000, 476), (-10_000, 485)):
        call(RESAMPLER_ADJUST, offset & 0xFFFFFFFF, 0, 0)
        blocks = [process(silence) for _ in range(10)]
        output_frames = sum(len(block) // 4 for block in blocks)
        require(output_frames == expected_frames and all(not any(block) for block in blocks),
                f"Unexpected stock rate adjustment for {offset} relative to 1,000,000")
        drift[str(offset)] = output_frames
    return {"initializer_call_site": "0x1ad80", "kernel": hex(RESAMPLER), "nominal_frames_per_block": 48,
            "step_crossing_frame_zero_based": 14, "opposite_channel_silent": True, "tail_returns_to_zero": True,
            "drift_480_input_frames": drift}


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


def inspect_encoder_marker(cpu):
    # This argument is prepared on the stock LC3 Plus setup assertion path.
    # It identifies code with an encoder reference, without selecting a codec.
    reference = 0x24EF4
    execute_addigp(cpu, struct.unpack("<I", cpu.mem_read(reference, 4))[0])
    address = cpu.reg_read(UC_RISCV_REG_A0)
    marker = bytes(cpu.mem_read(address, 128)).split(b"\0", 1)[0]
    require(marker.endswith(b"/lc3_plus/src/setup_enc_lc3.c"), "Missing relocated LC3 Plus encoder reference")
    return {"setup_source_reference": hex(reference), "string_address": hex(address)}


def replay_ring_reset(cpu, stack):
    """Verify stock PCM history clearing with a dirty ring and resampler."""
    initialize_resampler(cpu, stack)
    size = RING_SIZE * 4 + 16
    cursors = bytes(cpu.mem_read(RING_STATE + 8, 8))
    cpu.mem_write(RING_WORDS - 4, b"\xA5" * (size + 8))
    cpu.mem_write(RING_STATE + 20, b"\xA5" * 4)
    cpu.reg_write(UC_RISCV_REG_RA, 0x700)
    run_until(cpu, 0x4B54, 0x700, vendor_budget=16_384)
    require(cpu.mem_read(RING_WORDS, size) == bytes(size)
            and cpu.mem_read(RING_WORDS - 4, 4) == b"\xA5" * 4
            and cpu.mem_read(RING_WORDS + size, 4) == b"\xA5" * 4,
            "Stock reset did not clear the PCM ring and the four retained-sample words")
    require(cpu.mem_read(RING_STATE + 20, 4) == bytes(4), "Stock reset did not clear the timing state word")
    require(cpu.mem_read(RING_STATE + 8, 8) == cursors, "Stock buffer reset unexpectedly moved the cursors")
    return {"routine": "0x4b54", "ring_and_retained_sample_bytes_cleared": size,
            "write_and_read_cursors_preserved": True}


def replay_ring_consumer(cpu, first_word, expected_advance, pcm, stack):
    """Dispatch the stock ring consumer with one modeled caller request."""
    saved = cpu.context_save()
    read_counter = RING_STATE + 12
    previous_read = bytes(cpu.mem_read(read_counter, 4))
    consumer_state, leftovers = 0x88090, RING_WORDS + RING_SIZE * 4
    previous_state = bytes(cpu.mem_read(consumer_state, 20))
    previous_leftovers = bytes(cpu.mem_read(leftovers, 16))
    context = initialize_resampler(cpu, stack)
    channels = struct.unpack("<II", cpu.mem_read(context + 0xBB8, 8))
    output = 0xA2400
    returned = 0x700
    output_guard = b"\xA5" * 196
    cpu.mem_write(read_counter, struct.pack("<I", first_word))
    cpu.mem_write(output - 4, output_guard)
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
        # Execute the real JAL into the stock-initialized processor.
        run_until(cpu, PCM_PROCESS_CALL, 0x18B28, vendor_budget=8)
        for side, address in enumerate(channels):
            actual = bytes(cpu.mem_read(address, 150))
            expected_channel = struct.pack("<48h", *expected[side::2])
            require(actual == bytes(54) + expected_channel,
                    "Stock downstream loop did not preserve its caller's channels")
        run_until(cpu, 0x18B28, PCM_PROCESS_CALL + 4, vendor_budget=16_384)
        filtered_count = cpu.reg_read(UC_RISCV_REG_A0)
        require(filtered_count == 48, "Unexpected stock resampler count at the consumer return site")
        filtered = bytes(cpu.mem_read(pointer, filtered_count * 4))
        require(any(filtered) == any(pcm), "Unexpected stock resampler silence from the ring")
        run_until(cpu, PCM_PROCESS_CALL + 4, returned, vendor_budget=256)
        require(cpu.reg_read(UC_RISCV_REG_A0) == 47 and cpu.mem_read(output, 47 * 4) == filtered[:47 * 4]
                and cpu.mem_read(consumer_state, 4) == b"\x01\x00\x00\x00"
                and cpu.mem_read(leftovers, 4) == filtered[-4:]
                and cpu.mem_read(output - 4, 4) == output_guard[:4]
                and cpu.mem_read(output + 47 * 4, 4) == output_guard[-4:],
                "Stock consumer returned the wrong PCM or wrote outside its caller buffer")
        source_word = (expected_advance - 48) % RING_SIZE
        return {"source_ring_word": source_word, "read_cursor_after": signed_word(advanced), "frames_read": 48,
                "wrap_within_block": source_word + 48 > RING_SIZE,
                "negative_samples_rounded_toward_zero": sum(value < 0 for value in original),
                "frames_returned": 47, "frames_retained": 1,
                "pcm_sha256": hashlib.sha256(filtered[:47 * 4]).hexdigest()}
    finally:
        cpu.context_restore(saved)
        cpu.mem_write(read_counter, previous_read)
        cpu.mem_write(consumer_state, previous_state)
        cpu.mem_write(leftovers, previous_leftovers)


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

    def read_endpoint(machine, access, address, size, value, data):
        nonlocal position
        require(size == 1 and position < len(slot), "Stock code read past the USB slot")
        machine.mem_write(address, slot[position:position + 1])
        position += 1

    install_andes_hooks(cpu, ((0x800, 0x29723),))
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
    resampler = replay_resampler(cpu, stack)
    return {"dispatcher": hex(USB_DISPATCH), "handler": hex(USB_HANDLER),
            "no_pending_leaves_ring_untouched": True, "event_call": hex(EVENT_CALL),
            "ack_register_value": 1, "event_records_verified": len(slots),
            "ring_start": hex(RING_WORDS), "ring_words": RING_SIZE, "slots": len(slots),
            "exact_pcm_bytes": index * 4, "ring_wraps": wraps, "pcm_sha256": digest.hexdigest(),
            "downstream_input_split": split, "consumer_scenarios": consumers,
            "resampler_fixtures": resampler, "lc3_plus_setup": inspect_encoder_marker(cpu),
            "ring_reset": replay_ring_reset(cpu, stack)}


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

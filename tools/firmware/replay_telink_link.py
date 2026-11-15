"""Replay stock dongle audio and controller receive slots offline."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE
from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3,
    UC_RISCV_REG_MSTATUS, UC_RISCV_REG_RA, UC_RISCV_REG_S0,
    UC_RISCV_REG_S2, UC_RISCV_REG_S3, UC_RISCV_REG_S5, UC_RISCV_REG_SP,
)

from replay_telink_radio import (
    DECODE_FRAME, FRAME_BYTES, PCM_WRITE_CURSOR, PCM_WRITER, fifo_input, initialize_i2s, resample_to_fifo,
)
from replay_telink_rf import (
    AUDIO_OFFSET, RX_SLOTS, RX_STATE, SLOT_BYTES, SLOT_COUNT, SLOT_STATUS_OFFSET, TX_SLOTS,
    check_receive_rejections, receive_audio, run_receive_irq, transmit_audio,
)
from replay_telink_startup import replay_startup, require
from replay_telink_timer import CLOCK_MHZ, PERIOD_TICKS, SYSTEM_MHZ, ReceiveTimer
from replay_telink_usb import (
    EVENT_BYTES, EVENT_RING, RING_SIZE, RING_STATE, initialize_resampler,
    install_andes_hooks, replay_endpoint, run_until,
)
from usb_audio_trace import inspect


ENCODER_INIT = 0x26BC4
PAIR_START, PAIR_END = 0x5C6C, 0x5D18
WORKER_INTERRUPT, PCM_WORKER, WORKER_IRQ_STATUS = 0x3CDC, 0x5958, 0x80140143
PCM_FRAMES, CODED_BYTES = 240, 35
RECEIVE_SLOT, RECEIVE_DISPATCH = 0x131B4, 0x12834
RECEIVE_LEFT_OK, RECEIVE_LEFT_LOST, RECEIVE_RIGHT = 0x12F14, 0x129D8, 0x129F8
RECEIVE_MARK_LOSS, RECEIVE_AFTER_LOSS = 0x132A8, 0x132AE
RECEIVE_CONSUMED = 0x131D4
STEREO_RESAMPLER = 0x20022374
RECEIVE_PRIME_FRAMES = 160
RECEIVE_LOOKAHEAD = 5
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
    ("lose_pair", 1000, 200, 1),
    ("recover_pair", 1000, 200, 4),
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
    frame = transmit_audio(cpu, 1, packet)
    return frame, {"source_ring_word": first_word % RING_SIZE, "frames_read": frames,
                   "irq_entry": hex(WORKER_INTERRUPT), "no_pending_leaves_cursor_unchanged": True,
                   "ring_wrap": (first_word % RING_SIZE) + frames > RING_SIZE,
                   "packet_sha256": hashlib.sha256(packet).hexdigest(),
                   "rf_dma_sha256": hashlib.sha256(frame).hexdigest()}


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

    caller_stack = stack - 0x2000
    pcm_address, mono_address = caller_stack + 0xC0, caller_stack + 0x480
    guard = b"\xA5" * 4
    scenes = {}
    index = 1
    for name, left, right, blocks in (*SCENES, ("lookahead_silence", 0, 0, RECEIVE_LOOKAHEAD)):
        pcm = b"".join(struct.pack("<hh", l, r) for l, r in zip(sine(left), sine(right)))
        packets = []
        for _ in range(blocks):
            encoded_address = TX_SLOTS + SLOT_BYTES * index + AUDIO_OFFSET
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
            packets.append(transmit_audio(cpu, index, packet))
            index = (index + 1) % SLOT_COUNT
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


def receive_pair(cpu, stack, address, *, lost=False, priming=False):
    """Run the stock copy and decode path for a selected receive queue slot."""
    before, = struct.unpack("<H", cpu.mem_read(PCM_WRITE_CURSOR, 2))
    require(cpu.mem_read(RX_STATE + 0xA4, 1)[0] & 0x20, "Stock receiver is outside audio mode")
    cpu.reg_write(UC_RISCV_REG_SP, stack)
    cpu.reg_write(UC_RISCV_REG_S2, address)
    if lost:
        # Model a missed arrival by invoking the stock loss marker. The
        # enclosing RF scheduler and its timeout are outside this replay.
        run_until(cpu, RECEIVE_MARK_LOSS, RECEIVE_AFTER_LOSS, vendor_budget=128)
    slot = bytes(cpu.mem_read(address, SLOT_BYTES))
    require(slot[SLOT_STATUS_OFFSET] == (4 if lost else 1), "Unexpected stock queue status")
    run_until(cpu, RECEIVE_SLOT, RECEIVE_DISPATCH, vendor_budget=128)
    copied = cpu.reg_read(UC_RISCV_REG_A0)
    require(copied == stack and cpu.mem_read(copied, len(slot)) == slot,
            "Stock receive queue copy changed the slot")
    lanes = []
    entry = RECEIVE_DISPATCH
    for channel, after_call in enumerate((RECEIVE_LEFT_LOST if lost else RECEIVE_LEFT_OK, RECEIVE_RIGHT)):
        run_until(cpu, entry, DECODE_FRAME, vendor_budget=4096)
        offset = channel * CODED_BYTES
        source = cpu.reg_read(UC_RISCV_REG_A1)
        output = cpu.reg_read(UC_RISCV_REG_A2)
        require(cpu.reg_read(UC_RISCV_REG_A0) == channel and source == copied + offset
                and cpu.reg_read(UC_RISCV_REG_A3) == int(lost)
                and cpu.reg_read(UC_RISCV_REG_RA) == after_call,
                "Stock dispatch selected unexpected decoder arguments")
        require(cpu.mem_read(source, CODED_BYTES) == slot[offset:offset + CODED_BYTES],
                "Stock dispatch changed the codec bytes")
        run_until(cpu, DECODE_FRAME, after_call, vendor_budget=200_000)
        require(cpu.reg_read(UC_RISCV_REG_A0) == CODED_BYTES, "Stock slot decoder returned an unexpected size")
        lanes.append(tuple(sample for sample, in struct.iter_unpack("<h", cpu.mem_read(output, PCM_FRAMES * 2))))
        entry = after_call
    require(cpu.mem_read(address, SLOT_BYTES) == slot,
            "Stock dispatch changed the queued slot before both decoders returned")
    after, = struct.unpack("<H", cpu.mem_read(PCM_WRITE_CURSOR, 2))
    require((after - before) % 512 == (RECEIVE_PRIME_FRAMES if priming else 0),
            "Unexpected stock receive adjustment to the PCM write cursor")
    return lanes


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


def gated_pcm(pcm):
    return b"".join(struct.pack("<h", value if abs(value) > 1 else 0)
                    for value, in struct.iter_unpack("<h", pcm))


def validate_fifo(output, written, pending):
    pending.extend(written)
    require(output == pending[:len(written)], "Modeled FIFO differs from the queued writer bytes")
    del pending[:len(written)]


def send_to_fifo(cpu, tx, caller_stack, lanes, pending):
    interleaved = tuple(zip(*lanes))
    fifo_pcm = bytearray()
    for start in range(0, len(interleaved), 40):
        pcm = b"".join(struct.pack("<hh", *frame) for frame in interleaved[start:start + 40])
        resampled, output = resample_to_fifo(cpu, tx, caller_stack, pcm)
        written = gated_pcm(resampled)
        validate_fifo(output, written, pending)
        fifo_pcm.extend(written)
    return fifo_pcm


def render_received(cpu, tx, address, lanes, pending):
    """Continue the stock receiver until it writes stereo PCM and consumes the slot."""
    # Supply the steady playout state that is owned by the receive scheduler.
    cpu.mem_write(RX_STATE + 0x1C, struct.pack("<I", 1))
    resampler_inputs = []
    writer_inputs = []

    def read_pcm(machine, pc, size, captures):
        source = machine.reg_read(UC_RISCV_REG_A0)
        count = machine.reg_read(UC_RISCV_REG_A1)
        require(0 < count <= PCM_FRAMES, "Unexpected receiver PCM span")
        captures.append(bytes(machine.mem_read(source, count * FRAME_BYTES)))

    resampler_hook = cpu.hook_add(UC_HOOK_CODE, read_pcm, resampler_inputs, STEREO_RESAMPLER, STEREO_RESAMPLER)
    writer_hook = cpu.hook_add(UC_HOOK_CODE, read_pcm, writer_inputs, PCM_WRITER, PCM_WRITER)
    try:
        run_until(cpu, RECEIVE_RIGHT, RECEIVE_CONSUMED, vendor_budget=240_000)
    finally:
        cpu.hook_del(writer_hook)
        cpu.hook_del(resampler_hook)

    expected = b"".join(struct.pack("<hh", *frame) for frame in zip(*lanes))
    require([len(block) for block in resampler_inputs] == [40 * FRAME_BYTES] * 6
            and b"".join(resampler_inputs) == expected,
            "Stock receiver did not interleave all decoded samples in order")
    require(len(writer_inputs) == len(resampler_inputs), "Stock receiver skipped a PCM writer block")
    require(cpu.mem_read(address + SLOT_STATUS_OFFSET, 1) == b"\x00",
            "Stock receiver did not mark its slot consumed")
    written = gated_pcm(b"".join(writer_inputs))
    require(len(written) == len(expected), "Unexpected stock playout frame count")
    output, _ = fifo_input(cpu, tx, len(written) // FRAME_BYTES)
    validate_fifo(output, written, pending)
    return written


def drain_fifo(cpu, tx, pending):
    output, _ = fifo_input(cpu, tx, RECEIVE_PRIME_FRAMES)
    require(output == pending, "Modeled FIFO drain differs from the last queued PCM bytes")


def initialize_receive(cpu):
    tx, _ = initialize_i2s(cpu)
    # The controller IRQ starts with a negotiated link and an allocated DMA
    # buffer. The packet handler will select the audio mode from the header.
    cpu.mem_write(RX_STATE + 0xBC, b"\x01\x01")
    return tx


def play_timed_block(cpu, tx, timer, pending, frame, *, lost=False, late_ticks=0):
    """Verify what the stock Timer0 scheduler plays from the receive queue."""
    selected, decodes, decoded_peaks, pcm = [], [], [], []

    def select_slot(machine, pc, size, data):
        address = machine.reg_read(UC_RISCV_REG_S2)
        selected.append((address, machine.mem_read(address + SLOT_STATUS_OFFSET, 1)[0]))

    def decode(machine, pc, size, data):
        source = machine.reg_read(UC_RISCV_REG_A1)
        decodes.append((machine.reg_read(UC_RISCV_REG_A0), machine.reg_read(UC_RISCV_REG_A3),
                        bytes(machine.mem_read(source, CODED_BYTES)), machine.reg_read(UC_RISCV_REG_A2)))

    def decoded(machine, pc, size, data):
        mono = machine.mem_read(decodes[-1][3], PCM_FRAMES * 2)
        decoded_peaks.append(max(abs(sample) for sample, in struct.iter_unpack("<h", mono)))

    def write_pcm(machine, pc, size, data):
        source, count = (machine.reg_read(r) for r in (UC_RISCV_REG_A0, UC_RISCV_REG_A1))
        require(count == 40, "Stock Timer0 wrote an unexpected PCM block size")
        pcm.append(bytes(machine.mem_read(source, count * FRAME_BYTES)))

    hooks = [cpu.hook_add(UC_HOOK_CODE, callback, begin=address, end=address)
             for address, callback in ((RECEIVE_SLOT, select_slot), (DECODE_FRAME, decode),
                                       (RECEIVE_LEFT_LOST if lost else RECEIVE_LEFT_OK, decoded),
                                       (RECEIVE_RIGHT, decoded), (PCM_WRITER, write_pcm))]
    before = timer.tick
    try:
        first = timer.interrupt(late_ticks)
        after_playout = (len(selected), len(decodes), len(decoded_peaks), len(pcm))
        second = timer.interrupt()
        require(after_playout == (1, 2, 2, 6)
                and after_playout == (len(selected), len(decodes), len(decoded_peaks), len(pcm)),
                "Stock Timer0 cycle did not decode and write exactly once")
    finally:
        for hook in hooks:
            cpu.hook_del(hook)

    index = frame[12] & (SLOT_COUNT - 1)
    expected_address = RX_SLOTS + index * SLOT_BYTES
    require(selected == [(expected_address, 4 if lost else 1)]
            and cpu.mem_read(expected_address + SLOT_STATUS_OFFSET, 1) == b"\x00",
            "Stock Timer0 consumed the wrong receive slot or loss status")
    require([(channel, flag) for channel, flag, _, _ in decodes] == [(0, int(lost)), (1, int(lost))],
            "Unexpected stock Timer0 decoder arguments")
    if not lost:
        require(b"".join(payload for _, _, payload, _ in decodes) == frame[AUDIO_OFFSET:],
                "Stock Timer0 selected the wrong codec bytes")
    require((first, second) == ((3430 - late_ticks // SYSTEM_MHZ) * CLOCK_MHZ, 1570 * CLOCK_MHZ)
            and timer.tick == before + PERIOD_TICKS, "Stock timer compares did not recover the 5 ms period")
    written = gated_pcm(b"".join(pcm))
    output, _ = fifo_input(cpu, tx, PCM_FRAMES)
    validate_fifo(output, written, pending)
    return written, decoded_peaks, (first, second)


def replay_scheduled_link(cpu, scenes):
    stack = cpu.reg_read(UC_RISCV_REG_SP) - 0x1800
    tx = initialize_receive(cpu)
    timer = ReceiveTimer(cpu, stack, ((1 + RECEIVE_LOOKAHEAD) << 3) + 2)
    pending = bytearray(RECEIVE_PRIME_FRAMES * FRAME_BYTES)
    frames = [(name, frame) for name, *_ in SCENES for frame in scenes[name]]
    arrivals = frames + [("lookahead_silence", frame) for frame in scenes["lookahead_silence"]]
    report = {}
    compares = set()
    output = hashlib.sha256()
    counters = []
    frequencies = {name: (left, right) for name, left, right, _ in SCENES}
    for number, (name, frame) in enumerate(frames):
        newest = number + RECEIVE_LOOKAHEAD
        # RF delivery runs five packet periods ahead of playout. Withhold
        # the loss test packet entirely and let stock mark the empty slot.
        first_arrival = newest if number else 0
        receive_timestamp, = struct.unpack("<I", cpu.mem_read(RX_STATE + 0x10, 4))
        for arrival, packet in arrivals[first_arrival:newest + 1]:
            if arrival != "lose_pair":
                receive_audio(cpu, stack, packet, timestamp=receive_timestamp)
        counters.append(timer.advance_radio_clock())
        late = 100 * SYSTEM_MHZ if number % 8 == 3 else 0
        written, _, phase_compares = play_timed_block(
            cpu, tx, timer, pending, frame, lost=name == "lose_pair", late_ticks=late)
        compares.add(phase_compares)
        output.update(written)
        samples = tuple(zip(*struct.iter_unpack("<hh", written)))
        report[name] = [max(map(abs, lane)) for lane in samples]
        if name == "cold_silence":
            require(not any(written), "Stock scheduled playout introduced cold noise")
        if name == "lose_pair":
            require(all(report[name]), "Stock scheduled loss did not conceal both lanes")
        final_block = number + 1 == len(frames) or frames[number + 1][0] != name
        if final_block and name != "lose_pair":
            check_tones(samples, frequencies[name])
    require(report["drain"] == [0, 0], "Stock scheduled playout did not drain to silence")
    drain_fifo(cpu, tx, pending)
    return {"blocks_played": len(frames), "vector_interrupts": 2 * len(frames),
            "first_rf_counter": counters[0], "last_rf_counter": counters[-1],
            "compare_cycles": sorted(compares), "final_pcm_peaks_by_scene": report,
            "fifo_sha256": output.hexdigest()}


def decode_link(cpu, scenes):
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    caller_stack = stack - 0x1800
    tx = initialize_receive(cpu)
    fifo = hashlib.sha256()
    pending = bytearray(RECEIVE_PRIME_FRAMES * 4)
    frames = 0
    report = {}
    accepted = 0
    for name, left, right, _ in SCENES:
        packets = scenes[name]
        for frame in packets:
            lost = name == "lose_pair"
            if lost:
                address = RX_SLOTS + (frame[12] & (SLOT_COUNT - 1)) * SLOT_BYTES
            else:
                address = receive_audio(cpu, caller_stack, frame)
                accepted += 1
            if name == "lose_left":
                packet = bytes(cpu.mem_read(address, CODED_BYTES * 2))
                first = decode_lane(cpu, stack, 0, packet[:CODED_BYTES], True)
                second = decode_lane(cpu, stack, 1, packet[CODED_BYTES:], False)
                fifo_pcm = send_to_fifo(cpu, tx, caller_stack, (first, second), pending)
            else:
                first, second = receive_pair(cpu, caller_stack, address, lost=lost, priming=frames == 0)
                fifo_pcm = render_received(cpu, tx, address, (first, second), pending)
            fifo.update(fifo_pcm)
            frames += len(fifo_pcm) // 4
            if name == "cold_silence":
                require(not any(first) and not any(second), "Stock codec introduced audio during cold silence")
        final_fifo = tuple(zip(*struct.iter_unpack("<hh", fifo_pcm)))
        if name == "lose_left":
            require(any(first), "Stock loss concealment did not carry prior left audio")
            levels = [None, *check_tones((second,), (right,))]
            fifo_levels = [None, *check_tones((final_fifo[1],), (right,))]
        elif name == "lose_pair":
            require(any(first) and any(second), "Stock pair loss did not conceal both prior signals")
            levels, fifo_levels = [None, None], [None, None]
        else:
            levels = check_tones((first, second), (left, right))
            fifo_levels = check_tones(final_fifo, (left, right))
        report[name] = {"packets": len(packets), "final_decoder_levels": levels,
                        "final_fifo_levels": fifo_levels,
                        "final_peak": [max(map(abs, first)), max(map(abs, second))]}
    drain_fifo(cpu, tx, pending)
    return {"scenes": report, "fifo_frames": frames, "fifo_sha256": fifo.hexdigest(),
            "startup_silence_frames": RECEIVE_PRIME_FRAMES,
            "stock_slot_copy": hex(RECEIVE_SLOT), "stock_slot_dispatch": hex(RECEIVE_DISPATCH),
            "rf_frames_accepted": accepted,
            "slots_dispatched": sum(blocks for name, _, _, blocks in SCENES if name != "lose_left")}


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

            def worker(machine, stack):
                frame, report = encode_captured_worker(machine, stack, window)
                payload = frame[AUDIO_OFFSET:]
                # Repeat the exact captured payload under fresh sequence tags
                # so the receiver moves from cold to buffered playout.
                frames = [frame] * (RECEIVE_LOOKAHEAD + 2)
                for index in (0, *range(2, len(frames))):
                    machine.mem_write(TX_SLOTS + SLOT_BYTES * index + AUDIO_OFFSET, payload)
                    frames[index] = transmit_audio(machine, index, payload)
                reports.append((frames, report))

            replay_endpoint(cpu, window, worker)
            return reports[0]

        frames, report = replay_startup(dongle_image, "dongle", inspect_application=encode)["application"]

        def decode(cpu):
            stack = cpu.reg_read(UC_RISCV_REG_SP)
            tx = initialize_receive(cpu)
            caller_stack = stack - 0x1800
            timer = ReceiveTimer(cpu, caller_stack, (RECEIVE_LOOKAHEAD << 3) + 2)
            # The modeled previous sequence must differ from the first arrival.
            cpu.mem_write(RX_STATE + 0x9B, b"\x07")
            check_receive_rejections(cpu, caller_stack, frames[0])
            receive_audio(cpu, caller_stack, frames[0])
            require(not run_receive_irq(cpu, caller_stack, frames[0]), "Stock RF receiver queued a duplicate packet")
            for frame in frames[1:-1]:
                receive_audio(cpu, caller_stack, frame)
            pending = bytearray(RECEIVE_PRIME_FRAMES * 4)
            timer.advance_radio_clock()
            cold_pcm, cold_peaks, _ = play_timed_block(cpu, tx, timer, pending, frames[0])
            require(cold_peaks[active_side] > 1000 and not any(cold_pcm),
                    "Cold stock receiver did not decode and mute the first packet")
            receive_timestamp, = struct.unpack("<I", cpu.mem_read(RX_STATE + 0x10, 4))
            require(not run_receive_irq(cpu, caller_stack, frames[-1], timestamp=receive_timestamp + 16_001),
                    "Stock RF receiver accepted a packet outside its modeled timestamp window")
            receive_audio(cpu, caller_stack, frames[-1], timestamp=receive_timestamp)
            timer.advance_radio_clock()
            output, decoded_peaks, phase_compares = play_timed_block(cpu, tx, timer, pending, frames[1])
            drain_fifo(cpu, tx, pending)
            return {"decoder_peaks": decoded_peaks, "cold_decoder_peaks": cold_peaks,
                    "fifo_peaks": [max(map(abs, lane)) for lane in zip(*struct.iter_unpack("<hh", output))],
                    "fifo_frames": len(output) // 4, "startup_silence_frames": RECEIVE_PRIME_FRAMES,
                    "cold_muted_frames": len(cold_pcm) // FRAME_BYTES, "timer_compare_cycles": phase_compares,
                    "rx_guard_controls": ["no_pending", "crc_error", "length_limit", "duplicate", "late_timestamp"]}

        levels = replay_startup(radio_image, "radio", inspect_application=decode)["application"]
        peaks = levels["fifo_peaks"]
        require(peaks[active_side] > 1000 and peaks[1 - active_side] == 0,
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
    frames = [frame for name, *_ in SCENES for frame in encoded[name]]
    audio = b"".join(frame[AUDIO_OFFSET:] for frame in frames)
    report = {"encoded_pairs": len(frames), "encoded_bytes": len(audio),
              "encoded_sha256": hashlib.sha256(audio).hexdigest(),
              "rf_dma_sha256": hashlib.sha256(b"".join(frames)).hexdigest(),
              "decoder_to_fifo": decoded["application"],
              "scheduled_receive": replay_startup(radio_image, "radio", inspect_application=lambda cpu:
                                                  replay_scheduled_link(cpu, encoded))["application"]}
    if args.dongle_usb_trace is not None:
        report["capture_worker"] = replay_capture_worker(dongle_image, radio_image, args.dongle_usb_trace,
                                                        args.usb_address)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

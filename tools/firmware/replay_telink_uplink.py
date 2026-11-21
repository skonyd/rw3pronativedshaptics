"""Run the controller's stock I2S-to-RF audio upload path."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct

from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE
from unicorn.riscv_const import (
    UC_RISCV_REG_A0, UC_RISCV_REG_A1, UC_RISCV_REG_A2, UC_RISCV_REG_A3, UC_RISCV_REG_A4,
    UC_RISCV_REG_MSTATUS, UC_RISCV_REG_RA, UC_RISCV_REG_S1, UC_RISCV_REG_SP,
)

from replay_controller_i2s import inject_fifo_words
from replay_telink_duplex import CONTROLLER_REPLY_TX, deliver_reply
from replay_telink_radio import initialize_i2s
from replay_telink_rf import DMA_RAM_ALIAS, TX_DMA_SOURCE, TX_STATE
from replay_telink_startup import replay_startup, require
from replay_telink_usb import USB_DISPATCH, ENDPOINT_STATUS, install_andes_hooks, run_until
from usb_voice_trace import inspect_voice


UPLINK_WORKER, UPLINK_MODE = 0x12348, 0x87598
BEFORE_ENCODER, AFTER_ENCODER = 0x12404, 0x1240C
UPLINK_QUEUE, VOICE_TAG = 0x852B4, 0x38
DONGLE_VOICE_QUEUE, DONGLE_LINK_MODE = 0x800B4, TX_STATE + 0x34
DONGLE_VOICE_SLOTS, DONGLE_VOICE_WORKER = 0x8E570, 0x54C8
DONGLE_VOICE_DECODER, DONGLE_VOICE_DECODER_INIT = 0x26DEC, 0x26CCC
DONGLE_MONO_STATE, DONGLE_MONO_CAPACITY = 0x90A2C, 1024
DONGLE_MONO_SAMPLES = DONGLE_MONO_STATE + 0x28
DONGLE_USB_MODE, DONGLE_USB_VOICE = 0x87F08, 0x4F3C
DONGLE_USB_FIFO, DONGLE_USB_ACK = 0x80100827, 0x8010082F
DONGLE_USB_SEQUENCE = 0x88084
DONGLE_AUDIO_TIMER, DONGLE_TIMER_STATUS = 0x3CDC, 0x80140143
RETURN, FRAMES, CODEC_BYTES = 0x500, 250, 40


def run_uplink(cpu, frames):
    _, rx = initialize_i2s(cpu)
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    stride, capacity, write, read, base = struct.unpack("<IHBBI", cpu.mem_read(UPLINK_QUEUE, 12))
    require(stride == 104 and capacity == 4 and write == read, "Unexpected stock voice queue layout")

    def worker():
        cpu.reg_write(UC_RISCV_REG_SP, stack)
        cpu.reg_write(UC_RISCV_REG_RA, RETURN)
        run_until(cpu, UPLINK_WORKER, RETURN, vendor_budget=220_000)

    worker()
    require(cpu.mem_read(UPLINK_QUEUE + 6, 1)[0] == write, "Disabled Telink voice flow queued a record")
    cpu.mem_write(UPLINK_MODE, b"\x40")  # Link state supplies the microphone enable flag.
    worker()
    first = base + (write % capacity) * stride
    require(cpu.mem_read(UPLINK_QUEUE + 6, 1)[0] == (write + 1) & 255 and
            cpu.mem_read(first + 4, 2) == bytes([CODEC_BYTES, VOICE_TAG]),
            "Stock microphone enable did not prime the voice queue")
    write = (write + 1) & 255
    # The peer's retirement of the initial record is fixture state.
    cpu.mem_write(UPLINK_QUEUE + 7, bytes([write]))

    captured = {}

    def observe_encoder(machine, pc, size, data):
        if pc == BEFORE_ENCODER:
            pcm, output = (machine.reg_read(register) for register in (UC_RISCV_REG_A0, UC_RISCV_REG_A1))
            captured["pcm"] = bytes(machine.mem_read(pcm, 480))
            captured["buffer"] = output
        else:
            require(machine.reg_read(UC_RISCV_REG_A0) == CODEC_BYTES, "Stock microphone encoder returned the wrong size")
            captured["encoded"] = bytes(machine.mem_read(captured["buffer"], CODEC_BYTES))

    inject_fifo_words(cpu, rx, b"".join(struct.pack("<hh", left, right) for left, right in frames))
    hooks = [cpu.hook_add(UC_HOOK_CODE, observe_encoder, begin=pc, end=pc) for pc in (BEFORE_ENCODER, AFTER_ENCODER)]
    try:
        worker()
    finally:
        for hook in hooks:
            cpu.hook_del(hook)
    require("encoded" in captured and cpu.mem_read(UPLINK_QUEUE + 6, 1)[0] == (write + 1) & 255,
            "Stock microphone worker did not encode and publish a record")
    slot = base + (write % capacity) * stride
    record = bytes(cpu.mem_read(slot, 6 + CODEC_BYTES))
    require(record[4:] == bytes([CODEC_BYTES, VOICE_TAG]) + captured["encoded"],
            "Voice queue did not retain the exact stock codec bytes")

    cpu.reg_write(UC_RISCV_REG_RA, RETURN)
    run_until(cpu, CONTROLLER_REPLY_TX, RETURN, vendor_budget=5000)
    source, = struct.unpack("<I", cpu.mem_read(TX_DMA_SOURCE, 4))
    frame = bytes(cpu.mem_read(source - DMA_RAM_ALIAS, 7 + CODEC_BYTES))
    require(frame[5:] == bytes([CODEC_BYTES + 1, VOICE_TAG]) + captured["encoded"],
            "Stock controller RF formatter changed the voice record")
    report = {"fifo_words_injected": len(frames),
              "pcm_peak": max(abs(sample) for sample, in struct.iter_unpack("<h", captured["pcm"])),
              "pcm_sha256": hashlib.sha256(captured["pcm"]).hexdigest(), "codec_bytes": CODEC_BYTES,
              "codec_sha256": hashlib.sha256(captured["encoded"]).hexdigest(),
              "rf_voice_tag": hex(frame[6]), "rf_dma_sha256": hashlib.sha256(frame).hexdigest()}
    return frame.hex(), report


def decode_to_usb(cpu, frame):
    stack = cpu.reg_read(UC_RISCV_REG_SP)
    cpu.reg_write(UC_RISCV_REG_MSTATUS, cpu.reg_read(UC_RISCV_REG_MSTATUS) | 0x6000)
    cpu.reg_write(UC_RISCV_REG_S1, 4096)
    # This stock setup call creates the mono resampler context.
    run_until(cpu, 0x1AD84, 0x1ADA0, vendor_budget=160_000)

    def call(entry):
        cpu.reg_write(UC_RISCV_REG_SP, stack)
        cpu.reg_write(UC_RISCV_REG_RA, RETURN)
        run_until(cpu, entry, RETURN, vendor_budget=200_000)

    call(DONGLE_VOICE_DECODER_INIT)
    slot = DONGLE_VOICE_SLOTS + (frame[6] & 7) * 64
    before = bytes(cpu.mem_read(slot, 44))
    require(before == frame[6:7] + bytes(3) + frame[7:], "RF interrupt did not populate the voice decode slot")
    call(DONGLE_VOICE_WORKER)
    require(cpu.mem_read(slot, 44) == before, "Disabled voice worker consumed a slot")
    # The negotiated stream and RF epoch are fixture inputs. Stock selects
    # two slots behind that epoch in this stream state.
    cpu.mem_write(TX_STATE + 0x41, b"\x40")
    cpu.mem_write(TX_STATE + 0x65, bytes([(frame[6] + 2) & 7]))
    first_sample = DONGLE_MONO_CAPACITY - 4
    cpu.mem_write(DONGLE_MONO_STATE, struct.pack("<II", first_sample, first_sample))
    observed = {}

    def observe(machine, pc, size, data):
        if pc == 0x5618:
            source, length, destination, lost = (machine.reg_read(r) for r in
                                                (UC_RISCV_REG_A0, UC_RISCV_REG_A1,
                                                 UC_RISCV_REG_A2, UC_RISCV_REG_A3))
            require(length == CODEC_BYTES and lost == 0 and
                    machine.mem_read(source, length) == frame[7:], "Decoder received different voice bytes")
            observed["decoded_address"] = destination
        elif pc == 0x561C:
            require(machine.reg_read(UC_RISCV_REG_A0) == 0, "Stock dongle voice decoder failed")
            observed["decoded"] = bytes(machine.mem_read(observed["decoded_address"], 480))
        elif pc == 0x5594:
            require(machine.reg_read(UC_RISCV_REG_A3) == 240, "Unexpected voice resampler input count")
            observed["pcm_address"] = machine.reg_read(UC_RISCV_REG_A4)
        else:
            observed["samples"] = machine.reg_read(UC_RISCV_REG_A0)
            require(observed["samples"] == 240, "Unexpected voice resampler output count")
            observed["pcm"] = bytes(machine.mem_read(observed["pcm_address"], 480))

    hooks = [cpu.hook_add(UC_HOOK_CODE, observe, begin=pc, end=pc) for pc in (0x5618, 0x561C, 0x5594, 0x55A4)]
    timer_pending = 0
    timer_ack = []

    def read_timer_status(machine, access, address, size, value, data):
        machine.mem_write(address, bytes([timer_pending]))

    def acknowledge_timer(machine, access, address, size, value, data):
        nonlocal timer_pending
        timer_ack.append(value)
        timer_pending &= ~value

    timer_hooks = [cpu.hook_add(UC_HOOK_MEM_READ, read_timer_status, begin=DONGLE_TIMER_STATUS, end=DONGLE_TIMER_STATUS),
                   cpu.hook_add(UC_HOOK_MEM_WRITE, acknowledge_timer, begin=DONGLE_TIMER_STATUS, end=DONGLE_TIMER_STATUS)]
    try:
        call(DONGLE_AUDIO_TIMER)
        require(not timer_ack and not observed and cpu.mem_read(slot, 44) == before,
                "Audio timer consumed a voice frame without its pending bit")
        # Seed one voice phase of the dual-purpose stock audio timer.
        # Stock computes (1100 - (tick - 20 - reference) / 24) & 7.
        epoch = (frame[6] + 2) & 7
        cpu.mem_write(TX_STATE + 0x65, bytes([epoch | 0x10]))
        cpu.mem_write(TX_STATE + 0x68, bytes(4))
        cpu.mem_write(0x80140200, struct.pack("<I", 20 + 24 * ((1100 - epoch) & 7)))
        timer_pending = 1
        call(DONGLE_AUDIO_TIMER)
        # The handler acknowledges at 0x3D16 and the timer-arm helper clears
        # pending again before enabling the next interval.
        require(timer_ack == [1, 1] and timer_pending == 0, "Unexpected stock voice timer acknowledgement or rearm")
    finally:
        for hook in hooks + timer_hooks:
            cpu.hook_del(hook)
    require(observed.get("samples") == 240 and cpu.mem_read(slot, 1) == b"\0",
            f"Stock voice worker did not retire its audio slot: checkpoints {list(observed)}, slot {cpu.mem_read(slot, 1).hex()}")
    require(struct.unpack("<I", cpu.mem_read(DONGLE_MONO_STATE, 4))[0] == first_sample + 240,
            "Stock voice worker advanced the mono write cursor incorrectly")
    pcm = observed["pcm"]
    ring = b"".join(cpu.mem_read(DONGLE_MONO_SAMPLES + ((first_sample + index) % DONGLE_MONO_CAPACITY) * 2, 2)
                    for index in range(240))
    require(ring == pcm, "Stock voice resampler output changed in the mono ring")

    cpu.mem_write(DONGLE_USB_MODE, b"\x01")
    cpu.mem_write(TX_STATE + 0x50, b"\x01")  # Negotiated USB audio stream.
    cpu.mem_write(DONGLE_USB_SEQUENCE, b"\xFE")  # Exercise stock sequence wrap, skipping zero.
    blocks = []
    writes = []
    endpoint_pending = 0

    def read_endpoint_status(machine, access, address, size, value, data):
        machine.mem_write(address, bytes([endpoint_pending]))

    def acknowledge_endpoint(machine, access, address, size, value, data):
        nonlocal endpoint_pending
        endpoint_pending &= ~value

    hook = cpu.hook_add(UC_HOOK_MEM_WRITE, lambda _, access, address, size, value, data:
                        writes.append((address, size, value)), begin=DONGLE_USB_FIFO, end=DONGLE_USB_ACK)
    endpoint_hooks = [cpu.hook_add(UC_HOOK_MEM_READ, read_endpoint_status, begin=ENDPOINT_STATUS, end=ENDPOINT_STATUS),
                      cpu.hook_add(UC_HOOK_MEM_WRITE, acknowledge_endpoint, begin=ENDPOINT_STATUS, end=ENDPOINT_STATUS)]
    try:
        call(USB_DISPATCH)
        require(not writes and struct.unpack("<I", cpu.mem_read(DONGLE_MONO_STATE + 4, 4))[0] == first_sample,
                "USB dispatcher consumed voice data without an endpoint event")
        for index in range(6):
            writes.clear()
            endpoint_pending = 0x80
            call(USB_DISPATCH)
            require(endpoint_pending == 0, "Stock USB dispatcher did not acknowledge endpoint 7")
            require(all(size == 1 for _, size, _ in writes), "Unexpected endpoint register access width")
            packet = bytes(value for address, _, value in writes if address == DONGLE_USB_FIFO)
            require(len(packet) == 54 and packet[:2] == b"\x60\x23" and packet[3:6] == b"\x32\xC0\0" and
                    [(address, value) for address, _, value in writes if address == DONGLE_USB_ACK] == [(DONGLE_USB_ACK, 1)],
                    "Stock voice endpoint did not arm a complete Xbox audio packet")
            if blocks:
                require(packet[2] == blocks[-1][2] % 255 + 1, "USB audio sequence did not advance or skip zero")
            # The Xbox branch takes every second mono sample from each
            # 48-sample chunk. Validate the exact stock FIFO byte order.
            expected = b"".join(pcm[offset:offset + 2] for offset in range(index * 96, (index + 1) * 96, 4))
            require(packet[6:] == (expected if index < 5 else bytes(48)),
                    "Stock USB audio bytes differ from the decoded ring or underrun silence")
            blocks.append(packet)
    finally:
        cpu.hook_del(hook)
        for hook in endpoint_hooks:
            cpu.hook_del(hook)
    require(struct.unpack("<I", cpu.mem_read(DONGLE_MONO_STATE + 4, 4))[0] == first_sample + 240,
            "Underrun incorrectly consumed samples")
    # Missing RF audio follows the stock empty-slot branch. Run it until
    # the short resampler history drains, then restore the same real frame
    # through the stock RF IRQ and confirm decoding resumes.
    decodes = []
    hook = cpu.hook_add(UC_HOOK_CODE, lambda *_: decodes.append(True),
                        begin=DONGLE_VOICE_DECODER, end=DONGLE_VOICE_DECODER)
    missing = []
    try:
        for _ in range(2):
            start, = struct.unpack("<I", cpu.mem_read(DONGLE_MONO_STATE, 4))
            call(DONGLE_VOICE_WORKER)
            end, = struct.unpack("<I", cpu.mem_read(DONGLE_MONO_STATE, 4))
            require(end - start == 240 and not decodes, "Missing voice slot entered the codec or lost sample pacing")
            samples = b"".join(cpu.mem_read(DONGLE_MONO_SAMPLES + ((start + index) % DONGLE_MONO_CAPACITY) * 2, 2)
                                for index in range(240))
            missing.append(max(abs(value) for value, in struct.iter_unpack("<h", samples)))
        require(missing[-1] == 0, "Missing voice input did not settle to silence")
        deliver_reply(cpu, frame, receive_quality=0x3C)
        require(cpu.mem_read(slot, 1) == b"\0", "Stock voice receiver replayed a duplicate RF response")
        # The stock controller toggles this reply sequence bit at 0x10B30.
        # Supply the next RF header while retaining the original codec bytes.
        next_frame = frame[:4] + bytes([frame[4] ^ 1]) + frame[5:]
        deliver_reply(cpu, next_frame, receive_quality=0x3C)
        call(DONGLE_VOICE_WORKER)
        require(len(decodes) == 1 and cpu.mem_read(slot, 1) == b"\0",
                "Stock voice stream did not decode after a new RF response sequence")
    finally:
        cpu.hook_del(hook)
    return {"decoded_samples": 240, "mono_ring_wrap": True, "stock_timer_irq_used": True,
            "decoded_peak": max(abs(value) for value, in struct.iter_unpack("<h", observed["decoded"])),
            "resampled_peak": max(abs(value) for value, in struct.iter_unpack("<h", pcm)),
            "resampled_pcm_sha256": hashlib.sha256(pcm).hexdigest(),
            "usb_endpoint": "0x87", "usb_packets": len(blocks), "packet_bytes": 54,
            "usb_header_without_sequence": (blocks[0][:2] + blocks[0][3:6]).hex(),
            "usb_sequences": [packet[2] for packet in blocks],
            "usb_payload_sha256": hashlib.sha256(b"".join(packet[6:] for packet in blocks)).hexdigest(),
            "underrun_silent": True, "missing_slot_peaks": missing, "recovery_decoded": True}


def receive_uplink(cpu, frame, quality, *, playout=False):
    for address in (0x80140000, 0x80170000):
        cpu.mem_map(address, 0x1000)
    install_andes_hooks(cpu, ((0x800, 0x29723),))
    cpu.mem_write(DONGLE_LINK_MODE, struct.pack("<I", 1))  # Negotiated link state.
    stride, capacity, write, read, base = struct.unpack("<IHBBI", cpu.mem_read(DONGLE_VOICE_QUEUE, 12))
    require(stride == 104 and capacity == 4 and write == read, "Unexpected stock dongle voice queue layout")
    slot = base + (write % capacity) * stride
    before = bytes(cpu.mem_read(slot, stride))
    voice_slot = DONGLE_VOICE_SLOTS + (frame[6] & 7) * 64
    voice_before = bytes(cpu.mem_read(voice_slot, 64))
    deliver_reply(cpu, frame, pending=0, receive_quality=quality)
    require(cpu.mem_read(voice_slot, 64) == voice_before and cpu.mem_read(slot, stride) == before,
            "Stock dongle consumed voice input without an RF interrupt")
    deliver_reply(cpu, frame, crc_error=True, receive_quality=quality)
    require(cpu.mem_read(slot, stride) == before and cpu.mem_read(DONGLE_VOICE_QUEUE + 6, 1)[0] == write,
            "Stock dongle queued a CRC-invalid voice frame")
    require(cpu.mem_read(voice_slot, 64) == voice_before, "Stock dongle decoded a CRC-invalid voice frame")
    deliver_reply(cpu, frame, receive_quality=quality)
    after = cpu.mem_read(DONGLE_VOICE_QUEUE + 6, 1)[0]
    require(cpu.mem_read(voice_slot, 44) == frame[6:7] + bytes(3) + frame[7:],
            "Stock RF parser did not write the exact voice decode slot")
    if quality <= 0x38:
        require(after == write and cpu.mem_read(slot, stride) == before,
                "Stock dongle queued a voice frame below its metadata threshold")
        return {"queued": False, "control_queue_threshold_rejected": True, "audio_slot_written": True}
    require(after == (write + 1) & 255, "Stock dongle did not publish the voice record")
    record = bytes(cpu.mem_read(slot, 6 + CODEC_BYTES))
    require(record[4:] == bytes([CODEC_BYTES]) + frame[6:], "Dongle voice queue changed the codec record")
    report = {"queued": True, "queue": hex(DONGLE_VOICE_QUEUE), "crc_error_rejected": True,
              "codec_sha256": hashlib.sha256(record[6:]).hexdigest()}
    if playout:
        report["playout"] = decode_to_usb(cpu, frame)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("controller_radio_fw", type=Path)
    parser.add_argument("dongle_fw", type=Path)
    parser.add_argument("--dongle-usb-trace", type=Path, help="Compare USB IN framing with an original USBPcap capture")
    parser.add_argument("--usb-address", type=int, default=9)
    args = parser.parse_args()
    capture = inspect_voice(args.dongle_usb_trace, args.usb_address) if args.dongle_usb_trace else None
    image = args.controller_radio_fw.read_bytes()
    tone = [round(24_000 * math.sin(math.tau * 950 * index / 48_000)) for index in range(FRAMES)]
    fixtures = {"silence": [(0, 0)] * FRAMES, "low_lane_tone": [(sample, 0) for sample in tone],
                "high_lane_tone": [(0, sample) for sample in tone]}
    played = {name: replay_startup(image, "radio", lambda cpu: run_uplink(cpu, frames))["application"]
              for name, frames in fixtures.items()}
    report = {name: result[1] for name, result in played.items()}
    require(report["silence"] == report["high_lane_tone"] and report["silence"]["pcm_peak"] == 0,
            "Only the low FIFO half-word may enter the voice encoder")
    require(report["low_lane_tone"]["pcm_peak"] > 0 and
            report["low_lane_tone"]["codec_sha256"] != report["silence"]["codec_sha256"],
            "Active upstream PCM did not change the encoded RF payload")
    dongle = args.dongle_fw.read_bytes()
    frame = bytes.fromhex(played["low_lane_tone"][0])
    for name, quality in (("accepted", 0x3C), ("threshold_control", 0x38)):
        report[name] = replay_startup(dongle, "dongle", lambda cpu:
                                    receive_uplink(cpu, frame, quality, playout=quality > 0x38))["application"]
    require(report["accepted"]["codec_sha256"] == report["low_lane_tone"]["codec_sha256"],
            "Paired stock voice path changed codec bytes")
    require(report["accepted"]["playout"]["decoded_peak"] > 1000 and
            report["accepted"]["playout"]["resampled_peak"] > 1000 and
            report["accepted"]["playout"]["usb_payload_sha256"] != hashlib.sha256(bytes(6 * 48)).hexdigest(),
            "Active microphone tone was lost before USB output")
    for name in ("silence", "high_lane_tone"):
        silent_frame = bytes.fromhex(played[name][0])
        report[name]["playout"] = replay_startup(dongle, "dongle", lambda cpu:
                                                receive_uplink(cpu, silent_frame, 0x3C, playout=True))["application"]["playout"]
        require(report[name]["playout"]["decoded_peak"] == report[name]["playout"]["resampled_peak"] == 0,
                "Stock voice playout leaked another FIFO lane into the USB output")
    require(report["silence"] == report["high_lane_tone"], "Upstream isolation changed through USB playout")
    if capture:
        for playout in (report["accepted"]["playout"], report["silence"]["playout"], report["high_lane_tone"]["playout"]):
            require(playout["usb_header_without_sequence"] == capture["header_without_sequence"] and
                    playout["packet_bytes"] == capture["packet_bytes"],
                    "Stock replay USB packet framing differs from the recorded device")
        report["capture_framing"] = capture
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

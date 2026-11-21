"""Validate recorded Xbox dongle USB IN voice framing without altering PCM."""
from collections import Counter
import argparse
import hashlib
import json
from pathlib import Path
import struct

from usb_audio_trace import packets


def require(condition, message):
    if not condition:
        raise ValueError(message)


def parse_voice_urb(packet):
    require(len(packet) >= 39, "Truncated voice USBPcap header")
    header, = struct.unpack_from("<H", packet)
    status, = struct.unpack_from("<I", packet, 10)
    _, endpoint, transfer, length = struct.unpack_from("<HBBI", packet, 19)
    count, errors = struct.unpack_from("<II", packet, 31)
    require(endpoint == 0x87 and transfer == 0 and packet[16] & 1,
            "Expected endpoint 0x87 isochronous completion")
    require(status == errors == 0, "Failed voice USB transfer")
    require(0 < count <= 1024 and header == 39 + 12 * count and header + length == len(packet),
            "Invalid voice isochronous header or transfer length")
    slots, cursor = [], 0
    for index in range(count):
        offset, size, slot_status = struct.unpack_from("<III", packet, 39 + 12 * index)
        require(slot_status == 0 and offset == cursor and size == 54 and offset + size <= length,
                "Invalid voice isochronous slot")
        slot = packet[header + offset:header + offset + size]
        require(slot[:2] == b"\x60\x23" and slot[2] != 0 and slot[3:6] == b"\x32\xC0\0",
                "Unexpected Xbox voice header or zero sequence")
        slots.append(slot)
        cursor += size
    require(cursor == length, "Unaccounted voice transfer bytes")
    return slots


def inspect_voice(path, address):
    require(1 <= address <= 127, "USB device address must be in 1–127")
    groups, previous, wraps, total, peak = Counter(), None, 0, 0, 0
    payload_hash = hashlib.sha256()
    for _, packet in packets(path):
        device, endpoint, transfer = struct.unpack_from("<HBB", packet, 19)
        if device != address or endpoint != 0x87 or transfer != 0 or not packet[16] & 1:
            continue
        slots = parse_voice_urb(packet)
        groups[len(slots)] += 1
        for slot in slots:
            sequence = slot[2]
            if previous is not None:
                require(sequence == previous % 255 + 1, "Voice sequence gap or duplicate")
                wraps += int(previous == 255)
            previous = sequence
            total += 1
            pcm = slot[6:]
            payload_hash.update(pcm)
            peak = max(peak, max(abs(value) for value, in struct.iter_unpack("<h", pcm)))
    require(total > 0, "No recorded endpoint 0x87 voice packets for this USB address")
    return {"device_address": address, "usb_endpoint": "0x87", "urb_completions": sum(groups.values()),
            "packets_per_urb": dict(groups), "voice_packets": total, "packet_bytes": 54,
            "pcm_samples_per_packet": 24, "header_without_sequence": "602332c000",
            "sequence_wraps_skipping_zero": wraps, "pcm_peak": peak,
            "pcm_sha256": payload_hash.hexdigest(), "capture_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "scope": "captured framing and grouping; no synthetic waveform or timing equivalence"}


def check_negative_controls(packet):
    """Mutate one actual completion to ensure invalid captures fail closed."""
    parse_voice_urb(packet)
    header, = struct.unpack_from("<H", packet)
    controls = {"truncated_transfer": packet[:-1], "truncated_header": packet[:38]}
    for name, offset, value in (
        ("failed_urb", 10, struct.pack("<I", 1)),
        ("iso_error_count", 35, struct.pack("<I", 1)),
        ("slot_past_end", 39, struct.pack("<I", len(packet))),
        ("wrong_slot_size", 43, struct.pack("<I", 53)),
        ("failed_slot", 47, struct.pack("<I", 1)),
        ("zero_sequence", header + 2, b"\0"),
        ("wrong_gip_length", header + 3, b"\x31"),
    ):
        damaged = bytearray(packet)
        damaged[offset:offset + len(value)] = value
        controls[name] = bytes(damaged)
    for name, damaged in controls.items():
        try:
            parse_voice_urb(damaged)
        except ValueError:
            continue
        raise ValueError(f"Voice parser incorrectly accepted {name}")
    return list(controls)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("address", type=int)
    args = parser.parse_args()
    report = inspect_voice(args.capture, args.address)
    for _, packet in packets(args.capture):
        device, endpoint, transfer = struct.unpack_from("<HBB", packet, 19)
        if device == args.address and endpoint == 0x87 and transfer == 0 and packet[16] & 1:
            report["rejected_negative_controls"] = check_negative_controls(packet)
            break
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

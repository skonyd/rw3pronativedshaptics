"""Inspect a USBPcap capture for downstream isochronous GIP audio messages."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct


def packets(path):
    with path.open("rb") as stream:
        header = stream.read(24)
        if (len(header) != 24 or header[:4] != b"\xd4\xc3\xb2\xa1"
                or struct.unpack_from("<I", header, 20)[0] != 249):
            raise ValueError("Expected little-endian USBPcap PCAP")
        while record := stream.read(16):
            if len(record) != 16:
                raise ValueError("Truncated PCAP record header")
            seconds, micros, size, _ = struct.unpack("<IIII", record)
            if not 27 <= size <= 16 * 1024 * 1024:
                raise ValueError("Unexpected USBPcap record size")
            packet = stream.read(size)
            if len(packet) != size:
                raise ValueError("Truncated PCAP record")
            yield seconds * 1000000 + micros, packet


def gip_audio(slot):
    # MS-GIPUSB 3.2.5.1.3. Length can be zero-extended to align PCM.
    if len(slot) < 4 or slot[0] != 0x60 or slot[1] & 0xF0 != 0x20 or not slot[2]:
        return None
    length = 0
    for index, byte in enumerate(slot[3:7]):
        length |= (byte & 0x7F) << (index * 7)
        if not byte & 0x80:
            start = 4 + index
            if start % 2 == 0 and 0 < length <= len(slot) - start:
                return slot[2], slot[start:start + length]
            return None
    return None


def inspect(path, address):
    endpoints, slot_sizes, payload_sizes = Counter(), Counter(), Counter()
    blocks = []
    for time_us, packet in packets(path):
        device, endpoint, transfer, length = struct.unpack_from("<HBBI", packet, 19)
        if device != address:
            continue
        endpoints[f"0x{endpoint:02x}/{transfer}"] += 1
        if transfer != 0 or endpoint & 0x80 or packet[16] & 1:
            continue
        hlen = struct.unpack_from("<H", packet)[0]
        if hlen < 39 or hlen + length > len(packet):
            raise ValueError("Truncated isochronous output")
        count = struct.unpack_from("<I", packet, 31)[0]
        if hlen != 39 + 12 * count:
            raise ValueError("Unexpected isochronous header length")
        offsets = [struct.unpack_from("<I", packet, 39 + 12 * index)[0]
                   for index in range(count)] + [length]
        for start, end in zip(offsets, offsets[1:]):
            if not 0 <= start <= end <= length:
                raise ValueError("Invalid isochronous slot offsets")
            slot_sizes[end - start] += 1
            parsed = gip_audio(packet[hlen + start:hlen + end])
            if parsed:
                sequence, data = parsed
                payload_sizes[len(data)] += 1
                blocks.append((time_us, sequence, data))
    return {"device_address": address, "endpoints": dict(endpoints),
            "isochronous_out_slot_sizes": dict(slot_sizes),
            "gip_audio_payload_sizes": dict(payload_sizes),
            "gip_audio_messages": len(blocks),
            "gip_audio_sha256": hashlib.sha256(b"".join(block[2] for block in blocks)).hexdigest()
            if blocks else None}, blocks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("address", type=int)
    parser.add_argument("--blocks", type=Path, help="Write a local JSON trace of downstream payloads")
    args = parser.parse_args()
    if not 1 <= args.address <= 127:
        parser.error("device address must be in 1–127")
    report, blocks = inspect(args.capture, args.address)
    if args.blocks:
        args.blocks.write_text(json.dumps([
            {"urb_timestamp_us": timestamp, "sequence": sequence, "payload_hex": data.hex()}
            for timestamp, sequence, data in blocks
        ]), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

"""Classify the B91 update images by file offset, retaining unknown ranges."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import zlib

from capstone import Cs, CS_ARCH_RISCV, CS_MODE_RISCV32, CS_MODE_RISCVC


def map_image(path):
    image = path.read_bytes()
    payload_offset = 0x1000
    if len(image) <= payload_offset:
        raise ValueError(f"{path}: missing B91 payload")
    size, checksum = struct.unpack_from("<II", image, 4)
    if size != len(image) - payload_offset or checksum != (zlib.crc32(image[payload_offset:]) ^ 0xFFFFFFFF):
        raise ValueError(f"{path}: B91 length or CRC mismatch")

    startup = 0x1028
    anchors = [(0, 0x14, "header_words")]
    # Fixed anchors derived from the two distinct entry sequences. These short
    # basic blocks end before the first flow transfer or undecodable word.
    decoder = Cs(CS_ARCH_RISCV, CS_MODE_RISCV32 | CS_MODE_RISCVC)
    entry_blocks = []
    for start in (payload_offset, startup):
        end = start
        for instruction in decoder.disasm(image[start:start + 0x80], start):
            if instruction.mnemonic == "c.unimp":
                break
            end = instruction.address + instruction.size
            if instruction.mnemonic.startswith(("b", "j", "c.j", "c.b")):
                break
        if end > start:
            entry_blocks.append((start, end, "entry_instructions"))
    anchors.extend(entry_blocks)

    strings = []
    keywords = re.compile(rb"audio|usb|rf_|pair|i2s|sdk|dma|lc3", re.I)
    for match in re.finditer(rb"[ -~]{8,}\x00", image[payload_offset:]):
        value = match[0][:-1]
        if keywords.search(value):
            start, end = payload_offset + match.start(), payload_offset + match.end()
            strings.append({"offset": start, "text": value.decode("ascii")})
            anchors.append((start, end, "string_candidate"))

    ranges = []
    cursor = 0
    for start, end, label in sorted(anchors):
        if start < cursor:
            continue
        if start > cursor:
            ranges.append((cursor, start, "unknown"))
        ranges.append((start, end, label))
        cursor = end
    if cursor < len(image):
        ranges.append((cursor, len(image), "unknown"))
    return {"image": path.name, "sha256": hashlib.sha256(image).hexdigest(),
            "image_size": len(image), "payload_offset": payload_offset,
            "inverted_crc32": f"{checksum:08x}", "strings": strings,
            "ranges": ranges}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("images", type=Path, nargs=2, metavar="B91_IMAGE")
    args = parser.parse_args()
    print(json.dumps([map_image(path) for path in args.images], indent=2))


if __name__ == "__main__":
    main()

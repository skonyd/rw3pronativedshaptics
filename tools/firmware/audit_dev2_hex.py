"""Validate the stock Dev2FW Intel HEX envelope without generating an image."""
import argparse
import hashlib
import json
from pathlib import Path

STOCK_START, STOCK_END = 0x8000, 0x37800
STOCK_ENTRY = 0x8401
STOCK_SHA256 = "d19550cf434da3e3ae3c7e8e46f23e5c5f61274a0a462bf81b768c3953a1aee8"


def audit(lines):
    base = 0
    entry = None
    ended = False
    data = bytearray()
    counts = {key: 0 for key in (0, 1, 4, 5)}
    for number, line in enumerate(lines, 1):
        line = line.strip()
        if not line:
            continue
        if ended or not line.startswith(":"):
            raise ValueError(f"Unexpected record at line {number}")
        record = bytes.fromhex(line[1:])
        if len(record) < 5 or len(record) != record[0] + 5 or sum(record) & 255:
            raise ValueError(f"Bad record length or checksum at line {number}")
        address = int.from_bytes(record[1:3], "big")
        kind = record[3]
        payload = record[4:-1]
        if kind == 0:
            absolute = base + address
            if (not payload or address + len(payload) > 0x10000
                    or absolute != STOCK_START + len(data) or absolute + len(payload) > STOCK_END):
                raise ValueError(f"Gap, overlap or invalid data range at line {number}")
            data.extend(payload)
        elif kind == 4 and address == 0 and len(payload) == 2 and entry is None:
            base = int.from_bytes(payload, "big") << 16
        elif kind == 5 and address == 0 and len(payload) == 4 and entry is None:
            entry = int.from_bytes(payload, "big")
            if STOCK_START + len(data) != STOCK_END or entry != STOCK_ENTRY:
                raise ValueError(f"Unexpected entry or incomplete application at line {number}")
        elif kind == 1 and address == 0 and not payload and entry is not None:
            ended = True
        else:
            raise ValueError(f"Invalid record type or fields at line {number}")
        counts[kind] += 1
    if not ended:
        raise ValueError("Missing end-of-file record")
    digest = hashlib.sha256(data).hexdigest()
    if digest != STOCK_SHA256:
        raise ValueError("Decoded bytes differ from stock Dev2FW 2.04.00")
    return {"start": hex(STOCK_START), "end_exclusive": hex(STOCK_END), "entry": hex(entry),
            "data_bytes": len(data), "records": counts, "sha256": digest}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stock_hex", type=Path)
    args = parser.parse_args()
    with args.stock_hex.open(encoding="ascii") as source:
        print(json.dumps(audit(source), indent=2))


if __name__ == "__main__":
    main()

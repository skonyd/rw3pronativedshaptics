"""Runs the Wolverine V3 Pro Sensa frame decoders from firmware 2.04.00 (Dev2FW) in an ARM emulator.

Offline and read-only: nothing is sent to a controller. Needs `pip install unicorn capstone`.

    python -I emulate_decoder.py Dev2FW.bin pc   <frame hex>    decode a PC-mode frame (decoder 0x2A708)
    python -I emulate_decoder.py Dev2FW.bin xbox <frame hex>    decode an Xbox-mode frame (decoder 0xEF50)
    python -I emulate_decoder.py Dev2FW.bin selftest            Xbox encoder below vs the emulated decoder

Decoded body parts land in RAM at 0x20013530, 0x82 bytes each, as little-endian int16:
- v[0]: header-derived value.
- v[1]: transient count.
- v[2..]: transients as (time ms, pitch, amplitude).
- v[17..]: band 1, as point count then (time, pitch, amplitude) triples.
- v[33..]: band 2, same layout.
Palms: pitch in Hz (30 + p·370/127), amplitude a·520. Body parts 2/3 (249/149): raw 7-bit pitch, raw 6-bit amplitude.
See docs/SENSA_USB_PROTOCOL.md ("Xbox mode frame") and docs/FIRMWARE_ANALYSIS_20261110.md.
"""
import struct, sys
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_MODE_MCLASS
from unicorn.arm_const import UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_R0

BASE, FRAME, OUT, BODY = 0x8000, 0x20020000, 0x20013530, 0x82
DECODER = {'pc': 0x2A708, 'xbox': 0xEF50}
XBOX_MAX_HITS = (2, 2, 1, 1)   # body parts 216, 116, 249, 149


def decode(img, entry, frame):
    mu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
    mu.mem_map(0, 0x40000); mu.mem_write(BASE, img)
    mu.mem_map(0x20000000, 0x40000)
    mu.mem_map(0x40000000, 0x10000000)          # peripherals read as 0
    mu.mem_map(0xE0000000, 0x100000)
    mu.mem_write(0xE000ED88, (0xF << 20).to_bytes(4, 'little'))   # CPACR: FPU on
    mu.mem_write(FRAME, bytes(frame) + bytes(64))
    mu.reg_write(UC_ARM_REG_SP, 0x20027000); mu.reg_write(UC_ARM_REG_LR, 0xFFF1); mu.reg_write(UC_ARM_REG_R0, FRAME)
    mu.emu_start(entry | 1, 0xFFF0, count=2_000_000)
    ram = bytes(mu.mem_read(OUT, 4 * BODY))
    return [struct.unpack_from('<65h', ram, i * BODY) for i in range(4)]


def xbox_frame(bodies, header=0b0111100):
    """bodies: 4 × (bands [(a0, a1, pitch)] ≤ 2, hits [(strength, pitch, ms)] ≤ 2/2/1/1); values are raw field values."""
    bits = []
    put = lambda v, n: bits.extend((v >> (n - 1 - i)) & 1 for i in range(n))
    put(header, 7)
    for i, (bands, hits) in enumerate(bodies):
        for k in range(2):
            if k >= len(bands): put(0, 1); break
            a0, a1, p = bands[k]; put(1, 1); put(a0, 6); put(p, 7); put(a1, 6); put(p, 7)
        for k in range(XBOX_MAX_HITS[i]):
            if k >= len(hits): put(0, 1); break
            s, p, t = hits[k]; put(1, 1); put(s, 6); put(p, 7); put(t, 7)
    out = bytearray((len(bits) + 7) // 8)
    for i, b in enumerate(bits):
        if b: out[i >> 3] |= 0x80 >> (i & 7)
    return bytes(out)


def show(v):
    for i, b in enumerate(v):
        print(f'body {i}: hits {b[1]} {b[2:8]} band1 {b[17:27]} band2 {b[33:43]}')


if __name__ == '__main__':
    img = open(sys.argv[1], 'rb').read()
    if sys.argv[2] == 'selftest':
        f = xbox_frame([([(10, 20, 10)], []), ([(30, 40, 50)], [(63, 24, 5)]), ([(50, 60, 100)], []),
                        ([(63, 63, 127), (5, 6, 0)], [(20, 10, 30)])])
        print(len(f), 'bytes', f.hex()); show(decode(img, DECODER['xbox'], f))
    else:
        show(decode(img, DECODER[sys.argv[2]], bytes.fromhex(sys.argv[3])))

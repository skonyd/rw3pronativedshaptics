import sys, re, math, collections, struct
# Read-only scan: header words, printable strings, entropy per 4 KB, and USB/HID descriptor signatures.
for f in sys.argv[1:]:
    img = open(f, 'rb').read(); name = f.split('/')[-1]
    print(f"== {name}: first words", [hex(w) for w in struct.unpack_from('<8I', img, 0)])
    ents = []
    for o in range(0, len(img), 4096):
        blk = img[o:o + 4096]; c = collections.Counter(blk)
        ents.append(-sum(v / len(blk) * math.log2(v / len(blk)) for v in c.values()))
    print("  entropy/4KB:", ' '.join(f"{e:.1f}" for e in ents))
    strs = [(m.start(), m.group().decode()) for m in re.finditer(rb'[ -~]{6,}', img)]
    print(f"  {len(strs)} strings; sample:", [s for _, s in strs[:40]])
    for sig, label in [(b'\x05\x01\x09\x05', 'HID gamepad usage'), (b'\x06\x00\xff', 'HID vendor page'), (b'\x12\x01\x00\x02', 'USB dev desc 2.0'), (b'\x32\x15', 'VID 1532 LE'), (b'\x3f\x0a', 'PID 0A3F'), (b'\x4c\x0a', 'PID 0A4C'), (b'\xff\x5d\x01', 'XUSB iface')]:
        hits = [m.start() for m in re.finditer(re.escape(sig), img)]
        if hits: print(f"  {label}: {len(hits)} at", [hex(h + 0x8000) for h in hits[:8]])

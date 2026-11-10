import sys, re, math, collections
# Intel HEX -> flat binary (gaps 0xFF); prints segments, entropy and printable strings.
src, dst = sys.argv[1], sys.argv[2]
mem = {}; base = 0
for ln in open(src):
    ln = ln.strip()
    if not ln.startswith(':'): continue
    b = bytes.fromhex(ln[1:]); n, addr, typ = b[0], (b[1] << 8) | b[2], b[3]; d = b[4:4 + n]
    if typ == 0:
        for i, v in enumerate(d): mem[base + addr + i] = v
    elif typ == 4: base = ((d[0] << 8) | d[1]) << 16
    elif typ == 2: base = ((d[0] << 8) | d[1]) << 4
lo, hi = min(mem), max(mem) + 1
img = bytearray([0xFF]) * (hi - lo)
for a, v in mem.items(): img[a - lo] = v
open(dst, 'wb').write(img)
c = collections.Counter(img); ent = -sum(v / len(img) * math.log2(v / len(img)) for v in c.values())
print(f"{src.split('/')[-1]}: 0x{lo:08X}-0x{hi:08X} ({hi - lo} bytes, {len(mem)} defined), entropy {ent:.2f} bits/byte")

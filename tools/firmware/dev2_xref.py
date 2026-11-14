"""Cross references in the NXP image: which functions build a given address (or range) with movw/movt or a
literal, with the offsets they then access through that base.

    python -I dev2_xref.py Dev2FW.bin functions.csv 0x20013530 [0x20013740]   (needs capstone)
"""
import csv, re, struct, sys, collections
from capstone import Cs, CS_ARCH_ARM, CS_MODE_THUMB

B = 0x8000
img = open(sys.argv[1], 'rb').read()
funcs = sorted((int(r['address'], 16), int(r['size_bytes'])) for r in csv.DictReader(open(sys.argv[2], encoding='utf-8')))
lo = int(sys.argv[3], 16); hi = int(sys.argv[4], 16) if len(sys.argv) > 4 else lo + 1
md = Cs(CS_ARCH_ARM, CS_MODE_THUMB)
hits = collections.defaultdict(list)
for f, size in funcs:
    regs = {}
    for i in md.disasm(img[f - B:f - B + max(size, 2)], f):
        ops = i.op_str
        m = re.search(r'#(0x[0-9a-f]+|\d+)$', ops)
        r = ops.split(',')[0].strip()
        if i.mnemonic.startswith('movw') and m: regs[r] = int(m.group(1), 0)
        elif i.mnemonic.startswith('movt') and m and r in regs:
            v = (int(m.group(1), 0) << 16) | regs[r]; regs[r] = v
            if lo <= v < hi: hits[f].append((i.address, hex(v), 'movw/movt'))
        elif i.mnemonic.startswith('ldr') and '[pc' in ops:
            off = int(ops.split('#')[-1].rstrip(']'), 16); la = ((i.address + 4) & ~3) + off
            if 0 <= la - B < len(img) - 3:
                v = struct.unpack_from('<I', img, la - B)[0]
                if lo <= v < hi: hits[f].append((i.address, hex(v), 'literal'))
        else:
            # base + offset uses: [rX, #off] where rX holds an address in range
            mm = re.search(r'\[(r\d+|sb|sl|fp|ip|lr), #(-?0x[0-9a-f]+|-?\d+)\]', ops)
            if mm and mm.group(1) in regs and isinstance(regs[mm.group(1)], int):
                v = regs[mm.group(1)] + int(mm.group(2), 0)
                if lo <= v < hi and regs[mm.group(1)] > 0xFFFF: hits[f].append((i.address, hex(v), i.mnemonic))
for f in sorted(hits):
    kinds = collections.Counter(k for _, _, k in hits[f])
    addrs = sorted(set(v for _, v, _ in hits[f]))
    print(f'{f:#07x}  {dict(kinds)}  {" ".join(addrs[:12])}')

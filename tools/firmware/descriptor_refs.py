"""Read-only: finds the Sensa descriptor strings in a Wolverine haptics-MCU image and the code that selects them.

Usage: python -I descriptor_refs.py Dev2FW.bin   (needs `pip install capstone`; image base 0x8000)
Prints each body-part descriptor, MOVW/MOVT references to it, and every access to the mode flag byte
(0x20000F68 + 0xEF) that picks the 2- or 4-body-part descriptor.
"""
import re, sys
from capstone import Cs, CS_ARCH_ARM, CS_MODE_THUMB

BASE = 0x8000
img = open(sys.argv[1], 'rb').read()
descs = [(m.start() + BASE, m.group().decode()) for m in re.finditer(rb'\{"Bodypart":\[[ -~\t]+', img)]
for a, t in descs:
    print(hex(a), re.sub(r'\s{2,}', ' ', t)[:400], '...')
md = Cs(CS_ARCH_ARM, CS_MODE_THUMB); md.skipdata = True
ins = [(i.address, i.mnemonic, i.op_str) for i in md.disasm(img, BASE)]
lows = {a & 0xFFFF: a for a, _ in descs}
for k, (a, m, o) in enumerate(ins):
    x = re.search(r'#(0x[0-9a-f]+)$', o)
    if m.startswith('movw') and x and int(x.group(1), 16) in lows:
        print('ref', hex(a), m, o, '->', hex(lows[int(x.group(1), 16)]))
    if m.startswith('movw') and o.endswith('#0xf68'):
        reg = o.split(',')[0]
        for b, mm, oo in ins[k + 1:k + 40]:
            if re.search(r'\[' + reg + r', #0xef\]', oo):
                print('flag', hex(b), mm, oo)

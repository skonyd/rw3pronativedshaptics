"""Enumerates every Razer HID command class/id the NXP firmware (Dev2FW 2.04.00) handles, with handler addresses.

Dispatcher 0x30524: packet in RAM via pointer 0x200000D8; class byte at offset 6 (no report id); TBB class table at
0x30754 (class <= 0x16). Each class entry calls a per-class function that switches on the command byte (offset 7)
with "subs rX,#base; cmp rX,#n; tbb/tbh". Unhandled commands fall to a default that answers status 5 (not supported).

    python -I dev2_razer_commands.py Dev2FW.bin       (needs capstone)
"""
import re, struct, sys, collections
from capstone import Cs, CS_ARCH_ARM, CS_MODE_THUMB

B = 0x8000
img = open(sys.argv[1], 'rb').read()
md = Cs(CS_ARCH_ARM, CS_MODE_THUMB)


def dis(a, n=200):
    return list(md.disasm(img[a - B:a - B + n * 4], a))[:n]


def imm(o):
    m = re.search(r'#(-?0x[0-9a-f]+|-?\d+)$', o)
    return int(m.group(1), 0) if m else None


def switches_in(fn, limit=700):
    """All command switches of a per-class handler: tbb/tbh tables (base from the preceding subs, count from cmp)
    and compare chains "cmp rX,#cmd ; beq/bne target". Returns {cmd: target}. The command byte is the only value
    these handlers switch on, so every small compare before a branch is taken as a command id."""
    out = {}
    base, count = 0, None
    insns = dis(fn, limit)
    for idx, i in enumerate(insns):
        if i.mnemonic.startswith('pop') and 'pc' in i.op_str and idx > 6 and out: pass
        v = imm(i.op_str)
        if i.mnemonic.startswith('sub') and v is not None: base = v
        if i.mnemonic.startswith('cmp') and v is not None:
            count = v + 1
            nxt = insns[idx + 1] if idx + 1 < len(insns) else None
            if nxt is not None and nxt.mnemonic.split('.')[0] in ('beq',) and 0 <= v <= 0xFF:
                out.setdefault(v, int(nxt.op_str.lstrip('#'), 16))
        if i.mnemonic in ('tbb', 'tbh'):
            t = i.address + 4; w = 1 if i.mnemonic == 'tbb' else 2
            ents = []
            for k in range(count or 0):
                e = img[t - B + k] if w == 1 else struct.unpack_from('<H', img, t - B + 2 * k)[0]
                ents.append(t + 2 * e)
            if ents:
                dflt = collections.Counter(ents).most_common(1)[0][0]
                for k, tt in enumerate(ents):
                    if tt != dflt: out[(base + k) & 0xFF] = tt
            base, count = 0, None
    return out


# Class table (TBB right after the 'tbb [pc, r1]' at 0x30750)
tbase = 0x30754
classes = {c: tbase + 2 * img[tbase - B + c] for c in range(0x17)}
default_class = collections.Counter(classes.values()).most_common(1)[0][0]
result = {}
for c, entry in classes.items():
    if entry == default_class: continue
    # the class entry calls the per-class handler: first 'bl'
    callee = None
    for i in dis(entry, 12):
        if i.mnemonic == 'bl': callee = int(i.op_str.lstrip('#'), 16); break
    if callee is None: result[c] = {'entry': entry, 'handler': None, 'cmds': {}}; continue
    cmds = switches_in(callee)
    result[c] = {'entry': entry, 'handler': callee, 'cmds': cmds}

print(f'class table at {tbase:#x}; unsupported classes -> {default_class:#x}')
for c in sorted(result):
    r = result[c]
    h = f"{r['handler']:#x}" if r['handler'] else '-'
    print(f'class 0x{c:02X}: entry {r["entry"]:#x}, handler {h}, {len(r["cmds"])} commands')
    for cmd, t in sorted(r['cmds'].items()):
        kind = 'get' if cmd & 0x80 else 'set'
        print(f'    cmd 0x{cmd:02X} ({kind}) -> {t:#x}')

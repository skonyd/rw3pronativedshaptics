"""Byte-level map of the Wolverine V3 Pro NXP LPC55 application image (Dev2FW, Thumb-2, base 0x8000).

Read-only, offline; needs `pip install capstone`. The firmware itself is never stored in the repo: run this on
your own copy (tools/firmware/README in docs/firmware/README.md shows how to extract it).

    python -I fwmap_arm.py Dev2FW.bin out_dir

Writes to out_dir:
  functions.csv   address, size, our name, callers, callees, peripherals touched, strings referenced
  coverage.json   every byte classified: code / literal / jumptable / string / json / table / pad / unknown
  summary.txt     coverage percentages and counts
The analysis is recursive descent from the vector table, from every Thumb code pointer found in data, and from
call/branch targets, with TBB/TBH jump tables and PC-relative literal pools followed.
"""
import collections, csv, json, os, re, struct, sys
from capstone import Cs, CS_ARCH_ARM, CS_MODE_THUMB, CS_MODE_MCLASS
from capstone.arm import ARM_OP_IMM, ARM_OP_MEM, ARM_REG_PC

BASE = 0x8000
CONDS = {'eq','ne','cs','hs','cc','lo','mi','pl','vs','vc','hi','ls','ge','lt','gt','le','al'}

# LPC54605 peripheral map (the image carries NXP SDK 2.14 paths "devices/LPC54605/drivers/..."; NXP UM10912)
PERIPH = {
    0x40000000: 'SYSCON', 0x40001000: 'IOCON', 0x40002000: 'GINT0', 0x40003000: 'GINT1', 0x40004000: 'PINT',
    0x40005000: 'INPUTMUX', 0x40008000: 'CTIMER0', 0x40009000: 'CTIMER1', 0x4000C000: 'WWDT', 0x4000D000: 'MRT',
    0x4000E000: 'UTICK', 0x4000F000: 'reserved?', 0x40014000: 'EEPROM', 0x40015000: 'OTP', 0x40020000: 'SYSCON_ASYNC?',
    0x40028000: 'CTIMER2', 0x40029000: 'CTIMER3', 0x4002A000: 'CTIMER4', 0x4002C000: 'RTC', 0x4002D000: 'SMARTCARD0?',
    0x40048000: 'CTIMER3?', 0x40049000: 'CTIMER4?', 0x40080000: 'SPIFI', 0x40081000: 'EMC', 0x40082000: 'DMA0',
    0x40083000: 'LCD', 0x40084000: 'USB0(FS)', 0x40085000: 'SCT0', 0x40086000: 'FLEXCOMM0', 0x40087000: 'FLEXCOMM1',
    0x40088000: 'FLEXCOMM2', 0x40089000: 'FLEXCOMM3', 0x4008A000: 'FLEXCOMM4', 0x4008B000: 'MAILBOX', 0x4008C000: 'GPIO',
    0x40090000: 'DMIC', 0x40092000: 'ENET', 0x40094000: 'USB1(HS)', 0x40095000: 'CRC', 0x40096000: 'FLEXCOMM5',
    0x40097000: 'FLEXCOMM6', 0x40098000: 'FLEXCOMM7', 0x40099000: 'FLEXCOMM8', 0x4009A000: 'FLEXCOMM9',
    0x4009B000: 'SDIF', 0x4009D000: 'CAN0', 0x4009E000: 'CAN1', 0x400A0000: 'ADC0', 0x400A1000: 'SHA',
    0x400A2000: 'USB0_HOST', 0x400A3000: 'USB1_HOST', 0x40100000: 'USB_RAM', 0x40101000: 'USB_RAM',
    0x40108000: 'USB_RAM?', 0x4010B000: 'USB_RAM?',
    0xE000E000: 'SCS(NVIC/SysTick/SCB)', 0xE0001000: 'DWT', 0xE0002000: 'FPB',
}


def load(path):
    img = open(path, 'rb').read()
    return img, BASE, BASE + len(img)


def main(path, out, ram_path=None):
    ram_init = open(ram_path, 'rb').read() if ram_path else b''
    img, lo, hi = load(path)
    os.makedirs(out, exist_ok=True)
    md = Cs(CS_ARCH_ARM, CS_MODE_THUMB); md.detail = True   # not MCLASS: that mode drops the FPU instructions
    cls = ['unknown'] * len(img)
    def mark(a, n, kind, force=False):
        for i in range(max(0, a - lo), min(len(img), a - lo + n)):
            if force or cls[i] == 'unknown': cls[i] = kind
    def word(a): return struct.unpack_from('<I', img, a - lo)[0] if lo <= a <= hi - 4 else None
    def is_code_ptr(v): return v & 1 and lo <= (v & ~1) < hi

    # Vector table: SP + handlers until the first word that is neither 0 nor a code pointer.
    vectors = []
    a = lo + 4
    while a < lo + 0x200 and (word(a) == 0 or is_code_ptr(word(a)) or word(a) == 0xFFFFFFFF):
        vectors.append(word(a)); a += 4
    mark(lo, a - lo, 'vectors', True)

    # Strings / JSON first (>= 8 printable chars with letters): code flow never enters them.
    for m in re.finditer(rb'[ -~\t\r\n]{8,}\x00', img):
        txt = m.group()
        if sum(ch.isalpha() for ch in txt.decode('latin1')) >= len(txt) * 0.5:
            mark(lo + m.start(), len(txt), 'json' if txt.startswith(b'{') else 'string', True)
    seeds = collections.OrderedDict()
    for v in vectors:
        if v and v != 0xFFFFFFFF and is_code_ptr(v): seeds[v & ~1] = 'vector'
    # Code pointers stored in data (function tables, callbacks).
    ptr_words = {}
    for off in range(0, len(img) - 3, 4):
        v = struct.unpack_from('<I', img, off)[0]
        if is_code_ptr(v): ptr_words[lo + off] = v & ~1

    functions = {}            # start -> set of instruction addresses
    insn_at = {}
    callers = collections.defaultdict(set); callees = collections.defaultdict(set)
    literal_refs = collections.defaultdict(set)   # literal address -> referencing insn
    work = list(seeds.keys())
    queued = set(work)
    func_of = {}

    def add_func(t, why):
        if lo <= t < hi and t not in queued: queued.add(t); work.append(t)

    def prologue(t):
        if not (lo <= t < hi - 4): return False
        h = struct.unpack_from('<H', img, t - lo)[0]; h2 = struct.unpack_from('<H', img, t - lo + 2)[0]
        return (h & 0xFF00) == 0xB500 or (h == 0xE92D and (h2 & 0x4000)) or (h & 0xFF80) == 0xB080
    for wa, t in ptr_words.items():
        if prologue(t): add_func(t, 'pointer')
    # Code pointers in the decompressed initialised RAM image (callbacks copied to RAM at startup).
    for off in range(0, len(ram_init) - 3, 4):
        v = struct.unpack_from('<I', ram_init, off)[0]
        if is_code_ptr(v): add_func(v & ~1, 'ram')

    rounds = 0
    while True:
      while work:
          f = work.pop()
          if f in functions: continue
          body = set(); stack = [f]
          while stack:
              pc = stack.pop()
              if pc in body or not (lo <= pc < hi): continue
              if pc in insn_at and pc not in body and func_of.get(pc, f) != f:
                  continue
              off = pc - lo
              if cls[off] in ('string', 'json', 'vectors'): continue
              try: i = next(md.disasm(img[off:off + 4], pc))
              except StopIteration: continue
              body.add(pc); insn_at[pc] = i; func_of.setdefault(pc, f)
              m = i.mnemonic; nxt = pc + i.size
              ops = i.operands
              # literal pools (ldr rX, [pc, #imm])
              for op in ops:
                  if op.type == ARM_OP_MEM and op.mem.base == ARM_REG_PC:
                      la = ((pc + 4) & ~3) + op.mem.disp
                      literal_refs[la].add(pc)
                      if m.startswith('ldrd'): mark(la, 8, 'literal')
                      else: mark(la, 4, 'literal')
                      v = word(la)
                      if v is not None and is_code_ptr(v): add_func(v & ~1, 'literal')
              if m in ('tbb', 'tbh'):
                  # table follows the instruction; bound the entries by the first entry offset
                  tbase = nxt; width = 1 if m == 'tbb' else 2
                  entries = []; k = 0
                  limit = 1 << 20
                  # bound from the preceding "cmp rX, #N" (+ bhi/bhs to default): N + 1 entries
                  bound = None
                  for back in range(2, 24, 2):
                      j = insn_at.get(pc - back)
                      if j is None: continue
                      if j.mnemonic.startswith('cmp'):
                          mmb = re.search(r'#(0x[0-9a-f]+|\d+)$', j.op_str)
                          if mmb: bound = int(mmb.group(1), 0) + 1
                          break
                  while k < (bound or 512):
                      ea = tbase + k * width
                      if ea >= limit: break
                      e = img[ea - lo] if width == 1 else struct.unpack_from('<H', img, ea - lo)[0]
                      tgt = tbase + 2 * e
                      entries.append(tgt); limit = min(limit, min(entries))
                      k += 1
                      if bound is None and tbase + k * width >= limit: break
                  mark(tbase, len(entries) * width, 'jumptable', True)
                  for t in entries: stack.append(t)
                  continue
              mb = m.split('.')[0]
              if mb == 'b' or (mb[:1] == 'b' and mb[1:] in CONDS):
                  tgt = ops[0].imm if ops and ops[0].type == ARM_OP_IMM else None
                  cond = mb != 'b'
                  if tgt is not None: stack.append(tgt)
                  if cond: stack.append(nxt)
                  continue
              if m in ('cbz', 'cbnz'):
                  stack.append(ops[1].imm); stack.append(nxt); continue
              if m == 'bl' or (m == 'blx' and ops and ops[0].type == ARM_OP_IMM):
                  t = ops[0].imm
                  callers[t].add(f); callees[f].add(t); add_func(t, 'call')
                  stack.append(nxt); continue
              if m in ('bx',) and 'lr' in i.op_str: continue
              if m.startswith('pop') and 'pc' in i.op_str: continue
              if m.startswith('ldr') and i.op_str.startswith('pc,'): continue
              if m in ('udf', 'bkpt'): continue
              if m.startswith('it'):
                  stack.append(nxt); continue
              stack.append(nxt)
          functions[f] = body
          for pc in body: mark(pc, insn_at[pc].size, 'code', True)
          # pointer words that point into newly seen code are tables of callbacks
      # fixpoint: code pointers built with movw/movt (odd addresses inside the image)
      rounds += 1
      new = 0
      lastw = {}
      for pc in sorted(insn_at):
          i = insn_at[pc]
          mm = re.search(r'#(0x[0-9a-f]+|\d+)$', i.op_str)
          if i.mnemonic.startswith('movw') and mm: lastw[i.op_str.split(',')[0]] = int(mm.group(1), 0)
          elif i.mnemonic.startswith('movt') and mm and i.op_str.split(',')[0] in lastw:
              v = (int(mm.group(1), 0) << 16) | lastw.pop(i.op_str.split(',')[0])
              if is_code_ptr(v) and (v & ~1) not in functions and (v & ~1) not in queued: add_func(v & ~1, 'movw'); new += 1
      # gap code: an unknown run right after code, that decodes cleanly and contains a return, is code reached
      # through RAM pointers (RTOS tasks, callbacks copied at startup)
      off = 0
      while off < len(img) - 4:
          if cls[off] == 'unknown' and off > 0 and cls[off - 1] == 'code' and (lo + off) not in queued:
              end = off
              while end < len(img) and cls[end] == 'unknown': end += 1
              good = bad = rets = 0
              for i in md.disasm(img[off:end], lo + off):
                  good += i.size
                  if (i.mnemonic.startswith('pop') and 'pc' in i.op_str) or (i.mnemonic == 'bx' and 'lr' in i.op_str): rets += 1
              if end - off >= 8 and good >= (end - off) * 0.95 and rets:
                  add_func(lo + off, 'gap'); new += 1
              off = end
          off += 2
      # and still-unknown halfword-aligned spots that start with a prologue
      for off in range(0, len(img) - 4, 2):
          t = lo + off
          if cls[off] == 'unknown' and t not in functions and t not in queued and prologue(t):
              add_func(t, 'prologue'); new += 1
      if not work or rounds > 6: break
    # second pass: data words that point at known function starts become seeds too (indirect calls)
    for wa, t in ptr_words.items():
        if t in functions: mark(wa, 4, 'codeptr')
    # strings and JSON
    for m in re.finditer(rb'[ -~\t\r\n]{6,}\x00?', img):
        s = m.group()
        kind = 'json' if s.startswith(b'{') else 'string'
        a = lo + m.start()
        if all(c == 'unknown' or c == 'literal' for c in cls[m.start():m.end()]): mark(a, len(s), kind)
    # padding
    for m in re.finditer(rb'(\xff{8,}|\x00{16,})', img):
        a = lo + m.start()
        if all(c == 'unknown' for c in cls[m.start():m.end()]): mark(a, len(m.group()), 'pad')
    # what remains is data tables (initialised data copy, lookup tables)
    for i, c in enumerate(cls):
        if c == 'unknown': cls[i] = 'table'

    # peripherals per function (movw/movt pairs)
    periph = collections.defaultdict(set); strings_of = collections.defaultdict(set)
    str_at = {lo + m.start(): m.group().decode('latin1')[:60] for m in re.finditer(rb'[ -~]{6,}', img)}
    for f, body in functions.items():
        addrs = sorted(body); last = {}
        for pc in addrs:
            i = insn_at[pc]
            mm = re.search(r'#(0x[0-9a-f]+|\d+)$', i.op_str)
            if i.mnemonic.startswith('movw') and mm: last[i.op_str.split(',')[0]] = int(mm.group(1), 0)
            elif i.mnemonic.startswith('movt') and mm:
                r = i.op_str.split(',')[0]
                if r in last:
                    v = (int(mm.group(1), 0) << 16) | last.pop(r)
                    b = v & 0xFFFFF000
                    if b in PERIPH: periph[f].add(PERIPH[b])
                    if v in str_at: strings_of[f].add(str_at[v])
            for op in i.operands:
                if op.type == ARM_OP_MEM and op.mem.base == ARM_REG_PC:
                    v = word(((pc + 4) & ~3) + op.mem.disp)
                    if v is not None:
                        if (v & 0xFFFFF000) in PERIPH: periph[f].add(PERIPH[v & 0xFFFFF000])
                        if v in str_at: strings_of[f].add(str_at[v])

    with open(os.path.join(out, 'functions.csv'), 'w', newline='', encoding='utf-8') as fh:
        w = csv.writer(fh)
        w.writerow(['address', 'size_bytes', 'insns', 'callers', 'callees', 'peripherals', 'strings'])
        for f in sorted(functions):
            body = functions[f]; size = max(body) + insn_at[max(body)].size - f if body else 0
            w.writerow([hex(f), size, len(body), ' '.join(hex(x) for x in sorted(callers[f])),
                        ' '.join(hex(x) for x in sorted(callees[f])), ' '.join(sorted(periph[f])),
                        ' | '.join(sorted(strings_of[f]))])
    cnt = collections.Counter(cls)
    runs = []
    cur = None
    for i, c in enumerate(cls):
        if cur and cur[2] == c: cur[1] = i + 1
        else:
            if cur: runs.append(cur)
            cur = [i, i + 1, c]
    runs.append(cur)
    json.dump([[hex(lo + s), hex(lo + e), c] for s, e, c in runs], open(os.path.join(out, 'coverage.json'), 'w'), indent=0)
    with open(os.path.join(out, 'summary.txt'), 'w') as fh:
        fh.write(f'image {path}: {len(img)} bytes at {hex(lo)}..{hex(hi)}\n')
        fh.write(f'functions {len(functions)}, instructions {len(insn_at)}, vectors {len(vectors)}\n')
        for k, v in cnt.most_common(): fh.write(f'  {k:10s} {v:7d} bytes  {100 * v / len(img):5.1f} %\n')
        fh.write(f'regions {len(runs)}\n')
    print(open(os.path.join(out, 'summary.txt')).read())


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)

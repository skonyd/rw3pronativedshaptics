"""Simulates every Razer command (class x cmd 0..255) against the NXP firmware's own handlers in unicorn and reports
which ones the firmware accepts, per controller mode. Offline: nothing is sent to a controller.

Setup per call:
- flash image at 0x8000;
- RAM 0x20000000 with the initialised data (dev2_ram_init.py) and zeroed BSS;
- peripherals mapped and reading as 0 (writes are logged per peripheral page);
- the packet at 0x2003F000 = [status 0, tx, 0, 0, 0, size, class, cmd, args...].
The class handler is called as the dispatcher does: handler(packet, packet + 8, 0). Status after the call:
5 = not supported, anything else = handled (2 = OK in Razer's protocol). The mode flag (0x20000F68 + 0xEF) is set
to 1 (PC) or 0 (Xbox).

    python -I dev2_simulate_commands.py Dev2FW.bin ram_init.bin [out.json]
"""
import json, struct, sys
from unicorn import Uc, UcError, UC_ARCH_ARM, UC_MODE_THUMB, UC_MODE_MCLASS, UC_HOOK_MEM_WRITE
from unicorn.arm_const import UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2

B = 0x8000
CLASSES = {0x00: 0x165CC, 0x02: 0x17740, 0x05: 0x1B894, 0x07: 0x17370, 0x0A: 0x309DC, 0x0C: 0x18640,
           0x0F: 0x2F700, 0x10: 0x2B820, 0x16: 0x30BA8}
PKT = 0x2003F000
MODE_FLAG = 0x20000F68 + 0xEF


def run(img, ram, cls, cmd, mode, size=8):
    mu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
    mu.mem_map(0, 0x40000); mu.mem_write(B, img)
    mu.mem_map(0x20000000, 0x40000); mu.mem_write(0x20000000, ram)
    mu.mem_map(0x40000000, 0x200000)                 # LPC546xx APB/AHB peripherals
    mu.mem_map(0xE0000000, 0x100000)                 # SCS
    mu.mem_write(0xE000ED88, (0xF << 20).to_bytes(4, 'little'))
    mu.mem_write(MODE_FLAG, bytes([mode]))
    pkt = bytearray(96); pkt[1] = 0x55; pkt[5] = size; pkt[6] = cls; pkt[7] = cmd
    mu.mem_write(PKT, bytes(pkt))
    writes = set()
    mu.hook_add(UC_HOOK_MEM_WRITE, lambda u, acc, addr, sz, val, d: writes.add(addr & 0xFFFFF000) if addr >= 0x40000000 else None)
    mu.reg_write(UC_ARM_REG_SP, 0x2003E000); mu.reg_write(UC_ARM_REG_LR, 0xFFF1)
    mu.reg_write(UC_ARM_REG_R0, PKT); mu.reg_write(UC_ARM_REG_R1, PKT + 8); mu.reg_write(UC_ARM_REG_R2, 0)
    state = 'ok'
    try: mu.emu_start(CLASSES[cls] | 1, 0xFFF0, count=400_000)
    except UcError as e: state = 'fault:' + str(e)
    pc_after = None
    out = bytes(mu.mem_read(PKT, 96))
    return out[0], state, out[8:8 + max(0, out[5])], sorted(hex(w) for w in writes)


def main():
    img = open(sys.argv[1], 'rb').read(); ram = open(sys.argv[2], 'rb').read()
    report = {}
    for mode_name, mode in (('pc', 1), ('xbox', 0)):
        for cls in CLASSES:
            for cmd in range(256):
                st, state, data, wr = run(img, ram, cls, cmd, mode)
                if st != 5:
                    key = f'{cls:02X}/{cmd:02X}'
                    report.setdefault(key, {})[mode_name] = {'status': st, 'run': state, 'data': data.hex(), 'periph_writes': wr}
    for key in sorted(report):
        r = report[key]
        line = f'{key}  ' + '  '.join(f"{m}: st{v['status']}{'' if v['run'] == 'ok' else ' ' + v['run'][:28]} {v['data'][:24]}" for m, v in r.items())
        print(line)
    if len(sys.argv) > 3: json.dump(report, open(sys.argv[3], 'w'), indent=1)


if __name__ == '__main__':
    main()

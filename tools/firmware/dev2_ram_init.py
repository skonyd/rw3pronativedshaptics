"""Rebuilds the NXP image's initialised RAM (0x20000000, 0xA00 bytes) by running the firmware's own ARM
__decompress routine (0x8EBC) in unicorn. Scatter table at 0x36EA4: [src 0x37200 -> 0x20000000, 0xA00, decompress],
[0x20000A00, 0x13328 bytes, zero-init].   python -I dev2_ram_init.py Dev2FW.bin ram_init.bin   (needs unicorn)"""
import sys
from unicorn import Uc, UC_ARCH_ARM, UC_MODE_THUMB, UC_MODE_MCLASS
from unicorn.arm_const import UC_ARM_REG_SP, UC_ARM_REG_LR, UC_ARM_REG_R0, UC_ARM_REG_R1, UC_ARM_REG_R2
img = open(sys.argv[1], 'rb').read()
mu = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
mu.mem_map(0, 0x40000); mu.mem_write(0x8000, img); mu.mem_map(0x20000000, 0x40000)
mu.reg_write(UC_ARM_REG_SP, 0x20027000); mu.reg_write(UC_ARM_REG_LR, 0xFFF1)
mu.reg_write(UC_ARM_REG_R0, 0x37200); mu.reg_write(UC_ARM_REG_R1, 0x20000000); mu.reg_write(UC_ARM_REG_R2, 0xA00)
mu.emu_start(0x8EBC | 1, 0xFFF0, count=5_000_000)
open(sys.argv[2], 'wb').write(bytes(mu.mem_read(0x20000000, 0xA00)))
print('wrote', sys.argv[2])

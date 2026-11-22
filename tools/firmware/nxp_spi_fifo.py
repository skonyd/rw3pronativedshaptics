"""Bounded FLEXCOMM9 SPI master/FIFO subset with explicit clocked MISO bytes.

Register layout: NXP LPC54605.h. No electrical timing or automatic clocks.
Only the stock eight-bit transfer configuration is exercised here.
"""
from collections import deque
from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE, UC_MEM_READ, UC_MEM_WRITE

from simulate_audio_haptics import verify


SPI9 = 0x4009A000


class SpiMasterFifo:
    def __init__(self, cpu, *, present=0x20):
        verify(present in (0, 0x20), 'Supply an explicit SPI-presence fixture')
        self.cpu, self.present = cpu, present
        self.selection = 0
        # UM10912 SPI FIFOCFG: SIZE=1, eight entries (the SDK header's
        # USART-sized description is not applicable to the SPI function).
        self.registers = {0x400: 0, 0x404: 0, 0x424: 0, 0xE00: 0x10, 0xE08: 0}
        self.tx, self.rx, self.wire = deque(), deque(), []
        self.control, self.flushes = 0, 0
        self.writes = []
        cpu.mem_map(SPI9, 4096)
        cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=SPI9, end=SPI9 + 4095)
        cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=SPI9, end=SPI9 + 4095)

    @staticmethod
    def validate(access, address, size):
        offset = address - SPI9
        ordinary = {0x400, 0x404, 0x424, 0xE00, 0xE08, 0xFF8}
        reads, writes = ordinary | {0xE04, 0xE30}, ordinary | {0xE04, 0xE20, 0xE22}
        verify(access in (UC_MEM_READ, UC_MEM_WRITE), 'Unknown SPI access type')
        verify(offset in (reads if access == UC_MEM_READ else writes), 'Unmodeled SPI register')
        valid = size == 4
        if offset in (0xE20, 0xE30): valid = size in (1, 4)
        if offset == 0xE22: valid = size == 2
        verify(valid, 'Unmodeled SPI access width')

    def status(self):
        return (int(not self.tx) << 4 | int(len(self.tx) < 8) << 5 |
                int(bool(self.rx)) << 6 | int(len(self.rx) == 8) << 7 |
                len(self.tx) << 8 | len(self.rx) << 16)

    def read(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        offset = address - SPI9
        if offset == 0xFF8: value = self.present | self.selection
        elif offset == 0xE04: value = self.status()
        elif offset == 0xE30: value = self.pop()
        else: value = self.registers[offset]
        cpu.mem_write(address, value.to_bytes(size, 'little'))

    def write(self, cpu, access, address, size, value, data):
        self.validate(access, address, size)
        offset = address - SPI9
        self.writes.append((offset, size, value))
        if offset == 0xFF8:
            verify(value == 2 and self.present, 'Unmodeled SPI selection/lock')
            self.selection = 2
        elif offset == 0x400:
            verify(value in (0, 4, 5), 'Unmodeled SPI master configuration')
            self.registers[offset] = value
        elif offset == 0xE00:
            verify(value & ~0x33033 == 0, 'Unmodeled SPI FIFO configuration')
            if value & 0x10000: self.tx.clear(); self.flushes += 1
            if value & 0x20000: self.rx.clear(); self.flushes += 1
            self.registers[offset] = value & ~0x30030 | 0x10
        elif offset == 0xE04:
            verify(value & ~0x1F1FF3 == 0, 'Unmodeled SPI FIFO status clear')
            # Error injection is not modeled; all asserted errors are absent.
        elif offset == 0xE22:
            verify(value >> 8 == 7, 'Only eight-bit SPI words are modeled')
            self.control = value << 16
        elif offset == 0xE20:
            self.push(value, size)
        else:
            verify(value == 0 if offset == 0x404 else value == 3 if offset == 0xE08 else 0 <= value <= 0xFFFF,
                   'Unmodeled SPI delay/trigger/divisor')
            self.registers[offset] = value

    def push(self, value, width):
        verify(width in (1, 4) and 0 <= value < 1 << (width * 8), 'Invalid SPI FIFO word')
        verify(len(self.tx) < 8, 'SPI TX FIFO overflow is outside this fixture')
        word = self.control | value if width == 1 else value
        verify(word >> 24 & 15 == 7, 'Only eight-bit SPI words are modeled')
        verify(word & ~0x073F00FF == 0, 'Unmodeled SPI FIFO control bits')
        if width == 4: self.control = word & 0xFFFF0000
        self.tx.append(word)

    def clock(self, miso):
        verify(isinstance(miso, int) and 0 <= miso <= 255, 'SPI requires an explicit MISO byte')
        verify(self.selection == 2 and self.registers[0x400] == 5 and self.registers[0xE00] & 3 == 3,
               'SPI is not initialized/enabled')
        verify(self.tx and len(self.rx) < 8, 'SPI FIFO underflow/overflow is outside this fixture')
        word = self.tx.popleft()
        self.rx.append(miso)
        self.wire.append((word, miso))
        return word & 255

    def pop(self):
        verify(self.rx, 'SPI receive underflow; no default byte')
        return self.rx.popleft()

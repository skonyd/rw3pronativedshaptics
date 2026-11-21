"""Bounded LPC54605 DMA interrupt status/enable aliases, not bus timing.

Offsets/IRQ follow the NXP LPC54605 header. Transfer completion is supplied
by the replay's descriptor/FIFO fixture, never by elapsed wall time.
"""
import struct

from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

from simulate_audio_haptics import verify


DMA, DMA_IRQ, TX_CHANNEL = 0x40082000, 1, 13
CHANNEL_MASK = (1 << 30) - 1


class DmaInterruptStatus:
    def __init__(self, cpu, interrupts, *, clear_readback):
        self.cpu, self.interrupts = cpu, interrupts
        self.enabled = 0
        self.flags = {0x40: 0, 0x58: 0, 0x60: 0}
        self.acknowledgements = []
        # UM10912 defines INTENCLR as write-only. Stock nevertheless reads it
        # in RMW sequences. Supply this unknown explicitly; never silently
        # claim that zero is its physical readback value.
        verify(isinstance(clear_readback, int) and 0 <= clear_readback <= 0xFFFFFFFF,
               "INTENCLR requires an explicit unsigned fixture readback")
        self.clear_readback, self.undefined_reads = clear_readback, 0
        verify(DMA_IRQ not in interrupts.level_sources, "DMA IRQ source already attached")
        interrupts.level_sources[DMA_IRQ] = self.asserted
        for offset in (4, 0x40, 0x48, 0x50, 0x58, 0x60):
            cpu.hook_add(UC_HOOK_MEM_READ, self.read, begin=DMA + offset, end=DMA + offset + 3)
            cpu.hook_add(UC_HOOK_MEM_WRITE, self.write, begin=DMA + offset, end=DMA + offset + 3)

    def asserted(self):
        return bool(self.flags[0x40] | self.enabled & (self.flags[0x58] | self.flags[0x60]))

    def signal(self, *, a=0, b=0, error=0):
        verify(all(0 <= value <= CHANNEL_MASK for value in (a, b, error)), "Unsupported DMA channel mask")
        for offset, value in ((0x40, error), (0x58, a), (0x60, b)):
            self.flags[offset] |= value

    def read(self, cpu, access, address, size, value, data):
        offset = address - DMA
        verify(size == 4 and offset in (4, 0x40, 0x48, 0x50, 0x58, 0x60), f"Unmodeled DMA status read {address:#x}/{size}")
        if offset == 4:
            value = int(bool(self.enabled & (self.flags[0x58] | self.flags[0x60]))) << 1
            value |= int(bool(self.flags[0x40])) << 2
        elif offset == 0x48:
            value = self.enabled
        elif offset == 0x50:
            self.undefined_reads += 1
            value = self.clear_readback
        else:
            value = self.flags[offset]
        cpu.mem_write(address, struct.pack("<I", value))

    def write(self, cpu, access, address, size, value, data):
        offset = address - DMA
        verify(size == 4 and offset in (0x40, 0x48, 0x50, 0x58, 0x60), "Unmodeled DMA status write")
        value &= CHANNEL_MASK
        if offset == 0x48:
            self.enabled |= value
        elif offset == 0x50:
            self.enabled &= ~value
        else:
            self.flags[offset] &= ~value
            self.acknowledgements.append((offset, value))

import struct, sys
img = open(sys.argv[1], 'rb').read(); B = 0x8000
W = lambda a: struct.unpack_from('<I', img, a - B)[0]
irq = ['WDT_BOD','DMA0','GINT0','GINT1','PIN_INT0','PIN_INT1','PIN_INT2','PIN_INT3','UTICK0','MRT0','CTIMER0','CTIMER1','SCT0','CTIMER3','FLEXCOMM0','FLEXCOMM1','FLEXCOMM2','FLEXCOMM3','FLEXCOMM4','FLEXCOMM5','FLEXCOMM6','FLEXCOMM7','ADC0_SEQA','ADC0_SEQB','ADC0_THCMP','DMIC0','HWVAD0','USB0_NEEDCLK','USB0','RTC','r30','r31','PIN_INT4','PIN_INT5','PIN_INT6','PIN_INT7','CTIMER2','CTIMER4','RIT','SPIFI0','FLEXCOMM8','FLEXCOMM9','SDIO','CAN0_IRQ0','CAN0_IRQ1','CAN1_IRQ0','CAN1_IRQ1','USB1','USB1_NEEDCLK','ETHERNET','ETHERNET_PMT','ETHERNET_MACLP','EEPROM','LCD','SHA','SMARTCARD0','SMARTCARD1']
names = ['SP','Reset','NMI','HardFault','MemManage','BusFault','UsageFault','r7','r8','r9','r10','SVCall','DebugMon','r13','PendSV','SysTick'] + irq
targets = {}
for k, n in enumerate(names[1:], 1):
    v = W(B + 4 * k)
    if v in (0, 0xffffffff) or not (B <= (v & ~1) < B + len(img)): continue
    v &= ~1
    h = struct.unpack_from('<H', img, v - B)[0]; h2 = struct.unpack_from('<H', img, v - B + 2)[0]
    t = W(((v + 4) & ~3) + (h & 0xff) * 4) & ~1 if (h & 0xf800) == 0x4800 and h2 == 0x4700 else v
    targets.setdefault(t, []).append(n)
for t, ns in sorted(targets.items()): print(f'{t:#07x} {",".join(ns)}')

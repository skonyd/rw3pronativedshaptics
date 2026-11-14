# Dev2FW: controller main MCU (NXP LPC54605), firmware 2.04.00

Static analysis and emulation only. Addresses are flash addresses; the application starts at 0x8000.

## Chip and toolchain

- **NXP LPC54605**: Cortex-M4F with FPU. The image references the NXP MCUXpresso SDK 2.14 drivers
  `NXP_2.14/devices/LPC54605/drivers/`:
  - `fsl_adc`, `fsl_ctimer`, `fsl_dma`, `fsl_eeprom`, `fsl_flexcomm`, `fsl_gint`, `fsl_gpio`, `fsl_i2c`,
    `fsl_i2s` / `fsl_i2s_dma`, `fsl_mrt`, `fsl_pint`, `fsl_reset`, `fsl_sctimer`, `fsl_sdif`, `fsl_spi` /
    `fsl_spi_dma`, `fsl_wwdt`, `fsl_common_arm`.
  - The peripheral addresses the code uses match the LPC546xx map: SYSCON 0x40000000, IOCON 0x40001000,
    GPIO 0x4008C000, DMA0 0x40082000, USB0 0x40084000, SCT0 0x40085000, FLEXCOMM0–9, ADC0 0x400A0000,
    EEPROM 0x40014000, USB RAM 0x40100000.
- **Compiler: ARM Compiler (Keil MDK).** Evidence: the `__main` → `__scatterload` → `main` sequence and the ARM
  `__decompress` routine for compressed RW data.

## Memory map

| Range | Content |
| --- | --- |
| 0x0000–0x7FFF | Bootloader. Not in the update package; never erased by the official updater (erase 0x8000–0x37800). |
| 0x8000–0x81FF | Vector table: SP + 15 core + 57 IRQ entries. |
| 0x8200–0x83FF | 512 zero bytes (reserved). |
| 0x8400 | `__main`: set SP, `__scatterload` (0x8E98), jump to **`main` (0x2EF68)**. |
| 0x8414 | Reset handler: SYSCON[0x220] = 0x38, `SystemInit` (0x22C3C), `__main`. |
| 0x8430–0x853C | IRQ stubs: `ldr r0,[pc]; bx r0` into the real handlers. |
| … | Code, tables and strings (see coverage). |
| 0x34228, 0x347B1 | Sensa "Sage T1" body-part descriptors: Xbox 4-part and PC 2-part ([FIRMWARE_ANALYSIS](../FIRMWARE_ANALYSIS_20261110.md)). |
| 0x34AD4 | USB string / OS descriptors (`MSFT100` …). |
| 0x36EA0–0x37030 | GIP metadata (Gamepad + Headset). |
| 0x36EA4 | Scatter-load table (2 regions). |
| 0x37200–0x37800 | Compressed initialised RW data, unpacked to RAM 0x20000000 (0xA00 bytes). |

**RAM:**

| Range | Content |
| --- | --- |
| 0x20000000–0x200009FF | Initialised data. Contains `XGIP10` at 0x200000BE and `XUSB10` at 0x200007BE: the NXP serves the USB device in both modes. 17 callback pointers. |
| 0x20000A00–0x20013D27 | Zero-initialised data (BSS, 0x13328 bytes). |
| …–0x20027FF8 | Stack (initial SP 0x20027FF8). |

Selected RAM variables found so far:

| Address | Meaning |
| --- | --- |
| 0x20000F68 + 0xEF | Mode flag: 1 = PC, otherwise Xbox. It picks the Sensa descriptor and decoder. |
| 0x200000D8 | Pointer to the current Razer HID packet buffer. |
| 0x20000B2C / 0x20000B34 | New-Sensa-frame flag / frame buffer, filled by class 0x16 cmd 0x0E. |
| 0x20013530 | 4 × 0x82-byte decoded Sensa body-part structs (palms 216/116, triggers 249/149). |
| 0x2000515C | Razer packet buffer of the second (radio-link) handler 0x22D08. |

## SystemInit (0x22C3C)

1. Turns the FPU on: CPACR |= 0xF00000.
2. **VTOR = 0x8000**, so the application runs above a resident bootloader.
3. Writes SYSCON[0x304] = 0 and SYSCON[0x220] = 0x38.

## Interrupt handlers (vector → real handler)

| Handler | Vectors |
| --- | --- |
| 0x08414 | Reset |
| 0x08430 | default for ADC0 (all), RTC, RIT, SPIFI0, CAN0/1, USB1 (HS), ENET, EEPROM, LCD, SHA, SMARTCARD0/1 (unused) |
| 0x08434–0x08454 | NMI, HardFault, MemManage, BusFault, UsageFault, SVCall, DebugMon, PendSV, SysTick |
| 0x0B370 / 0x0B3D4 / 0x0B438 / 0x0B49C / 0x0B500 | CTIMER0 / 1 / 2 / 3 / 4 (SDK dispatch) |
| 0x0C478 | DMA0 (SDK) |
| 0x0CE34 / 0x10384 | DMIC0 / HWVAD0 |
| 0x0E6E4 … 0x0E924 | FLEXCOMM0 … FLEXCOMM9 (SDK `s_flexcommIrqHandler`) |
| 0x0ED2C / 0x0ED54 | GINT0 / GINT1 |
| 0x1485C | MRT0 |
| 0x15764 … 0x15978 | PIN_INT0 … PIN_INT7 |
| 0x1F2E8 | SCT0 (PWM) |
| 0x1FF1C | SDIO |
| **0x23160** | **USB0 (full-speed device: XUSB / GIP)** |
| 0x23170 / 0x231B0 | USB0_NEEDCLK / USB1_NEEDCLK |
| 0x2964C | UTICK0 |
| 0x29674 | WDT_BOD (watchdog) |

## Coverage (fwmap_arm.py, with the RAM-init seeds)

- 960 functions, 59,431 instructions.

| Class | Bytes | Share |
| --- | --- | --- |
| code | 169,482 | 87.1 % |
| table / data | 13,795 | 7.1 % |
| strings | 3,719 | 1.9 % |
| switch tables | 2,585 | 1.3 % |
| JSON descriptors | 2,226 | 1.1 % |
| padding | 1,489 | 0.8 % |
| literals | 604 | 0.3 % |
| vectors | 512 | 0.3 % |
| code-pointer tables | 148 | 0.1 % |

The data still to be named includes float tables (0x305C8, 0x3085E, 0x35CE9), USB descriptors, GIP metadata, LED
colour tables (0x36CDF) and the compressed RW data.

## Subsystems (to be filled in)

- Razer command dispatcher 0x30524: class table at 0x3074C. Class 0x16 commands are at 0x30BC2: 07–0E, 87–8D,
  8F, 90, 91.
- Sensa: cmd 0x0E (0x30EFC) → frame buffer → decoder 0x2A708 (PC) / 0xEF50 (Xbox) → body structs 0x20013530 →
  output (to trace).
- GIP class dispatch with the 0x60 audio test: 0x25AD4.
- USB0 ISR 0x23160.
- I2S + DMA (0x12418 …) driven from 0x13D6C.
- SPI + DMA (0x20374 …).
- SCTimer PWM (0x1F374 …).

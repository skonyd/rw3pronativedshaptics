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

## Subsystems

### Razer commands

See [RAZER_COMMANDS.md](RAZER_COMMANDS.md): 104 commands, dispatcher 0x30524.

### Sensa (palm haptics) and the actuator drive

1. **Input:** class 0x16 cmd 0x0E (0x30EFC) copies the frame to 0x20000B34 and sets the flag 0x20000B2C. Frames
   less than 11 ticks apart are dropped.
2. **Decoding:** 0x2B538 picks decoder 0x2A708 (PC) or 0xEF50 (Xbox) by the mode flag. Either one fills four
   0x82-byte body-part structs at 0x20013530. A struct holds a band count, then points of
   (time, pitch Hz, amplitude), then transients.
3. **Consumers of the structs:** 0x0DFF0, 0x17C1C, 0x1C43C, 0x227E4, 0x22B20, 0x2BD00, 0x2BDA8 and 0x1051C
   (synthesis and render state at 0x20013738–0x2001373E).
4. **Actuator drive: SCTimer PWM.** Init is 0x1F48C, called from board init 0x27C40.
   - Four PWM channels on SCT0 outputs 3, 5, 6 and 7, active-high, **40 kHz** (`SCTIMER_SetupPwm`, 0x1FA00).
   - Initial duty: SCT 3 and 5 from 0x200007A4, SCT 6 and 7 at 0.
   - Event numbers are stored at 0x20002270, 0x74, 0x78 and 0x7C.
   - Two output pairs at 40 kHz fit an H-bridge drive of the two voice-coil palm actuators.
5. **Sample clock:** the CTIMER2 match callback (0x2B6F0, a RAM callback) runs the SCT duty update 0x1F5CC when
   0x200044C4+0x29 == 1, then clears the timer interrupt.
   - 0x1F5CC switches on a mode byte at 0x200090E6: 0/2 normal, 3, 0xFE, 0xFF.
   - CTIMER3 (0x2B718) runs two more periodic tasks, 0x2D534 and 0x2D210.

### Headset audio (console ↔ controller) passes through the NXP

The NXP runs a **full-duplex I2S on FLEXCOMM6/7 with DMA** (init 0x13D6C; DMA channels 13 and 14).
- The blocks are **192 bytes = 1 ms of 48 kHz, stereo, 16-bit**, the USB/GIP audio packet size.

**Downstream: console → headset jack.**
- USB audio is written into a 3-block ring at **0x20013A38** (3 × 192 bytes, index 0x20013C78).
- The **I2S TX DMA callback 0x22FF8** hands the next block to I2S every millisecond, towards the audio SoC.
- With no audio it sends the zero block at 0x200056EC.
- The streaming flag is 0x20000A59.

**Upstream: microphone → console.** The I2S RX callback 0x1F230 collects 192-byte blocks into a 6144-byte ring at
0x2000712C (write offset at 0x20008F10+0xA4).

**Consequence for the custom firmware:**
- The console's game audio exists **as PCM in the NXP's RAM every millisecond** (wired Xbox mode). The I2S TX
  callback 0x22FF8 is a natural hook.
- So the NXP that drives Sensa also sees the audio, and an audio → Sensa patch needs **only the NXP image**.
- The wireless path through the dongle and the radio SoC is still to be checked.

### Flash use

- **Application:** 0x8000–0x37800.
- **IAP wrappers** (ROM entry 0x03000205): `PrepareSectors` 0xE6A8 (IAP 50), `CopyRamToFlash` 0xE60C (51),
  `EraseSectors` 0xE65C (52).
- **Staging area 0x40000–0x77FFF** (sectors 8–14, 32 KB each):
  - Used by class 0x10 commands (handler 0x2B820). 10/09 prepares sectors 8..14.
  - 10/83 writes a 32 KB block from RAM 0x2000B4C8 to sector [0x200000FC] in 4 KB steps (0x2C604).
  - This is where firmware for the radio SoC is staged (`UpdateType.FLASH` through the controller).
- **Settings and profiles** live in the on-chip **EEPROM** (`fsl_eeprom`, 0x40108000 window), not in flash.
- **Free flash:** 0x37800–0x3FFFF (~34 KB, between the application and the staging area), available for patch code.
  - The official updater erases [first HEX record, last HEX record end].
  - Whether the bootloader accepts an image beyond 0x37800 is unknown.

### Next

- USB/GIP device stack: USB0 ISR 0x23160, GIP class dispatch 0x25AD4, descriptors for XUSB (PC) and GIP (Xbox).
- Audio path from USB message 0x60 into the ring at 0x20013A38.
- The input path: ADC sticks and triggers, PINT buttons.
- The link to the radio SoC (FLEXCOMM9; 0x22D08 packet handler).

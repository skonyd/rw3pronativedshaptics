# Razer HID command catalog (NXP firmware 2.04.00)

The NXP MCU handles **104 Razer commands**, the same in PC and Xbox mode. Transport:
- **PC mode:** HID feature report 4, 91 bytes, with a report id.
- **Xbox mode:** GIP message 0x0F, 60 bytes, answers in 0x10 (docs/research/XBOX_MODE_HAPTICS_ROUTE.md).

The packet layout as the firmware reads it (no report id) is
`[0 status, 1 tx, 2-3 remaining, 4 protocol, 5 size, 6 class, 7 cmd, 8.. args]`, with an XOR CRC.

## How this list was made

1. `tools/firmware/dev2_razer_commands.py` reads the class table and each class handler's switches.
2. `tools/firmware/dev2_simulate_commands.py` runs every class × command 0..255 through the firmware's own
   handlers in unicorn, in both modes.

Status meanings: 5 = not supported (left out of this list), 3 = handled but the all-zero test arguments were
invalid, 0/2 = handled.

The emulated answers match the real controller where we have captures:
- 00/81 → `02 04 00 00` (firmware 2.04.00);
- 0F/80 → `05 19 03 01 02`, identical to the answer captured from Razer Controller Setup for Xbox;
- 16/90 → `05 89` (descriptor length 0x589, identical to the capture).

**Class dispatcher:** 0x30524, class table 0x3074C.

| Class | Handler | What it is (evidence) |
| --- | --- | --- |
| 0x00 | 0x165CC | Device: mode, versions, serial, name, Sensa HD switch |
| 0x02 | 0x17740 | Button mapping |
| 0x05 | 0x1B894 | Profiles |
| 0x07 | 0x17370 | Power / battery |
| 0x0A | 0x309DC | System and power control: 0A/00, 0A/05 write SYSCON and ASYNC SYSCON (reset/clock) |
| 0x0C | 0x18640 | Vibration and haptics settings |
| 0x0F | 0x2F700 | Lighting (Chroma) |
| 0x10 | 0x2B820 | Flash / update of the radio SoC through the NXP: 10/04 writes FLEXCOMM9, 10/03/05/83 write SYSCON |
| 0x16 | 0x30BA8 | Sensa (Interhaptics) stream and storage |

## Commands

"Known" means seen on the wire from Razer software or Synapse, or mapped from the Razer app's API
(HaileyGameController internal ids, E-20261111-08). Other names are inferred from the handler and the emulated
answer.

| Cmd | Meaning | Evidence |
| --- | --- | --- |
| 00/02, 00/05, 00/06, 00/0E, 00/24, 00/34, 00/3D, 00/54, 00/55, 00/56 | Device sets, not yet named. 00/24 writes GPIO | emulation |
| **00/04 [01]** | Set device mode → **bootloader** (1532:110E) | updater disassembly (E-20261111-11) |
| 00/06, 00/86 | USB enumeration mode set/get | Razer app API 0x10006 |
| **00/08, 00/88** | **Sensa HD state** set/get (01 = on) | Razer app API 0x10008; captured 00/88 → 01 |
| 00/0B | Writes FLEXCOMM9: link to the radio SoC | emulation |
| **00/53 [01]** | **Switch to Xbox mode** (after 0C/95) | Synapse capture, used by RW3 |
| **00/81** | Firmware version → `02 04 00 00` | emulation + Razer app API 0x10001 |
| **00/82** | Serial number (22 bytes) | capture + API 0x10002 |
| 00/84, 00/85 | Gets: dock FW / edition? | API 0x10003/0x10004 (uncertain) |
| **00/87** | Version info `02 04 00 00 02 00 02 00` (NXP + radio + dongle?) | capture |
| 00/8E, 00/A4, 00/BD, 00/D3–D6 | Device gets, not yet named | emulation |
| **00/B4** | Device name, UTF-16 "Razer Wolverine …", 45-byte chunks | capture |
| 02/02, 02/0C | Mapping sets | emulation |
| 02/82, 02/84 (→ 06), **02/8C** | Mapping gets; 02/8C [profile, key] read per button 0x10–0x15 by the Razer app | capture |
| **05/04, 05/08** | Active profile set; profile name set | capture (05/04 [03]) |
| **05/80, 05/81, 05/84, 05/88, 05/8A** | Profile count (04), list (`04 01 02 03 04`), active, name (UTF-16 "Profile N"), … | capture + emulation |
| 07/01, 07/03, 07/07 | Power sets (sleep timer, battery thresholds?) | API 0x60100x |
| **07/80, 07/81, 07/83, 07/84, 07/87** | Battery / charging gets; 07/83 → `84 03`, 07/84 → `00 01`, 07/80 → `00 FF` on the device | capture |
| 0A/00, 0A/02, 0A/04, 0A/05 | System control (clock/reset) | emulation (SYSCON writes) |
| 0A/80, 0A/81, 0A/88 | System info: 0A/80 → `01 01 03 28`, 0A/88 → `FF FF` | emulation |
| 0C/02, 0C/05, 0C/07, 0C/08, 0C/11–0C/19 | Haptics / vibration sets | emulation |
| **0C/06 [03, ch, %]** | **Vibration strength**: ch5 = left (big motor + left palm), ch6 = right; ch7/8 probably the triggers | Synapse capture + mic (E-20261110-05) |
| **0C/80** | Vibration channel list → `06 05 06 07 08 01 02` | emulation |
| 0C/81, 0C/82, 0C/85, **0C/86**, 0C/88, 0C/91–0C/99 | Haptics gets; 0C/86 [p, ch] → % (0x64); 0C/85/92/93/95/96/97 read per profile by the Razer app | capture |
| 0C/95 [01 02 00] | Sent by Synapse before 00/53 | Synapse capture |
| 0F/02, 0F/03, 0F/04 | Lighting sets | emulation |
| **0F/80, 0F/81, 0F/82, 0F/84** | Lighting gets: 0F/80 → `05 19 03 01 02` (zones/LEDs), 0F/82 effect, 0F/84 brightness (FF) | capture |
| 10/00–10/05, 10/83 | Radio SoC flashing through the NXP (UpdateType.FLASH): erase / program / verify / reboot | emulation + updater |
| 16/07–16/0D | Sensa settings / effect slots | emulation |
| **16/0E [01, frame]** | **Sensa frame** (PC 2-hand layout or Xbox 4-body-part layout), ≥ 11 ms apart | decoded + hardware |
| 16/87–16/8F | Sensa gets; 16/8C → `00 04 …` | emulation |
| **16/90, 16/91** | Sensa descriptor/storage length (0x589) and read (offset, len) | capture |

The bootloader (1532:110E) has its own protocol, class 0x10: 10/01 [start, end] erase, then program and verify
(E-20261111-11).

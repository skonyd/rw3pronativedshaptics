# Sensa USB transport (Synapse → Wolverine V3 Pro), captured 2026-11-08

**Status: transport decoded from one capture of the user's own controller. Frame encoding
(the 41/44-byte payload) is not decoded yet. Nothing has been written to the device by RW3.**

## Setup

- Wolverine V3 Pro on **USB cable, PC mode** (1532:0A4C, Synapse device `2636-{9183b804-…}`).
  On the HyperSpeed dongle alone, Synapse did not attach the controller to its haptics service
  (RzInterHaptics logged senders but no `SenderGetMetadata device` line) and nothing reached
  the motors (microphone ~0 dB).
- After a Windows restart the Synapse "Sensa HD" module only started once its page was opened.
- USBPcap 1.5.4.0 (official signed release), root hub `USBPcap5`, **only the controller's
  address** captured (`--devices 11`). A control capture first verified the method: an XInput
  rumble command appeared as 8-byte interrupt OUT packets.
- Known pattern from `RW3Pro.HardwareProbe capture-pattern` with UTC step times:
  left 0.25, left 1.0, right 1.0, both 0.5 (60 Hz test pulses, 1 s each), then 80 Hz and
  190 Hz left tones through the feature path.

Raw files (git-ignored): `TestResults/usb-capture-20261108/wolverine.pcap`, `timeline.json`.

## Transport (all 192 packets)

| Field | Value |
| --- | --- |
| USB request | `21 09 04 03 01 00 5b 00`: HID SET_REPORT, **Feature**, report ID **4**, interface **1** (Razer control collection, `MI_01&COL04`, feature length 91), wLength 91 |
| byte 0 | `0x04` report ID |
| byte 1 | `0x00` status |
| byte 2 | sequence / transaction counter, +1 per packet, wraps |
| bytes 3–5 | `00 00 00` |
| byte 6 | data size: `0x2A` (42) normally, `0x2D` (45) when the frame carries transients |
| byte 7 / 8 | command class `0x16`, command id `0x0E` |
| byte 9 | `0x01` |
| bytes 10… | the haptic **frame**: starts `0x79`, 41 bytes (44 with transients), left block then right block |
| byte 89 | CRC = XOR of Razer-packet bytes 2–87 (verified 192/192) |
| byte 90 | `0x00` |

Cadence: one report every **33.0 ms** (p10 32.7, p90 33.3) while vibrating; **none in silence**.
This independently confirms the 33 ms frame period found acoustically (SENSA_PIPELINE_ANALYSIS §14).

Example frames (first bytes after `0x79`; left block … right block):

| Step | Left block | Right block |
| --- | --- | --- |
| left 0.25 | `3c 51 e2 8f 14 78 a8 …` | `00 30 01 80 0c 00 68 …` (idle) |
| left 1.00 | `fc 57 e2 bf 15 f8 a8 …` | idle |
| right 1.00 | idle | `fc 57 e2 bf 15 f8 a8 …` |
| both 0.50 | `7c 53 e2 9f 14 f8 a8 …` | same |

Left and right blocks are identical in layout, so the side is positional, not flagged.

## What this enables

1. **No Synapse in the path:** the same frames (HAR + provider produce them; the provider's
   debug accessor returns exactly these bytes) can be written with `HidD_SetFeature` to the
   controller's control collection. Needs an explicit, consented write experiment.
2. **No HAR either:** requires decoding the frame (amplitude points, frequency, transients per
   block) from more captures with swept known inputs.

Open: does the firmware need a start/handshake command from Synapse; coexistence when Synapse
is also writing; dongle/wireless transport (different path); Xbox mode.

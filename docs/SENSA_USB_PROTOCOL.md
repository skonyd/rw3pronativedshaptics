# Sensa USB transport (Synapse → Wolverine V3 Pro), captured 2026-11-08

**Status (2026-11-08, night): transport and frame format decoded; RW3 writes frames itself
(Settings → Gelişmiş → Bağlantı yolu → Doğrudan USB). No Synapse, no HAR, no admin, no driver.
Microphone comparison: Direct USB equals the Synapse path (level, L/R, frequency, hits, tail).**

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

## Frame format (decoded)

Learned from 1225 + 526 frames rendered by the licensed HAR/provider with known inputs
(`RW3Pro.HardwareProbe frame-learn`, engine `CommandProbe`), then verified: our encoder
(`SensaFrameEncoder`) reproduces **1696/1696** of those frames byte-exact, plus the frames
captured from Synapse on the USB bus.

The frame is one **MSB-first bit stream**:

```
7 bits   0111100                                         header
per hand (left, then right):
  3 x ( 1 bit = 1
        4 x ( 6 bit strength = floor(a*63), 7 bit pitch = floor(p*127) ) )   band 1..3
  transient list, up to 2 records:
    1 bit = 1, strength 6, pitch 7, time 7 (ms within the frame)   per record
    1 bit = 0                                                        ends the list early
pad with 0 to the byte (41 bytes without transients, 44 / 47 with one / two records in total)
```

- `a` is the strength 0..1 at the 4 points of the 33 ms frame; `p` is 0..1 linear over 30–400 Hz.
- Band 1 is the plain signal. Bands 2–3 are extra simultaneous components; HAR fills them from
  multi-melody `.haps` (e.g. 80+190 Hz → P 17 and P 54 in bands 1 and 2). Unused bands are
  strength 0, pitch 0.
- Silence frame = all strengths 0, band-1 pitch P 6 (50 Hz). Synapse sends nothing at rest, but
  the controller keeps playing the last frame, so a direct sender must end with silence frames.
- Transient record, as the firmware 2.04.00 decoder reads it (emulated 2026-11-10, see
  [FIRMWARE_ANALYSIS_20261110.md](FIRMWARE_ANALYSIS_20261110.md)):
  - flag 1, strength 6 = floor(a*63), pitch 7 (HAR always writes 24 ≈ 100 Hz), time 7 in whole ms.
  - Then the next flag. A hand can carry 2 records. A 0 flag ends the hand.
- RW3 writes one record per hand for the frame's first hit (setting; off = folded into the nearest strength
  point).
  - Until 2026-11-10, RW3 wrote `time 8 + tail 100` instead (see the study below). That put 3 extra bits after
    a left-hand record and shifted the right hand.
  - Fixed: with a left hit, both hands now decode correctly in the emulated firmware.

## Xbox mode frame (4 body parts), decoded 2026-11-10

Source: the firmware's Xbox-mode decoder (0xEF50 in `Dev2FW`), run in an ARM emulator
([tools/firmware/emulate_decoder.py](../tools/firmware/emulate_decoder.py)) and verified with an encoder whose
frames decode field by field. The controller selects this decoder when it is in Xbox mode. The frame travels
in the same Razer command (class 0x16, cmd 0x0E, arg 0x01 + frame), tunnelled in GIP message 0x0F
(docs/research/XBOX_MODE_HAPTICS_ROUTE.md). **Works on the controller since 2026-11-11** (E-20261111-03/04).

```
7 bits   header (0111100 as in PC mode; its value sets the point spacing: points at 0 and 60)
per body part, in order 216 (left palm), 116 (right palm), 249, 149:
  band list, up to 2:       1 bit = 1, then 2 x ( strength 6, pitch 7 )   per band; 1 bit = 0 ends it early
  transient list, up to 2 (palms) / 1 (249, 149):
                            1 bit = 1, strength 6, pitch 7, time 7 (ms)   per record; 1 bit = 0 ends it early
pad with 0 to the byte
```

- Palms: pitch is mapped like PC mode (30 + p·370/127 Hz), and the Xbox decoder stores strength as a × 520 (63 → 32760).
- Body parts 249/149 are the **left/right trigger motors** (blind test, E-20261111-04). Pitch and strength are
  stored raw (7 / 6 bits). Strength raises level and motor speed. The effect of the pitch field is not measured yet.
- Sizes:
  - Smallest frame: header + four 0 flags pairs = 15 bits, so 2 bytes.
  - All 4 body parts with 1 band and no hits: 7 + 2·(28+1) + 2·(28+1) = 123 bits, 16 bytes.
  - Maximum: 349 bits, 44 bytes.
  - A GIP 0x0F packet leaves room for 1 + 47 frame bytes, so every frame fits.
- The firmware only accepts a new 0x0E frame every ≥ 11 ms. Like PC mode, it keeps playing the last frame
  until the next one arrives.

## What this enables

1. **Done:** `SensaHidTransport` writes feature report 4 (consented test 2026-11-08: captured
   left/right frames gave +19 / +17 dB at the mic; the silence frame stops the motors).
2. **Done:** `SensaFrameEncoder` builds frames itself, including 3 bands, so Standard, Hybrid
   and Multiband run on the Direct USB path without HAR's `.haps` machinery.
3. Mic comparison, 60 cases (TestResults/transport-comparison-20261108): Direct USB vs Synapse
   medians within ~1 dB in every scene, same 80/190 Hz split in stereo, hits 6.2 vs 4.9 dB,
   no tail on either.

Verified 2026-11-08 morning: works with Synapse fully closed, and **wirelessly** through the
HyperSpeed dongle (its own COL04 collection, same report) — which Synapse never did.
Open: Xbox mode; coexistence when Synapse also writes.


## Transient record, 2026-11-09 study

Method: HAR rendered offline with a controlled clock (Synapse open and attached; RenderHapticsDebug returns -12 otherwise). We varied 129 time offsets in 0.25 ms steps and a grid of envelope × transient amplitude × transient pitch × effect pitch. We also ran per-tick updates and rising/falling envelopes. Source: `tests/RW3Pro.HardwareProbe/HarTransientSweep.cs.txt`.

Record layout:
- **strength 6 bits:** ⌊amplitude × 63⌋.
- **7 bits:** always 24, whatever the transient or effect pitch. This is a type/constant, not a pitch.
- **time 8 bits:** 2 × round(position × 33). Whole milliseconds within the frame in 0.5 ms units, so the value is always even.
- **tail 3 bits:** `100` in every controlled case (~300 records).

**Corrected 2026-11-10 (firmware decoder):**
- "time 8" is really time 7 (ms) plus the next record flag, which was 0. That is why the value was always even.
- The "tail" bits are not part of the record. They are the first bits of the right hand: band flag `1`, then
  the first strength bits. That explains `100` (a quiet right hand) and the live-stream `101` (an active right
  hand).
- HAR wrote the correct layout. RW3's encoder copied those 3 bits as a fixed tail and then wrote the real right
  hand after them, so the right hand started 3 bits late.

The tail value `101` appeared only in the live "am" stream of 2026-11-08 (14 of 57 records). Its cause is not reproduced: it is not time, amplitude, pitch, envelope shape or the update pattern.

Offline back-to-back rendering produced header values 0110001/0110010 besides 0111100; live frames are always 0111100.

**Next step:** write records as strength / 24 / time / `100`, at most one per hand. Stop folding hits into the points: HAR does not fold. Compare against the folded version with the mic on the short-hits scene.

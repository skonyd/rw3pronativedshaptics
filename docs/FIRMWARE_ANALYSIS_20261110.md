# Wolverine V3 Pro firmware analysis (2026-11-10)

Static, read-only analysis of Razer's official firmware updater. The updater was **never run** and nothing was
sent to the controller. Firmware binaries are Razer's property and are **not** committed; the scripts in
[`tools/firmware/`](../tools/firmware/) rebuild everything from the user's own copy of the updater.

## Source

- File: `WolverineV3Pro_0A3F_FirmwareUpdater_v2.04.00_r1.exe` (7,903,312 bytes), Razer download.
- It is a .NET self-extractor (`SFX.Program`) with a ZIP appended. 7-Zip lists and extracts it.
- Contents: `WolverineV3ProCFU.exe`, `FWUpdaterDLL.dll`, `Controller_Update_Lib.dll`, a WinUSB driver package
  and `DeviceUpdater.resources`.
- `DeviceUpdater.resources` is a .NET resource set. The firmware sits in it as **plain Intel HEX text**, one line
  per resource string (`<Image>Line<N>`), not encrypted:

| Image | Lines | Address range | Version key | What it is (inferred from strings and code) |
| --- | --- | --- | --- | --- |
| `Dev1FW` | 6459 | 0x8000–0x3A6B6 | `DevFWVer1` = 2.00.03 | USB/GIP side: USB descriptors for 0A3F and 0A4C, `XGIP10`, `GIP init set` |
| `Dev2FW` | 12166 | 0x8000–0x37800 | `DevFWVer2` = 2.04.00 | Main MCU (NXP SDK asserts, PWM/SCT, DMA): Sensa descriptors, Razer command dispatcher, GIP metadata |
| `Flash2FW` | 5364 | 0x8000–0x31DD0 | — | Radio side: `Enter Pairing Mode`, `usb mode`, `SPI_CMD_SLEEP_MODE`, `Audio Jack` |

Other resource values: VID 1532, PID 0A3F for both devices, `FeatureReportLen` 91, `ReportType` 4,
`InputReportLen` 33, `OutputReportLen` 37.

`Dev2FW` is an ARM Cortex-M Thumb-2 image (vector table at 0x8000: SP 0x20027FF8, reset 0x8415). Entropy is
~6.5 bits/byte, normal for code.

## Finding 1: two Sensa descriptors, chosen by controller mode

`Dev2FW` contains two "Sage T1" body-part descriptors (the JSON that Razer's haptics service reads):

| Address | Body parts | Per body part | Used when |
| --- | --- | --- | --- |
| 0x347B1 | 2: **216** (left palm), **116** (right palm) | VCM, 30–400 Hz, resonance 50 Hz, 3 bands / 4 points / 2 transients | flag = 1 (**PC mode**) |
| 0x34228 | 4: **216**, **116**, **249**, **149** | palms: 20–300 Hz, resonance 50 Hz, 2 bands / 2 points / 2 transients; 249/149: 0–300 Hz, resonance 20 Hz, 2 bands / 2 points / 2 transients | flag ≠ 1 (**Xbox mode**) |

- 249 and 149 follow the same left = 2xx / right = 1xx pattern as the palms. With resonance 20 Hz and a 0 Hz
  minimum they look like the **trigger motors**. This is inferred, not proven.
- The selection code is at 0x3110A–0x3113A: it reads the byte at RAM 0x20000F68 + 0xEF. If it is 1, it serves
  the 2-part descriptor, otherwise the 4-part one.
- That flag is set at boot (0x9496–0x94CE and 0xBAEE–0xBB3E): 0 when the boot handoff word asks for it or the
  stored mode byte is 1, and 1 when the stored mode byte is 2. The flag is read in about 40 places. Its use in
  the Razer packet handler (below) shows that 1 = PC mode (Razer HID present) and ≠ 1 = Xbox mode.
- In PC mode the controller really reports the 2-part descriptor. Synapse reads it in the mode-switch capture
  (`TestResults/usb-capture-modeswitch/modeswitch.pcap`, local) with Razer class 0x16:
  - cmd 0x90 returns the total length (0x0589 = 1417 bytes).
  - cmd 0x91, args `[offset hi, offset lo, 0x4D]`, returns 77 bytes per call. The JSON is followed by profile data.
  - The RzInterHaptics logs show the same descriptor: always `NumberBodyPart 2`, body parts 216/116.

**Meaning:** the firmware is built to drive the palms *and* (probably) the trigger motors as one haptic
stream, but **only in Xbox mode**, with lower resolution (2 bands / 2 points instead of 3 / 4). PC mode exposes
only the palms. This matches the measured behaviour: no trigger-motor route exists in PC mode (STATUS.md).

## Finding 2: GIP metadata in Xbox mode

`Dev2FW` 0x36EA0–0x37030 holds the GIP device metadata.

**Gamepad**, preferred type `Windows.Xbox.Input.Gamepad`.
- Upstream (device → host) system messages: 01 02 03 04 06 07.
- Downstream (host → device) system messages: 01 04 05 06 0A.
- Interface GUIDs, as stored little-endian:

| GUID | Known as |
| --- | --- |
| `{9776FF56-9BFD-4581-AD45-B645BBA526D6}` | unknown; no public reference found |
| `{082E402C-07DF-45E1-A5AB-A3127AF197B5}` | Gamepad |
| `{B8F31FE7-7386-40E9-A9F8-2F21263ACFB7}` | Navigation |
| `{AAE22FB7-E167-4908-9A40-FF9C687FA986}` | unknown; no public reference found. Probably Razer's vendor interface |
| `{ECDDD2FE-D387-4294-BD96-1A712E3DC77D}` | Console function map |

- Message descriptors: three entries with message IDs **0x09** (rumble), **0x10** (size field 0x10) and **0x0F**
  (size field 0x08). 0x0F and 0x10 are not standard gamepad messages, so they are probably vendor messages.

**Headset**, preferred type `Windows.Xbox.Input.Headset`, with audio messages 0x60.

## Finding 3: Razer packet path

- The Razer class dispatcher is the function at 0x30524. It checks the XOR CRC and runs a `tbb` jump table over
  class ≤ 0x16 (0x3074C).
- A second handler, 0x22D08, validates 90-byte Razer packets in a different buffer (0x2000515C) for class 0x04
  commands 07 / 02 / 05. It answers differently by mode:
  - Flag = 1 (PC mode): it marks the reply in the HID packet buffer (pointer at 0x200000D8).
  - Flag ≠ 1 (Xbox mode): it writes **0x10** to a send buffer (0x20004FA8).
- So in Xbox mode Razer packets most likely travel inside GIP message **0x10**. This is the leading hypothesis
  for how Razer's own Xbox app ("Razer Controller Setup for Xbox") configures the controller. It is not confirmed.

## Host side, checked on this PC (read-only)

- The only audio endpoint in Xbox mode is `Kulaklıklar (Xbox Controller)`: 2 channels, 48 kHz, mask 0x3,
  headset only. There is no haptic audio endpoint. Tones on it do not vibrate the controller (STATUS.md).
- `HKLM\SOFTWARE\Microsoft\GameInput\Devices` has no entries. GameInput's haptics API (v2.2+,
  `GameInputHapticInfo`) needs the device registered there with an audio endpoint.
- Installed: inbox `GameInput.dll` 0.2309 (v0) and `GameInputRedist.dll` 4.0.306.

## Reproduce

```bash
7z x -o<out> WolverineV3Pro_0A3F_FirmwareUpdater_v2.04.00_r1.exe
dotnet run --project tools/firmware/resdump -- <out>/DeviceUpdater.resources <res>   # writes Dev1FW.txt, Dev2FW.txt, Flash2FW.txt
python -I tools/firmware/hex2bin.py <res>/Dev2FW.txt <res>/Dev2FW.bin
python -I tools/firmware/fwscan.py <res>/Dev2FW.bin
python -I tools/firmware/descriptor_refs.py <res>/Dev2FW.bin                          # needs capstone
```

Work on copies in a scratch folder. Never run the updater or the extracted EXEs for analysis.

## Not done / limits

- The handlers for GIP messages 0x0F / 0x10 were not traced to the Sensa decoder.
- The decoder's 4-body-part bit layout is unknown.
- `Dev1FW` was only string-scanned.
- That body parts 249/149 are the trigger motors is an inference.
- Nothing here was sent to the controller.

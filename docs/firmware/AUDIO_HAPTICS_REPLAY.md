# Controller-side audio-to-haptics replay

The lab runs the original NXP I2S transmit callback and an independently written,
freestanding Cortex-M4 audio processor on identical PCM. It also passes the
processor's output through the original Xbox Sensa frame decoder. The linked
processor is a simulator module with a synthetic address and no reset vector.

## What the replay establishes

- Razer's 2.04.00 NXP image, SHA-256
  `d19550cf434da3e3ae3c7e8e46f23e5c5f61274a0a462bf81b768c3953a1aee8`,
  executes `0x22FF8` and reaches the DMA submit routine at `0x12F9C`.
  The ABI carries the selected buffer in R2 and length 192 in R3.
- With fixture state initialized in RAM, the callback selects one of the three
  slots at `0x20013A38`, an alternate buffer at `0x200057AC + offset`, or the zero
  block at `0x200056EC`. The three-slot index wraps. Selected audio bytes arrive
  unchanged at the DMA boundary. This does not identify the producer of either
  buffer or prove which path wireless mode uses.
- The callback leaves all four Sensa body structures untouched in these runs.
  Earlier physical headset tests also found no autonomous Sensa
  (E-20261111-08/09). The Microsoft
  [GIP message table](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/04cbe5e6-9502-4f9b-8adc-f7a2d3fcf0df)
  identifies 0x60 as audio. The USB receiver and transfer scheduling remain open.
- The independent DSP uses 32 bytes of persistent state, two integer filter
  bands per stereo channel and a bounded 0–63 envelope. The ARM text is 344
  bytes. Fixed output pitches are demo parameters, with no calibration claim.
  Full-strength fixtures saturate some bands. The highest measured count was
  3,932 emulated instructions per block, excluding the stock callback and
  decoder. Instruction count is not an MCU cycle budget or end-to-end latency.
- The actual Xbox decoder accepts the demo's palm bands. It writes strength
  `a × 520`, with 63 becoming 32760. Trigger bands are zero.

| Scenario | Peak L low/high | Peak R low/high |
| --- | --- | --- |
| Left 80 Hz | 63 / 63 | 0 / 0 |
| Right 190 Hz | 0 / 0 | 56 / 63 |
| Stereo 80+190 / 110+320 Hz | 62 / 63 | 48 / 63 |
| Alternate buffer | 63 / 63 | 56 / 63 |
| Inactive stream, unprimed stream, near-silence | 0 / 0 | 0 / 0 |

Active tones reached zero 30–32 scheduled 1 ms steps after the first silence
block. Interrupting an active stream produced zero on the first inactive step.
A default-off → enabled → disabled sequence also verified silence while off
and byte-identical PCM throughout. The switch is a simulator input. Its wire
command and authentication, cable/dongle routing and reset behavior are still
open. The fixture executes 1,311 callback invocations in nine scenarios.

## Reproduce

Extract the three images locally using [the firmware README](README.md). Use Zig
0.15.2 and Unicorn 2.1.4. Set Zig cache directories to writable local paths if
needed. These commands generate files under the ignored `TestResults/` folder.

```powershell
$lab = 'TestResults/firmware-lab'
zig cc -target thumb-freestanding-eabi -mcpu=cortex_m4 -Oz -ffreestanding -fno-builtin -fno-unwind-tables -ffunction-sections -nostdlib '-Wl,-e,audio_haptics_step' -T tools/firmware/audio_haptics_sim.ld tools/firmware/audio_haptics_core.c -o "$lab/audio_haptics.elf"
zig objcopy -O binary --only-section=.text "$lab/audio_haptics.elf" "$lab/audio_haptics.bin"
python -B tools/firmware/simulate_audio_haptics.py "$lab/images/Dev2FW.bin" "$lab/audio_haptics.bin"
```

## Capture and replay the same stimulus on cable and dongle

1. Add `--fixture-wav "$lab/headset-fixture.wav"` to the replay command.
   It writes six seconds of 48 kHz stereo PCM16: silence, left 80 Hz, right
   190 Hz, separate stereo mixtures, then two seconds of silence.
2. Connect just one transport and plug a headset into the controller. Locate
   the active headset render endpoint using HardwareProbe `audio-endpoints`.
   Start USBPcap on the current root hub and device address only. Confirm the
   address with the injected USB descriptor.
3. Run HardwareProbe `play-headset-fixture <endpoint-id> <wav>` and record a
   repeatable input sequence during capture. Repeat with the other transport.
4. Run `usb_audio_trace.py <pcap> <address> --blocks <local-json>` to report
   isochronous OUT slots and extract aligned GIP 0x60 payloads. The timestamp
   is the USB request timestamp, shared by its slots. Other transfer types and
   dongle framing need their own parser.
5. Verify the negotiated format independently. With contiguous 192-byte
   48 kHz stereo PCM16 slots, use `--verified-pcm-trace <local-json>` to run
   the trace at the callback boundary. It rejects sequence gaps and other
   payload sizes. For the first active block, the replay constructs a matching
   198-byte GIP slot and invokes the stock receive callback with a fixture event.

The parser and replay handoff passed a synthetic 198-byte isochronous slot
containing a 6-byte GIP header and 192 bytes of test data.
See [USBPcap format](https://desowin.org/usbpcap/captureformat.html).

### Windows hardware capture

| Transport | USB isochronous OUT | GIP 0x60 blocks | Payload | Sequence gaps |
| --- | --- | --- | --- | --- |
| Wired Xbox | 0x03 | 6,552 | 192 bytes | 0 |
| HyperSpeed dongle in Xbox mode | 0x06 | 6,272 | 192 bytes | 0 |

Both streams used 198-byte slots, four slots per submitted USB transfer. The
left-only 80 Hz, right-only 190 Hz and stereo mixture appeared in the intended
channels. After sample alignment, the 143,900 compared stereo frames differed
by at most two PCM counts across the two USB captures. Both PCM sequences
passed the offline callback/DSP replay. Local captures and replay JSON live
under `TestResults/firmware-lab`.

NXP callback `0x28C80` accepts a 198-byte event, skips six bytes at
`0x2000B3C0` and copies the rest into the 6,144-byte ring at `0x200057AC`.
Execution stopped at its accounting call `0x2384C`. For a non-silent PCM
block from each capture, the real machine code preserved every payload byte
at offset 0 and at offset 6080 where the copy wrapped to the start of the ring.
The replay also invokes the stock TX routine on the same RAM after the stock
receive branch. At offsets 0 and 5952, the TX routine selects the slot just
written and advances its read pointer correctly, including ring wrap. The
USB event and already-primed stream state are supplied by the fixture.
This corroborates the wired USB-to-I2S buffer route. The dongle capture ends
at the host-to-dongle USB link. Its over-the-air framing and where the
controller receives or decodes the audio remain open. No planned button
sequence was captured in this pass.

## Remaining work

1. Trace endpoint registration, DMA completion and queue priming into the
   confirmed 198-byte branch and on to I2S without fixture state. Capture a
   timed button/analog sequence. Identify the NXP–Telink link and use it to
   connect the two B91 call graphs. The [B91 map](TELINK_B91.md) supplies anchors.
2. Reconstruct NXP peripheral behavior in that order: USB endpoint transfer,
   DMA descriptor completion, timer IRQ scheduling and SCT PWM capture. Add
   ADC and GPIO/PINT input replay with USB reports as the acceptance output.
   Seed models from the [NXP reference manual](https://www.nxp.com/products/LPC546XX)
   and compare model traces against independently captured stock behavior.
   Identify float table users and measure the synthesis path from Sensa RAM to
   PWM before claiming actuator equivalence.
3. Integrate the DSP at a verified PCM tap with bounded work outside the DMA
   callback. Verify ownership of Sensa render state, coexistence with game
   haptics, sustained audio underrun behavior and timing under interrupt load.
   Compare raw PCM, decoded haptic frames and PWM using the same event trace.
4. Read the resident bootloader and stock flash if a non-destructive debug
   path is available. The updater does not contain 0x0000–0x7FFF. Updater CRC
   findings do not determine bootloader acceptance, hardware security state or
   recovery after corrupted app flash. Validate readback and stock recovery on
   a spare unit before a controller image is tried. See the
   [rollback plan](../research/CUSTOM_FIRMWARE_PLAN.md).

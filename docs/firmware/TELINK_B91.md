# Telink B91 update images

These are findings from Razer updater 2.04.00. File offsets in this document
are relative to each extracted binary. Add 0x8000 for the Intel HEX record
addresses. Runtime destinations are listed separately below.

| Image | Update role | Size | Header payload length | Inverted CRC32 |
| --- | --- | --- | --- | --- |
| Dev1FW | HyperSpeed dongle | 206518 | 0x316B6 | 0x2F55E8C3 |
| Flash2FW | Controller radio/audio | 171472 | 0x28DD0 | 0x34244D8B |

Header words at offsets 4 and 8 match payload size and inverted CRC32 over
`[0x1000, end)`. Both payloads contain compressed and ordinary RISC-V
instructions. Dev1FW opens with a compressed jump to payload offset 0x28.
Flash2FW begins with an `mcause` read and conditional branches. Early startup
contains floating-point control instructions and trap-vector setup. The
[Telink platform SDK](https://github.com/telink-semi/tl_platform_sdk/releases)
identifies B91 as TLSR921x/TLSR951x with a RISC-V toolchain.

- Dev1FW: LC3 Plus source-path marker at 0x30C60 and B91 SDK marker at 0x32694.
  The codec marker alone does not establish the dongle's active Xbox audio format.
- Flash2FW: I2S log at 0x23284, pairing request at 0x23540 and jack log at
  0x2376C. These are cross-reference targets for audio buffers and RF handlers.

Run `python -B tools/firmware/map_telink.py Dev1FW.bin Flash2FW.bin` with Capstone
5.0.7. The JSON covers every file offset with header words, short entry blocks,
string candidates and explicit unknown ranges. The initial pass finds 3
filtered string candidates in Dev1FW and 19 in Flash2FW. Trace references to
the markers through RISC-V PC-relative addresses and startup relocation next.
RF framing, audio codec call sites, pairing state and runtime address space
remain open.

## Startup and Xbox USB boundary

The official product instructions connect the HyperSpeed dongle directly
to Xbox or PC. The official updater lists the dongle and controller as
separate update selections. In the extracted package, `Dev1FW` is the
dongle image, `Flash2FW` is the radio/audio image inside the controller,
and `Dev2FW` is the controller's NXP application.
[Razer connection guide](https://mysupport.razer.com/app/answers/detail/a_id/14587)
[Razer updater guide](https://mysupport.razer.com/app/answers/detail/a_id/14630)

The header's repeated `0x20009000` word is used as the replay entry and
payload mapping. With both local memory windows initially filled with
`0xA5`, stock RV32 startup reaches the relocated application entry for
each image. The test checks the ordered store addresses and every byte
of each copied or cleared range. It models the documented Andes
`nds.addigp` immediate and the values of platform CSRs
`0x800`, `0x7CA`, and `0x7D0`. Those CSRs' hardware side effects and
the resident loader remain outside the model.
[Telink driver SDK handbook](https://doc.telink-semi.cn/doc/en/software/res/sdk/driver/driver_sdk_en/)
[Andes opcode definitions in the binutils patch](https://sourceware.org/pipermail/binutils/2025-November/145934.html)

| Image | Application entry | Initial SP | Relocated bytes | Zeroed bytes |
| --- | --- | --- | --- | --- |
| Dev1FW | 0x20644 | 0xC0000 | 202,004 | 98,664 |
| Flash2FW | 0x9CDC | 0xA0000 | 100,160 | 67,280 |

The dongle copy loop reaches `0x29724` exclusive. The replay maps that
observed range. The Telink public SDK's reference map for some B91 parts
describes a smaller local instruction window. The product's exact memory
variant and physical configuration still need verification.
[Telink B91 reference memory map](https://doc.telink-semi.cn/doc/en/software/res/sdk/ble/b91_ble_single_connection_en/b91_ble_single_connection_en/)

After relocation, the dongle data contains the Xbox USB device
descriptor `1532:0A3F` at `0x864C8` and its configuration at
`0x80298`. The stock setup block at `0x1F1C0` passes those
addresses and the 64-byte configuration size to its USB helper.
Interface 1, alternate setting 1 lists isochronous audio
OUT `0x06` and IN `0x87`. Both appear in the Windows dongle capture.
That capture contains 6,272 ordered 192-byte downstream GIP audio
blocks on `0x06`. The stock endpoint handler replay below extends
the verified boundary to the dongle's PCM ring.
No Xbox console session or USB enumeration runs in this replay.

```powershell
python -B tools/firmware/replay_telink_startup.py <Dev1FW.bin> <Flash2FW.bin> --dongle-usb-trace <dongle.pcap>
```

The lab trace uses USB address 9. Pass `--usb-address` for a capture
with a different assigned address. The tool writes its report to
stdout and issues no USB or radio requests.

## Dongle USB audio handler and PCM ring

```powershell
python -B tools/firmware/replay_telink_usb.py <Dev1FW.bin> <dongle.pcap>
```

The replay uses the original 198-byte slots from the Windows capture.
It starts stock code at dispatcher `0x3C60`, with the endpoint-6
pending state and audio mode seeded. The dispatcher takes `0x51B8`,
which consumes the six header bytes and stores 48 interleaved PCM words
in the ring at `0x91254`. The write counter is at `0x90A34`.
The no-pending case leaves the ring and counter untouched. The handler
clears the length register. The full trace matches **1,204,224 PCM bytes**
across **6,272 slots and 294 ring wraps**, including the adjacent-word
guards. A host-input hash and a ring-output hash also agree.

The model provides length and stream reads at `0x80100816` and
`0x80100826`, respectively, as used by these stock instructions.
This is an observed code-access pattern in the replay. The public B91
SDK labels the latter address as an endpoint control register, so this
model does not establish the product's physical register semantics.
The handler returns normally after the event helper call at `0x5310`
and writes 1 to `0x8010082E`. Each of the 6,272 slots publishes an event
of type 8 to the 512-entry ring at `0x88154`. The event carries the low
16 bits of the PCM write counter, which first rolls over at slot 1,366.
Event timestamps match 1 ms steps from a modeled 24 MHz clock. The
host dispatches each slot and provides the clock and endpoint registers.
[Telink B91 USB registers](https://github.com/telink-semi/tl_ble_sdk/blob/master/tl_ble_sdk/drivers/B91/reg_include/usb_reg.h)
[Andes DSP instruction encodings](https://github.com/hpmicro-rs/andes-riscv/blob/main/src/dsp.rs)

The downstream code at `0x57EC` reads the same ring, and call site
`0x5858` passes samples to `0x18AB8`. A separate fixture runs the input
loop of that routine directly on one asymmetric captured ring block.
It checks the exact 48 left and right samples in separate buffers using
a seeded context. The replay also enters the stock consumer at `0x5664`
with a fixture request for 47 frames and verifies its 48-frame internal
read. Two asymmetric capture blocks cover contiguous and wrapped reads.
In these fixtures stock preserves nonnegative samples and rounds each
negative sample one integer toward zero. The real call at `0x5858`
then splits those adjusted samples into separate buffers.

With only 48 published frames and zero elapsed fixture time, stock
moves its read cursor to −414 and reads 48 frames, ending at −366. The
read began at ring word 610. An initialized-to-zero segment produced
silence. A segment filled with `A5` produced the expected pattern after
rounding. This checks what the routine reads under these initial
conditions. The product's real startup scheduling and memory state
require independent evidence.

### Stock stereo resampler and caller buffer

The stereo initialization call at `0x1AD80` enters `0x26B00`, which
calls `0x26700` to build its coefficient table. The replay enables
RV32 floating-point execution to run these stock routines. It starts
at the call setup with the value of S1 loaded earlier by the firmware.
The initializer installs the resulting context at `0x8809C`.
The modeled Andes DSP instructions cover the register results used by
this path. The replay does not model their saturation status flags.
[Andes GNU opcode definitions](https://github.com/andestech/binutils/blob/dd22be9/include/opcode/riscv-opc.h)
[Q15 saturation semantics](https://doc.nucleisys.com/nmsis/core/api/nmsis_core_dsp_intrinsic/nmsis_core_dsp_intrinsic_non_simd/api_nmsis_core_dsp_intrinsic_non_simd_q15_sat_alu.html)

The same four ring scenarios now run through the actual `0x18AB8`
kernel and return to the caller of `0x5664`. Each 47-frame request
reads 48 frames and returns 47 processed frames. The last processed
frame is retained at `0x92254`. The test checks the returned bytes,
retained frame and caller-buffer guards. The nominal stereo step
fixtures cross from under 500 to over 24,000 at zero-based frame 14,
with the opposing channel staying at zero and the filter tail
decaying to zero. For 480 frames of silence, the isolated resampler
produces 476 frames at a +10,000 test ratio offset and 485 at −10,000
relative to the base 1,000,000. These offsets exercise the adjustment
routine directly. Physical drift and end-to-end latency remain unmeasured.

The replay now reaches the ring consumer's return buffer. The stock
instruction at `0x24EF4` loads the relocated LC3 Plus encoder setup
source marker at `0x86540` on an assertion path. This locates code
associated with the encoder. The real consumer scheduler, stream
negotiation, active codec, RF send/receive and NXP-facing handoff remain
open.

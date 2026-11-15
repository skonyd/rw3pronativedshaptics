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
rounding. In a separate dirty-memory fixture, the stock `0x4B54`
reset returns after clearing 4,096 ring bytes, four retained-sample
words and the timing word at `0x90A40`. The cursor words and adjacent
guards keep their previous values. Startup call order and physical
consumer scheduling still need independent evidence.

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

## Controller Telink I²S and DMA replay

```powershell
python -B tools/firmware/replay_telink_radio.py <Flash2FW.bin>
```

The stock controller routine at `0x20029E3C` initializes the PCM
history, DMA registers and linked descriptors, port-C pin functions
and I²S format. At the checkpoint just after I²S enable,
`0x2002A086`, the replay verifies both 512-word self-linked
descriptors against the corresponding channel registers.
[Telink B91 audio registers](https://github.com/telink-semi/tl_ble_sdk/blob/master/tl_ble_sdk/drivers/B91/reg_include/audio_reg.h)
[Telink B91 DMA registers](https://github.com/telink-semi/tl_ble_sdk/blob/master/tl_ble_sdk/drivers/B91/reg_include/dma_reg.h)

| Channel | Direction at I²S FIFO `0x80120000` | CPU-visible PCM ring | CPU-visible descriptor |
| --- | --- | --- | --- |
| 3 | Transmit to FIFO | `0x93878` | `0x9568C` |
| 2 | Receive from FIFO | `0x87D24` | `0x95678` |

The stock call at `0x12B44` sends the stereo stack buffer to writer
`0xF808`. The fixture enters at `0x12B3C` with an output frame
count and synthetic interleaved PCM. All **623 stereo frames**
(2,492 bytes) reach the modeled FIFO input in order and byte for
byte. The five blocks cover left only, right only, stereo, an
in-block wrap with one descriptor reload, and silence. The two
words neighboring the transmit ring keep their guard values.

The host models DMA movement using the source register and the
stock descriptor. It supplies the transfer pacing for each block.
This establishes the stock caller-to-FIFO path with prepared PCM.
The earlier stock call setup at `0x12B2C` enters stereo resampler
`0x20022374` in place. Its stock initializer at `0x20022728`
builds the coefficient table using floating-point instructions.
The nominal-ratio fixture supplies six 40-frame PCM blocks for
silence, a left step and tail, a right step and tail, and final
silence. All 240 processed stereo frames reach the modeled FIFO
unchanged after the resampler. The steps cross the test threshold
at zero-based frame 14, the opposing channel stays silent,
and each step tail decays to zero. This is a sample-domain
fixture with known input, outside physical timing.

Paired per-channel processing at `0x2002656C` precedes those
calls. There is a separate LPC SPI initialization marker at
`0x2002A8B0`. The active RF codec, physical I²S signal and
controller-to-NXP link still need to be demonstrated independently.

### Controller decoder and end of board setup

The replay now continues from the I²S checkpoint to the normal return
from `0x20029E3C`. It runs the stock debug-ring setup first and models
a zero read from the board selection register at `0x801401C8`. The
decoder initializer at `0x20025618` in isolation configures 480 samples
per channel. The complete board routine configures **240 samples per
channel**, consistent with a 5 ms block at 48 kHz. The production board
register value during a live link is still unmeasured.

With both contexts initialized, `0x2002656C` returns 35 for every
tested case, including a null input pointer. The null pointer leaves
the PCM buffer untouched. Cold packet loss writes 240 zeros without
reading payload bytes. An all-zero packet reads 11 distinct bytes
and writes 240 zeros. A loss after a nonzero decode can retain
energy. The first such loss in the 200 Hz fixture has a peak of 292
PCM counts. The replay checks reads, writes and guard words.

The two independent tone inputs are the first frame of 200 Hz and
1,000 Hz sines with 20,000-count amplitude, encoded as 35-byte, 48 kHz,
5 ms frames with liblc3 v1.1.3 in 16-bit PCM mode. Its decoder produces
peaks of 19,969 and 19,698. The stock emulator produces peaks of 516
and 0 in the respective controller fixture channels. These outcomes
do not demonstrate equivalence with the independent LC3 decoder or
identify the active HyperSpeed payload format.
[liblc3 v1.1.3](https://github.com/google/liblc3/tree/96a3af0beb5487aca3b98a4b992a539a1f6d80d1)

The host interleaves those exact two decoded lanes for the downstream
test. The stock stereo resampler, writer and modeled DMA feed all 240
frames to the I²S FIFO. The writer zeroes 18 one-count samples in this
run. The test checks this rule and preservation of the remaining
resampled samples. Upstream scheduling and the stock channel
interleaver remain outside this fixture.

### Stock dongle encoder paired with the controller decoder

```powershell
python -B tools/firmware/replay_telink_link.py <Dev1FW.bin> <Flash2FW.bin>
```

The dongle initializes its two encoder contexts at `0x26BC4`.
The replay supplies 240 interleaved PCM frames at `0x5C6C`.
Stock code separates the channels, calls the encoder at
`0x26D58` from `0x5CB0` and `0x5D04`, and copies two
35-byte outputs to the destination. Each call returns zero and
reports 35 bytes. The test checks both lengths and the destination
guards. The frame size and sample rate agree with the controller
board setup fixture.

The host forwards the resulting encoded bytes to the stock controller
decoder unchanged. It models a left-only loss by withholding that
payload and supplying the stock loss flag. It interleaves the
decoded PCM and then executes the receiver's stock resampler,
writer and modeled DMA. Across eight scenes, **29 stereo blocks**
(2,030 encoded bytes) produce **6,960 stereo frames** at the
modeled I²S FIFO. The scenes cover cold silence, each channel
alone, stereo, a left drop, recovery, swapped channels and drain.

The 20,000-count synthetic signals recover in the stock decoder
at 20,002.8 counts for left 200 Hz and 19,975.4 for right 1 kHz
in the settled stereo block. Swapping the inputs moves the tones
to the opposite channels at 19,990.9 and 19,999.9 counts. Opposite
test-tone components stay under 5 counts in those settled decoder
blocks. The inactive output is zero in the settled single-channel
tests. The right signal continues during the left-only loss and
both channels recover. The settled drain block is silent.

This establishes an interoperable stock codec pair with the
tested initialization. The previous independent frame comparison
still disagrees with this configuration. The paired test supplies
the PCM before the encoder's stock splitter and bridges the encoded
bytes in the host.

### Windows USB capture through the stock audio worker

Add `--dongle-usb-trace <dongle.pcap>` to the paired command to
run the two active windows from the lab capture. The fixture
uses zero-based slots 600 and 1600 at USB address 9.

The stock endpoint routine fills the PCM ring, writes the worker
cursor at `0x880A4` from `0x530C`, and publishes the same index
in its event. The fixture seeds the read position and steady-state
mode, then sets the modeled timer interrupt status bit. The stock
interrupt routine at `0x3CDC` reaches worker `0x5958` through
the original call at `0x3D28`. With the pending bit clear the read
position stays unchanged. The stock worker reads 241 samples per channel from the
1,024-word ring. At the resampler call `0x5F54`, the PCM matches the
recorded inputs after the documented fixed-point rounding of
negative samples. The worker passes the first 240 stereo frames of
its resampled block to the two stock encoders and returns with a
70-byte pair at `0x8E1FD`.

| Capture window | Ring read | Controller decoder peaks L/R | Modeled FIFO peaks L/R |
| --- | --- | --- | --- |
| 15 slots from slot 600 | Contiguous at word 258 | 10,757 / 1 | 10,757 / 0 |
| 27 slots from slot 1600 | Wrap starting at word 834 | 1 / 11,602 | 0 / 11,598 |

Both windows deliver 240 stereo frames to the controller's
modeled I²S FIFO. The host supplies the interrupt arrival and
forwards the pair to the local receive queue fixture below. Stock
interrupt timing, over-the-air headers and integrity, retransmission
and the physical NXP-facing link remain open.

### Controller receive slot and loss flags

The paired replay also enters the controller's stock local queue
copy loop at `0x131B4`. The host places the 70 bytes from both
dongle encoders at the front of a prepared 128-byte slot and
seeds the application state needed by the audio branch. The
stock loop copies the complete slot into its stack and calls
`0x12834`. The receive code passes byte offsets 0 and 35 to
decoder `0x2002656C` for left and right respectively. The replay
checks the complete copy and both call arguments.

The healthy fixture uses status byte `0x81` at slot offset 127.
Changing it to `0x85` sets bit 2. In this branch the stock
dispatcher passes the loss flag to **both** decoder calls.
Concealment retains previous signal energy on both channels,
and subsequent healthy slots restore the correct stereo tones.
The existing left-only loss case continues to exercise the
decoder directly. It describes decoder behavior under selective
loss and gives no evidence that the live receiver signals that
condition.

The expanded synthetic replay covers **34 encoded pairs** and
**8,160 decoded stereo FIFO frames**. The stock receive code handles
33 of those pairs plus both recorded USB windows. For each fresh
receiver, the stock code advances the PCM writer 160 frames ahead of
the modeled DMA reader on the first slot. The test verifies that
initial region contains silence, maintains the 160-frame lead while
checking every FIFO word against the expected stream, and drains
the remaining words at the end. The host still
prepares queued records, supplies the clock state used at this
boundary and interleaves the decoded PCM for the stock resampler.
The receiver queue producer, radio DMA and transmission protocol
remain open.

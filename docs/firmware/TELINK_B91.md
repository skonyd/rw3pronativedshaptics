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

### Stock RF DMA transmit and receive

The paired replay now passes its encoded payload through the real
stock transmit and receive code. Both captured USB windows and the
synthetic scenes use the dongle's TX selection routine at `0x2FA4`.
Its stock code selects a 128-byte record from `0x8E170`, writes a
length value of 77 at offset 5, writes the audio tag `0x28 | slot`
at offset 12, and leaves the 70 codec bytes at offset 13. The stock
write at `0x30D8` sets the channel 0 DMA source register
`0x80100448` to that record's DMA address. The 32-bit DMA length
word is `0x00C00014` for 79 bytes after the DMA prefix.
[Telink TX DMA length encoding and channels](https://doc.telink-semi.cn/doc/en/software/res/sdk/driver/platform_sdk_handbook_en/#rf-dma)

The host reads from that DMA source, copies the 79 bytes unchanged
into the controller's modeled receive DMA buffer at `0x936CC`, and
supplies the receive metadata and IRQ bit. The stock RF handler at
`0x118A0` checks the length limit and CRC-error flag before calling
`0x11844` and `0x11024`. The stock audio branch uses the tag's
low three bits as an index into the eight 128-byte queue slots at
`0x87644`, copies the 70 payload bytes and sets status byte 127
to 1. The replay verifies the whole queue to catch changes outside
the selected record and exercises wrap from slot 7 to slot 0.
[Telink RX buffer metadata layout](https://doc.telink-semi.cn/doc/en/software/res/sdk/driver/driver_sdk_en/#ble-packet-format)

Across the 34 transmitted synthetic frames, 33 reach the stock
receive IRQ. For the scheduled-drop fixture, the host withholds
one DMA delivery and invokes the stock missing-slot marker at
`0x132A8`. It writes status 4 and the stock decoder takes the
loss branch for both lanes. The direct left-only decoder control
remains in the suite. The two captured USB windows also exercise
four negative IRQ cases each. No pending bit, a CRC-error flag,
a length above the configured limit, and an immediate duplicate
produce no queue writes. The normal packets in those windows
retain their prior modeled FIFO peaks of 10,757 left and 11,598
right, with zero in the opposing lane.

The fixture supplies established link state, the dongle's packet
counter and TX completion, and the controller's arrival timestamps
and metadata. It drives the chosen receive queue slot into stock
copy and decode. The physical radio waveform, CRC generation, link
negotiation, retransmission timing, and autonomous queue consumption
remain unverified.

### Stock receive playout and slot completion

The paired replay continues from the second decoder's return at
`0x129F8` to the receive return at `0x131D4`. The fixture supplies
the steady playout flag at `0x87510`. The stock routine makes two
calls per block to `0xE53C` to interleave the channels and passes six
40-frame blocks to the stock stereo resampler at `0x20022374`.
The test observes each input to the resampler and checks every
interleaved sample against the two decoded lanes. The stock writer
at `0xF808` writes all output blocks. After each packet the replay
advances modeled DMA, checks the FIFO's bytes against the writer's
output and checks that stock cleared status byte 127 of the consumed
queue slot. A final modeled drain verifies the last 160 queued frames.

The same stock playout code covers fresh audio and concealment after
the stock missing-slot marker. Both captured USB windows reach it
with 240 decoded frames each. Including the stock playout rate
adjustment, their modeled FIFO peaks are 10,758 / 0 and 0 / 11,603.
Only the separate synthetic left-only loss control supplies
host-interleaved PCM. The fixture uses a selected queue slot and
controlled I²S state. Actual interrupt pacing and the LPC-facing
connection remain open.

### Controller Timer0 vector and queued audio

The same `replay_telink_link.py` command now also executes the
controller's stock Timer0 vector at `0x199C`. The RV32 replay enters
with modeled trap registers. It runs the stock context save and
call to `0x12F78`, observes the timer acknowledgement and PLIC
completion 4, and executes the stock restore and `mret`. The
Andes `mxstatus` read and write use a controlled value and the
replay verifies that the handler preserves it and the interrupted
integer registers.
[Telink B91 vector assignments and Timer0 mode](https://doc.telink-semi.cn/doc/en/software/res/sdk/driver/driver_sdk_en/)

Stock reads the RF slot counter at `0x8750C` and relative offsets
from receiver state, selects the queue address at `0x13140` and
copies the record at `0x131B4`. In this fixture five packets lead
playout. The host sets the initial RF counter and supplies arrivals.
The stock system-timer handler updates the counter for playout. The test
checks the selected slot, both decoder calls and all six PCM
blocks, then checks that the slot was cleared and its FIFO samples
appeared in order. One frame is withheld from RX. The stock
timer selects the empty slot, writes its loss flag and passes the
two loss controls to the decoders. The test checks recovery after
that loss, ring wrap and eventual silence.

The first interrupt writes 82,320 peripheral clock cycles to
the compare and handles audio. The second writes 37,680 cycles
without another decode. At the modeled 24 MHz peripheral clock
and 16 MHz system tick, those phases take 3,430 and 1,570 µs.
When the first interrupt is delayed by 100 µs, stock shortens
its phase by 2,400 cycles and the next audio deadline stays at
5,000 µs. The test feeds the actual compare values into the
next modeled interrupt time.
[Telink B91 system tick reference](https://doc.telink-semi.cn/doc/en/software/res/sdk/ble/b91_ble_single_connection_en/b91_ble_single_connection_en/#system-timer-usage)

The two Windows capture cases use repeated RF frames from the same
stock-encoded 70 bytes under fresh sequence tags. The first one
decodes to peaks of 10,757 / 1 and 1 / 11,602 respectively. In the
cold state the stock output routine writes all 240 frames as
silence. This is in addition to the 160 initial silent ring frames.
The next packet plays and yields modeled FIFO peaks of 11,003 / 0
and 0 / 11,619 respectively. The prior isolated playout result
used a fixture-ready flag and began after the stock cold transition.

### Stock RF counter and timestamp gate

The scheduled replay enters the stock system-timer handler at
`0x11C8C` immediately before each modeled Timer0 playout event.
It initializes the RF counter at `0x874FA` and its clock deadline
at `0x87508` once. Stock copies the current counter to the input
used by playout at `0x11D92`, then calls the stock catch-up helper
at `0xE460` to advance the counter from the system tick.
The test verifies both changes on each call and derives the slot
by executing the Timer0 vector.

The RF IRQ checks the timestamp in its DMA buffer against the
current stock receiver timestamp at `0x87504`. Each recorded
USB fixture delivers one packet at the stock reference and retries
the same sequence after verifying that a timestamp 16,001 ticks
above the reference produces no queue writes. Stock proceeds with
the correctly timed packet and preserves the earlier channel
isolation and cold mute. The 16 MHz system tick makes that test
offset just over 1 ms.

The fixture supplies the system-timer handler's invocation,
modeled RF arrival times, initial sync epoch and peripheral/DMA
timing. Live RF sync acquisition and drift behavior, TX completion
and retransmission, nested interrupts, and the physical
Telink-to-NXP connection remain open.

### Controller SPI control transaction

```powershell
python -B tools/firmware/replay_controller_spi.py <Flash2FW.bin> <Dev2FW.bin>
```

The stock Telink transaction at `0x20029844` selects PSPI and
starts it via `0x80140046`. The cold form configures a four-byte
write of `04 12 30 80` and a 96-byte read. The normal form
transmits 100 bytes beginning `04 12 30 00 BB 00`. The model
reads these bytes from the source of the stock DMA descriptor.
[Telink SPI driver handbook](https://doc.telink-semi.cn/doc/en/software/res/sdk/driver/driver_sdk_en/)

The NXP runtime control bus is prepared by `0x13B90` on
FLEXCOMM5 at `0x40096000`, with 100-byte DMA descriptors
for channels 10 and 11. The model moves the bus bytes using
those descriptors and invokes the stock DMA dispatcher
at `0xC478`. RX-first and TX-first completions both release
exactly one application callback at `0x1FFC4` after both
channels complete. A malformed header sets the firmware's
error flag and takes the branch that skips rearming.
[NXP LPC546xx user manual](https://community.nxp.com/pwmxy87654/attachments/pwmxy87654/lpc/58795/1/lpc546_series.pdf)

The stock NXP response starts `00 00 00 00 AA 00 01 03`,
includes the XOR at byte 98 and increments its counter to 1.
The next modeled read returns bytes 4–99 unchanged into the
Telink's stock DMA destination at `0x957C4`. The following
stock normal transaction puts these bytes into its receive
queue, validates them at `0x20028958` and selects the stock
handler at `0x20028C84`. Flipping the checksum byte reaches
the stock rejection path instead. Both records retire from
the queue.

The simulator chooses the cold and normal calls and drives
SPI clock and DMA events. This exercises the firmware on both
ends of the control transaction. The live transaction schedule
and the physical I²S path require separate evidence.

### Controller I²S clock roles and receive reader

```powershell
python -B tools/firmware/replay_controller_i2s.py <Flash2FW.bin> <Dev2FW.bin>
```

The stock Telink setup leaves `0xC2` at `0x80140501`. Bit 6
selects the receive-side master clock and bit 7 selects the
transmit-side master clock. The format and width fields select
classic I²S and 16-bit samples. The stock NXP setup leaves
`CFG1=0x000F0031` on FLEXCOMM6 and `CFG1=0x000F0001` on
FLEXCOMM7, with `CFG2=0x1F` on both. FLEXCOMM6 is master and
FLEXCOMM7 is slave. Both NXP ports use 16-bit samples and 32
SCK cycles per stereo frame. These initializations alone
cannot establish a shared PCB clock net or who drives it in
each active mode.
[NXP register definitions](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h)
[Telink B91 I²S driver](https://github.com/telink-semi/tl_ble_sdk/blob/master/tl_ble_sdk/drivers/B91/audio.c)
[Telink B91 register definitions](https://github.com/telink-semi/tl_ble_sdk/blob/master/tl_ble_sdk/drivers/B91/reg_include/audio_reg.h)

The receive fixture advances DMA channel 2 through its stock
512-word self-link. Reader `0xE658` takes no samples with an
empty ring or with 241 words pending. Adding the 242nd word
releases a block. The traced loads at `0xE788` read only the
low half of each FIFO word, crossing the wrap and advancing
the stock read cursor by 242. The function returns 240 mono
samples after its stock processing. Inverting the high lane
leaves the output byte-identical. Zeroing the low lane while
retaining the high lane yields silence. FIFO words and DMA
progress are modeled, and this test leaves their physical
source open.

### Controller voice uplink

```powershell
python -B tools/firmware/replay_telink_uplink.py <Flash2FW.bin> <Dev1FW.bin>
# Optional independent comparison with a local original dongle capture:
python -B tools/firmware/replay_telink_uplink.py <Flash2FW.bin> <Dev1FW.bin> --dongle-usb-trace <dongle.pcap> --usb-address 9
python -B tools/firmware/usb_voice_trace.py <dongle.pcap> 9
```

The stock worker at `0x12348` stays out of the voice queue while
the microphone mode bit is clear. Applying the open state runs its
priming path. The fixture then advances the initial queue read cursor
and models 250 FIFO words through the stock RX DMA descriptor. Stock
produces a 240-sample mono input and a 40-byte codec output. All 40
bytes match between the encoder, the queue at `0x852B4` and the RF
DMA frame produced by `0x10A40` with tag `0x38`. A 950 Hz tone in
the low half-word changes the encoded bytes. Putting the same tone
in the high half-word is byte-identical to silence at the encoder
input, codec output and RF frame.

The dongle receives the same frame through stock RF IRQ `0x3B08`.
The parser writes a 64-byte audio slot in the eight-slot buffer at
`0x8E570`, then also copies the record to its control queue at `0x800B4`.
An absent IRQ or modeled CRC error leaves both destinations untouched.
The metadata threshold `0x38` affects the control-queue copy. The
audio slot is populated at that boundary. The physical meaning of
this metadata byte remains open.

The stock mono resampler setup at `0x1AD84` and decoder initializer
`0x26CCC` prepare the dongle voice worker `0x54C8`. The test invokes
the stock audio-timer handler `0x3CDC` in its modeled voice phase,
with a fixture clock and RF epoch. An absent timer pending bit leaves
the slot untouched. With the pending bit set, the handler selects
the voice branch, acknowledges and rearms the timer, and calls the
worker. The worker selects the audio slot, passes
the original 40 codec bytes to `0x26DEC`, clears the slot and writes
240 resampled mono samples into the 1024-sample ring at `0x90A54`.
The fixture starts four samples before wrap and checks every output
sample. For the 950 Hz case the decoder peak is 23,916 and the
resampled peak is 23,919 PCM counts. The same route produces silence
with only the high FIFO half-word active.

The stock USB dispatcher `0x3C60` calls the Xbox-mode USB producer
at `0x4F3C` when the endpoint-7 event bit is set, and acknowledges
that bit. Without it the PCM cursor and FIFO stay untouched.
The producer consumes 48 mono samples per
call and writes every second one, in order, to the modeled endpoint
`0x87` FIFO. Five calls drain that 240-sample block into five 54-byte
GIP audio packets. The next call sends a silent packet and keeps the
read cursor fixed. Sequence bytes advance from 254 through 255 to 1,
skipping zero, and the endpoint is armed once per packet. The Xbox mode,
enabled stream, initial sequence and host consumption are fixture inputs.

The optional capture check independently validates all 784 successful
endpoint-`0x87` completions in the original dongle trace, each grouping
eight 54-byte packets. All 6,272 packets have the same header format
`60 23 <sequence> 32 C0 00` and 24 mono PCM16 samples. All 25 sequence
wraps skip zero, agreeing with stock replay. Nine mutations of one
recorded completion check rejection of truncation, failed URB/slot,
bad descriptors, zero sequence and wrong GIP length. The captured PCM
peak is 43 counts, so it is a framing reference, not the synthetic
950 Hz voice source or a waveform fidelity measurement. The USB host
groups packets in the capture; the replay still supplies each endpoint
event rather than implementing the host's polling schedule.

Two subsequent empty-slot worker calls take the stock silence branch.
The first retains a filter tail (peak 23,892 in the tone case), and the
second is silent. Repeating the old RF response leaves the consumed
slot empty. For recovery, the fixture toggles the reply-sequence bit
used by stock controller instruction `0x10B30`, retaining the original
codec bytes. The dongle accepts the new response and decodes again.

The fixture supplies link state, controller input FIFO words and
peer retirement of the priming frame. Physical transfer timing,
RF synchronization and a complete USB host schedule remain open.

### Stock dongle retry and controller reply

```powershell
python -B tools/firmware/replay_telink_duplex.py <Dev1FW.bin> <Flash2FW.bin>
```

The stock dongle sends audio slot 1 and sets its in-flight mask at
`0x8E084` to 2. The replay advances one RF subslot with the reply
withheld. Stock selects the same DMA address and keeps all 70 codec
bytes and the sequence tag. The send counter changes from 8 to 9.
The stock controller RX handler queues the original once, rejects
the retry as a duplicate, and runs its reply formatter at `0x10A40`
on both arrivals. Both formatter calls produce the same six DMA
bytes `01 00 80 00 00 00`.

The dongle's stock RF IRQ at `0x3B08` reads the reply buffer and
distributes validated replies to `0x4214`. The fixture models the
DMA metadata and write-to-clear status at `0x80170220`. Tests with
no pending interrupt, local TX completion only, a CRC error and
an altered reply sequence all leave the in-flight mask at 2.
Delivering the original controller reply clears it. Stock then
sends slot 2 with mask 4 and without a host write to clear the
previous in-flight or pending state.

The test enters the TX selector at scheduled subslots and moves
each RF frame between DMA buffers. It exercises one complete
retry and reply cycle. Physical RF delivery, the retry budget and
the system-timer events that invoke the TX selector remain open.

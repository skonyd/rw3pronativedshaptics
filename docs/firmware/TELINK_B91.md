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
blocks on `0x06`. This anchors the host-side transport to data from
the stock dongle loader. The audio handler, codec selection, RF
send/receive and the NXP-facing audio handoff still require a trace.
No Xbox console session or USB enumeration runs in this replay.

```powershell
python -B tools/firmware/replay_telink_startup.py <Dev1FW.bin> <Flash2FW.bin> --dongle-usb-trace <dongle.pcap>
```

The lab trace uses USB address 9. Pass `--usb-address` for a capture
with a different assigned address. The tool writes its report to
stdout and issues no USB or radio requests.

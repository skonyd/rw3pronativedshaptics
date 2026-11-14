# Telink B91 update images

These are initial static anchors from Razer updater 2.04.00. Offsets in this
document are relative to each extracted binary. Add 0x8000 for the Intel HEX
record addresses. The runtime CPU address mapping still needs verification.

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

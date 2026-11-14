# Wolverine V3 Pro firmware: full analysis (in progress)

The goal is to understand the controller firmware well enough to build our own. The motivation is console-side
audio → Sensa without a PC; see [../research/CUSTOM_FIRMWARE_PLAN.md](../research/CUSTOM_FIRMWARE_PLAN.md) for the
plan and safety gates.

All of this is static analysis and emulation of Razer's official updater v2.04.00 (`Dev2FW` 2.04.00,
`Dev1FW` 2.00.03, `Flash2FW`).
- **Nothing in this folder was written to a controller.**
- **No Razer binaries or raw disassembly listings are stored here**, only our maps, names, protocols and tools.
- Everything can be regenerated from your own copy of the updater with the scripts in
  [`tools/firmware/`](../../tools/firmware/).

| Image | Chip | Role | Document |
| --- | --- | --- | --- |
| `Dev2FW` | NXP **LPC54605** (Cortex-M4F, 180 MHz). The image carries NXP SDK 2.14 paths `devices/LPC54605/drivers/…` | Controller main MCU: USB (XUSB in PC mode, GIP in Xbox mode), Razer commands, inputs, Sensa decoders, motors | [DEV2_NXP_LPC54605.md](DEV2_NXP_LPC54605.md) |
| `Flash2FW` | Telink **B91** (RISC-V; `B91m_driver_sdk`) | Controller radio / audio SoC: 2.4 GHz, pairing, 3.5 mm jack | to do |
| `Dev1FW` | Telink **B91** | HyperSpeed dongle | to do |

## Reproduce

```bash
7z x -o<out> WolverineV3Pro_0A3F_FirmwareUpdater_v2.04.00_r1.exe
dotnet run --project tools/firmware/resdump -- <out>/DeviceUpdater.resources <res>
python -I tools/firmware/hex2bin.py <res>/Dev2FW.txt Dev2FW.bin
python -I tools/firmware/dev2_ram_init.py Dev2FW.bin ram_init.bin          # unicorn
python -I tools/firmware/fwmap_arm.py Dev2FW.bin map_dev2 ram_init.bin     # capstone
python -I tools/firmware/dev2_vectors.py Dev2FW.bin
```

`fwmap_arm.py` writes `functions.csv` (address, size, callers, callees, peripherals, strings), `coverage.json`
(every byte classified) and `summary.txt`.

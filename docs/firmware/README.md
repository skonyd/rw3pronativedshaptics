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
| `Flash2FW` | Telink **B91** (RISC-V) | Controller radio / audio SoC | [TELINK_B91.md](TELINK_B91.md) |
| `Dev1FW` | Telink **B91** | HyperSpeed dongle | [TELINK_B91.md](TELINK_B91.md) |

The [audio replay](AUDIO_HAPTICS_REPLAY.md) builds and runs an original Cortex-M4
DSP module with stock NXP routines in a controlled simulator. The resident
bootloader, actual peripheral behavior and wireless protocol remain open.

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

## Run the offline replay suite

Install Unicorn 2.1.4 in the Python environment and have Zig 0.15.2
available. Use the local stock images and the two original USBPcap
captures with independently verified 48 kHz stereo PCM16. Build the
simulator module with the stock-and-DSP build script, then run the suite.

```powershell
$lab = 'TestResults/firmware-lab'
& tools/firmware/build_audio_replay.ps1 -Stock "$lab/images/Dev2FW.bin" -Output "$lab/suite-build"
python -B tools/firmware/run_simulator_suite.py "$lab/images" "$lab/suite-build/audio_haptics.bin" "$lab/wired.pcap" "$lab/dongle.pcap" --active-stereo-excerpt 3000
```

The default USB addresses are 1 for wired and 9 for HyperSpeed. Pass
`--wired-usb-address` and `--dongle-usb-address` for other captures.
The stereo excerpt starts at slot 3000 in the lab captures. Omit
`--active-stereo-excerpt` or pass a verified active stereo index when
using different traces. The runner derives both NXP PCM inputs from the
supplied captures and rejects missing audio, the wrong slot width and
sequence gaps. It writes individual reports and a `summary.json` under
`$lab/suite`. Check both `complete` and `passed`. Firmware, packet data
and full local reports remain under ignored `TestResults/`.

The suite covers currently modeled paths, including the stock and
candidate NXP runs, Telink boot and audio, RF retry, controller SPI,
I²S, the voice encoder/RF/decoder/USB IN path, captured voice framing
with malformed controls, and the ADC-to-USB descriptor and completion
path. The [startup I²C replay](AUDIO_HAPTICS_REPLAY.md#register-driven-startup-i²c-peers)
checks stock configuration, transport failures and retry using explicit
synthetic peers and original interrupt handlers.
The [basic Cortex-M IRQ replay](CORTEX_M_IRQ.md) separately checks
memory-backed exception frames, mask/priority behavior and the original
startup handlers. It retains the direct vector-call regression.
Its bounded FP extension separately checks eager/lazy frames, nested
preservation and the original Sensa timer vectors. Shared NXP audio
Replay and core fixtures now assert an explicitly selected Cortex-M4;
see the IRQ report for the corrected Unicorn CPU selection.
The [DMA/DSP extension](DMA_DSP_PREEMPTION.md) adds original DMA/I²S/TX
delivery during the compiled worker and nested motor IRQs without a
host CPU-context restore. Its write-only register readback remains an
explicit fixture; that reference still uses host-assembled frames.
The new [compiled publication path](COMPILED_SENSA_PUBLICATION.md) packs
and publishes its own candidate Xbox frames with accepted-game ownership,
protected decoding and source-age checks. The decoder-only helper now
also explicitly selects M4. Other standalone legacy helpers are not
claimed to have changed CPU model by this increment.
The [stock-clock replay](STOCK_CLOCK_REPLAY.md) drives the publisher's
counter through original MRT stores and shares a virtual schedule with
the initialized motor timers. A masked-event test exposes counter lag;
the foreground safeguard is not an independent wall-clock watchdog.
Instruction time, full main-loop tasks and physical timing remain open.
The [WWDT/header stage](WATCHDOG_AND_LOOP_GATE.md) adds stock watchdog
setup, masked feed, warning IRQ and the main loop's counter gate. It
detects terminal reset requests and shows that an enabled warning ISR
can keep feeding WWDT during a foreground stall. Actual IRQ0 setup and
reset/recovery remain open; this is not an independent audio-only stop.
Voice framing is checked against every captured endpoint-0x87
packet, including host grouping and sequence wrap; its background-level
PCM is not a reference for the synthetic microphone tone.
The physical inter-chip audio route,
live timing, GPIO and analog board behavior, console authentication,
and the resident bootloader need further evidence.

The [latest 2026-11-22 validation summary](validation/20261122-watchdog.json)
preserves the 19-stage result, input/source hashes, unchanged module
hash and watchdog/header results with terminal reset guards. The
[clock summary](validation/20261122-clock.json) retains the 18-stage
result and separate silent-PCM motor references. The
[publication summary](validation/20261122-publication.json) retains the
17-stage run and three reproducible module builds. The
[DMA summary](validation/20261122-dma-preemption.json) retains the earlier
16-stage result and DMA/worker results. The [FP summary](validation/20261122-fpu.json)
retains the 15-stage result, core/FP contracts and six timer comparisons.
The [basic IRQ summary](validation/20261121-exceptions.json)
retains the earlier 14-stage result and its CPU-selection limitation.
The [startup summary](validation/20261121-startup.json) retains the
earlier 13-stage run and register-peer controls.
The [earlier voice summary](validation/20261121-voice.json)
preserves that 12-stage result and voice checks without
firmware images or raw packet payloads. Its explicit unverified fields
remain part of the result.

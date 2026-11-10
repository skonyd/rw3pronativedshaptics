# Custom firmware: console audio → Sensa (started 2026-11-11)

**Goal:** on an Xbox Series X|S, without a PC, the Wolverine V3 Pro turns the game audio it receives (headset
stream) into Sensa palm haptics, the way RW3's "Sesten titreşim" does on a PC.

**Why firmware:**
- The stock firmware does not do this (E-20261111-08/09).
- No third-party code runs on the console.
- The images are protected only by checksums, not signatures (E-20261111-10, FIRMWARE_ANALYSIS finding 5).

**Owner's decision (2026-11-11):** start the work with complete analysis, step by step. Every flash needs a separate,
explicit go from the user.

## Safety gates (nothing is flashed before gate G3)

| Gate | Condition to pass | Decided by |
| --- | --- | --- |
| G0 rollback known | How the bootloader is entered (software and by hand), what happens when the app image is broken, which flash regions an update writes and which it never touches (bootloaders) | analysis, then user |
| G1 rollback proven | The official updater reflashes the **original** v2.04.00 on this controller and it works afterwards. This is a flash, but of Razer's own image | user (explicit) |
| G2 patch proven offline | The patched image builds, CRCs/checksums are correct, the changed code passes the unicorn emulator with recorded inputs, and the diff touches only the planned areas | analysis |
| G3 first custom flash | G0–G2 done; the user agrees to the residual risk | user (explicit) |

Rules:
- No blind commands.
- Do not change bootloader regions.
- Keep a copy of the original images: they live only in the user's updater download and are never committed.
- Never distribute modified Razer firmware; the patch tooling may be shared, the images may not.

## Phases

0. **Rollback analysis** (static):
   - The updater's flow: bootloader entry, PIDs, erase ranges, verification.
   - The chip roles.
1. **Audio path analysis** (static):
   - Where GIP audio (message 0x60) arrives in wired mode, and in which format.
   - Whether the NXP haptics MCU sees it, or only the Telink radio/audio SoC.
   - The wireless (dongle) path.
2. **Design:**
   - A small audio → envelope/band analyser that writes the Sensa body-part structs (the same layout the Xbox
     decoder fills).
   - The hook points.
   - Free flash/RAM.
   - A switch to turn it on and off (e.g. a button combo or a Razer command).
3. **Build:**
   - arm-none-eabi toolchain.
   - A patcher that writes the new code into free space plus hooks, and recomputes the Intel HEX and the checksums.
   - Emulator tests.
4. **G1**, then **G3**, then hardware tests (palms and stability), always with the original image ready to reflash.

## Log

Findings go to [EXPERIMENTS.md](EXPERIMENTS.md) as usual. This page keeps the plan and the gate status.

| Date | Step | Result |
| --- | --- | --- |
| 2026-11-11 | Plan written | — |
| 2026-11-11 | Phase 0: updater decompiled (ILSpy, `WolverineV3ProCFU.exe`) | See the findings below. |

## Phase 0 findings so far (static)

- **Chip roles:**
  - `Dev1FW` (Telink B91, 2.00.03) = **dongle**. It is updated as `UpdateType.FLASH` when a dongle is detected.
  - `Dev2FW` (NXP, 2.04.00) = **controller main MCU**: USB/GIP device, Razer HID, Sensa decoders and motors. It is
    updated through its own bootloader (`UpdateType.Bootloader_110E`).
  - `Flash2FW` (Telink B91) = **controller radio / audio SoC**: pairing, 2.4 GHz, the 3.5 mm jack. It is updated
    as `UpdateType.FLASH` through the controller.
- **Update flow:**
  - The .NET updater extracts the Intel HEX lines to `FW.hex`.
  - The native `Controller_Update_Lib.upgrade_firmware_process` then does the update. `FWUpdaterDLL` exports:
    `OpenDevice`, `GetBootloaderHandle`, `GetDevPIDInBootloader`, `EnterDeviceMode`, `DFUErase(start, end)`,
    `DFUProgram`, `DFUVerify`, `DFUExit`, `DeviceReset`.
  - Verification is checksum only (`VerifyChecksum`, CRC16).
- **Not visible in the package:** the resident NXP bootloader (0x0000–0x7FFF) and the Telink boot area. What
  happens on a crash after boot is therefore unknown.
- **Safety design proposal (for phase 2): a dormant patch.**
  - The new code only hooks in after the stock firmware has booted and enumerated.
  - It stays off until it is switched on at run time (a Razer command or a button combo).
  - A bug in it then only triggers after an explicit switch-on, and a power cycle brings back the stock behaviour,
    so the normal update path stays usable for a reflash.
- **Audio path hint:** the NXP GIP metadata declares the headset interface with audio messages 0x60. In wired mode
  the console's headset audio therefore passes through the NXP MCU, the same chip that drives Sensa, so a
  wired-only first version may need just one chip patched. To be confirmed in phase 1.

## Open for G0

1. Disassemble `Controller_Update_Lib` (`Bootloader_110E` path):
   - the bootloader entry command;
   - the bootloader PID;
   - the exact `DFUErase` range, to prove that the bootloader area is never erased.
2. Find out whether the bootloader can be entered by hand (a button held at plug-in) when the app does not run.
   This needs the device or Razer support documentation.

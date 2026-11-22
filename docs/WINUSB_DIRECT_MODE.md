# Direct mode (WinUSB): RW3 drives the Wolverine without Windows' controller stack

Status (2026-11-22): **phases 1-4 implemented, not yet tested on hardware.** Nothing here changes a driver until the
user presses the button in Settings and confirms. Code: `GipCodec.cs`, `WinUsbGipTransport.cs`, `DirectModeDriver.cs`,
the direct branch in `XboxAudioGameBridge.cs` and `GipSensaTransport.cs`, Settings in `MainWindow.xaml(.cs)`.

## Why

In Xbox mode, while any game or controller app is in front, Windows drops RW3's background output to the pad:
GameInput, Windows.Gaming.Input, Razer's Sensa GIP messages (palms + Sensa trigger drive), all of them, on the cable
and on the wireless adapter (E-20261122-52…54). Only XInput body rumble passes. The pad's XInput HID child has no
output report (E-20261122-55) and the GIP driver's control device `\\.\XboxGIP` passes only the LED command
(E-20261122-57). So the focus gate lives in Windows' controller stack, and the only way around it is to take the pad
out of that stack.

The Wolverine in Xbox mode is a standard GIP USB device (E-20261122-58): class FF/47/D0, interface 0 with interrupt
OUT 0x01 / IN 0x81 (64-byte GIP messages), interface 1 = headset audio. With Microsoft's signed **WinUSB** driver on it,
RW3 talks GIP itself: reads the buttons, sends rumble with the trigger motors and the Sensa frames, with no focus gate.
Games keep seeing RW3's virtual Xbox Series pad (HIDMaestro), which RW3 feeds from the real pad's input.

## What changes for the user

| | Normal (dc1-controller) | Direct mode (WinUSB) |
|---|---|---|
| Games see | the Wolverine itself, or RW3's virtual pad | **only** RW3's virtual pad (RW3 must run) |
| Game rumble / triggers | from the game, or through RW3 | through RW3 (GIP 0x09, all four motors) |
| Palms (sound → Sensa) | silent while a game is in front | **work with the game in front** |
| Xbox button | Game Bar / Windows | RW3 (forwards it to the virtual pad) |
| Razer Synapse / Xbox Accessories | see the pad | do not see the pad |
| Without RW3 running | pad works everywhere | pad does nothing until direct mode is removed |

## How the driver is switched (no certificates, no test signing)

* **Install**: RW3 (already elevated for the virtual pad) selects the inbox, Microsoft-signed `winusb.inf`
  ("WinUsb Device") for the Wolverine's device instance, the way Device Manager does with *Update driver → Let me pick →
  Universal Serial Bus devices → WinUsb Device*. No INF of our own, no catalog, no certificate store change. It also
  writes `DeviceInterfaceGUIDs = {RW3 direct-mode GUID}` into the instance's device parameters first, so RW3 can find it.
* **Remove**: RW3 uninstalls the device instance's driver (and the remembered selection) and rescans. Windows then picks
  its best matching driver again, which is the inbox `dc1-controller` (Microsoft's Xbox controller driver). Nothing is
  deleted: WinUSB and dc1-controller are both inbox drivers.
* Both act only on `USB\VID_1532&PID_0A3F` instances (cable and, if it shows up the same way, the wireless adapter;
  each is its own instance and is switched separately). PC mode (`0A4C`) is never touched.
* Every step is written to RW3's log and to `%LOCALAPPDATA%\RW3ProNativeDSHaptics\direct-mode.json`
  (which instances were switched, when, their previous driver), so removal knows what to undo even after a reinstall
  of RW3.

## Before switching (either way)

* Close every app that holds controllers: **Ubisoft Connect, Steam, browsers, Xbox app, PowerA, test range, games**.
  Windows cannot change a device's driver while an app has it open; on 2026-11-22 Ubisoft Connect's `upc.exe` vetoed
  the switch (System log: Kernel-PnP event 225), Windows asked for a restart, and an RW3 bug then locked up Windows'
  device manager so that shutdown hung for 10 minutes (E-20261122-60).
* If RW3 reports "Windows yeniden başlatma istiyor", restart Windows. The switch (or the rollback) completes during the
  restart; the result message names the app that held the pad.
* RW3 never waits on Windows' device manager for more than 30 s and no longer reopens the pad while it is being
  switched.

## Settings button

*Ayarlar → Doğrudan mod (WinUSB)*

* **"Doğrudan modu kur"**: lists the Wolverine instances it will switch, asks for confirmation (what changes, see the
  table above), switches them, then tells the user to replug the pad. Disabled when the pad is in PC mode.
* **"Doğrudan modu kaldır"**: switches every recorded instance back, rescans, checks that the pad is on dc1-controller
  again and shows the result. Always enabled while anything is recorded.
* Status line: which instances are on WinUSB, whether RW3 has the GIP pipe open, input packets per second.

## Manual rollback (if RW3 is gone or broken)

1. Unplug the Wolverine (or switch it off).
2. **Device Manager** → *View → Show hidden devices* → **Universal Serial Bus devices** → *WinUsb Device*
   (or *Razer Wolverine V3 Pro for Xbox*) → right click → **Uninstall device** (do **not** tick "attempt to remove the
   driver": it is the inbox WinUSB).
3. Plug the pad in again. Windows installs *Xbox Game Controller* (dc1-controller) by itself.
4. Check: Device Manager → *Xbox Peripherals* → the pad is listed; the Xbox button opens Game Bar.

Command-line equivalent (elevated PowerShell), for the cable instance:

```powershell
pnputil /enum-devices /connected /class USBDevice | Select-String -Context 0,6 "VID_1532&PID_0A3F"
pnputil /remove-device "USB\VID_1532&PID_0A3F\<serial>"
pnputil /scan-devices
```

## Reinstall

Same button again. Switching back and forth is safe: both drivers are inbox, nothing is downloaded or signed.

## Phases

1. **GIP codec** (pure, contract-tested): framing (command, options, sequence, varint length), power-on, ACK,
   input report 0x20 → XInput-style pad state, guide 0x07, rumble 0x09 (four motors), Razer 0x0F Sensa packet.
2. **WinUSB transport**: find the instance by the direct-mode interface GUID, `WinUsb_Initialize`, read pipe 0x81 on its
   own thread, write 0x01; init sequence (power on, ACK what asks for it); reconnect on replug.
3. **Backend in the strict bridge**: when direct mode is active, input comes from the GIP pipe instead of XInput, body +
   trigger rumble and Sensa frames go out on the pipe; no focus gate.
4. **Driver switch + Settings UI + direct-mode.json**, then the hardware test with the user's consent (cable first,
   then the wireless adapter).

Open questions to settle on hardware: the GIP options byte Razer's 0x0F command needs (WGI sends it as
`GipMessageClass.Command`), whether the pad needs more than power-on to start reporting, and whether the wireless
adapter exposes the same USB GIP interface.

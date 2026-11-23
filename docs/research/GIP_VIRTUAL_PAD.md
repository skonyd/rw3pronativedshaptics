# A virtual pad that Windows drives as a wired Xbox controller (GIP)

Goal (user, 2026-11-23): with the Xbox Series output, a game's own trigger rumble (impulse triggers) should reach RW3
and the Wolverine's trigger motors, the way it reaches a real Xbox Series controller on a cable.

## Why the current virtual Series pad cannot get trigger rumble

* Measured: with the Xbox Series output the game's body rumble arrives, its trigger values are 0 every time
  (test range E-20261122-12, Banishers E-20261122-15 and, in direct mode, E-20261123-72: 270 of 270 windows).
* Every HIDMaestro Xbox One / Series / Elite profile is presented through `xinputhid`, the Bluetooth / HID path
  (`driverMode: xinputhid`, USB variants included). Its output report has the trigger fields, but Windows writes 0 into
  them. This matches Microsoft's own pads: impulse triggers work over USB and the Xbox Wireless Adapter (GIP), not over
  Bluetooth.
* So the trigger rumble only exists on the GIP path: Windows' Xbox driver (`dc1-controller` / `xboxgip`) talking to a
  USB device of class FF/47/D0, as it did with the Wolverine before direct mode (E-20261122-58).

## What HIDMaestro offers (read from HIDMaestro.Core.dll, no source)

* **usbip backend with any USB device:** profiles can carry byte-exact device / configuration descriptors and
  interfaces of any class, vendor class (0xFF) with interrupt or bulk endpoints included (`usbConfiguration`: e.g.
  `gamecube-adapter`, `switch2-pro-composite`, `dualsense-composite`).
* **A declarative device protocol** (`protocol`: `state`, `counters`, `commands` with `rules` that match host
  writes on an OUT endpoint and `reply` on an IN endpoint, `input` with a clock, `halt`, `getStatus`, `getReport`),
  used by the GameCube adapter (commands 0x11-0x17) and the Switch 2 Pro (bulk command protocol, flash reads).
* **Custom profiles:** `HMContext.LoadProfilesFromDirectory(path)`.
* **App side:** `HMController.OutputReceived` / `OutputDecoded` (host writes), `SubmitRawReport` (device input).
* No GIP profile ships; the strings `PacksGip` / `_gipBuf` belong to the xinputhid presentation (its output report
  carries the GIP rumble payload `[LT, RT, strong, weak, FF, 00, EB]`, E-20261122-12).

## The idea: a GIP proxy

```
game ─XInput/GameInput/WGI─▶ Windows Xbox driver (dc1-controller) ─GIP over virtual USB─▶ RW3 ─GIP over WinUSB─▶ Wolverine
                                                                   ◀── input 0x20 ─────────  RW3 ◀── input 0x20 ───────
```

RW3 presents a virtual USB device of class FF/47/D0 (the Wolverine's own descriptors, or a Series pad's 045E:0B12) and
plays the device side of GIP: announce, identify, ACKs, input reports. Windows binds its own Xbox driver to it, so games
see a genuine wired Xbox controller and send it everything a real one gets, trigger rumble included (GIP 0x09
`[00, 0F, LT, RT, L, R, …]`). RW3 receives those packets and forwards them to the real pad over the direct-mode pipe,
where there is no focus gate; palms (Sensa) keep coming from RW3 itself.

## Open questions, in the order to settle them

1. **Does Windows' Xbox driver bind to an emulated FF/47/D0 device and start GIP with it?** Prototype profile, no game:
   create the device, log every host write. Expected: Windows waits for the device's announce (0x02), then asks for
   identify (0x04), powers it on (0x05), sets the LED (0x0A).
2. **What must the identify reply contain?** The Wolverine's own identify data, read from the real pad over the WinUSB
   pipe (RW3 is the host there: send the identify request 0x04, reassemble the chunked reply). Read-only for the pad.
3. **Does Windows require GIP authentication (0x06) for a wired pad?** Unknown on this PC. Licensed pads carry an
   authentication chip for the console; whether Windows checks it is the main risk of the whole approach. If it does,
   the approach stops here (RW3 cannot and will not forge it).
4. **Can the declarative protocol leave the GIP handshake to the app** (every host write delivered to
   `OutputReceived`, replies sent with `SubmitRawReport`), or does the handshake have to be written as profile rules?
5. Then: input timing (the pad reports every 4 ms), the Xbox button (0x07), and the headset audio interface (leave out).

## Cost and risk

* Each prototype run creates and removes one virtual USB device. Many create/remove cycles once left Windows' game input
  broken until a restart (memory: GameInput stale after virtual pad churn): keep the prototype runs few.
* No driver is installed: Windows binds its inbox Xbox driver; the usbip transport is already installed for the
  virtual DualSense. Nothing touches the Wolverine's firmware.
* Outcome is uncertain until question 3 is answered.

## Results so far (E-20261123-74, -75)

* **Binding:** solved. Windows' Xbox driver takes Microsoft pads by hardware id and third-party pads by the Microsoft OS
  1.0 compatible id `XGIP10` (`USB\MS_COMP_XGIP10`); the clone (1532:0A3F, class FF/47/D0, GIP OUT 0x01 / IN 0x81,
  MS OS vendor code 0x90 as Windows cached for the real pad, a placeholder HID interface HIDMaestro insists on) is bound
  as "Xbox Oyun Cihazı". Host writes reach `OutputReceived` (GIP command as report id), `SubmitRawReport` reaches the
  GIP IN endpoint.
* **Identify:** the pad's identify data was read over the direct-mode pipe (260 bytes, asked before power-on, as Windows
  does). For 1532:0A3F Windows did not ask again (cached).
* **Authentication: required.** After power on and LED, Windows sends GIP 0x06 challenges (`00 41 00 01 00 2C 01 01 00 28`
  + 40 random bytes), retries, then resets the device. A stand-alone virtual pad stops here: RW3 cannot and will not
  forge that answer.

## Next: a transparent GIP proxy

RW3 relays Windows' GIP messages to the real Wolverine (direct-mode pipe) and the pad's messages back, authentication
included, so the genuine pad answers for itself, the way a USB hub passes them through. RW3 only reads the rumble
(0x09) on the way, to apply the user's choices (and keeps adding the palms). Needs: the pad in direct mode, both
directions forwarded with their sequence numbers and ACKs, and the pad's input forwarded to Windows instead of RW3's
HIDMaestro Xbox pad.

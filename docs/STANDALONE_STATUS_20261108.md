# Standalone status — night of 2026-11-08

Goal (user): the app installed alone does everything, without Synapse or any other app.

## Done and verified (webcam mic, Wolverine on USB cable, PC mode)

| Piece | Evidence |
| --- | --- |
| Sensa USB transport decoded (feature report 4, class 0x16/0x0E, 33 ms, XOR CRC) | USBPcap capture, 192/192 CRC, docs/SENSA_USB_PROTOCOL.md |
| Direct HID write (no Synapse, no admin, no driver) | captured frames → +19 / +17 dB L/R; silence frame stops the motors |
| Own frame encoder (3 bands × 4 points × (6-bit strength, 7-bit pitch)) | byte-exact for 1696 HAR frames incl. multiband; contract tests |
| Direct USB path in the engine (Standard / Hybrid / Multiband) | 60-case mic comparison: equal to Synapse; 45-case: Hybrid places 80+190 Hz at 80/190 Hz and keeps hits |
| Settings → Gelişmiş → Bağlantı yolu (Synapse / Doğrudan USB) | auto Direct USB when the Razer DLLs are absent |
| Synapse attachment check (warning when Synapse did not attach the controller) | RzInterHaptics.log, contract test |
| Virtual DualSense bridge (HIDMaestro): input, haptic PCM → engine, triggers → motors | builds; parser/interpreter contract-tested; **not run with a game yet** |

Contract suite: 412/412.

## Needs the user (could not be done unattended)

1. **Synapse fully closed test.** Closing Synapse was blocked for the agent. Close Synapse
   (tray icon → Exit), select Doğrudan USB, run Test → Sol/Sağ and a sound test.
2. **Wireless (dongle).** Unplug the cable with the dongle in, select Doğrudan USB, try
   Test → Sol/Sağ. The transport picks the dongle's control collection automatically; whether
   the dongle forwards frames is unknown.
3. **Virtual DualSense with a game.** Settings → Sanal DualSense → "Yönetici olarak başlat"
   (UAC), then a DualSense-native game. Check: buttons/sticks (Y axis direction is a guess),
   haptic block counter rising, trigger reports, double input (HidHide not configured).
4. **Blind hand test** Standard vs Hybrid on Direct USB.

## Known limits

- Transient records are not written (last 3 bits of the 24-bit record not understood); hits are
  folded into the strength points — measured at least as strong as Synapse's.
- Bands 2–3 come from the 85 ms spectrum (Hybrid/Multiband); Standard frames have no added delay.
- Xbox mode (impulse triggers) has no Sensa; trigger effects go to rumble motors in PC mode.

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

## Morning tests (user present)

- **Synapse fully closed** (user exited it; RazerAppEngine 0 for the whole run), USB cable:
  direct write left +10…+20 dB, right +10.5…+11.3 dB, silence stops; engine 45 cases
  (TestResults/direct-synapse-closed-20261108): Hybrid puts mixed 80+190 Hz at 80/190 Hz,
  hits 14.9 dB = Standard 15.0 dB, no tails. **Synapse is not needed.**
- **Wireless (HyperSpeed dongle only, cable out, Synapse closed):** the dongle exposes its own
  COL04 (`B&11DF2D84`); direct write left +21…+23 dB, right +17…+19 dB, silence stops — Synapse
  itself never drove Sensa over the dongle. Engine 45 cases (TestResults/direct-wireless-20261108):
  same picture (Hybrid mixed 80/190 = 5.1×/24.6× vs wrong bands 2.1×/2.1×, hits 12.1 dB, no tails).
  Writes over the dongle are a little slower (40–43 instead of 46 per 1.5 s); no backlog observed.

## Still needs the user

1. **Virtual DualSense with a game.** Settings → Sanal DualSense → "Yönetici olarak başlat"
   (UAC), then a DualSense-native game. Check: buttons/sticks (Y axis direction is a guess),
   haptic block counter rising, trigger reports, double input (HidHide not configured).
2. **Blind hand test** Standard vs Hybrid on Direct USB.

## Known limits

- Transient records are not written (last 3 bits of the 24-bit record not understood); hits are
  folded into the strength points — measured at least as strong as Synapse's.
- Bands 2–3 come from the 85 ms spectrum (Hybrid/Multiband); Standard frames have no added delay.
- Xbox mode (impulse triggers) has no Sensa; trigger effects go to rumble motors in PC mode.

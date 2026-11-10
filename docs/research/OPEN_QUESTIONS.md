# Open research questions

Each entry has a next concrete step and lists what is already ruled out. Close an entry by moving its outcome
to [EXPERIMENTS.md](EXPERIMENTS.md).

## Q1. Palms + trigger motors together (Xbox-mode 4-zone stream)

- **Why:** the firmware has a 4-body-part Sensa descriptor (palms + 249/149, probably the triggers), used only
  in Xbox mode.
- **Ruled out:**
  - PC-mode trigger motors through XInput, WGI or GameInput (E-20261108-03, E-20261110-09).
  - Razer HID in Xbox mode (E-20261110-02).
  - GIP headset audio (E-20261110-03).
  - A GameInput haptic endpoint, as shipped (E-20261110-12).
  - Firmware flashing (safety rule).
- **Done (2026-11-10):**
  - A third-party app gets the GIP provider.
  - Razer packets tunnel in GIP 0x0F/0x10, and our app can send them.
  - Sensa class 0x16 answers in Xbox mode and serves the 4-body-part descriptor (E-20261110-13..16).
- **Next step:** Q2 (frame layout), then, with the user's consent, a first Sensa frame write through the tunnel.
- **Details:** [XBOX_MODE_HAPTICS_ROUTE.md](XBOX_MODE_HAPTICS_ROUTE.md)

## Q2. Bit layout of the 4-body-part Sensa frame

- **Why:** needed before any frame could be sent through route A.
- **Known:** the PC-mode layout (2 hands × 3 bands × 4 points), see [SENSA_USB_PROTOCOL.md](../SENSA_USB_PROTOCOL.md).
- **Next step:** trace the Sensa decoder in `Dev2FW` from the class 0x16 handler (dispatcher 0x30524, class
  table at 0x3074C). Look at how the header and the body-part count change with the mode flag.

## Q3. Hum suppression hurts hits at low volume (-12 dB)

- **Known:** E-20261110-00. The user said "leave it for now".
- **Next step:** none planned.

## Q4. First hit after silence is ~4.5 dB weaker with transient records

- **Known:** E-20261110-01. The `101` tail bit is still not reproduced.
- **Next step:** compare HAR renders whose tail is `101` against `100`.

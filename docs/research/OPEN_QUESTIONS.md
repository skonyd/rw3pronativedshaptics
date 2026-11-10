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
- **Next step:** Q2 is solved. With the user's consent, write a first Sensa frame through the tunnel: a silence
  frame, then a short weak pulse on one body part at a time (216, 116, 249, 149), with the mic running.
- **Details:** [XBOX_MODE_HAPTICS_ROUTE.md](XBOX_MODE_HAPTICS_ROUTE.md)

## Q2. Bit layout of the 4-body-part Sensa frame (closed 2026-11-11)

- **Answer:** solved by emulating the firmware decoder (E-20261111-01). Layout in
  [SENSA_USB_PROTOCOL.md](../SENSA_USB_PROTOCOL.md), section "Xbox mode frame".
- **Still open:** what the raw pitch / strength values of body parts 249/149 do on the trigger motors. Answer on
  hardware with the first write test (Q1).

## Q3. Hum suppression hurts hits at low volume (-12 dB)

- **Known:** E-20261110-00. The user said "leave it for now".
- **Next step:** none planned.

## Q4. First hit after silence is ~4.5 dB weaker with transient records

- **Known:** E-20261110-01.
- **New 2026-11-11 (E-20261111-02):**
  - The old record layout shifted the right hand when the left hand had a hit. That bug is fixed, which may
    change this result.
  - The `101` "tail" is explained: it was the right hand's first bits.
- **Next step:** repeat the mic A/B for transient records with the fixed encoder.

# Research: reaching the 4-zone haptic stream in Xbox mode (started 2026-11-10)

**Goal:** palms (Sensa) and trigger motors driven together. The firmware has a 4-body-part Sensa descriptor
that is active only in Xbox mode ([FIRMWARE_ANALYSIS_20261110.md](../FIRMWARE_ANALYSIS_20261110.md)).
The open question is how a Windows program can send that stream to the controller.

## What we know

| Fact | Evidence | Source |
| --- | --- | --- |
| In Xbox mode the controller is a GIP device only: no Razer HID, Synapse does not see it | measured | E-20261110-02 |
| The firmware serves a 4-body-part descriptor in Xbox mode: palms 216/116 plus 249/149, 2 bands / 2 points | static | E-20261110-10 |
| GIP metadata declares two unknown interface GUIDs: `{AAE22FB7-E167-4908-9A40-FF9C687FA986}` and `{9776FF56-9BFD-4581-AD45-B645BBA526D6}` | static | E-20261110-11 |
| GIP metadata declares non-standard messages 0x0F (size 8) and 0x10 (size 16) | static | E-20261110-11 |
| In Xbox mode the firmware answers Razer packets through a send buffer tagged 0x10 | static | FIRMWARE_ANALYSIS, finding 3 |
| The only Xbox-mode audio endpoint is a 2-ch headset; no GameInput haptic registration exists | measured | E-20261110-12 |
| The GIP headset audio does not drive Sensa | measured + felt | E-20261110-03 |
| Razer's "Controller Setup for Xbox" (Microsoft Store) configures the V3 Pro in Xbox mode, vibration intensity included | doc | Razer support FAQ |

## Candidate routes

### Route A: GIP vendor messages via Windows.Gaming.Input.Custom (most promising)

Windows has a public API for this:
- `GameControllerFactoryManager.RegisterCustomFactoryForGipInterface(factory, interfaceGuid)` registers a
  custom controller factory.
- The factory receives a `GipGameControllerProvider` with `SendMessage(GipMessageClass, messageId, buffer)` and
  `SendReceiveMessage`.
- The custom controller gets `OnMessageReceived` for incoming messages.

Razer's own Xbox app almost certainly uses this with `{AAE22FB7-…}`.

Hypothesis: Razer's 90-byte packets travel in GIP message 0x10 (and/or 0x0F). Then the PC-mode Sensa command
(class 0x16, cmd 0x0E, frame) could work in Xbox mode too, as a 4-body-part frame.

Steps, safest first:
1. **Passive** (no output): a probe registers a custom factory for `{AAE22FB7-…}` and for `{9776FF56-…}`. It logs
   whether a provider arrives, plus `FirmwareVersionInfo`, `HardwareVersionInfo` and every
   `OnMessageReceived` (class, id, bytes). It sends nothing.
2. **Capture the reference:** the user installs Razer Controller Setup for Xbox (Microsoft Store; their
   decision). With the controller in Xbox mode on the cable, USBPcap records the app while it reads settings and
   changes vibration intensity. This gives the real message format: GIP header, message 0x0F/0x10 payload,
   and how a Razer packet is wrapped.
3. **Replay** only what the Razer app sent. First read-only commands, for example the 0x16/0x90 descriptor
   length, so we can check that the 4-part descriptor comes back. Then a vibration-intensity write, compared with
   the capture.
4. Only then, and with the user's consent, send a Sensa frame (0x16/0x0E) through the same wrapper. The bit
   layout for 4 body parts / 2 bands / 2 points is not known yet. The descriptor read in step 3 and the firmware
   decoder are the sources for it.

Risks:
- SendMessage on GIP is reported not to work for some devices (STATUS.md). That is not verified for this
  controller.
- A custom factory may need package identity or the right capability. Test in a plain desktop app first.

### Route B: GameInput haptics (v2.2+, `GameInputHapticInfo`)

GameInput maps up to 8 haptic locations to the channels of one WASAPI endpoint:
`GRIP_LEFT {08c707c2-66bb-406c-a84a-dfe085120a92}`, `GRIP_RIGHT {155a0b77-8bb2-40db-8690-b6d41126dfc1}`,
`TRIGGER_LEFT {8de4d896-5559-4081-86e5-1724cc07c6bc}`, `TRIGGER_RIGHT {ff0cb557-3af5-406b-8b0f-555a2d92a220}`.
These four match the firmware's 4 body parts exactly.

The device needs `HKLM\SOFTWARE\Microsoft\GameInput\Devices\<VID><PID><UsagePage><Usage>` with
`HapticSamplesPerSecond`, `HapticBitsPerSample` and `HapticLocations`, and an audio endpoint in the same
container. Sources:
- https://devdocs.xbox.com/build/core-features/common/input/hardware/input-hardware-haptics
- https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/structs/gameinputhapticinfo

Status: the Wolverine exposes no such endpoint (E-20261110-12), so this route needs Razer or Microsoft
support. Recheck after Windows, GameInput or Razer firmware updates: list the endpoints and that registry key.

### Route C: Razer HID in Xbox mode

Ruled out (E-20261110-02). The interface does not exist in Xbox mode.

### Not acceptable

- Flashing modified firmware, or changing the boot mode flag.
- Blind GIP vendor messages.

Both are against the AGENTS.md safety rules, and recovering a bricked controller is not possible.

## Progress, 2026-11-10 (route A)

Route A steps 1–3 are done, all with read-only commands:

1. **Provider:** Windows gives a third-party desktop app a `GipGameControllerProvider` for both unknown
   interfaces (E-20261110-13).
2. **Wrapper:** captured from Razer Controller Setup for Xbox. A Razer packet travels in GIP message **0x0F**
   (60 bytes: `[status, tx, 0, 0, 0, size, class, cmd, args…]`, XOR of bytes 2..57 at byte 58), and the answer
   comes back in **0x10** (E-20261110-14).
3. **Replay:** our `SendMessage(Command, 0x0F, …)` reaches the controller and it answers (E-20261110-15).
   - Sensa class 0x16 works in Xbox mode.
   - The descriptor read back is the **4-body-part** one: palms + 249/149 (E-20261110-16).
   - Limitation: Windows does not deliver the 0x10 answers to our sink, and `SendReceiveMessage` is not
     supported. Sensa frames are fire-and-forget writes, so streaming would not need answers.

Tools: [`tools/gip/`](../../tools/gip/README.md).

## Next step

Step 4 is a **write**: a Sensa frame (0x16/0x0E) through the same wrapper. It needs:
1. The **4-body-part frame layout**. The PC layout is 2 hands × 3 bands × 4 points. The Xbox descriptor says
   4 body parts × 2 bands × 2 points. The bit order and header are unknown; trace the decoder in `Dev2FW`
   (OPEN_QUESTIONS Q2).
2. A **size check:** a GIP 0x0F packet carries at most ~47 data bytes, and a PC frame is 41–47 bytes.
3. The user's **consent**. Start with a silence frame and a short, weak pulse on one body part, with the mic
   running.


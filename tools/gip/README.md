# GIP probes (Xbox mode, 1532:0A3F)

Both are plain desktop apps (.NET 10, Windows SDK projection). They show a small top-most window, because GIP
delivers to the foreground app. Each writes `result.txt` next to the exe.

| Probe | Sends to the controller | What it does |
| --- | --- | --- |
| `GipPassive` | nothing | Registers custom factories for the two unknown interfaces in the firmware metadata. It logs whether Windows hands over a `GipGameControllerProvider` and any messages that arrive. |
| `GipRazerRead` | read commands only (allow list in code) | Wraps a Razer packet in GIP message 0x0F, the way Razer Controller Setup for Xbox does, and sends it with `SendMessage`. |

Arguments for `GipRazerRead`: `class:cmd:size[:argshex]` in hex, for example `16:90:04` (Sensa descriptor
length) or `16:91:03:000030` (48 bytes from offset 0).

Responses come back as GIP message 0x10, but Windows does not deliver them to the custom sink, and
`SendReceiveMessage` returns 0x80070032 (not supported). Read them from a USBPcap capture instead.

Packet format: 60 bytes `[status, tx, 0, 0, 0, size, class, cmd, args…]`, XOR of bytes 2..57 at byte 58,
byte 59 = 0. The data area therefore ends at byte 57: ask for at most 47 bytes per 0x91 read.

Rules (AGENTS.md, `docs/research/README.md`):
- Extend the allow list only with commands that Razer software itself was seen sending.
- Any write needs the user's consent and a row in `docs/research/EXPERIMENTS.md`.

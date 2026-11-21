# Compiled Xbox Sensa publication

Status: **prototype, simulator only**, 2026-11-22. No controller access or
flashing. This extends [DMA/DSP preemption](DMA_DSP_PREEMPTION.md).
The C module now constructs and publishes its own candidate Sensa frames;
the harness no longer writes those frames for this new path. Older
host-built references remain separate regression tests.

## Build and run

Requires Zig 0.15.2 and Unicorn 2.1.4. Keep the locally extracted stock
image and captures outside Git, as before.

```powershell
tools/firmware/build_audio_replay.ps1 -Stock <Dev2FW.bin> -Zig <zig.exe> -Output TestResults/firmware-lab/publication
python -B tools/firmware/replay_nxp_publication.py <Dev2FW.bin> TestResults/firmware-lab/publication/audio_haptics.bin
```

The full suite's `--active-stereo-excerpt 3000` additionally supplies
96-slot windows from each verified wired/dongle PCM trace to this stage.
The original payload bytes are preserved; the fixture regenerates GIP
sequence numbers and event times. These are previously recorded headset
audio payloads, not proof of a game-authored DualSense source.

## Compiled path

The TX tap and DSP worker are unchanged in purpose. New original C code
in `audio_haptics_publish.c` packs the four palm-band strengths into the
established Xbox bitstream: header `0x3C`, two possible bands per palm,
fixed pitch codes 17/55, no transients and empty trigger parts. The stock
decoder expands these into 79/190 Hz bands with strength scaled by 520.
This remains a lossy two-band transformation, not raw PCM or preserved
waveform phase.

The module zeroes the complete 64-byte staging buffer, rejects strengths
above 63, and returns the actual encoded length. An invalid encoding in
the publisher is diagnosed, resets private DSP state and prepares a stop
frame instead of retaining its previous audio output.

Preparation is interruptible and touches private state. Commit saves
PRIMASK, masks external interrupts, rechecks ownership/mode/source age,
copies the complete packet, uses a memory barrier, publishes the stock
pending flag, calls the original Sensa dispatcher, and restores the
caller's PRIMASK. The mask covers stock decoding as well as the copy,
so maskable producers cannot replace the packet while it is being read.
Motor IRQs queued there run after the original mask is restored.

Only Xbox frame mode is implemented. PC/unknown mode attempts are
diagnosed without silently sending an Xbox frame to the PC decoder.
Transitioning an already active physical controller between modes is
not validated by this test.

## Game ownership

An emulator-only branch redirect at `0x30F36` replaces the original
accepted-command `MOVS/STRB` pair with a compiled notice. The original
class `0x16/0x0E` parser, rate gate and frame copy still run. The notice
counts the accepted command, latches game ownership, then performs the
original flag write. Original stock RAM and command response behavior
are compared with the unhooked handler for accepted, rate-rejected,
bad-marker and oversized commands.

A game command arriving during DSP/packing wins at the commit recheck.
One arriving during the protected copy/decode waits, then publishes its
own complete packet and is consumed on the next foreground call. A game
packet already pending, including an unknown stock producer, also wins.
Once a game owns output, audio cannot silently resume over a continuing
effect merely because the pending flag has cleared. Even a game packet
identical to the previous audio packet acquires ownership.

An explicit replay control requests rearm. It clears the ownership latch
and discards stale mailbox/staging data; a fresh PCM block is required
before audio takes over. Disabling audio while a game owns Sensa does not
publish an audio stop packet over that game effect.

The command-input fixture is a small original IRQ shim calling the real
class handler with a synthetic packet. It replaces one vector only in
emulated memory. It is **not** the complete USB IRQ/console handshake or
an actual game. Stock short commands overwrite only their copied prefix;
the unused tail can remain from an earlier packet. The oracle checks
that exact prefix and retained tail, rather than normalizing game bytes.
Injected corruption after either audio or game publication must fail.

## Cadence, source loss and busy state

Default audio publication spacing is 12 increments of the stock counter
at `0x200139F4`. The private period can be varied by a replay input. Rapid
foreground polling without new PCM holds output; it does not immediately
reset the filter and send silence on every empty poll.

After 250 counter increments without a new processed block, the running
foreground code sends one stop frame for its own audio output. Disable,
silent-source reset and invalid encoding need not wait for the normal
publication period. All publications, including stops, still wait for
the stock decoder's busy flags to clear. The test checks that a blocked
stop is not falsely counted as completed. Source age is checked again at commit: a long
interruption during preparation must not let expired audio start later.
The earlier module fails that regression; the fixed module passes it.
Unsigned counter wrap is tested.

These are **counter ticks**, not measured milliseconds. This is a
foreground safeguard, not an independent hardware watchdog if that
foreground stops running. The counter's physical cadence and sustainable
worker/decoder schedule still need validation.

A fresh prepared packet survives the stock decoder's busy flags. If it
expires before busy clears, it is discarded rather than replayed late.
The one-slot PCM tap still skips a new haptic copy while busy; byte-exact
audio playback does not establish zero haptic-input loss.

## Checks and timing boundary

- 8,464 encoder cases compare every emitted byte and the zero tail to an
  independent Python encoder. Pairwise 0–63 strengths, corner combinations
  and deterministic mixed cases are covered; this is not all 64^4 inputs.
- 32 representative packets also run through the original stock decoder,
  checking valid time/pitch/amplitude triples. Twelve invalid-strength
  inputs must return failure without crossing output guards.
- Control cases cover cadence/wrap, caller masks, fresh/expired busy
  staging, unsupported modes, game latching, rearm, retained rate gating
  and identical game/audio payloads.
- Injected game/motor IRQs arrive during encoding, copy, flag publication
  and decode. Separate control races disable output, corrupt a strength,
  change mode or advance the counter past expiry during preparation.
- Synthetic and recorded PCM streams pass through original DMA/I²S/TX,
  compiled DSP/packing/commit, original decoding and stock PWM writes.
  Opposite trigger outputs remain inactive in these audio-only scenes.

Masked instruction-hook observations are reported. They are **not**
physical cycles, microseconds, interrupt latency or worst-case execution
time. The critical section includes the stock decoder and can be much
longer than the packet copy alone; hardware suitability is still open.
The build uses shared typed interfaces, compile-time state-size checks
and `-Wall -Wextra -Werror`. Independent output/cache directories produce
the same module bytes; the tested module hash is retained in validation.

## Allocation and remaining work

The simulator maps an 8 KiB module at `0x00100000`; publication, encoder
and game notice entries are `+0xC00`, `+0x1000` and `+0x1800`. Private
publication state/staging is 128 bytes at `0x20024200`. Reset tests clear
this region under both dirty-memory patterns and keep stack/data checks
separate. These are explicit **simulator allocations**, not proven free
flash/RAM on the actual controller or a loadable firmware image.

The selector, runtime enable, period and rearm controls are replay memory
inputs. A real USB command for configuring this module has not been
integrated into the controller parser. Full loop/clock closure and
worst-case masked timing remain open. Ownership currently observes
class-`0x16` Sensa; coexistence with separate GIP normal-rumble/trigger
commands is not established by this latch or by inactive-trigger tests.
Resident bootloader acceptance, physical recovery, the real second I²C
peer, RF acquisition and actuator fidelity remain separate prerequisites.

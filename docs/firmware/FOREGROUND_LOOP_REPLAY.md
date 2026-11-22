# Foreground iterations and explicit GPIO inputs

Status: **prototype, bounded fixture**, 2026-11-22. No hardware access
or firmware flashing. This extends [the watchdog/header stage](WATCHDOG_AND_LOOP_GATE.md)
with the same unchanged original compiled module (`f2c5f153...`).

## What changed

Earlier audio tests called the original Sensa callsite directly. The new
`LoopLab` resumes original code at `0x2F100`, executes an entire iteration
in the selected fixture, and returns to that same boundary. The original
loop reaches its own `0x2F5FA` call and original `0x2B538` dispatcher;
the host no longer schedules that dispatcher directly in this path.
Stock tick tasks, digital scanning, background checks, watchdog feeds
and the compiled publisher execute along the selected branches.

The lab supplies prologue registers, a stack boundary and initialized
audio/timer state. It sets raw runtime fields `0x200019C0=1` and
`0x2000A538=1`. Their complete production meanings are not established;
the script's `connection`/`gate` parameter names are fixture labels,
not verified controller-mode or connection detection. This is not
successful boot into every production main-loop configuration.

For the three other zero/one combinations, execution reaches an
unmodeled FLEXCOMM3 I²C configuration read at `0x10E5E`, address
`0x40089800`. Each case stops there. They are failure boundaries,
not three additional successful full-loop cases. Existing startup I²C
models still need integration into this later foreground state in this
frozen stage. The later [I2C extension](FOREGROUND_I2C_REPLAY.md) separately
completes (1,0) and moves both (0,*) states to the next FLEXCOMM9 boundary;
it does not relabel these older missing-model controls as successful loops.

The real FLEXCOMM3 vector is restored rather than using the older
synthetic class-16 IRQ17 shim as a device source. No real USB interrupt,
authentication or new game-feedback source is claimed by this stage.

## GPIO contract

`nxp_gpio_inputs.py` supplies only explicitly declared byte pin inputs
at `GPIO.B[port][pin]`. Undefined pins, word aliases, writes/output
configuration and invalid levels fail. It does not model pin mux,
clock gating, electrical behavior, output latches or GPIO interrupts. The contract
comes from NXP's [LPC54605 GPIO register header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h).

The original scanner at `0x32128` packs 29 inputs into the raw word at
`0x20000F68`. A separate static load/shift map is checked against actual
execution for 63 scenes: all high/low, each pin independently low and
32 deterministic mixed patterns. Bits 10/11 are forced high by the
original code. Physical A/B/D-pad labels, polarity at the board switch,
debounce acceptance and final game input reports are not established by
this raw-word test. Pin P4_9 is separately supplied low for background
checks in the selected fixture; no default value is invented for it.

During complete foreground iterations a separate MMIO guard permits
only these defined GPIO reads and modeled watchdog feed writes. Other
foreground peripheral accesses fail. Nested modeled IRQ accesses are
handled by their existing peripheral models. Unknown mapped registers
therefore cannot silently become a passing all-zero foreground device.

## Comparison and audio path

The stock variant restores original tap, foreground and game-notice
instructions in Unicorn; no stock file on disk is patched. With the
candidate inactive, 32 iterations per variant compare 81,192 stock RAM
bytes, persistent registers, PWM and watchdog feeds. Sixteen changed
counter passes execute the original countdown/digital scan once each;
16 extra polls take the background path. Both variants dispatch Sensa
32 times and finish a seeded four-count stock task.

The DMA test continues to invoke the original USB receive function at
iteration boundaries with supplied packet events, then follows stock
descriptors and original completion IRQs. The tap, DSP and encoder run
as before, but foreground processing now occurs inside actual loop
iterations. This is not delivery through a fully modeled USB0 receive
interrupt or host bus. Clock events remain supplied; CPU instruction
time is not charged to the virtual clocks.
The harness requests one iteration per supplied DMA completion, with
extra rapid polls in the separate idle comparison. A continuously
scheduled whole CPU is not implemented by this stepping interface.

Stereo synthetic PCM, independent left/right sources and the earlier
wired/dongle capture windows are tested against a stock idle trace.
Selected outputs must differ from idle, opposite outputs in the mono
cases must match idle, triggers must remain inactive, and final eight
snapshots must return to idle. The inherited runtime packet oracle
checks the complete compiled frame at each stock decoder entry.
No host-built candidate Sensa frames are used.

Capture payloads are historical headset-audio bytes; event scheduling
and GIP sequence are regenerated by the fixture. These recordings are
not game-authored DualSense haptic proof. DSP remains a lossy two-band
mapping and this test does not validate actuator fidelity.

## Execution boundaries

The core stays at the same stack/loop boundary between iterations.
The test does not restore an opaque CPU context after foreground work.
External receive calls still use the explicitly supplied boundary-call
fixture. Their ingress and timing limitations remain separate.

Unicorn translations are flushed when changing `until` boundaries.
Otherwise a previously translated block can run past a newly selected
interior stop address. Observer hooks use a distinct method name so the
inherited full-frame oracle remains attached exactly once. Call counts,
end PC, stack and the stock/candidate comparison check this behavior.

Next integrate the later foreground I²C state for the other gate
combinations, identify those flags and IRQ0 configuration, connect
analog/button processing to full game reports, and close instruction,
peripheral and reset timing. Full-board boot, runtime controls,
independent audio-stop safety, bootloader acceptance and recovery remain
open. No loadable firmware image follows from these tests.

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_loop.py <Dev2FW.bin> <audio_haptics.bin>
# Add both --wired-pcm-trace and --dongle-pcm-trace for capture windows.
```

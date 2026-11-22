# Original RGB serialization and FLEXCOMM9 SPI/DMA

Status: **prototype, bounded offline replay**, 2026-11-23. No hardware
access or firmware flashing. The DSP module remains `f2c5f153...`; this
extends the simulator, not its haptic tuning or loadable-image support.

## The previously missing path

The two raw-gate states that stopped at `0x20672 / 0x4009AE00` reach the
original SPI DMA transfer routine through the RGB serializer `0x2B104`.
Its input bytes become three-bit symbols and a leading zero byte. This
is a concrete RGB-use path; it does not identify every FLEXCOMM9 use as
the runtime radio link. The earlier paired radio control replay uses
separate FLEXCOMM5. PCB routing and the physical RGB peer are unverified.

`replay_nxp_rgb_spi.py` executes original routines:

- `0x137C4`: global DMA initialization, register reset, descriptor-table
  address and controller enable. The older audio boundary fixture did
  not execute this global initializer or require CTRL enable for its
  supplied descriptor copies.
- `0x13D6C`: reconstruct the existing I²S handles after that reset,
  rather than retaining their pre-reset register state.
- `0x140A0`: SPI clock/pin setup, eight-bit master configuration,
  original DMA-handle/callback registration and the initial 110-byte job.
- `0x2B104`: RGB conversion and actual SPI DMA submission.
- Original DMA IRQ1, RX callback `0x20A40`, TX callback `0x20BFC` and
  application callback `0x20A18`. Core/FP exception returns remain checked;
  the inherited IRQ priorities are test fixtures, not measured board priorities.

The new reset hook covers idle DMA register reset only. Reset during
an armed/pending transfer fails explicitly; it is not a full board,
clock-domain or peripheral-reset simulator. Buffer/handle addresses
and the prologue are still supplied initialized-state fixtures.

## FIFO and descriptor contract

`nxp_spi_fifo.py` models the exercised master subset, read-only SPI
presence and FIFO-size fields, control-word retention, FIFO levels,
self-clearing empty commands and explicit full-duplex byte clocks.
No input byte is invented when MISO data or a FIFO entry is absent.
Unmodeled offsets, widths, modes and FIFO overflow/underflow fail.
Electrical fault behavior and error-flag generation are outside this subset.

The SPI FIFO has **eight entries**, with SIZE=`1`. NXP's UM10912 rev 2.4,
SPI FIFO configuration table on PDF page 417, distinguishes this from
the USART-sized description copied into the SDK header. Sources:
[NXP LPC54605 register header](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h),
[NXP-authored UM10912 rev 2.4 mirror](https://www.zlgmcu.com/data/upload/file/Utilitymcu/UM10912-LPC546xx%20User%20manual.pdf#page=417),
and [NXP SPI control/data feature description](https://www.nxp.com/docs/en/data-sheet/LPC546XX.pdf#page=72).
The local manual hash is retained in the earlier watchdog research.

The original engine uses RX channel 22 and TX channel 23. A normal TX
job uses a byte descriptor followed by a one-word terminal descriptor
that carries the final byte and end-of-transfer control. Single-byte
jobs also work. RX receives the exact supplied bytes. The harness
checks valid bits, field widths, increments, terminal flags, FIFO
addresses, counts, RAM bounds, descriptor links and cycle absence.
Only exercised buffer locations and transfer forms are accepted. The
receive checks cover eight-bit payloads, not full receive-side status
metadata. DMA INTENCLR retains the earlier explicit zero-readback fixture;
its physical write-only-register readback is still unknown.

No application completion is accepted before both DMA completions.
Tests alternate RX-first and TX-first order, verify the ready callback,
and check the original 1,200-byte staging-buffer clear. The independent
RGB oracle builds a bit string (`0→100`, `1→110`) instead of sharing the
original shift/OR implementation. It checks every byte value in every
RGB component, plus lengths, incomplete triplets and the 300-byte clamp.

## A stock error-propagation gap

Given explicitly supplied DMA ERRINT flags, the original RX/TX callbacks
receive transfer-done=`0`. After both callbacks, they still pass status
`0` to the application, set its ready flag and clear its staging buffer.
Both completion orders do this with **zero FIFO clocks** in the fixture.
This is CPU/interrupt-status evidence, not a simulated physical bus fault.
The lab name `READY` may mean availability for the next job; its full
production semantics are not established. This observation does not
prove unsafe hardware or a violated vendor delivery contract.

Consequently, a callback, ready flag or successful API return alone is
not digital delivery proof. The replay separately requires descriptor,
wire-byte and independent-codec evidence. A future custom firmware must
preserve DMA errors, define abort/retry behavior and avoid reporting
delivery based on this ready flag. That firmware work is still open;
the stock code is not silently patched to hide the observed behavior.

## Foreground and audio coexistence

The new stage compares stock/inactive-candidate RAM, persistent
registers, PWM, SPI bytes, I²C commands and watchdog feeds across all
four initial raw `connection/gate` combinations. Their physical meanings
remain unknown. Earlier missing-I²C/SPI boundary tests remain unchanged
in their own stages rather than being relabeled as completed production boot.

Long audio fixtures explicitly supply GPIO P3_17 low and P0_23 low/high.
Stereo and independent L/R PCM run through original USB receive calls,
I²S DMA, actual main-loop Sensa dispatch and the compiled publisher.
The formerly separate `(1,0)` I²C gate also receives active PCM here.
Motor activity/drain is checked against a separate inactive reference;
mono scenes must keep the other palm and trigger outputs at idle.

Five extra RGB requests are supplied at iteration boundaries through
the original serializer. If a stock RGB job is already armed, the
fixture queues the request until the next available boundary. This
explicit host scheduling is not a real USB RGB-command interrupt or
a production command arbiter. RX/TX SPI completion bits are delivered
alongside the I²S completion to the same original DMA IRQ. These tests
check shared-handler coexistence and byte preservation, not real bus timing.

With P3_17 high, both supplied P0_23 levels reach an unmodeled GPIO
write at `0xD7F8 / 0x4008C028` after 19 complete iterations when USB events
are present. Both runs stop and are **not** full-run successes. That
output and its calibrated busy wait are followed by the later
[GPIO extension](GPIO_BUSYWAIT_REPLAY.md). This original stage retains
its boundary controls. The falling-input I2C path, board pin meanings
and physical timing remain open; the selected low-input run cannot prove them.

## Validation

[The public summary](validation/20261123-rgb-spi.json) records 22/22
passing stages and identical preceding 21 reports. The complete new
stage also has identical normal/optimized output, without removable
asserts in its guard logic. It includes eight register checks, 25
rejections, 780 independent RGB-oracle cases and 64 idle iterations
across the four initial gate states. Each inactive comparison covers
81,192 stock RAM bytes, persistent registers, PWM and transactions.

Five active audio cases run 795 iterations, 41 shared SPI/I2S IRQ
batches, 11,395 checked IRQ returns and 45 independent runtime Sensa
frame checks. All drain to the separate stock-idle reference. The
extra RGB request at iteration 120 waits until 121 when the stock
RGB job already owns the engine; this is a supplied logical boundary
delay, not measured transport latency. Stock error-ready behavior and
both unmodeled high-GPIO branches are retained explicitly.

## Reproduce and limits

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_rgb_spi.py <Dev2FW.bin> <audio_haptics.bin>
```

The standard suite includes this as a separate stage. Reports retain
scope, unsupported boundaries, independent-oracle results and original
error behavior. No SPI electrical timing, physical RGB response, full
CPU/event schedule, whole-device equivalence or flashable image is
established. Bootloader acceptance, recovery and independent audio-stop
safety remain separate open requirements.

# Original calibration wait time and supplied DMA arrivals

2026-11-28. **Bounded offline replay; untested on hardware.** Stock NXP
v2.04.00 and the simulator-only DSP module are unchanged. No controller
access, firmware upload, driver or service changes.

## Clock scope

The [unified ADC stage](ADC_DUAL_SEQUENCE_REPLAY.md) executes calibration
waits without advancing peripheral time inside them. This separate stage
adds an instruction-clock budget to original `0x1FF70`, its original
64-bit division/shift helpers and the IRQs that actually interrupt it.
MRT, the existing motor timers and WWDT advance from those observed
instructions. Original code still performs the delay and status checks;
the host does not skip the wait or set firmware success flags.

The core and bus clocks are supplied as 96 MHz. The original ADC code
passes a separate literal 180 MHz argument to this delay helper. Both
values are retained rather than silently making them agree. Supplied
CPI budgets 1/3/7 are sensitivity tests, not measured instruction cycles,
physical microseconds or motor latency. Clock changes are rejected.
IRQ entry/unstack bus costs, memory wait states and arbitrary CPU work
outside the two tracked wait helpers are not fully timed.

`0xD620` keeps its earlier clock implementation. Both helpers share the
same fractional bus/WDCLK phase and reject overlapping charging. This
matters at a boundary: original `0x1FF70(11, 180000000)` followed by
`0xD620(1)` uses **9360 supplied bus cycles** and must produce **78 WDCLK
cycles**. Independently rounding the two helpers yields **77**. The
new mixed-helper test checks an independent whole-interval division
oracle and retains that discriminating negative control. Every calibration
record also checks fractional-clock conservation.

## Original waits and failure boundaries

Nine profiles cover zero duration, the smallest nonzero loop, 96/180 MHz
arguments, original 1/11/121 arguments and a 1000 argument under CPI1/3/7.
The independent loop oracle is `floor(argument * clock_argument / 4000000)`
for nonzero duration. The zero-duration branch has no loop. All original
foreground PCs are counted and hashed. The three 1000 profiles execute
the same 136,605 foreground instructions and 45,000 loop iterations;
their handler work and supplied elapsed time differ with CPI.

An intentionally invalid tiny-clock case has zero quotient with nonzero
duration. The original decrement-before-compare loop wraps instead of
returning promptly; the bounded run fails its instruction limit and never
creates a successful completion record. An excessive product reaches
the original assertion branch, outside this modeled helper subset, and
fails explicitly. Neither case is evidence of a problem in the normal
ADC call arguments.

Fifteen negative controls cover invalid CPI, arguments/events, duplicate
events, missing core clock, clock transition, shortened loop evidence,
double charging, the zero-quotient limit and the assertion boundary.
An additional supplied near-expiry WWDT counter reaches a terminal reset
after eight WDCLK cycles during the wait. It does not pretend to execute
reset, bootloader recovery or a physical PWM stop.

## IRQ masking and DMA completion

The masked 1000/CPI3 wait produces 34 MRT expirations and 33 coalesced
events. The original counter remains zero. After unmasking, the original
MRT ISR acknowledges one flag and advances its phase once; it does not
invent 34 serviced events. This preserves the reason an independent
elapsed-time motor-stop source is still required.

Four supplied DMA scenes cover RX→TX, TX→RX, both pending while masked,
and arrivals while MRT/DMA handlers execute. The original RGB descriptor
and FIFO fixture exchanges its exact initial 110 bytes before the wait.
Scheduled completion states are then delivered on modeled bus-cycle
deadlines; actual IRQ1, driver callbacks and W1C acknowledgment execute.
The application callback occurs exactly once after the paired completion,
retiring its context/buffer. It remains absent during masking until
the original pending handlers are allowed to run.

In the overlap scene the first completion arrives at cycle 12030 inside
MRT IRQ9 (IPSR25); DMA IRQ1 preempts it with the original nested exception
return. The second completion arrives at cycle 12060 inside DMA IRQ1
(IPSR17). Delivery is quantized to the supplied instruction budget;
deadline/actual cycle, PC and IPSR are retained. These are supplied DMA
completion states, not a bus model, FIFO pacing measurement or actual
USB host arrivals.

## ADC and audio integration

Four full original ADC initializations cover calibration, already-calibrated
dummy conversion, a late completion/retry and four failed attempts. Their
original return values are unchanged while the wait/IRQ time now advances.
ADC readiness itself remains supplied by status-read counts. Returning
from setup after failed calibration still does not establish ADC health.

One stock/disabled-candidate pair and one active stereo scene execute
127 complete foreground iterations each, carrying 64 original stereo PCM
blocks and a drain tail. The original B reader is called at supplied
foreground boundaries 32/33: eight invalid polls retain 3200 and READY0,
full power reinitialization executes timed calibration waits, and a new
valid 3450 then sets READY1. This is a deliberate test injection, not a
claim that the autonomous B task ran earlier than its normal schedule.

The disabled pair compares 81,192 shared RAM bytes, persistent registers,
PWM, A/B reads, power/I2C transactions and calibration traces. Active
stereo checks source PCM preservation, original Sensa dispatch and
independently decoded frames, positive palm outputs, idle triggers and
final return to its own stock-idle reference. The active run checks 127
foreground iterations, 63 A scans, 2271 exception returns, nine combined
SPI/I2S batches, eight Sensa frames and five original calibration waits.
Exact counts and normal/optimized equality are in the
[public validation](validation/20261128-calibration-time.json).
Earlier stage reports remain unchanged and are reused only after
input/source/import/runtime/report-hash audits.

## Remaining work

Extend supplied peripheral events into actual USB receive/descriptor
arrivals while CPU waits or stalls. Advance all relevant foreground and
handler work without double charging; keep masked/coalesced events honest.
Then validate an independent elapsed-time output stop and arbitration.
This increment does **not** implement that stop or a flashable candidate.

Physical ADC readiness, PLL/oscillator/pin behavior, full board boot,
radio timing, Xbox authentication, bootloader image acceptance and safe
hardware recovery remain unverified. Complete device equivalence is false.

```powershell
$env:PYTHONPATH = 'TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_calibration_time.py `
  TestResults/firmware-lab/images/Dev2FW.bin `
  TestResults/firmware-lab/publication-final-20261122/audio_haptics.bin
# Repeat with -O and compare complete parsed JSON reports.
```

Suite stage: `nxp-calibration-time`. Firmware/capture binaries remain local.

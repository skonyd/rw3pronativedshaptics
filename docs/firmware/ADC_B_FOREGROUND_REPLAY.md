# Original ADC-B sampling and foreground averaging

2026-11-28. **Working in bounded offline fixtures; untested on hardware.**
NXP v2.04.00 and the existing simulator-only DSP module remain unchanged.
No controller access, firmware upload, driver change or SDK installation.

Continuation: [ADC_RECOVERY_REPLAY.md](ADC_RECOVERY_REPLAY.md) executes the
power fallback and complete original initializer in a separate strict model.
This report and its tests retain their earlier, narrower boundaries.

## Original code and supplied state

The previous [I2C/release stage](I2C_RELEASE_REPLAY.md) stopped at ADC0
`SEQ_CTRL[1]`, `0x400A000C`, in both longer ACK and active-audio NACK runs.
This extension supplies a strict sequence-B/channel-8 model. The original
`0x93AC` setter receives the configuration bytes from the stock `0x90E0`
initializer. Its `0x924E -> 0x9256` enable fragment sets sequence B active.
The checked result is `0xD0040100`. Full calibration, pin mux, ADC clock
setup and the parent's steady-state RAM/prologue are still separate limits.

The original `0x9FA4` routine triggers conversion, calls the real `0xD620`
busy wait, polls through `0x8FE0`, and publishes a result only when
DATA_VALID is set. These original ARM instructions execute; the host does
not patch the polling loop or write its completion flag.

Samples are explicitly supplied 12-bit integers. Readiness is supplied as
a count of invalid data reads, including a never-ready option. It is a
sensitivity fixture, not a physical conversion-time measurement. START
self-clears; DATA_VALID clears on consumption while the last payload is
retained. Unknown channels/registers, conversion without setup, missing
or exhausted input and unsupported reconfiguration fail explicitly.
Sequence A remains in its earlier standalone replay and is not silently
enabled here.

## Independent checks

Nineteen negative register/input cases reject unsupported accesses,
unstarted reads, pending restarts/reconfiguration, exhausted samples,
nonfinite/boolean/out-of-range samples and invalid readiness specifications.
Five positive checks cover configuration, START, valid data, read-clear
and retained payload. A zero sample with DATA_VALID is a real zero in
the fixture; an invalid zero payload is never promoted into a measurement.

Seven original-reader cases cover zero and nonzero values, two delayed
polls, readiness on the eighth/last poll, and readiness too late or never.
Missing data retains a sentinel previous result and leaves the success
flag clear. The delay arguments, watchdog feeds and result-structure
metadata/padding are checked separately.

The original clamp fragment is exercised for all **4,096** possible raw
samples with explicit caller registers. Its output is bounded to
3056..3523. The full original averaging routine is checked over all
**468** possible means against independent integer division:

`percent = floor((mean - 3056) * 100 / 467)`

The maximum is 100. The initial independent denominator hypothesis 468
failed; the stock multiply/shift constant and boundary outputs established
467, while 468 is the number of means. Cold ten-sample and subsequent
five-sample windows also check clipping, ring contents and averaging.
These are raw firmware fields; physical voltage units/scale are unverified.

## Foreground/audio integration

The earlier 255-iteration ACK path now continues through ten conversions.
A 575-iteration exploratory stream also completes with both GPIO cycles.
The current matrix extends this to stereo, separate L/R, delayed variable
ADC data and full-audio address NACK. Disabled-candidate pairs compare
81,192 shared RAM bytes, persistent registers, PCM, PWM and transactions.
Active output is compared with independently retained stock-idle traces;
mono must leave the opposite palm and triggers idle and stop must drain.
All original GPIO writes and their PCs are retained, including later
NACK-path high reassertions; the trace is not normalized to one high/low
pair. Pin direction, electrical role and physical motor effect are unknown.
In the active NACK fixture, the additional 62 high writes come from
original PC `0x1F012`; the initial high/low writes still come from
`0xD7F8`/`0xD7B2`. This identifies code locations, not the pin's board role.

| Active fixture | Complete iterations | Valid ADC conversions | Independent Sensa frame checks |
| --- | --- | --- | --- |
| Stereo | 575 | 10 | 44 |
| Left only | 255 | 10 | 18 |
| Right only | 255 | 10 | 18 |
| Delayed variable ADC | 255 | 10 | 18 |
| Address NACK with audio | 159 | 10 | 10 |

Together these run 1,499 iterations, 23,821 checked IRQ returns,
50 valid ADC conversions and 108 frame checks. All drain to the
independent stock-idle PWM reference in their declared schedules.

The [public validation](validation/20261128-adc-b.json) records the complete
new normal/optimized comparison. Twenty-four previous stage reports are
reused only after source/import/input/runtime/report-hash checks. This is
one fresh stage plus 24 audited report reuses, not 25 fresh runs. The
standard runner includes all 25 stages for a complete rerun. Previous
stages keep their own unsupported-ADC boundary contracts unchanged.

## Still open

After eight invalid polls the original routine enters reset/reinitialization.
The new replay follows it to PC `0xA034`, writing `0x400` to `0x40000620`.
The NXP header identifies this as `SYSCON.PDRUNCFGSET[0].PDEN_ADC0`.
That ADC power-control write is still outside the foreground model. It is
retained as a failed full-recovery boundary, with the old result intact;
neither an instruction limit nor a synthetic valid sample completes it.
Next model that power/reset path and the full `0x90E0` initialization,
including calibration readiness, rather than assuming recovery.

D620 retains supplied instruction budgets. I2C wait quanta and ADC polling
do not deliver concurrent USB/DMA arrivals while foreground is blocked.
Continuous event timing, independent elapsed-time motor stop, actual ADC
clock/data, physical pins, complete boot, radio/USB/authentication,
bootloader acceptance, flash placement and safe hardware restoration remain
open. Whole-device equivalence and a flashable custom image are unproven.

```powershell
$env:PYTHONPATH='TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_foreground_adc.py <Dev2FW.bin> <audio_haptics.bin>
python -O -B tools/firmware/replay_nxp_foreground_adc.py <Dev2FW.bin> <audio_haptics.bin>
```

Definitions: existing local [NXP header reference](https://github.com/nxp-mcuxpresso/mcux-sdk/blob/master/devices/LPC54605/LPC54605.h)
and the pinned original firmware. Stock binaries/captures are not distributed.

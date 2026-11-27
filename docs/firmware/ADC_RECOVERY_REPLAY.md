# Original ADC power fallback and startup/calibration

2026-11-28. **Working in bounded offline fixtures; untested on hardware.**
The stock NXP v2.04.00 image and simulator-only DSP module are unchanged.
No controller access, firmware upload, driver or service change.

## Original path now executed

The preceding [ADC-B stage](ADC_B_FOREGROUND_REPLAY.md) deliberately stops
the missing-conversion case at `0xA034`, before a write of `0x400` to
SYSCON `PDRUNCFGSET[0]`, `0x40000620`. This new, separate stage executes
that write and the complete original `0x90E0` initializer. Earlier stages
retain their original strict boundaries and regression reports.

The executed code masks seven IOCON registers, clears the five original
power bits, programs clock selection/dividers, calls original `0x9050`
configuration and `0x8F1C` calibration/startup, and configures/enables both
ADC sequences. All seven masked values retain unrelated dirty fixture bits.
Clock-write order, sequence A `0xC00402F8`, sequence B `0xD0040100`,
CTRL `0x665F`, INSEL clearing, original enable flag, and caller persistent
registers/stack are checked. The original routines are neither replaced
with host callbacks nor given fabricated success flags.

The strict model uses the NXP register definitions in `LPC54605.h`:
STARTUP ENA clears on ADC power loss; INIT and CALIB requests clear on
supplied completion; CALREQD stays set until supplied calibration completion.
Power loss retires the explicit aborted conversion so its late result cannot
be accepted as the next measurement. Calibration retention across this ADC
power transition is a supplied fixture, not measured board behavior.

The explicit clock fixture selects the supplied 96 MHz main-clock value
and AHB divider zero. Original calibration waits receive a separate
180 MHz argument. Their argument pairs are retained in the report; executing
those busy-wait instructions does not prove physical clock agreement or
conversion time. Status completion is supplied after N register reads.
This model does not derive analog readiness from elapsed silicon cycles.

## Independent checks and observed stock behavior

Six startup scenes cover initial calibration, an already-calibrated dummy
conversion, delayed calibration, delayed dummy completion, never-completing
calibration and never-completing dummy conversion. Original watchdog feed
counts and return values are checked. The original initializer makes up to
**four** attempts. It then configures the sequences and sets its ADC-enabled
RAM flag even if all four return zero. Consequently, a returned initializer
or that RAM flag alone is not a health check. The simulator refuses a later
conversion while calibration/startup is still unresolved.

The original calibration helper also issues a dummy INIT request following
a calibration request whose busy bit has not cleared. The report retains
these writes, with a separate count when CALREQD remains set. The supplied
status model permits observing both requests independently; whether silicon
accepts/ignores their overlap needs hardware evidence. No physical recovery
claim follows from the fixture's responses.

Two full reader/fallback scenes supply a never-ready sample or readiness
after the last allowed poll. Both execute eight invalid polls, retain the
previous result 3200 with READY clear, feed the watchdog ten times across
retry/fallback/startup, power down/up and return through the original code.
A subsequent independent valid sample 3450 sets READY and replaces 3200.
The aborted source sample is not replayed as fresh data. Real `0xD620`
delay traces and checked exception returns remain part of the test.

Four ten-sample averaging scenes remove the first, middle, last or all
measurements. An independent oracle reproduces an important original-code
limitation: `0x2C678` includes the retained previous result for each failed
measurement. With **all ten missing**, zero valid conversions still yield
mean **3200** and original average-ready bytes **01 01**. First/middle/last
failure cases yield means 3267/3254/3254. The test records this behavior as
a failure mode to address in a future custom firmware, not a successful
new measurement or a physical defect confirmed on this controller.

Fifteen negative cases reject unsupported channels/registers/access widths,
invalid schedules/flags, unsupported power commands, unpowered startup,
uninitialized conversion, sequence-A conversion without data, failed enable
and attempted conversion after four failed startup/calibration attempts.
Guard checks remain active under optimized Python.

## Remaining boundary and next increment

After full setup, the original analog scanner `0x197F8` now reaches its
sequence-A start store at **`0x1982C`**. This stage rejects that conversion
because no sequence-A channel fixture is supplied. The earlier standalone
[analog replay](DEV2_NXP_LPC54605.md#analog-input-and-queued-gip-trigger-reports)
already covers six channels; the next increment must combine it with this
strict A/B lifecycle and foreground scheduling. Configuring/enabling A here
is not proof that the whole foreground loop runs after reset.

The existing `0xD620` supplied instruction budget advances its modeled
peripherals. The separate `0x1FF70` calibration waits execute original
instructions but do **not** advance concurrent peripheral time. Continuous
USB/DMA arrivals during all waits, independent elapsed-time motor stop,
full boot, physical ADC clock/voltage/pin behavior, radio timing,
bootloader image acceptance and recovery remain open. This is not a
whole-device simulator or a flashable candidate.

## Reproduce and evidence

```powershell
$env:PYTHONPATH = 'TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_adc_recovery.py `
  TestResults/firmware-lab/images/Dev2FW.bin `
  TestResults/firmware-lab/publication-final-20261122/audio_haptics.bin
# Repeat with -O; compare complete parsed JSON reports.
```

The suite runner includes this as `nxp-adc-recovery`. See the
[public validation and source/input provenance](validation/20261128-adc-recovery.json).
All underlying firmware/capture binaries stay local and are not distributed.

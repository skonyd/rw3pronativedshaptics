# Unified original ADC A/B lifecycle and foreground replay

2026-11-28. **Bounded offline replay; untested on hardware.** Original NXP
v2.04.00 and the existing simulator-only DSP module remain unchanged.
No controller access, firmware write, driver or service change.

Continuation: [CALIBRATION_TIME_REPLAY.md](CALIBRATION_TIME_REPLAY.md)
adds supplied clock time and DMA completions during calibration waits.
This earlier stage retains its original timing scope and reports.

## What this adds

The [preceding recovery stage](ADC_RECOVERY_REPLAY.md) deliberately rejects
the original sequence-A start at `0x1982C`. The new stage owns one ADC page
from construction and connects the six A channels to the same power,
startup/calibration and sequence-B/channel-8 lifecycle. The original full
`0x90E0` initializer, `0x197F8` foreground scan, `0x9268` reader, `0x1615C`
moving average, `0x1E628` startup scan and B reader/averager execute.
Earlier stages and their explicit boundaries stay unchanged.

The original A configuration is `0xC00402F8`; START self-clears. Supplied
scans contain exactly channels **5, 9, 3, 4, 6, 7** in original read order.
Original successful reads invert channels 3 and 9 with `4096 - raw`.
The host supplies raw 12-bit samples and independent per-channel readiness
counts, including never-ready. It does not fill the firmware's result
buffer, set conversion-success/average-ready flags, or bypass its retries.

Each channel retains its own payload. VALID clears on consumption, without
inventing a second successful sample. ADC power-down aborts all pending
channels in A as well as a pending B conversion. A later scan takes its
next explicitly supplied values; late data from an aborted scan cannot
be reused as a new conversion. Retaining the old DAT payload over that
power transition is an explicit fixture, not measured silicon behavior.
Unsupported channels, widths, scans, schedules and overlapping A/B requests
reject. Hardware sequence priority, external triggers, burst conversion
and conversion IRQ/DMA timing are not modeled by this subset.

## Independent original-code checks

- Twelve full foreground scans include distinct channel values, raw zero
  and 4095, two invalid polls on channel 3 and readiness on the eighth poll
  on channel 7. All 72 valid reads, nine invalid reads, channel order,
  inversion and three ten-sample moving averages match an independent
  oracle. Disabled/not-due calls perform no conversion.
- Eighteen original startup scans produce 108 valid reads and the expected
  trimmed averages, removing minimum and maximum before division by 16.
- Six warm fault scenes remove one channel at each position in the scan.
  A preceding successful scan gives every channel a distinct retained
  payload. After eight invalid polls the original reader powers down ADC,
  aborting that channel and those still pending. Later reads in the same
  scan therefore fail too, causing up to six fallback initializations.
  The original scanner stores retained raw payloads; its success-only
  inversion is skipped for invalid channels, including channels 3 and 9.
  An independent ring-buffer oracle checks these values. The next A scan
  recovers fresh values, with valid B results zero and 3450 interleaved.
  For example, channel 9's priming raw value 1487 was correctly interpreted
  as 2609. On its missing next conversion the ring instead receives raw
  1487, rather than the old interpreted 2609 or a valid new measurement.
- A separate unified fixture runs twenty A scans plus two interleaved B
  reads. Original trigger mapping produces `(458, 808)`, then `(0, 0)` on
  release. The original GIP translator/builder queues 36-byte reports with
  those exact fields. Negotiated queue state and trigger calibration are
  explicitly supplied. No USB send is performed: the send sequence remains
  1 in both queued reports. Host delivery and Xbox authentication are not
  inferred from queued bytes.
- Twenty negative cases reject malformed/missing channel inputs,
  nonfinite/boolean/out-of-range samples, invalid readiness, unstarted
  reads, unsupported widths/channels, overlaps, pending reconfiguration,
  exhausted scans and unpowered conversion. Metadata and VALID read-clear
  are independently checked on all six channels.

These fault scenes reproduce original behavior under supplied faults.
They do not establish a physical controller defect. A future candidate
must retain data freshness/status and apply channel interpretation only
to data known to be valid; a ready flag alone is insufficient.
The candidate acceptance contract should keep raw and interpreted values
separate, attach a per-channel valid mask and conversion epoch, invalidate
pending channels on power recovery, and exclude missing conversions from
the valid-sample count. Holding a previously interpreted input may be an
explicit policy; silently treating retained raw data as a fresh sample is
not equivalent. This stage records the requirement and does not patch the
original scanner or implement that candidate policy.

## Combined audio/foreground acceptance

The stage includes one stock-versus-disabled-candidate pair and five
active Sensa scenes: stereo, left-only, right-only, an A timeout and a B
timeout. Each scene supplies 192 stereo PCM blocks and runs 255 complete
original foreground iterations, including A scans and the original ten
B measurements. Fault cases have separate stock-idle reference runs with
the same injected ADC fault. Disabled-candidate comparison covers 81,192
shared RAM bytes, persistent registers, PWM, I2C transactions and A/B reads.

Active runs check source PCM byte preservation, original Sensa dispatch,
independent decoded frame checks, combined SPI/I2S DMA completion, palm
positive controls, final idle drain and no trigger leakage. Mono runs
also require the opposite palm to remain at its independent idle reference.
The B-timeout case retains the original prior result 3200 for its missing
measurement, yielding mean 3290; healthy B scenes yield 3300. No stale
measurement is relabeled as a fresh conversion in the model.

Across the five active scenes: 1275 iterations, 635 A scans, 3809 valid
A reads, eight invalid A reads, 49 valid B reads, 22,249 checked exception
returns, 60 combined SPI/I2S batches and 105 independently checked Sensa
frames. These are normal-run counts; the separate optimized run matches.
Exact per-scene counts and execution provenance are in the
[public validation](validation/20261128-adc-dual.json). A new stage is run
under normal and optimized Python; unchanged preceding reports are reused
only after input/source/import/runtime/report-hash audits. Stage counts
are not a percentage of whole-device completion.

## Remaining limits and next step

The former `0x1982C` boundary is covered for this strict sequential
fixture. Full board startup, physical pin/voltage calibration, simultaneous
A/B priority and continuous conversion timing remain unverified.
`0xD620` retains its supplied instruction budget and modeled IRQ activity;
`0x1FF70` calibration waits execute original code without advancing
concurrent peripheral time. Independent USB/DMA arrivals during waits and
an elapsed-time motor-stop source remain the next timing work.

The RAM queue checks do not extend the earlier USB driver replay into a
complete live host/console handshake. Radio timing, bootloader acceptance,
image placement and safe hardware recovery remain open. Neither complete
device equivalence nor a flashable custom image is claimed.

## Reproduce

```powershell
$env:PYTHONPATH = 'TestResults/firmware-lab/deps'
python -B tools/firmware/replay_nxp_adc_dual.py `
  TestResults/firmware-lab/images/Dev2FW.bin `
  TestResults/firmware-lab/publication-final-20261122/audio_haptics.bin
# Repeat with -O; compare complete parsed JSON reports.
```

Suite name: `nxp-adc-dual-sequence`. Firmware/capture binaries remain local.

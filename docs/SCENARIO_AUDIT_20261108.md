# Repeated scenarios and false-positive audit — 2026-11-08

**Status: software safety working in these tests; lossy output prototype; physical equivalence unverified.** This extends [the first acoustic run](ACOUSTIC_RESULTS_20261108.md). It does not replace unfavorable earlier observations or turn equal feature vectors into a fidelity claim. No actual game or physical DualSense reference was played.

## Corrections

- Comparator rejects missing final frames, missing pitch (including 120→0), either side's envelope changes, per-frame hit-count changes, both hits' amplitudes/positions and invalid values. Eighteen individual side/field mutations and hits moved between frames with unchanged global totals must fail. Empty comparisons fail. A further defect found here discarded the final **partial 33ms frame on both paths**; it is now retained and tested with a 50ms scene whose final 10ms differ. Feature extraction still has 1ms slots; preserving this partial frame does not recover sub-slot waveform information.
- Production audio capture observes unexpected RecordingStopped/failure and clears health/output. Bounded serialized delivery has an independent 250ms watchdog after queued intentional delay drains, clears pending output on Stop/fault/overflow and prevents writes after Stop completes. Regression tests include 0/10/300/1000ms delay, concurrent Accept/Stop, queue overflow and sink failure. These are software guarantees while the process/pump and device APIs run, not a measured physical stop deadline under a stalled vendor runtime.
- Removed the automatic **2.2 input gain** from SensaAudioSink: production now uses unity **1.0**. This prevents that stage from clipping otherwise valid full-scale input. It can reduce the strength of quiet ordinary audio compared with the previous setting. Existing Wolverine output gain remains separate. The 2.2 value is retained only as an explicitly requested diagnostic counterfactual. No automatic normalization/EQ was fitted to microphone results. Original Native/physical DualSense reference bytes remain unchanged.
- NaN/Infinity Sensa input is counted and replaced by silence; Infinity previously could clamp to full-scale vibration. Xbox input also sanitizes/counts nonfinite samples before its stateful filters and rejects incomplete stereo frames, preventing permanent filter poisoning. The Xbox diagnostic test verifies valid audio recovers after malformed input. Capture rechecks finiteness after volume compensation, including multiplication overflow. Input-clip and output-envelope-ceiling observations are distinct.

## Independent software observations

Current Windows x64 WPF/HardwareProbe Release build: **0 warnings/errors**; required contract suite passed. Local results concern uncommitted work, not the published main CI artifact.

| Test | Result | Scope |
| --- | --- | --- |
| 30–400Hz, 1Hz steps × four levels × 48/44.1/96k | 4,452 within stated feature tolerances | Finite synthetic tones, shared lossy representation; not all waveforms or hardware |
| 72 pulse offset/width combinations + eight multitone/phase/DC/full-scale/out-of-band patterns × three rates | **231/240 within tolerance, nine outside** | 72 pulse cases use widths 0.1/0.25/0.5/1/2/5/10/20ms and offsets 0/0.25/0.5/1/7/16/31/32/32.75ms |
| Original 80Hz signal vs polarity inversion | Original RMS difference **0.70710678**, steady feature frames identical | Independent raw-sample calculation demonstrates phase information loss; whole-onset features differ by 1.76Hz |
| Independent original-sample spectral projections | 80Hz **0.25**, 190Hz **0.25** simultaneously | Current side representation has one carrier frequency; a feature match cannot establish preservation of both original components. No actuator spectrum or harmonic energy is inferred |
| Unscaled sine amplitude .05/.2/.4/1÷2.2/.5/.7/.9/1 at unity | Zero input-stage clipped samples | Does not guarantee source/Windows mixer/output stages cannot saturate |
| Same sine at legacy gain 2.2, amplitudes .5/.7/.9/1 | Active-channel samples clipped **27.67/55/66.33/69.67%** | No source pre-attenuation; shows the defect hidden by equalized fixtures |
| Actual XboxAudioSink mapping + injected diagnostic command writer | Nonzero mapping; all four fields zero at watchdog; no post-Stop write; filter reset, partial-frame rejection and NaN/Infinity recovery passed | Real mapping/delivery code; **no physical Xbox-mode controller test** |

Stress tolerance: envelope .01, frequency 1Hz, transient amplitude .02, exact hit positions/counts, no missing/invalid frames. The nine failures were **not waived**: eight at 44.1k, one at 96k, all rectangular pulses of 0.1/1ms. Frequency differences 1.81778–2.69330Hz; maximum envelope difference across the complete stress set .004297, transient amplitude .006059. Resampling abrupt short pulses changes the samples/pitch estimate; narrow rectangular pulses are broadband and do not have an authored single carrier. Their exact physical reproduction is not established. 48k stress cases all match feature tolerances. DC/1k/full-scale software-only patterns were never sent to motors.

## 68 new acoustic cases, all retained

Windows 11 build 26320; Wolverine USB **1532:0A4C**, wired PC mode. A4tech microphone, 48k stereo capture through explicitly selected installed .NET Host, volume 100%, unmuted. User-reported fixed position, Xbox button toward mic. Licensed HAR/provider hashes and endpoint/setup details are retained with results; controller firmware **not recorded**. Voice Clarity temporarily disabled for these measurements, then **Voice Clarity / automatic Voice Focus restored and visually verified**. No driver, privacy setting, service, firmware or physical position changed by this audit. No raw microphone audio retained.

The first 48 cases use 80Hz left, amplitude .5, one second drive: four engine/mic reuse combinations, six repeats per route, alternating route order. Engine/mic reuse flags are explicit. With a kept engine, the field EngineOffAfter means **post-source idle**, not a stopped engine. Twelve further cases repeat the initial cold/cold condition at the end. These 60 cases used the old 2.2 sink with explicitly equalized audio source. The final eight cases used the new unity input with **no pre-attenuation**, levels .5/.9, two repetitions per route. The term reference below means synthetic source→Native extractor→**Wolverine**, never physical DualSense.

Steady bins are microphone sample time **(350,1100]ms**, raw band RMS, without background normalization. CV is sample standard deviation/mean across six repeats; it measures relative repeat variation, not error against DualSense.

| Engine | Mic | Reference CV | Audio CV |
| --- | --- | --- | --- |
| Reopened, initial group | Reopened | 21.66% | 24.45% |
| Reopened | Kept open | 4.15% | 3.86% |
| Kept running | Reopened | 3.45% | 3.66% |
| Kept running | Kept open | 3.05% | 3.27% |
| Reopened, final control | Reopened | 4.58% | 0.98% |

**The final cold/cold control also improves.** Therefore engine/mic reuse alone is not an established fix. Fixed group order confounds elapsed time, mechanical/thermal settling, provider state and measurement conditions. Group RMS means also drift (reference about .03037 in one middle group versus .02287 at the end). Do not discard the first group, normalize these means into equality, blame Synapse alone or generalize the later small CVs to other frequencies/levels. Future measurements need predefined settling and balanced/counterbalanced order across independent sessions.

All 68 reports contain actual microphone bins and completed capture; no audio failures or microphone clipping. Source hashes are identical for equal authored scenes across both routes. AudioInputGain distinguishes build conditions. In the **eight unity live tests**, both .5/.9 audio inputs have zero counted sink clipping. At .9 and existing **140% output gain**, Native/reference and audio show respectively **242/243 saturated envelope points** summed across two repeats. At .5 both show zero. This is a real remaining command-ceiling limitation, even though audio input clipping is fixed. It is not a direct sensor measurement of actuator distortion. Two unity repeats per route are a spot check, not long-run acceptance.

## Reproduction and next gates

Reports under ignored local `TestResults/scenario-audit-20261108/`: software-unity.json, grid-unity/grid-{rate}.json, xbox-safety.json, contracts-final.txt, build.txt, session/audit-000..047.json, final-cold-check/audit-000..011.json, unity-hardware/audit-000..007.json and repeat-summary.json. Pre-unity executable hashes are retained separately. `tools/analyze-session-audit.py` recalculates every group without deleting outliers.

```powershell
dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj
dotnet build tests/RW3Pro.HardwareProbe/RW3Pro.HardwareProbe.csproj -c Release
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-software-scenarios <report.json>
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-software-grid <report-directory>
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-xbox-safety <report.json>
python tools/analyze-session-audit.py
```

Still open: independently repeated counterbalanced frequency/level sessions, >=10-minute general audio use, real endpoint mute/volume/disconnect/reconnect and sleep/resume, real Xbox-mode physical silence, resolving output-ceiling policy without changing original source, calibrated stereo/phase/transient/latency acceptance and later physical DualSense A/B. One airborne microphone cannot prove every waveform's displacement or channel isolation. No supported richer live vendor transport has been found; M3/M4 remain open and the system is **not certified lossless or fully ready**.

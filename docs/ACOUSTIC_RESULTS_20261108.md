# A4tech single-microphone results — 2026-11-08

**Status: measurements completed; physical equivalence/repeatability acceptance remains open.** These compare two paths into the same Wolverine, not physical DualSense against Wolverine. No genuine game source was used. Firmware remains unrecorded.

Conditions: Windows 11 26320, Wolverine wired PC `1532:0A4C`, Synapse 4.0.896 observed, existing user-licensed HAR/provider DLLs, A4tech first input channel 48k, microphone volume 100%, Voice Clarity temporarily off. Previous Voice Clarity/automatic Voice Focus restored and visually checked after the last case. RW3 output 100%, source gain 140%; test audio divided by fixed 2.2 pre-gain for equalized inputs, selected Steam Streaming Speakers 48k virtual endpoint, audio sync override 0. Software endpoint-volume compensation was active. Original canonical quad PCM16 hashes match for all 41 sweep pairs. DualSense output was not played.

Acquisition: **130 controlled cases** (8 controls + 82 sweep + 24 repeat + 16 extended), plus 8 earlier Voice-Clarity-on controls kept separately. No capture exceptions or microphone clipping in the 130 controlled reports. Equalized audio source had no pre-gain clipping; this does not exclude the later Wolverine 140% envelope ceiling or general game-audio clipping.

Main findings:

- Engine-idle and engine-off audio controls stayed near the background in the unprocessed control set; active 80Hz examples were clearly above it.
- 24/41 sweep pairs have both paths >=6dB above background; 17 pairs are unresolved at that descriptive screen. Raw microphone audio/reference RMS differences in the 24 stronger pairs reach about 2.49dB. This is not a calibrated haptic tolerance or proof of equal spectra/timing.
- Three repeats of each of four frequency/level combinations, on both paths, give RMS coefficients of variation about 48–73%. Some repeats approach background. Cause is not isolated between source scheduling, vendor rendering/startup/device behavior and this one microphone's response. Do not calibrate EQ from these observations or label them all passes.
- An elevated late tail appeared in one 120Hz/0.9 audio sweep case (+12.37dB relative to its conservative baseline); it was not a persistent lock established by the aggregate meter. Keep long-run and stop acceptance open.
- Extended stereo 80/190Hz, 20ms bursts, alternating sides and 5s sustained source were measured through both paths. Diagnostic feature peaks agreed closely, and single-left cases had zero right feature peak; this is software channel evidence, not sensor-confirmed physical isolation.
- Both explicit source cancellations occurred after approximately 717/720ms. Late-tail and post-engine-stop microphone levels were close to their backgrounds in these two cases. The audio cancellation's selected short steady window was only +2.25dB above background, so it cannot establish a successful physical vibration-and-stop comparison. No calibrated physical stop deadline is claimed.

Windows apphost `.exe` microphone opens returned E_ACCESSDENIED. The same test DLL captured real samples through the installed .NET Host, whose microphone permission was shown enabled. This launch-dependent difference is observed; no privacy setting was modified and its exact policy cause is not established. Packaged-app microphone consent remains a separate release check.

The tables below are generated from local aggregate-only JSON by `tools/analyze-acoustic-audit.py`. Original microphone PCM/voice is not saved by the probe. Requested-tone magnitude and 10ms energy bins are acoustic observations, not actuator displacement. No sensor calibration, full-spectrum equivalence, absolute latency, actual-game provenance, Xbox-mode acceptance or >=10-minute continuous-game performance result follows from this run.

## Relative microphone measurements

Uncalibrated one-mic sound levels, not displacement/fidelity/side-isolation or absolute latency. Reference is synthetic Native-extractor-to-Wolverine, not physical DualSense. Drive bins 350ms through source drive end minus 100ms; late tail begins command completion +400ms. Sample-clock bins are not synchronized with command onset. 6dB is a descriptive detectability screen, not a fidelity tolerance. Ratios are raw microphone RMS ratios; background energy is not normalized away.

Completed cases: {"controls-unprocessed": 8, "sweep-unprocessed": 82, "repeat-unprocessed": 24, "extra-unprocessed": 16}

| Hz | Level | Side | Reference/background dB | Audio/background dB | Audio/reference dB |
| --- | --- | --- | --- | --- | --- |
| 30 | 0.2 | left | -0.14 | -0.36 | unresolved |
| 30 | 0.5 | left | -0.33 | +1.40 | unresolved |
| 30 | 0.9 | left | +0.72 | +3.36 | unresolved |
| 40 | 0.2 | left | +1.01 | +1.33 | unresolved |
| 40 | 0.5 | left | +14.03 | +15.93 | +1.42 |
| 40 | 0.9 | left | +15.99 | +13.54 | -2.49 |
| 50 | 0.2 | left | -0.05 | -0.04 | unresolved |
| 50 | 0.5 | left | +0.68 | +14.43 | unresolved |
| 50 | 0.9 | left | +16.63 | +15.91 | -0.58 |
| 60 | 0.2 | left | +13.83 | +13.06 | -0.49 |
| 60 | 0.5 | left | +13.80 | +14.12 | -0.51 |
| 60 | 0.9 | left | +16.59 | +16.47 | -0.58 |
| 80 | 0.2 | left | +7.04 | -0.99 | unresolved |
| 80 | 0.5 | left | +16.35 | +15.65 | -0.26 |
| 80 | 0.5 | right | +15.93 | +16.60 | +0.55 |
| 80 | 0.9 | left | +17.70 | +19.46 | +0.11 |
| 100 | 0.2 | left | +13.42 | +13.54 | -0.06 |
| 100 | 0.5 | left | +18.36 | +18.83 | -0.17 |
| 100 | 0.9 | left | +20.17 | +18.28 | -1.82 |
| 120 | 0.2 | left | +5.54 | +6.36 | unresolved |
| 120 | 0.5 | left | -0.42 | +20.78 | unresolved |
| 120 | 0.9 | left | +24.93 | +24.57 | +0.09 |
| 150 | 0.2 | left | +6.60 | +5.88 | unresolved |
| 150 | 0.5 | left | +13.69 | +12.80 | +1.17 |
| 150 | 0.9 | left | +17.61 | +1.37 | unresolved |
| 190 | 0.2 | left | +1.15 | -0.29 | unresolved |
| 190 | 0.5 | left | +13.72 | +13.75 | +0.05 |
| 190 | 0.5 | right | +18.35 | +19.15 | +0.93 |
| 190 | 0.9 | left | +15.72 | +17.07 | +0.64 |
| 250 | 0.2 | left | +12.14 | +12.29 | +0.39 |
| 250 | 0.5 | left | +14.58 | +13.99 | -0.31 |
| 250 | 0.9 | left | +15.17 | +0.00 | unresolved |
| 300 | 0.2 | left | -0.53 | +11.54 | unresolved |
| 300 | 0.5 | left | -0.14 | +16.31 | unresolved |
| 300 | 0.9 | left | +17.43 | +17.99 | +0.63 |
| 350 | 0.2 | left | +8.38 | +8.76 | +0.19 |
| 350 | 0.5 | left | +14.79 | +14.90 | +0.02 |
| 350 | 0.9 | left | +15.87 | +14.89 | -1.18 |
| 400 | 0.2 | left | +4.33 | +4.48 | unresolved |
| 400 | 0.5 | left | +0.26 | +10.98 | unresolved |
| 400 | 0.9 | left | +12.73 | +11.77 | -0.65 |

Unresolved ratios have at least one path less than 6dB above its background; no equality is inferred.

Repeat observations:

```json
[
  {
    "Hz": 50,
    "Level": 0.5,
    "Route": "reference",
    "N": 3,
    "MeanDriveRms": 0.013256883047817964,
    "CoefficientOfVariation": 0.6818194469960825
  },
  {
    "Hz": 50,
    "Level": 0.5,
    "Route": "audio",
    "N": 3,
    "MeanDriveRms": 0.014129230410620173,
    "CoefficientOfVariation": 0.6077482280002304
  },
  {
    "Hz": 80,
    "Level": 0.2,
    "Route": "reference",
    "N": 3,
    "MeanDriveRms": 0.006196928207842608,
    "CoefficientOfVariation": 0.4813664782311816
  },
  {
    "Hz": 80,
    "Level": 0.2,
    "Route": "audio",
    "N": 3,
    "MeanDriveRms": 0.0064181246711041764,
    "CoefficientOfVariation": 0.4929966849101083
  },
  {
    "Hz": 80,
    "Level": 0.5,
    "Route": "reference",
    "N": 3,
    "MeanDriveRms": 0.018251334041654865,
    "CoefficientOfVariation": 0.7299804937733517
  },
  {
    "Hz": 80,
    "Level": 0.5,
    "Route": "audio",
    "N": 3,
    "MeanDriveRms": 0.018001684181439524,
    "CoefficientOfVariation": 0.6738042000298857
  },
  {
    "Hz": 190,
    "Level": 0.5,
    "Route": "reference",
    "N": 3,
    "MeanDriveRms": 0.007825368973887968,
    "CoefficientOfVariation": 0.6201188067025217
  },
  {
    "Hz": 190,
    "Level": 0.5,
    "Route": "audio",
    "N": 3,
    "MeanDriveRms": 0.006798457122059872,
    "CoefficientOfVariation": 0.5306578470437666
  }
]
```

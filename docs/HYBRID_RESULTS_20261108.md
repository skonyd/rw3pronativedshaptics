# Hybrid output, soft limiter, audio sensitivity — 2026-11-08

**Status: Hybrid is an opt-in experimental output method (Settings → Gelişmiş → Wolverine çıkış
yöntemi). Standard remains the default.** Promotion to default needs the blind hand test.

## Why a hybrid

60-case microphone comparison (docs/MULTIBAND_RESULTS_20261108.md, TestResults/physical-comparison-20261108):

- Standard plays a mixed 80+190 Hz scene at its zero-crossing average (~120–135 Hz), a frequency
  the source does not contain.
- Multiband places that scene at 80 and 190 Hz, but loses short hits (−9.8 dB in all five
  repeats): its amplitude came from the 85 ms spectrum, which dilutes a 20 ms hit.

## Design (`HybridOutputPolicy`, `SensaStereoPrototype.HybridSide`)

Per hand, per 33 ms frame:

- Strength always comes from the fast 1 ms-slot envelope.
- Multiband only for steady, really mixed sound: ≥ 2 spectral components with the weakest
  ≥ 20 % of the strongest, active ≥ 42.7 ms (half the FFT window), no transient in the frame,
  envelope max/min ≤ 2. Enter after 2 steady frames; leave at once on a hit or loss of the mix.
  The fast envelope is split across components by their spectral shares.
- Otherwise the standard parametric frame (transients kept). While a mixed sound starts, the
  standard frame plays its strongest real component instead of the crossing average.
- Switching uses `StopEvent` on the effect not in use. A zero-amplitude parametric curve is
  not silent: it hums at its pitch (first hybrid run put the mixed scene back at ~130 Hz).

## Measurements (A4tech webcam mic, wired PC mode, 100 % gain/output, medians of 5)

Single runs jump ±10–15 dB at random in every method, so medians and band shares are used.
Final focused run (TestResults/method-comparison-20261108c), × background in mic tone bands:

| Scene | Standard | Hybrid | Multiband |
| --- | --- | --- | --- |
| mixed 80+190 Hz: 80 / 190 vs 120 / 135 | 2.1 / 3.6 vs **15.2 / 22.0** | **6.2 / 5.8** vs 3.2 / 3.4 | 6.6 / 7.7 vs 2.6 / 5.6 |
| short hits (driven dB) | 8.9 | **9.7** | 1.8 |
| stereo 80 L / 190 R (driven dB) | 16.5 | 16.4 | 16.7 |

Hybrid sent 90 % of the mixed scene's active frames multiband and 0 % of the hits / single tones.
Earlier full runs (…1108, …1108b) are kept; their hybrid rows predate the StopEvent and dominant
pitch fixes. Offline (contract test, real extractor + spectrum): the steady mixed scene makes one
switch into multiband and stays; a hit train never leaves standard.

Not verified: real game content, blind hand comparison, latency of the multiband frames
(85 ms spectral history still applies to them), long sessions.

## Also in this change

- `HapticLimiter`: gain ≤ 100 % unchanged; above, soft knee at 0.7 instead of a hard 1.0
  ceiling (300 %: 0.33 → 0.89, 0.5 → 0.98, 1.0 → ≈1.0), so strong moments stay distinct.
- "Sesten titreşim hassasiyeti" (Normal 100 / Yüksek 160 / Çok yüksek 250): feature-level gain
  for game audio only, after extraction, so the waveform is never clipped; Test-tab sound tests
  bypass it.
- Settings left card: Temel / Gelişmiş toggle instead of a scrolling page.

# Experimental multiband output — 2026-11-08

**Implemented, opt-in prototype. Not physical DualSense equivalence.** Settings now offers **Deneysel çok bantlı Wolverine çıkışı**, off by default. It applies to the main Native bridge, PC audio→Sensa and Wolverine demos. The retained channel/endpoint diagnostic lab still uses its separately identified single-band backend. Xbox/XInput and physical DualSense reference are unchanged. There is no silent single-band fallback on experimental backend failure.

## New route and what was actually found

Original 48k rear PCM / explicit PC audio → original-sample spectral history → up to three separated frequency/amplitude components → project-authored multi-melody `.haps` → documented HAR AddHM/UpdateHM → installed Razer provider/runtime. Existing four-point envelope controls shape each component. Components have stable low-to-high melody order, rather than swapping bands when their amplitudes cross. The vendor performs packet encoding; RW3 never hand-packs unknown stream fields or invokes undocumented encoder signatures.

The former conclusion “the public API can only reach one band” was too broad: it described prior **Add/UpdateParametricEffect and multiple-event** experiments. The `.haps` route produces a different result. Three sets of 36 controlled provider cases (initial schema, UpdateHM control, generated-schema control) completed: silence, 80, 190, 80+190 in both melody orders, averaged-single-tone controls, unequal amplitudes and 40+120+300, both sides and reversed-order repeats. Excluding the changing header, all active-side payloads repeat exactly, opposite-side payloads stay unchanged, and mixtures differ from either single carrier and the averaged-carrier control. Previously unused payload regions change with additional melodies. This is evidence for richer provider representation, **not independently decoded frequencies or measured motor spectra**.

Official `.haps` docs allow simultaneous melodies **subject to device capabilities**. Schema was checked against the official public MaxOne/Car Engine v5 examples; generated test scenes are project-authored constants, not redistributed vendor effects. No SDK, driver, service or firmware installation/change occurred. User reconnected Wolverine wired PC mode and closed RW3 to release the single-owner mutex.

## Spectrum and phase analysis

Hann-window FFT, 4096 original samples at48k (**85.33ms history**), log-peak interpolation and signed original-sample projection. At most three peaks, selected bin indices at least three bins apart (35.16Hz); amplitude floor .004 and relative peak-power threshold .01. Resolution is level/window/spacing dependent. The algorithm does not reliably resolve 80+84Hz, broadband/noise or arbitrary waveforms into their true components. It is not an exhaustive spectral decomposition or PCM reconstruction.

The capture callback only appends to bounded original-sample history while opted in. Snapshot copying is bounded; FFT, JSON and vendor calls run on the consumer outside the capture/feature lock. Warmed mono FFT allocates zero managed bytes per window. JSON/vendor work is additional and allocates; the FFT microbenchmark is not the bridge's total CPU cost. Stop clears/disables retained spectral history. Source sample values are never changed by analysis.

Independent known-signal gates:

- 240 single tones:16 frequencies including fractional81.3Hz and band edges ×5 levels(.01–.9) ×3 phases. **240 pass**, maximum frequency error **.19563Hz**, amplitude error **.00543** under1Hz/.025 limits.
- Separate 108-tone contract grid, equal/unequal80+190 and three-tone40+120+300 recover authored components within stated tolerances. Original PCM remains unchanged. Uneven source packets, side isolation, silence/history clearing, stable ordering and nonfinite input have regression checks.
- Polarity-inverted original waves have distinguishable phase in analysis. **No carrier-phase parameter is sent to the motor:** the documented interface does not expose one. Per-frame retriggering, phase and transient loss remain limitations.
- One shared scale keeps the sum of component amplitudes × user gain at most1; reductions are counted. This prevents that command sum from hard-clipping, but changes absolute gain and is not proof of hardware headroom. Each note explicitly reaches zero amplitude at66ms if another update does not arrive; process/device/runtime stalls still require physical stop acceptance.

## Live and microphone observations

Wolverine **1532:0A4C**, Windows11 build26320, same licensed HAR/provider hashes as earlier audit; exact firmware remains unrecorded. Provider output cadence remains33ms. Original PCM→FFT→UpdateHM→provider completed16 low-level scenes:80,190,80+190,40+120+300 ×left/right ×two repeats. Another16 bounded-tail scenes completed with A4tech microphone, zero microphone clipping, unchanged source hashes and stopped software workers. These microphone settings were left unchanged (Voice Clarity/automatic processing may affect measurements); they are not unprocessed calibrated spectra. Integrated driven/background ratios range approximately1.47–6.96dB, and post-stop/background −.25 to+2.74dB. Many scenes are weak against the background; no spectral/phase/physical-stop pass follows.

Eight additional current-code cases compare synthetic Native ingress with **real Windows48k WASAPI loopback** using Steam Streaming Speakers:80Hz and80+190Hz, two routes, two repeats with reversed order. Every audio run delivered118–119 callbacks, no capture failure and zero sink input clipping. Integrated microphone driven/background-or-idle ratios approximately7.90–12.72dB. Source hashes match for equal scenes. The engine stayed alive between cases; post-source EngineOffAfter is idle, explicitly labelled in reports. This proves software source/capture/provider activity and relative acoustic activity, not actual-game authorship or physical fidelity. No Sony output was played.

Initial exploratory runs failed on native provider render. Isolation showed static schema and UpdateHM controls worked. When `.haps` events are stopped for idle, the vendor debug accessor can index a nonexistent material frame and throw SEHException; the prototype now uses normal provider rendering for that idle state and clears its debug snapshot. Failure never becomes a success result. Empty-material and disconnected-device attempts are excluded from completed cases, retained as failures. No unknown native ABI was guessed to bypass this.

## Latency experiment

85.33ms is history/startup requirement, **not a measured fixed game-to-motor delay**. The Hann center is42.67ms behind the latest sample, which can smooth/delay spectral amplitude changes. Four-point envelopes use recent source slots, and the provider is updated every33ms. Capture, scheduling, vendor queue and actuator response remain separate unknowns; do not add arbitrary constants into a claimed end-to-end number.

A2048-sample/42.67ms **diagnostic-only** profile was tested against the same240 cases: **55 fail**, all in30–40Hz. Maximum frequency error30Hz and amplitude error.5643; it is not exposed as a production low-latency option.4096 passes240/240. Warmed per-mono-window median times were approximately.031ms(short) and.070ms(default), zero allocations. CPU computation is not the main reason for retaining longer history. A future low/high-frequency hybrid or independently validated parametric estimator can be investigated; neither is implemented or silently substituted.

## Evidence and remaining gates

Ignored local artifacts: `TestResults/multitone-20261108/` contains provider-v3, provider-update, provider-generated, provider-summary.json, exploratory live failures, live-v5(pre-final-tail), live-final(current bounded-tail/cleanup), live-microphone, audio, spectrum-2048.json, spectrum-4096-final.json, contracts-final.txt and build files. `tools/analyze-multiband-provider.py` reproduces raw-payload comparisons without a speculative decoder. Earlier failed short-pulse tests in SCENARIO_AUDIT_20261108.md remain failures of the separately tested old representation; the new prototype does not erase them. WPF Debug/Release builds pass with warnings-as-errors and the required contracts pass. The new checkbox was toggled on/off in Normal; final state off. Settings scrolling preserves access to lower controls.

Still needed: counterbalanced old/new physical comparisons with adequate SNR, short hits/first85ms coverage, nearby tones, sweeps/band-entry continuity, output gain/sustained-session CPU and queue behavior, real game source/input association, physical channel isolation, synchronized latency and unchanged physical DualSense reference. Keep M3/M4 open; multiple encoded components do not imply lossless waveform output.

Sources: [public Engine Control](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-sdk-for-game-engines/engine-control/), [official .haps format](https://doc.wyvrn.com/docs/interhaptics-sdk/haptic-composer/haps-file-format/), [official v5 example schema](https://github.com/WyvrnOfficial/Interhaptics_Unity_CoreSDK/blob/master/Assets/HapticEffects/MaxOne.haps).

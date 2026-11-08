# Synapse Audio-to-Haptics reference experiment — 2026-11-08

Status: **in progress; no success/fidelity percentage established**.

## Completed Soundcore-muted reference (later user-confirmed condition)

The user subsequently confirmed music-video haptics with no radiated speaker sound. The actual default endpoint was Soundcore Motion+ 44.1k, hardware mute enabled, endpoint volume100%; A4tech microphone volume100%. Other audio was confirmed stopped. A three-repeat short stop test completed, followed by the user-authorized full reference: **102 scenes ×3 repeats =306/306**, no acquisition failure, missing case or microphone clipped sample, and endpoint/level guards remained stable. Data: `TestResults/synapse-audio-reference-20261108/soundcore-muted-reference-full/`; analysis: sibling `soundcore-muted-analysis/`. Earlier unmuted-Soundcore, changing-default Steam/MSI, and Audeze attempts remain separate controls and are not merged into this reference.

Median between-repeat CV is12.47% for the nominal source window and14.23% for the complete driven/tail window. These medians include near-background scenes and therefore do not establish reliable physical output. The long60Hz control's source-window CV is3.40%; long-hit control17.35%. Silence exposure CV is14.71%. Preserve all repeats and unresolved scenes.

Only5 active scenes satisfy the predeclared all-three-repeats6dB source-window screen, and3 satisfy the analogous full-exposure screen. **These are not success percentages.** The screen conservatively uses the larger pre/post level; the post window can contain Synapse's continuing haptic tail, not merely ambient noise. Maximum pre/post drift reached21.23dB, particularly for the long tone. Consequently the screen cannot separate weak response, environment contamination, intended Controlled-profile suppression and delayed output. The user's earlier late-stop observation is supported by aggregate tail measurements, but no calibrated physical stop deadline or exclusive Synapse-vs-Bluetooth cause was measured.

The independent source/playback hashes allow later paired RW3 measurement. **No paired PC/Xbox candidate series or better/worse verdict is completed yet.** Before comparative scoring, use a sufficiently long silence/settling window with pre-only ambient estimates and separate post-tail reporting; retain this original analysis rather than retroactively changing its criterion. Long tests remained reference-only; RW3's application was closed and no RW3 motor writer was constructed.

User prepared Wolverine in PC mode, RW3 fully closed, Synapse Audio-to-Haptics on, controller near the A4tech microphone. Synapse/Chroma UI showed global Audio-to-Haptics and Wolverine enabled, global/device intensity 6. Existing microphone volume 100% and all processing settings remain unchanged. No raw microphone audio is retained.

## Apparatus validation before scoring

The first 18 Synapse-on pilot cases and 18 device-off controls used Soundcore Motion+ 44.1k playback. A further global-off control was interrupted after five cases. User then confirmed the test tones were audible from the speaker. Therefore these recordings contain **direct speaker sound** and are retained solely as contaminated apparatus controls, not as motor-response or comparison evidence. Original 48k source and explicitly converted 44.1k playback hashes are retained; no normalization or gain inversion was applied.

User subsequently selected a non-radiating playback device: Steam Streaming Speakers, 6ch/48k, master volume .63. This is a new apparatus condition, not comparable to the earlier Soundcore run. New negative/positive pilots must validate motor detectability with Synapse off/on before the full series. Transient environment noise in silence/background windows remains in the records; cases are not discarded for appearing unfavorable.

## Fixed comparison design

`AudioReferenceFixtures` supplies 100 deterministic stereo system-audio scenes, each repeated three times in a fixed seeded random order:

- 13 frequencies from30–400Hz ×3 amplitudes(.15/.5/.9) ×isolated left/right =78.
- Out-of-band20/600/1000Hz controls, stereo80/190/400Hz, silence.
- Split/swapped/opposite-phase stereo,80+190Hz,40+120+300Hz,80+84Hz.
- 5/20/50ms repeated pulses, AM, up/down sweeps, deterministic noise, alternating channels and level steps.

Every case has200ms source lead silence,1200ms drive (1800ms pulse cases),800ms source tail,600ms pre-background and400ms after-background. The same original source and emitted playback hashes must match across backends. Pilot uses six scenes ×three repeats. Explicit sinc conversion is recorded if the selected endpoint is not48k; the native DualSense reference path is not used or changed.

Reference route **does not construct any RW3 motor writer or Sensa engine**. PC candidate uses production AudioHapticsBridge/SensaAudioSink, Direct USB Standard, source gain140%, level100%, sensitivity100%. Xbox candidate uses production body-only XboxAudioSink; trigger channels must never be synthesized from audio. Intentional sync delay is explicitly0 for the controlled conversion comparison; this does not verify the app's automatic Bluetooth timing. Synapse Audio-to-Haptics must be off for RW3 candidate runs. Match source endpoint/volume, microphone/volume, physical placement and processing. Restore the user's reference setting afterward; no service/driver/firmware changes.

## Interpretation

The analyzer retains all repeats, clipping/missing-sample failures, pre/post noise changes, late tails and source identity. A descriptive detectability screen requires an active window at least6dB above the larger pre/post background in all three repeats. Relative level comparisons use the observed reference repeat range with an explicitly disclosed3dB margin; this is **not a physical haptic tolerance**. Louder output is not automatically better. Report unresolved cases, repetition variation, response differences and tails separately. One airborne microphone cannot prove independent actuator displacement, carrier phase, absolute game-to-motor latency, or universal DualSense/Synapse equivalence.

Local reports: `TestResults/synapse-audio-reference-20261108/`. Reproduce with `audio-reference <synapse|disabled|pc|pc-hybrid|xbox> <directory> <mic-id> <pilot|full>` through installed x64 dotnet.exe, not the previously denied apphost microphone path. Analysis: `tools/analyze-synapse-reference.py --reference <directory> [--candidate <directory>] --out <analysis-directory>`. Build/contracts validate scene generation independently; they are not physical test passes.

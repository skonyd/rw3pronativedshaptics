# Phase and simultaneous-frequency improvement — 2026-11-08

Status updated: the original plan below led to an opt-in spectral/multi-melody `.haps` prototype. [Implemented route, tests, new provider findings and limitations](MULTIBAND_RESULTS_20261108.md). Default remains the existing single-band backend; physical phase/fidelity is not established. The2048-sample short-window experiment failed55/240 cases and is diagnostic only.

## Practical sequence

1. Analyze original left/right PCM spectrally before collapsing it to one pitch. Keep bounded windows, component amplitudes/confidence; distinguish simultaneous tones from broadband clicks. A stronger estimator may improve the one-carrier approximation, but cannot create simultaneous output carriers. Independently measure accuracy, latency and runtime cost before changing production pitch selection.
2. Test whether a documented multi-melody `.haps` asset survives the Wolverine provider as distinct simultaneous bands. Use AddHM/UpdateHM, not unknown encoder exports or hand-packed USB frames. First observe isolated provider rendering without treating frame changes as physical proof. Existing multi-event tests merged/saturated into one band; a `.haps`-specific route is a hypothesis, not a proven bypass.
3. If distinct bands survive, implement a separately labelled experimental backend with current 33ms pacing, original PCM preservation, separate L/R and independent safe stop. Measure physical response using controls and counterbalanced repeats. Otherwise retain the negative result and seek a documented vendor encoder/live interface. Extra analysis alone does not establish extra motor bands.

## Phase limitation

Documented Add/UpdateParametricEffect carries time/amplitude, time/pitch and transient triples. No signed waveform or initial carrier phase argument is documented. Source phase can be preserved in original PCM, but faithful actuator phase cannot be promised through this backend. Beat envelopes can carry some consequences of interference; that is not carrier-phase preservation. Physical acceptance requires controllable phase-aware output and synchronized sensing; one uncalibrated airborne microphone cannot establish it.

## Acceptance scenes

Keep original PCM immutable. Include single 80/190Hz controls, equal/unequal 80+190, 80+84 beating, three tones 40+120+300, 0/90/180-degree phase changes, channel-swapped/opposite-phase stereo, pulses, onset/offset, silence, headroom and sustained/repeated sessions. Separate source components, representation losses, provider behavior and physical observations. Alternating frequencies between frames is not simultaneous reproduction; one-band selection is not lossless.

## Official sources checked

- [Engine Control](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-sdk-for-game-engines/engine-control/): public parametric arguments and `.haps` loading/updating.
- [.haps File Format](https://doc.wyvrn.com/docs/interhaptics-sdk/haptic-composer/haps-file-format/): melodies can express simultaneous low/high patterns, explicitly subject to device capability; not Wolverine multiband proof.
- [Device rendering](https://doc.wyvrn.com/docs/interhaptics-sdk/haptic-performance/how-haptics-renders-on-each-device/): rendering differs by destination; the generic table does not establish Wolverine phase/multiband transport.

No SDK/driver/runtime installation, service change, proprietary distribution or physical output experiment occurred during this feasibility review.

# Feedback backlog review — 2026-11-09

Review is against current local working tree, including earlier uncommitted implementation;
the pasted proposal describes several already-implemented items as absent.

2026-11-09 follow-up: [Audio/game coexistence and router audit](HAPTIC_ROUTER_AUDIT_20261109.md).
Items4/10 remain incomplete: add an authenticated game-feedback adapter, a common Xbox
four-channel owner, independent PCM/rumble/trigger enable and gain/priority controls, then
actual-game acceptance. The Native router subset already exists; do not duplicate it.
Body-only XInput does not prove impulse preservation: observed GIP packets carried all-channel
enable masks and zero triggers. General flexible routing UI is not currently implemented.

| Item | Current assessment |
|---|---|
|1 Adaptive triggers|Implemented: genuine virtual DualSense HID effects plus physical pull; Xbox impulse output, PC classic body approximation. No resistance claim.|
|2 Normal rumble|Implemented: RumbleValid report bytes retained and routed even without rear PCM, in PC and Xbox modes. New parser-to-router regression verifies this.|
|3 Four motor tests|Existing trigger-only and seven-phase combined demo; complete physical phase acceptance still open.|
|4 Single output ownership|BridgeMotorOutput serializes Native four-channel commands, level/stop/watchdog. PC Sensa versus compatible body output and cross-process Xbox-game arbitration are not a proved universal mixer.|
|5 Virtual feedback adapter|HIDMaestro callbacks already capture genuine report02 and rear PCM. PadForge adapter is not required for the currently implemented source.|
|6 Feature fallback|Legacy rumble is already forwarded without requiring HD PCM. No automatic audio substitution should disguise absent game-authored feedback. Compatibility diagnostics remain limited.|
|7 Raw high-resolution PCM|Not implemented as lossless physical waveform. Direct USB uses bounded parametric bands/points. Never claim PCM preservation from matching envelopes.|
|8 Event DSP|Onset/envelope/spectrum extraction exists; semantic gunshot/footstep/rain classification is not implemented.|
|9 Stereo|Rear PCM and Hand L/R paths separate; XInput heavy/light motors are not proof of true stereo.|
|10 Audio/game priority|Three exclusive modes and body-only audio commands exist; simultaneous game impulse preservation needs hardware arbitration acceptance.|
|11 Latency/jitter|Telemetry and USB timing studies exist; physical game-to-motor latency remains unmeasured.|
|12 Game profiles|Queued; not implemented as verified game-specific presets.|
|13 Intensity|Limiter/sensitivity exist; speech/music semantic suppression does not.|
|14 Pull-sensitive triggers|Already implemented in TriggerEffectInterpreter with actual physical pull.|
|15 Mode detection|Implemented USB PC/Xbox identification and mode-dependent outputs.|
|16 Telemetry|Counters/maxima and diagnostics exist; complete four-channel live visual meter remains future UX.|
|17 A/B laboratory|Synthetic native/trigger scenes exist; calibrated full DualSense/Wolverine acceptance remains open.|
|18 Hardware evidence|Logs distinguish API, subjective sensation and physical verification; do not upgrade earlier limited user feedback.|
|19 Recovery|Bridge watchdog/input-loss neutral/stop guards exist. User-reported YouTube seek recovery is a separate open investigation.|
|20 Game diagnostics|Source/engine health exists; automatic per-game capability certification does not.|
|21 Auto game profile|Queued; not implemented.|
|22 Benchmarks|Software/USB/source timing exists; calibrated physical response/p95/p99 acceptance remains open.|

## Concrete correction in this review

Running Xbox audio did not receive MainWindow.Level_Checked changes. New bridge-to-delivery
configuration serializes the update with Accept, zeros the previous command, and prohibits
post-stop updates. No driver/service/firmware/HidHide configuration changed.
Windows Release build passes. HardwareProbe audit-xbox-safety reports live zero/resume/stop,
watchdog, malformed input and body-only channel tests true; PhysicalXboxHardwareTested=false.
Required contract run: TestResults/native-fallback-review-20261109.log (check final process
exit before declaring suite complete). Installed copy has not been republished by this increment.

## Preserved user priority

Investigate/fix YouTube seek-to-start recovery before implementing Custom editor. Then return
to profile/UI/startup/HidHide work, Shortcuts, Lighting, and finally discuss DSX feature scope.
Custom is a user editor, not another fixed preset. Current Custom audio series was cancelled
at the user's request; partial records must not be counted as a completed profile test.

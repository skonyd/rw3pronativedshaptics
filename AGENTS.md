# AI Handoff — rw3pronativedshaptics

Read README.md and docs/{PROJECT_BRIEF,RESEARCH_AND_REFERENCES,ARCHITECTURE,STATUS_AND_PERFORMANCE,VALIDATION_PLAN,ROADMAP,WINDOWS_APP}.md in that order at the start of every coding session.

## Non-negotiable user intent
- User owns Wolverine V3 Pro, DualSense and DSX+; prefers the Wolverine's shape because DualSense hurts/tire hands.
- Windows 11 PC story games are the target, not esports latency, Xbox, trigger resistance or 8 kHz polling.
- **Native DualSense Haptics** means game-authored, stereo haptic PCM forwarded to Wolverine's two body actuators with fidelity, not merely game audio → vibration or XInput rumble.
- Three mutually exclusive modes: Normal Rumble, Audio-to-Haptics, Native DualSense Haptics.
- Windows 11 double-click desktop app; clicking X minimizes to notification area, tray right-click changes mode; modern UX; portable ZIP and installer; version/update management.
- Public GitHub repo: https://github.com/skonyd/rw3pronativedshaptics

## Current experimental code (read before changing source/output)
- `NativeDualSenseCapture.cs` explicitly captures **only 4ch/48k** UAC audio and extracts rear haptic channels 3/4, not system stereo audio. It cannot prove the data comes from the game merely by receiving samples.
- `SensaStereoPrototype.cs` creates **two parametric Interhaptics events** targeting Palm Left and Palm Right, then modulates intensity from the captured waveform envelopes at 50 Hz. It is NOT direct PCM and not proven physically.
- `NativeBridgeWindow.xaml(.cs)` exposes source selection, status, side pulses and end-to-end **synthetic 4ch PCM** tests via `NativePcmTestPlayer.cs`. Tests must distinguish source capture, provider render and user physical sensation.
- `TestLabWindow.xaml(.cs)` provides game-free Normal XInput and Audio→XInput tests; no native synthetic data is ever mislabeled an actual game signal.
- Read `docs/NATIVE_BRIDGE_IMPLEMENTATION.md` and `interop/README.md` before relying on the native prototype. No third-party native DLLs are present in the repo.
- Current Windows app **has not passed an observed clean Windows CI build or actual Wolverine validation**; do not overstate readiness.
- Contract tests now cover 30 deterministic checks for provenance, stereo PCM16/Float32 isolation, speaker-only negative cases, malformed input and safe WASAPI silence handling; run them on Windows/CI.

## Engineering integrity
- Never present native PCM rendering through the physical Wolverine as operational until measured on actual hardware.
- Read each source in RESEARCH_AND_REFERENCES; distinguish confirmed hardware facts from hypotheses and external third-party project claims.
- Do not label XInput motor vibration as HD haptics. Do not say two voice-coil motors guarantee DualSense equivalence.
- Do not install drivers, hijack HID, patch firmware, or intercept arbitrary games without explicit user consent and rollback steps.
- Default to documented Razer/WYVRN APIs. The currently documented WYVRN workflow is *event + .haps mapping*, not proof of arbitrary low-latency raw PCM input.
- Respect licenses of DSX+, PadForge, ApexSenseBridge, Razer and Sony libraries. Never distribute proprietary binaries.
- Build/tests must run on Windows x64; Linux cannot validate WPF/device output. Fail clearly on unavailable controller/SDK.
- Preserve native stereo PCM before processing. Maintain separately verifiable mock/real backends and no silent fallback.
- When shipping, maintain changelog, reproducible signed hashes, no claim that unsigned executables are trusted.

## Live diagnostics and performance requirements
- Read docs/STATUS_AND_PERFORMANCE.md before changing mode labels, counters or backend implementations.
- App CPU/memory observations, WASAPI frame counts and XInput acknowledgements are telemetry evidence, **not** measured physical HD haptic output. Never mark native successful without three independent proofs.
- Keep WASAPI callbacks allocation-/I/O-light; bound processing and command rate; log only transitions/errors; retain below-normal priority and independent 250 ms silence watchdog. Test opposite-channel isolation, not just selected-channel activity.
- Run `dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj` and WPF build on Windows runner. Any original motor response needs real Windows 11 + Wolverine user confirmation.

## Development workflow
1. Inspect current commit, build status and issues; run build.
2. Implement one observable increment; add test or diagnostic log, include conditions and firmware versions.
3. Label feature as `working`, `prototype`, `blocked`, or `untested-on-hardware`.
4. Prioritize native output probe: independent L/R pulses, frequency, transient accuracy and safe stop; then game's stereo haptic capture.
5. Keep Windows app usable and explicit that 'Native' is blocked until bridge confirmed.
6. Update ROADMAP, references and hardware test logs with each discovery.

## Definition of done — native mode
Validated stereo DualSense game-authored signal captured, transmitted to Razer left/right VC actuators with no accidental mono collapse, measured latency/response and repeatable tests against a real DualSense. As of 2026-11-05 **not done**.

## v2 route / source truth (2026-11-06)
Read `docs/ADR-002-NATIVE-BRIDGE-ROUTE.md`. Do not write a new Windows audio driver while existing supported virtual DualSense Full endpoints are untested. Never bundle/inject/auto-install external drivers or proprietary Wwise runtimes. Source instrumentation distinguishes front speaker from rear haptic channels; 4ch PCM alone is not game-authored proof. Wwise offline WAV capability is NOT proof of supported live arbitrary PCM. Keep the experimental Interhaptics output flagged lossy. New CI coverage includes malformed Float32 frames and silent speaker-only negative tests.

## Physical A/B lab and reference invariant
`BehavioralHapticFixtures.Generate` emits the one deterministic synthetic 4ch PCM16 48kHz scene. The real USB DualSense reference must be sent **byte-for-byte unmodified by our application**; do not apply `HapticOutputLevel` or user gain to this path. Use WASAPI Exclusive when technically supported; never silently fall back or claim physical bit-perfect proof. Wolverine separately uses current **lossy** 50Hz parametric Sensa mapping; two-user level controls may scale ONLY Wolverine/RW3 outputs. The lab stores subjective user ratings, not game-authored PCM evidence or automatic physical-sensor verification. Any new device driver/Synapse/Wwise/Razer SDK still requires external user setup and license.

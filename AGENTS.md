# AI Handoff — rw3pronativedshaptics

Read README.md and docs/{PROJECT_BRIEF,RESEARCH_AND_REFERENCES,ARCHITECTURE,STATUS_AND_PERFORMANCE,VALIDATION_PLAN,ROADMAP,WINDOWS_APP}.md in that order at the start of every coding session.

## Non-negotiable user intent
- User owns Wolverine V3 Pro, DualSense and DSX+; prefers the Wolverine's shape because DualSense hurts/tire hands.
- Windows 11 PC story games are the target, not esports latency, Xbox, trigger resistance or 8 kHz polling.
- **Native DualSense Haptics** means game-authored, stereo haptic PCM forwarded to Wolverine's two body actuators with fidelity, not merely game audio → vibration or XInput rumble.
- Three mutually exclusive modes: Normal Rumble, Audio-to-Haptics, Native DualSense Haptics.
- Windows 11 double-click desktop app; clicking X minimizes to notification area, tray right-click changes mode; modern UX; portable ZIP and installer; version/update management.
- Public GitHub repo: https://github.com/skonyd/rw3pronativedshaptics

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
- Keep WASAPI callbacks allocation-/I/O-light; bound processing and command rate; log only transitions/errors; retain below-normal priority and watchdog or document alternatives.
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

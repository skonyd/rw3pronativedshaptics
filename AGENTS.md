# AI Handoff — rw3pronativedshaptics

## Next session reminder requested by user — 2026-11-08
At the beginning of the next user work session, remind them to revisit reducing85.33ms spectral history using a validated low/high-frequency hybrid and a separate short-transient path. The user explicitly asked for the next conversation session, not a dated notification. First finish/read the physical old/new comparison results; do not silently enable the failed2048 shortcut. This is a conversational handoff reminder, not permission to schedule autonomous runs or change drivers.

Read README.md, docs/IMPLEMENTATION_STATUS.md and docs/ROADMAP.md first, then docs/{PROJECT_BRIEF,RESEARCH_AND_REFERENCES,ARCHITECTURE,STATUS_AND_PERFORMANCE,VALIDATION_PLAN,WINDOWS_APP}.md before modifying source.

## Non-negotiable user intent
- User owns Wolverine V3 Pro, DualSense and DSX+; prefers the Wolverine's shape because DualSense hurts/tire hands.
- Windows 11 PC story games are the target, not esports latency, Xbox, trigger resistance or 8 kHz polling.
- **Native DualSense Haptics** means game-authored, stereo haptic PCM forwarded to Wolverine's two body actuators with fidelity, not merely game audio → vibration or XInput rumble.
- Three mutually exclusive modes: Normal Rumble, Audio-to-Haptics, Native DualSense Haptics.
- Windows 11 double-click desktop app; clicking X minimizes to notification area, tray right-click changes mode; modern UX; portable ZIP and installer; version/update management.
- Public GitHub repo: https://github.com/skonyd/rw3pronativedshaptics

## Current experimental code (read before changing source/output)
- `NativeDualSenseCapture.cs` explicitly captures **only 4ch/48k** UAC audio and extracts rear haptic channels 3/4, not system stereo audio. It cannot prove the data comes from the game merely by receiving samples.
- `SensaStereoPrototype.cs` creates **two parametric Interhaptics events** targeting Hand Left and Hand Right (GroupID 302, matching Razer DevKit configs; Palm 400 reached only the left actuator weakly in the first hardware test), then modulates intensity from the captured waveform envelopes at 50 Hz. It is NOT direct PCM and not proven physically.
- `NativeBridgeWindow.xaml(.cs)` exposes source selection, status, side pulses and end-to-end **synthetic 4ch PCM** tests via `NativePcmTestPlayer.cs`. Tests must distinguish source capture, provider render and user physical sensation.
- `TestLabWindow.xaml(.cs)` provides game-free Normal XInput and Audio→XInput tests; no native synthetic data is ever mislabeled an actual game signal.
- Read `docs/NATIVE_BRIDGE_IMPLEMENTATION.md` and `interop/README.md` before relying on the native prototype. No third-party native DLLs are present in the repo.
- Windows Actions **has passed** .NET 10 build, contract tests, portable/installer smoke on main (e.g., run 42223466818); **no actual Wolverine/DualSense physical motor verification** has been performed. Check latest run each session.
- Contract suite covers synthetic behaviors, USB identity, provenance, A/B ratings, original PCM/WAV copy, and bounded raw transport; avoid fixed outdated assertion-count claims. Run it on Windows CI.

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

## Hardware facts confirmed 2026-11-07 (see docs/HARDWARE_TEST_LOG.md)
- Wolverine wired PC mode = USB `1532:0A4C`; dongle / Xbox mode = `1532:0A3F`. Only 0A4C means "wired PC mode".
- Interhaptics `GroupID.Hand`(302) + `LateralFlag.Left/Right` reach the two body actuators separately (user-felt). Palm(400) did not reach the right.
- `PlayEvent` plays an effect once from "now"; never use short envelopes without re-triggering. The engine uses a 600 s envelope + `SetEventLoop` + re-trigger per onset.
- Main UI is a single tabbed window (Genel / Test / Ayarlar), fully controller-driven (LB/RB tabs, D-pad focus, A select, B back, Menu start/stop); controller input is ignored unless an app window is in the foreground.

## Native v2 engine (2026-11-07)
Read docs/SENSA_PIPELINE_ANALYSIS.md first. The Wolverine stream is parametric ("Sage T1": 3 bands / 4 points / 2 transients, 30–400 Hz, resonance 50 Hz); there is no PCM path to its actuators. v2 = `HapticFeatureExtractor` (1 ms slots) + 5 ms high-resolution engine tick using `UpdateParametricEffect`. Do not reintroduce `Thread.Sleep`-based ticks or single-intensity updates. Bands 2–3 need Razer's encoder (undocumented) — do not hand-pack frames.

## UI state (2026-11-07, later)
Main window only: Genel (modes auto-run on selection and at start; retries every 5 s when a source/runtime is missing), Test (motor test for Wolverine/DualSense/Xbox; 23 Native v2 demos playable on Wolverine or directly on DualSense), Ayarlar (output level, Native gain 140/200/300 %, XInput slot, autostart, updates, diagnostics). Removed: right-motor finder (Hand L/R confirmed), Native expert panel window, A/B lab window and its player. Library code they used (WAV export, transport probe, behavioral fixtures/rules) remains and is still covered by the contract tests.

## Physical A/B lab and reference invariant
`BehavioralHapticFixtures.Generate` emits the one deterministic synthetic 4ch PCM16 48kHz scene. The real USB DualSense reference must be sent **byte-for-byte unmodified by our application**; do not apply `HapticOutputLevel` or user gain to this path. Use WASAPI Exclusive when technically supported; never silently fall back or claim physical bit-perfect proof. Wolverine separately uses current **lossy** 50Hz parametric Sensa mapping; two-user level controls may scale ONLY Wolverine/RW3 outputs. The lab stores subjective user ratings, not game-authored PCM evidence or automatic physical-sensor verification. Any new device driver/Synapse/Wwise/Razer SDK still requires external user setup and license.

## Stereo WAV reference/capture invariant
Never alter audio bytes sent to the original physical USB DualSense reference. `HapticStereoWave` extracts rear UAC channels 3/4 into offline WAV by copying sample bits exactly; no normalization/resampling. PCM16 -> 44-byte WAV; IEEE Float32 -> 56-byte WAV with fact chunk. In-app source-only recording must remain explicit opt-in, bounded to 10s in RAM and wall time, with file save only after a separate user action. Bridge/endpoint stop and window close discard unsaved data. WAV export is **not** a real-time Razer high-fidelity output implementation and Wwise plugin/Motion distribution has licensing requirements.

## Latest canonical milestone snapshot
As of 2026-11-06, `main` `ad2aed3b` passed Windows CI run 42214409530. M0–M2 research baseline, Windows shell and experimental capture/A-B/WAV tools are implemented; M3 physical authentic-source + Wolverine stereo actuator acceptance remains open, M4 vendor-supported **live** high-fidelity waveform stream is **not implemented**. Consult [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md), rather than counting checkboxes or estimating a misleading percent-complete. The real 48kHz DualSense PCM remains unmodified by RW3 gain/EQ, while Razer currently uses **lossy 50Hz parametric** events.

## New realtime transport prerequisite (source-only)
`RealtimeStereoPcmTransport.cs` implements a bounded stereo 48k PCM ring and `StereoPcmDiscardProbe` independently drains original 960-sample blocks approximately every 20ms, without converting to 50Hz RMS or invoking Razer. `NativeDualSenseCapture.AttachTransport` must be allowed **only** when source-only capture is active, no recording is active, and no Sensa sink is attached. Detach before stopping/disposal and clear retained PCM. This is **not** a physical Sensa output backend, do not mark M4 complete. See docs/REALTIME_PCM_TRANSPORT.md.

## Low-latency source pipeline safeguard
`ConfigurableWasapiLoopbackCapture` requests explicit 10/20/50/100ms **shared WASAPI loopback** buffers; it does NOT use IAudioClient3 low-latency mode (incompatible with loopback). `StereoPcmDiscardProbe` is event-woken and consumes 5ms/240-frame blocks without fake silence on absent source callbacks. Maintain byte-identical source PCM, actual callback→dequeue Stopwatch timing and all loss counters. A smaller queue capacity can discard original samples on large WASAPI bursts and is **not** itself a latency-reduction guarantee. Do not equate queue age with game-to-motor latency. See docs/LATENCY_BUDGET.md.

## Audio review handoff — 2026-11-08 (supersedes stale tick/backend claims above)
Current Sensa output cadence is 33ms after observed provider backlog; 1ms feature slots are not a 1ms motor stream. PC audio→Sensa and Xbox WGI body/trigger prototypes now exist. AudioSinkDelivery bounds retained samples, preserves intentional delay, has independent 250ms stale-output silence and forbids post-stop delivery; source failure must clear health. Do not restore the old unbounded queue or no-op Sensa silence. AudioPathEquivalence now rejects missing/empty frames, missing pitch and both transient timing/amplitude mismatches; equalized fixtures remain limited evidence. Read docs/AUDIO_AUDIT_20261108.md and the updated roadmap before claiming hardware progress. New mic audit is blocked: WASAPI E_ACCESSDENIED, then Wolverine PC USB node absent; no completed new physical measurement. No privacy setting/driver changed.

Standalone MME microphone-only retry also failed (waveInOpen UnspecifiedError); no new physical measurements completed. Do not report the MME path as hardware-verified.

## Completed audio audit — 2026-11-08 (supersedes the blocked audit snapshot)
A4tech capture works when the same built probe DLL is explicitly run through installed dotnet.exe (.NET Host shown allowed); apphost .exe returns E_ACCESSDENIED. Do not globally change microphone privacy or call this packaged-app permission solved. 130 controlled cases completed: 8 controls +82 sweep +24 repeat +16 extended; extra 8 Voice-Clarity-on controls separate. Restore was completed: Voice Clarity + automatic Voice Focus, volume unchanged100%. Read docs/ACOUSTIC_RESULTS_20261108.md: 17/41 pairs below the descriptive background screen, repeat CV48–73%, one elevated tail; physical equivalence/reliability not established. No DualSense reference/game source was played in this run. Source cancellation completed but audio cancellation had weak acoustic positive control. Native M3/M4 remain open.
Read docs/SYNAPSE_INDEPENDENCE.md for the user's pasted-conversation plan and vendor-dependency research. No Claude account was accessed; use the original attachment/chat. Current Sensa still requires vendor runtime; no Razer service, driver or firmware changed. Do not interpret provider direct-branch static notes as a proven standalone USB backend or implement unknown vtables/frame encoders.

## Repeated scenario audit — 2026-11-08 (latest)
Read docs/SCENARIO_AUDIT_20261108.md. Added 68 real one-mic cases: 48 engine/mic-reuse factorial, 12 final cold controls, eight unity-input live cases. Final cold controls also improve, so time/settling confounds reuse; no proven cure or physical fidelity pass. Voice Clarity + automatic Voice Focus restored, mic volume100%. Default SensaAudioSink input is now unity1, legacy2.2 diagnostic only: unscaled old gain clips27.67–69.67% of active sine samples at levels.5–1. Existing Wolverine output140% still saturates strong envelopes. Never restore automatic2.2 or equate zero input clips with lossless output. NaN/Infinity becomes counted silence. Comparator now retains partial final33ms frames and validates missing pitch/hits/timing/amplitude. 4,452 tone cases pass but nine of240 short-pulse/mixed stress cases fail strict frequency tolerance; preserve these failures. Independent raw PCM polarity/multitone checks demonstrate representational loss. Xbox mapping/watchdog tests inject a diagnostic command writer; physical Xbox output was not tested. WPF Release build/contracts pass locally; M3/M4 stay open.

## Opt-in multiband increment — 2026-11-08 (newest)
Read docs/MULTIBAND_RESULTS_20261108.md. Public AddHM/UpdateHM multi-melody `.haps` DOES change previously unused provider regions repeatably, unlike earlier parametric/multiple-event tests. Do not repeat the blanket claim that all public output is limited to one band or requires unknown encoder signatures. ExperimentalMultiband is persisted, defaultfalse, applies to main Native/PC audio/demos; retained channel/endpoint lab still uses its separately identified old backend. Bounded original4096-sample spectral history, consumer-thread FFT (zero warmed managed allocations), stable low-to-high components, shared headroom scale,66ms amplitude-zero tail, no silent fallback; phase and separate transient replay remain unsupported.240 original-tone cases pass;2048/42.67ms diagnostic profile fails55/240 low30–40Hz cases and is NOT a production option. Existing33ms render remains. Source spectrum has85.33ms history, not measured end-to-end latency. Provider debug accessor throws on idle `.haps` without a material frame: normal idle render is used instead. Stop clears/disables spectral history.
108 controlled raw-provider cases, live PCM tests including16 mic cases, eight Windows-loopback cases and12 cadence cases completed; no physical DualSense/game or calibrated physical spectrum/phase/latency proof. Latest mic settings left unchanged, may be processed. Read docs/LATENCY_BUDGET.md: official33ms statement is in WYVRN event-service context, not universal immutable hardware proof.30ms cadence trials show greater acoustic tails; keep33ms, retain the weak/contaminated30ms repeat, do not use USB polling as motor rate. No drivers/services/firmware changed. UI checkbox/scrolling verified; software builds/contracts pass. M3/M4 remain open.

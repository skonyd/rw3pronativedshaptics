# Roadmap — 2026-11-08

The opt-in Xbox audio/game bridge is implemented as a prototype. Verify live
HID callbacks, real-game input ownership, physical LT/RT plus audio, and HidHide
rollback before marking it ready. [Routing evidence](HAPTIC_ROUTER_AUDIT_20261109.md).
Audio mode lets users enable Xbox game body rumble and LT/RT independently.
Either game source activates the same opt-in virtual bridge.

Audio mode now offers local Controlled, Balanced and Dynamic profiles based on measured
Synapse cue ranges and gains. Custom editing and same-source physical comparison remain
open. [Capture results and preset implementation](SYNAPSE_USB_PROFILE_STUDY_20261108.md).

The General screen now has per-source Native checkboxes and an Xbox trigger
destination switch. Remaining work includes capturing real Xbox game feedback
for simultaneous Audio mode output and physical/real-game acceptance.
[Routing and test evidence](HAPTIC_ROUTER_AUDIT_20261109.md).

2026-11-09 follow-up: [current router/coexistence audit](HAPTIC_ROUTER_AUDIT_20261109.md).
Native report rumble/adaptive-trigger routing already exists; independent source switches,
general priority mixer and Audio plus Xbox-game impulse preservation remain open.
Unproven continuous endpoint polling and temporary audio traces were removed and the clean
build installed. Safe-stop/watchdog retained; Opera foreground failure is not fixed.

**Current reconciled plan:** [conversation snapshot](CURRENT_PLAN_20261108.md). Includes the user's later positive but approximate combined-demo physical feedback, remaining real-game/Classic/coexistence/safety acceptance, deferred latency work and a concrete live-level gap in the separate Xbox audio route. This supersedes older blanket “Synapse required” and “no user-felt Xbox output” claims below; historical tests remain unchanged.

Local desktop preparation completed: original vector/controller-wave icon, multi-size Windows ICO, EXE/window/tray/installer integration, per-user program-folder copy and Start Menu shortcut. Published self-test passes and shortcut-launched UI was visually checked. See [Windows desktop setup](WINDOWS_APP.md#local-desktop-setup--2026-11-08). Public installer/signing/update and physical Native acceptance remain separate gates.

The Native test card now includes the [combined 35s seven-phase demo](XBOX_NATIVE_ROUTING_20261108.md#combined-demo-on-the-native-test-card) needed for the next physical acceptance: same-side, stereo, crossed body/trigger and independent off controls with live instructions. Software source/wire tests pass; retain physical verification as open.

Latest user increment: [Native trigger conversion / Audio separation](XBOX_NATIVE_ROUTING_20261108.md). Software implemented/tested: actual DualSense report + device-bound physical pull → Xbox impulse motors, simultaneous rear-PCM body output, live global level and safe-stop guards. Audio no longer synthesizes impulse feedback or writes four-channel WGI state. Next gate is a visible wired Wolverine and real L2/R2 ranges, followed by simultaneous trigger/body and normal Xbox-game impulse coexistence tests. Four read-only attempts found no Wolverine; no physical acceptance was obtained. Review [standalone status](STANDALONE_STATUS_20261108.md) for already committed Direct USB/Hybrid work before repeating old vendor-dependency tasks.

## Objective from the conversation

Use the comfortable Wolverine V3 Pro for Windows 11 story games while preserving as much of the game-authored DualSense body-haptic signal as the supported output path permits. Keep Normal Rumble, Audio-to-Haptics and Native DualSense Haptics distinct. Synthetic game-free tests must exercise production input/processing, preserve original source bytes and disclose conversion losses. A successful software test must never become a claim of physical DualSense equivalence.

**M3 remains open. M4 supported live high-fidelity output is not implemented.** Main `2d59f2d` passed Windows CI run 42232498582; local uncommitted changes require their own publication/CI. [Current evidence](IMPLEMENTATION_STATUS.md), [this review](AUDIO_AUDIT_20261108.md).

## Ordered work and acceptance gates

| Order | Work | Status / acceptance |
| --- | --- | --- |
| 1 | Make audio comparison and stop behavior trustworthy | **Working in software:** missing/empty frames, missing frequency, both transient amplitudes/timings, invalid values, independent 250ms watchdog, bounded serialized delivery, safe stop and source failure. Windows contracts/build pass. Physical stop remains unverified in this review. |
| 2 | Compare both Wolverine paths using the single A4tech microphone | **Acquisition completed:** .NET Host captures real A4tech samples; apphost .exe access remains denied. 130 controlled cases completed, Voice Clarity restored. Repeatability/fidelity acceptance remains open; see ACOUSTIC_RESULTS_20261108.md. |
| 3 | Frequency, level, stereo, transient and repeatability coverage | **Software grid passed:** 4,452 equalized tone cases plus finite demo/onset catalogue. **Hardware observations completed:** 82 sweep + 24 repeat + 16 extended cases, including separate right, stereo, bursts, alternating, 5s sustain and source cancellation. Only 24/41 sweep pairs clear the descriptive background screen; repeats vary substantially. This does not pass physical equivalence. Record controls/tails and uncertainty. Do not tune EQ from an uncalibrated airborne mic. |
| 4 | Stabilize Audio-to-Haptics for daily use | **Prototype:** PC→lossy Sensa and Xbox-mode WGI body/trigger output. Validate unclipped/general audio, mute/volume changes, endpoint loss/reconnect, 44.1/48/96k devices, sleep/resume, burst/queue faults and a >=10-minute session. Front L/R-only selection needs an explicit surround/downmix policy. Bluetooth 300ms auto delay is a heuristic; validate per endpoint. Test Xbox route only with the corresponding hardware mode. |
| 5 | Prove authentic game-authored native source and controller association | **Open:** physical 4ch/48k DualSense endpoint and synthetic Windows loopback→Wolverine route were tested previously and the user felt Wolverine output. Select one supported real game, record game/build/settings and prove rear-channel authorship plus Wolverine input→physical/virtual DualSense association without duplicate input. Existing endpoints come before new drivers. Explicit local HIDMaestro setup/self-test code does not prove this. No auto-install or arbitrary-game interception. |
| 6 | Physical DualSense/Wolverine A/B | **Pending later, per user:** send the same canonical source byte-for-byte to physical USB DualSense via Exclusive only, fail clearly if unsupported. Wolverine uses disclosed lossy conversion and its own level/gain. Repeat stereo/transient/frequency/stop comparisons. One mic provides relative acoustic observations, not displacement equivalence or absolute actuator latency; stronger claims need calibrated sensing and repeatable reference measurements. |
| 7 | Improve output fidelity through supported vendor interfaces | **M4 blocked:** Wolverine route is parametric, one frequency/four envelope points/two transients per side. Bands 2–3 require the undocumented vendor encoder; do not hand-pack frames. Seek a documented, licensed live richer output interface. Wwise offline WAV support does not establish a live arbitrary-PCM backend. No hardware/software promise of lossless DualSense equivalence. |
| 8 | Release a usable Windows app | **Shell implemented, release acceptance pending:** Genel/Test/Ayarlar, auto-run/retry, foreground controller navigation, tray close/right-click modes, diagnostics, portable ZIP/installer, version/update management. Run current Windows CI/build/contracts and installer smoke after publication, verify sustained gameplay frame times/CPU/RAM, package only licensed redistributables, publish hashes/changelog; do not call unsigned binaries trusted. |

## Current practical next step

The `.haps` multiband increment is implemented experimentally, default off: [results](MULTIBAND_RESULTS_20261108.md). Provider payloads retain more structure than the old single-band controls, and current Native PCM/Windows audio tests complete. Next validate old/new physical benefits with adequate SNR and counterbalanced sources, short/nearby-tone coverage and sustained performance. Keep phase/waveform equivalence open.4096-window source tests pass240 cases;2048 fails55 low-frequency cases and is not enabled. Re-evaluate33ms using official event-service scope and separately measured queue behavior, not USB polling rate.

The one-microphone access blocker is resolved for the explicitly selected .NET Host. The engine/mic reuse experiment is now complete: 48 factorial cases plus 12 final cold controls and eight unity live cases. Later cold controls also improve, leaving elapsed-time/settling confounded with reuse. Next use predefined settling and counterbalanced groups across independent sessions before generalizing small later CVs. Default audio input gain was changed from 2.2 to unity because an independent unscaled grid proves input clipping; output 140% still reaches the envelope ceiling at strong levels. Nine short-pulse/resampling feature comparisons remain outside strict tolerance. Do not tune EQ or discard early cases to force equality. Then test sustained daily audio, actual endpoint loss/recovery, mute/volume and Xbox hardware, followed by the deferred physical DualSense reference. See [new detailed results](SCENARIO_AUDIT_20261108.md), [earlier results](ACOUSTIC_RESULTS_20261108.md) and [conversation plan / Synapse independence](SYNAPSE_INDEPENDENCE.md).

The 33ms Sensa output pacing follows observed Synapse/provider backlog; do not reinstate 5ms output just because source analysis uses 1ms slots. Original DualSense PCM is never altered by Wolverine gain/EQ. No new device driver or proprietary runtime was installed in this review.

## Retained foundation

Phase/multitone improvement: [staged plan](PHASE_AND_MULTITONE_PLAN.md). Test documented multi-melody `.haps` rendering before assuming the whole SDK is restricted to the prototype's one-band representation. Generic multi-melody support is not Wolverine capability proof; earlier multi-event tests merged into one band. Carrier-phase control is not documented in the current parametric API. Production output remains unchanged pending independent evidence.

M0–M2 cover research/licensing, Windows shell, strict rear-PCM capture, deterministic source/evidence fixtures, original stereo WAV/10s opt-in RAM recording and event-woken original-PCM discard transport. These libraries do not establish a physical waveform sink. The current game-free channel card and independent endpoint source distinguish synthetic replay, real Windows source capture, provider rendering and user-felt output. See [channel lab](CHANNEL_LAB.md), [hardware log](HARDWARE_TEST_LOG.md), [validation](VALIDATION_PLAN.md), [ADR-002](ADR-002-NATIVE-BRIDGE-ROUTE.md) and [pipeline analysis](SENSA_PIPELINE_ANALYSIS.md).

## Synapse-independent operation requested — 2026-11-08

The original supplied conversation plan was reviewed: mode-aware audio prototypes exist; game-rumble→Sensa and virtual-DualSense trigger conversion remain open. Full Synapse independence is a separate research/output-backend milestone. Current official and PadForge routes retain the vendor runtime. Read-only local inventory and third-party static analysis distinguish GUI, mixer and final device transport. No driver/service was changed; no standalone output backend was introduced. See [the concrete staged plan](SYNAPSE_INDEPENDENCE.md).

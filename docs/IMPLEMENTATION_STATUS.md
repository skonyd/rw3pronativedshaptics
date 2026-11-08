# Implementation status — 2026-11-07

Native test-card addition: [35s combined haptic/trigger demo](XBOX_NATIVE_ROUTING_20261108.md#combined-demo-on-the-native-test-card), with seven left/right/stereo/cross/isolation phases and live physical-pull instructions. Uses existing synthetic 4ch PCM + HID game-format path, not a motor shortcut. Phase/isolation/off contracts and WPF build pass; physical sensation remains unverified.

2026-11-08 routing correction: [Native Xbox / Audio separation](XBOX_NATIVE_ROUTING_20261108.md) supersedes older Xbox WGI body+trigger claims below. Native virtual-bridge Xbox input/output now bind the same strict Wolverine GameInput device; real report effects plus physical pull drive impulse motors alongside rear-PCM body envelopes. Global level, immediate input-loss zero, independent watchdog and stop guards are implemented. Audio now writes only XInput body channels. Windows build/contracts and audio command audit pass; four live read-only attempts found no present Wolverine and prove no new physical output. Game impulse-trigger coexistence in Audio mode and genuine-game Native acceptance remain open. Prior Direct USB/Hybrid changes are described in [standalone status](STANDALONE_STATUS_20261108.md); older vendor-only descriptions below are historical.

**M3 open; M4 blocked / not implemented.** Native v2 is a lossy prototype, not a validated authentic-game high-fidelity bridge.

Newest increment: [opt-in multiband `.haps` prototype](MULTIBAND_RESULTS_20261108.md) now exists alongside the default single-band path. Public AddHM/UpdateHM with separate melodies changes additional provider payload regions repeatably; earlier one-band observations were specific to parametric/multiple-event tests, not the entire SDK.240 source-spectrum cases pass;42.7ms diagnostic profile fails55/240 low-tone cases. Live PCM and Windows audio routes completed; phase, nearby-tone/short-hit fidelity and physical latency remain unverified. This does not close M4 waveform-fidelity acceptance.

Latest local increment (2026-11-08): [scenario audit](SCENARIO_AUDIT_20261108.md) adds 68 physical one-mic cases, comparator partial-tail regression, independent phase/multitone oracles, Xbox safety command-writer tests and unity audio input. WPF Release build has zero warnings/errors and required contracts pass. 4,452 finite tones match feature tolerances; **nine of 240 stress cases fail**. Later repeats are steadier, but final cold controls also improve, so reuse is not a proven cure. 140% output still saturates strong signals. This does not close M3/M4 or establish production readiness.

## Evidence and implemented behavior

| Area | Current evidence | Limits |
| --- | --- | --- |
| Main baseline | `2d59f2d`, [successful CI 42232498582](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42232498582) | Does not cover uncommitted v2/UX changes or hardware |
| Desktop | Single Genel/Test/Ayarlar window, tray, auto-run/retry, foreground controller navigation | Real installation/update/game performance acceptance remains |
| Normal / Audio | Game-managed rumble; local PC audio→Sensa and Xbox WGI body/trigger prototypes, generic XInput route | Lossy Sensa output; current PC-mode hardware cannot validate Xbox-mode output |
| Native source | Strict 4ch/48k rear PCM; compatible name-based endpoint discovery; 10ms buffer request | No real-game provenance or input-to-virtual-DualSense mapping proof |
| Native v2 output | 1ms feature slots; current 33ms output pacing after provider-backlog findings; amplitude/frequency/transients, Hand L/R, 30–400Hz | Lossy one-band parametric mapping; bands 2–3 encoder undocumented; physical latency/fidelity unproven |
| Hardware | User felt Hand Left/Right body pulses; provider frame separation and acoustic observations recorded | Subjective isolation varied; acoustic desk/mic timing is not calibrated actuator measurement |
| Test | 23 synthetic demos on Wolverine or DualSense; side tests and physical-feel answers | Synthetic signal is not game-authored evidence |
| DualSense reference | Unmodified quad PCM16 Exclusive only; unsupported endpoints fail without Shared fallback | Physical response and bit-perfect hardware output unverified |
| Settings | RW3 0–100% output and 140/200/300% Native gain | Only Wolverine/RW3 outputs; never scale DualSense reference |
| Retained libraries | Deterministic behavioral fixtures, ratings rules, WAV/10s opt-in RAM capture, original 48k transport | Separate A/B, expert and source-only diagnostic windows removed; no current UI entry points |

## Hardware conditions and next acceptance

Prior tests: Windows 11 build 26320, Synapse 4.0.896, Interhaptics v1.8.0, Wolverine wired PC `1532:0A4C`; exact controller firmware version was not recorded. [Hardware log](HARDWARE_TEST_LOG.md), [pipeline analysis](SENSA_PIPELINE_ANALYSIS.md).

1. Repeat side isolation, silence/STOP/unplug/reconnect and idle-output checks on actual hardware ([#1](https://github.com/skonyd/rw3pronativedshaptics/issues/1)).
2. Prove real-game rear PCM and physical/virtual input/audio binding, without double input ([#2](https://github.com/skonyd/rw3pronativedshaptics/issues/2), [#5](https://github.com/skonyd/rw3pronativedshaptics/issues/5)).
3. Compare unchanged DualSense reference to lossy Wolverine with repeatable subjective and sensor measurements, and real game frame times ([#3](https://github.com/skonyd/rw3pronativedshaptics/issues/3)).
4. Establish a documented, licensed live high-fidelity destination before claiming M4 ([#4](https://github.com/skonyd/rw3pronativedshaptics/issues/4)). Wwise offline WAV support does not prove live PCM support.

Audio-to-Sensa and Xbox WGI backends now exist locally as prototypes; game-rumble-to-Sensa remains unimplemented. Explicit virtual-controller setup is separate from proven input mapping. No driver/SDK was installed in this audit. No overall completion percentage is defined. See [ROADMAP](ROADMAP.md) and [VALIDATION_PLAN](VALIDATION_PLAN.md).

## Channel conversion lab increment — 2026-11-07

Test card **DualSense → W3 Pro** adds seven channel/negative-control scenes plus the 23 demos, one canonical unmodified 4ch PCM source, the shared production decoder boundary, strict separate DualSense Exclusive reference and optional microphone level metering. Original-source SHA256 and software saturation/channel statistics are separate from acoustic observations. This is synthetic replay, not USB/WASAPI source capture or game-authored evidence; M3/M4 remain open. See [channel lab contract and procedure](CHANNEL_LAB.md).

## Physical USB endpoint / full loopback increment — 2026-11-07

After the user connected DualSense, Windows exposed a physical **4ch/48k Float32 quad** render endpoint. The lab now has a separate independent-process Shared source → real WASAPI → v2 → Wolverine route, with an 8-second bounded source-only sample-comparison pass before live Sensa. Controls verified exact active source samples at unity Windows level, rear-channel isolation and no rear activity for speaker-only/silence. The user confirmed vibration on Wolverine. This proves a **synthetic full Windows route**, not actual game authorship or DualSense-equivalent physical fidelity. The Exclusive reference is unchanged; no new driver was installed. Optional temporary source unmute/unity level is explicit and restored after the test. See [route details](CHANNEL_LAB.md) and the hardware log. M3/M4 remain open.

## 2026-11-08 audit outcome

Microphone access now works through the explicitly selected installed .NET Host. Apphost permission remains a separate release check. The aggregate-only A4tech/Wolverine audit completed 130 controlled cases, including 82 frequency/level paths, 24 repeats, stereo/bursts/alternating/5s sustain/source cancellation. Previous mic processing restored; no vendor service or driver changed. This is **physical acoustic observation**, not a calibrated DualSense reference or authentic-game validation. Repeat variation and unresolved weak pairs keep M3/M4 open. [Results](ACOUSTIC_RESULTS_20261108.md).

The user-supplied earlier conversation plan was reviewed. Mode-aware audio prototypes exist; game-rumble→Sensa and virtual-DualSense trigger conversion remain open. True standalone Sensa remains research; the current route depends on the vendor runtime. [Concrete staged plan](SYNAPSE_INDEPENDENCE.md).

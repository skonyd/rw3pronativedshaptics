# RW3 Pro Native DS Haptics

Windows 11 desktop application and research project for game-authored DualSense body haptics on Wolverine V3 Pro.

**Current state: [docs/STATUS.md](docs/STATUS.md)** (one page, kept up to date). Documentation index: [docs/README.md](docs/README.md). The dated notes below are history.

2026-11-08: [Native Xbox trigger routing / Audio-mode separation](docs/XBOX_NATIVE_ROUTING_20261108.md). Native can approximate game trigger commands on impulse motors alongside haptic body output; Audio generates body output only. New routing is software-checked, not newly verified on physical hardware. See [standalone Direct USB status](docs/STANDALONE_STATUS_20261108.md) for later work beyond the historical milestones below.

**2026-11-07: Native v2 is a lossy prototype. M3 remains open; M4 high-fidelity live output is not implemented.** A real game's authored source and end-to-end fidelity have not been validated. Prior user-felt Wolverine Hand Left/Right pulses are recorded in [hardware tests](docs/HARDWARE_TEST_LOG.md); they do not prove waveform fidelity.

## Current application

One controller-driven window: **Genel / Test / Ayarlar**. Modes run when selected and at startup; missing sources/runtimes retry every 5 seconds. Tests pause the selected mode, which resumes afterwards. Close/minimize hides to tray; tray right-click changes mode or exits. Updates are in Ayarlar.

- **Normal Rumble:** the game manages ordinary XInput vibration.
- **Audio-to-Haptics:** local PC-mode audio → shared lossy Sensa engine; Xbox-mode Windows.Gaming.Input body/trigger rumble prototype; generic XInput route remains separate. Hardware acceptance is incomplete.
- **Native DualSense:** strict 4ch/48k PCM16 or Float32 source, rear channels 3/4 into a lossy v2 feature extractor and Interhaptics. The bridge requests a 10ms loopback buffer; Windows may grant more. The current engine paces output at 33ms to avoid provider backlog, with amplitude, frequency and transients. Its 1ms source features do not imply 1ms actuator output. This is not 48k waveform replay or proof of game provenance.

Test includes side pulses and **23 synthetic demos** for Wolverine or USB DualSense. DualSense reference playback uses unchanged quad PCM16 in **WASAPI Exclusive only**; unsupported/busy endpoints fail explicitly, with no Shared fallback. RW3 gain affects Wolverine only. Software playback does not establish physical response or physical bit-perfect output.

Ayarlar includes output level, Native gain 140/200/300%, XInput slot, startup, updates and diagnostics. LB/RB switches tabs, D-pad moves focus, A selects, B goes back; input is ignored while the app is not foreground.

2026-11-08 scenario audit: audio input now uses unity instead of clipping-prone 2.2 gain; Wolverine output gain remains separate. Comparator tail/missing-pitch/transient regressions and software stop safety are tested. 4,452 tone cases pass feature tolerances, but nine of 240 pulse/complex cases remain outside them. Sixty-eight additional one-mic cases show time-dependent repeat variation and output saturation at 140% gain. This is **not physical DualSense equivalence**. See [full results and remaining gates](docs/SCENARIO_AUDIT_20261108.md).

Settings also offers **Deneysel çok bantlı Wolverine çıkışı** (default off) for Native, PC audio and Wolverine demos. Original-sample FFT→documented multi-melody `.haps` carries up to three separated components; repeatable provider payloads and real Windows loopback are tested.85ms history, unresolved nearby tones, phase/short-transient loss and unverified physical spectra remain. The42.7ms shortcut failed low-frequency tests and is not enabled. [Multiband results](docs/MULTIBAND_RESULTS_20261108.md).

The right-motor finder, expert bridge window, separate Test Lab and A/B rating window were removed. Their fixture/evidence, source-only recording/WAV and raw transport library code remains contract-tested; these are **not current UI features**. The old nine-route catalog is retained library code, not a visible selector.

## Build and verification (Windows x64)

Requires .NET 10 SDK:

```powershell
dotnet build src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj -c Release -warnaserror
dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj -c Release
dotnet run --project src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj
```

Main baseline `2d59f2d` passed [Windows CI 42232498582](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42232498582). Uncommitted v2/UX changes are not covered by that run. CI builds portable ZIP/installer and hashes; it cannot validate physical controllers. See [Windows app](docs/WINDOWS_APP.md).

## Setup and remaining work

Sensa requires the compatible Wolverine PC mode, Synapse/Chroma and legally user-supplied HAR.dll / Interhaptics.RazerProvider.dll; proprietary binaries are not distributed. Local tests observed wired PC identity `1532:0A4C`; Xbox/dongle `1532:0A3F` had trigger response but no Sensa SDK device. See [pipeline analysis](docs/SENSA_PIPELINE_ANALYSIS.md).

Native automatically picks a compatible endpoint named DualSense/Wireless Controller. Its name and active rear PCM cannot establish authorship or physical identity. A game needs a compatible physical or user-installed virtual DualSense input/audio association. Local Settings code includes explicit HIDMaestro setup/self-test actions; it does not prove game input mapping. No driver was installed in this audit. Audio-to-Sensa and Xbox WGI backends exist locally as prototypes; game-rumble-to-Sensa remains unimplemented.

See [implementation status](docs/IMPLEMENTATION_STATUS.md), [roadmap](docs/ROADMAP.md), [validation](docs/VALIDATION_PLAN.md), [research references](docs/RESEARCH_AND_REFERENCES.md), [ADR-002](docs/ADR-002-NATIVE-BRIDGE-ROUTE.md) and [AGENTS.md](AGENTS.md).

## Channel conversion lab increment — 2026-11-07

Test card **DualSense → W3 Pro** adds seven channel/negative-control scenes plus the 23 demos, one canonical unmodified 4ch PCM source, the shared production decoder boundary, strict separate DualSense Exclusive reference and optional microphone level metering. Original-source SHA256 and software saturation/channel statistics are separate from acoustic observations. This is synthetic replay, not USB/WASAPI source capture or game-authored evidence; M3/M4 remain open. See [channel lab contract and procedure](docs/CHANNEL_LAB.md).

## Physical USB endpoint / full loopback increment — 2026-11-07

After the user connected DualSense, Windows exposed a physical **4ch/48k Float32 quad** render endpoint. The lab now has a separate independent-process Shared source → real WASAPI → v2 → Wolverine route, with an 8-second bounded source-only sample-comparison pass before live Sensa. Controls verified exact active source samples at unity Windows level, rear-channel isolation and no rear activity for speaker-only/silence. The user confirmed vibration on Wolverine. This proves a **synthetic full Windows route**, not actual game authorship or DualSense-equivalent physical fidelity. The Exclusive reference is unchanged; no new driver was installed. Optional temporary source unmute/unity level is explicit and restored after the test. See [route details](docs/CHANNEL_LAB.md) and the hardware log. M3/M4 remain open.

## Audio review — 2026-11-08

See the [single-microphone audit and software results](docs/AUDIO_AUDIT_20261108.md) and [ordered roadmap](docs/ROADMAP.md). Stronger comparison and output-stop safeguards pass Windows software checks. The completed acoustic measurements below supersede the initial access blocker; no physical equivalence claim follows from the software grid.

The [completed 2026-11-08 microphone run](docs/ACOUSTIC_RESULTS_20261108.md) supersedes its earlier access-blocked snapshot: 130 controlled cases were acquired through the explicitly selected .NET Host. Repeat variation/weak acoustic pairs leave physical equivalence and readiness open. See [the supplied conversation plan and Synapse-independence research](docs/SYNAPSE_INDEPENDENCE.md); no standalone Sensa backend or driver change was introduced.

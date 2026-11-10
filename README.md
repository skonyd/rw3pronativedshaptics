# RW3 Pro Native DS Haptics

A Windows 11 desktop app that brings DualSense-style haptics to the Razer Wolverine V3 Pro.

**Current state: [docs/STATUS.md](docs/STATUS.md)** (one page, kept up to date). Documentation index: [docs/README.md](docs/README.md).
Shared test and analysis log for every AI and person: [docs/research/](docs/research/README.md). Search it before you test
anything, and add a row afterwards.

## What it does

| Mode | Wolverine in PC mode (1532:0A4C) | Wolverine in Xbox mode (1532:0A3F) |
| --- | --- | --- |
| **Normal** | the game's own rumble | the game's own rumble and trigger motors |
| **Sesten titreşim** (audio → vibration) | Sensa palms, stereo | Sensa palms over Razer's GIP route, stereo (setting off: body motors) |
| **Oyunun DualSense titreşimi** (virtual DualSense) | haptic PCM → palms; trigger effects → body motors, or the palms ("avuçta tetik hissi") | haptic PCM → palms; trigger effects → **trigger motors** (Sensa + GameInput), in the same frame; game rumble → body motors |

- **No Synapse, no Razer DLLs.** RW3 encodes the Sensa frames itself:
  - PC mode: HID feature report 4 ("Direct USB"), over the cable and the HyperSpeed dongle.
  - Xbox mode: the same Razer command tunnelled in GIP message 0x0F through `Windows.Gaming.Input.Custom`.

  Synapse/HAR remains an optional legacy backend.
- **Trigger effects** (DualSense resistance, weapon, bow, vibration) become vibration on the trigger motors. A weapon
  trigger's vibration rises toward the break point, gives a strong click at the break, then stops. The Wolverine
  has no resistive trigger motors, so this is never physical force.
- **Tests:** motor test, 23 Native v2 demos, game simulator, and the blind **Genel kalibrasyon testi**, run from the
  controller. The calibration covers palms, triggers and grips at varied strengths, pitches and mixes, plus
  "1 or 2" comparisons. It saves raw data and a summary.
- One window (**Genel / Test / Ayarlar**), fully controller-driven: LB/RB tabs, D-pad focus, A select, B back,
  Menu changes the mode. Closing hides it to the tray.

How the Xbox-mode route was found (firmware analysis, USB captures, emulated firmware decoder, blind tests):
- [docs/research/XBOX_MODE_HAPTICS_ROUTE.md](docs/research/XBOX_MODE_HAPTICS_ROUTE.md)
- [docs/FIRMWARE_ANALYSIS_20261110.md](docs/FIRMWARE_ANALYSIS_20261110.md)
- [docs/SENSA_USB_PROTOCOL.md](docs/SENSA_USB_PROTOCOL.md)

## Build and verification (Windows x64)

Requires the .NET 10 SDK:

```powershell
dotnet build src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj -c Release -warnaserror
dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj -c Release
dotnet run --project src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj
```

CI builds the portable ZIP, the installer and their hashes. It cannot exercise a physical controller; hardware
results are in [docs/STATUS.md](docs/STATUS.md) and [docs/research/EXPERIMENTS.md](docs/research/EXPERIMENTS.md).
The virtual DualSense uses HIDMaestro ([tools/get-hidmaestro.ps1](tools/get-hidmaestro.ps1)) and needs administrator
rights once. See [docs/WINDOWS_APP.md](docs/WINDOWS_APP.md).

## Limits

- **Not physical DualSense equivalence.** Sensa is a parametric actuator: bands, points and transients per 33 ms
  frame, not a 48 kHz waveform.
- **Xbox mode is a lower-resolution stream:** 2 bands × 2 points per palm (PC mode: 3 × 4).
- **No trigger motors in PC mode:** the firmware exposes them only in Xbox mode.
- **Not yet confirmed in real games:** the Xbox-mode Sensa route was verified with blind hand-held tests, the
  calibration and the game simulator, not yet in a real game.

## History

The dated notes below describe earlier stages. Some of their statements (for example "Sensa needs Synapse/HAR" or
"no Sensa in Xbox mode") are superseded; [docs/STATUS.md](docs/STATUS.md) is authoritative.

2026-11-08: [Native Xbox trigger routing / Audio-mode separation](docs/XBOX_NATIVE_ROUTING_20261108.md). Native can approximate game trigger commands on impulse motors alongside haptic body output; Audio generates body output only. New routing is software-checked, not newly verified on physical hardware. See [standalone Direct USB status](docs/STANDALONE_STATUS_20261108.md) for later work beyond the historical milestones below.

**2026-11-07: Native v2 is a lossy prototype. M3 remains open; M4 high-fidelity live output is not implemented.** A real game's authored source and end-to-end fidelity have not been validated. Prior user-felt Wolverine Hand Left/Right pulses are recorded in [hardware tests](docs/HARDWARE_TEST_LOG.md); they do not prove waveform fidelity.

## Channel conversion lab increment — 2026-11-07

Test card **DualSense → W3 Pro** adds seven channel/negative-control scenes plus the 23 demos, one canonical unmodified 4ch PCM source, the shared production decoder boundary, strict separate DualSense Exclusive reference and optional microphone level metering. Original-source SHA256 and software saturation/channel statistics are separate from acoustic observations. This is synthetic replay, not USB/WASAPI source capture or game-authored evidence; M3/M4 remain open. See [channel lab contract and procedure](docs/CHANNEL_LAB.md).

## Physical USB endpoint / full loopback increment — 2026-11-07

After the user connected DualSense, Windows exposed a physical **4ch/48k Float32 quad** render endpoint. The lab now has a separate independent-process Shared source → real WASAPI → v2 → Wolverine route, with an 8-second bounded source-only sample-comparison pass before live Sensa. Controls verified exact active source samples at unity Windows level, rear-channel isolation and no rear activity for speaker-only/silence. The user confirmed vibration on Wolverine. This proves a **synthetic full Windows route**, not actual game authorship or DualSense-equivalent physical fidelity. The Exclusive reference is unchanged; no new driver was installed. Optional temporary source unmute/unity level is explicit and restored after the test. See [route details](docs/CHANNEL_LAB.md) and the hardware log. M3/M4 remain open.

## Audio review — 2026-11-08

See the [single-microphone audit and software results](docs/AUDIO_AUDIT_20261108.md) and [ordered roadmap](docs/ROADMAP.md). Stronger comparison and output-stop safeguards pass Windows software checks. The completed acoustic measurements below supersede the initial access blocker; no physical equivalence claim follows from the software grid.

The [completed 2026-11-08 microphone run](docs/ACOUSTIC_RESULTS_20261108.md) supersedes its earlier access-blocked snapshot: 130 controlled cases were acquired through the explicitly selected .NET Host. Repeat variation/weak acoustic pairs leave physical equivalence and readiness open. See [the supplied conversation plan and Synapse-independence research](docs/SYNAPSE_INDEPENDENCE.md); no standalone Sensa backend or driver change was introduced.

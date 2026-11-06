# RW3 Pro Native DS Haptics

**Windows 11 desktop application + research project** to bring game-authored DualSense HD body haptics to Razer Wolverine V3 Pro while retaining three explicit modes.

> **Status (2026-11-06): M3 — real-game source and physical Sensa stereo verification, OPEN/BLOCKED.** Windows software, 10-scene A/B lab, original rear-PCM WAV processing and CI/package infrastructure are implemented; **live high-fidelity game-authored DualSense → Wolverine playback (M4) is NOT implemented or hardware-verified.** See [CURRENT STATUS / NEXT STEPS](docs/IMPLEMENTATION_STATUS.md), [ROADMAP](docs/ROADMAP.md) and [VALIDATION PLAN](docs/VALIDATION_PLAN.md). CI success is not proof of real physical haptics.

## Experimental DualSense → Sensa bridge (2026-11-05)

The app now has **an opt-in prototype** behind the **“Deneysel Native Stereo Bridge”** button. It enumerates Windows render devices, requires a **4-channel 48 kHz DualSense UAC** source, reads the haptic channels separately (3/4), converts them into two left/right parametric amplitude streams and calls the Razer Interhaptics engine/provider. This is **lossy** and does not preserve the original 48 kHz HD waveform. It is neither physically tested nor a proof that a game authored the incoming samples.

The separate **Haptic Test Lab** offers side-by-side XInput conventional motor tests, real 2-channel WASAPI sound→XInput tests and synthetic 48 kHz PCM fixture export. The **Native Bridge** window also offers direct left/right Interhaptics test pulses and genuine **4-channel UAC→Sensa end-to-end synthetic PCM playback**, independent of any game. A command accepted by the API is never called physical success unless the user confirms vibration, and that confirmation does not prove raw PCM fidelity.

See [Native Bridge Implementation](docs/NATIVE_BRIDGE_IMPLEMENTATION.md). **Vendor HAR.dll / Interhaptics.RazerProvider.dll are not bundled.** You need their legally obtained official SDK copies and working Synapse/Chroma on Windows x64. **Windows GitHub Actions build, contract suite, portable and installer smoke have been verified** on [main run 42214409530](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530). **Physical hardware runs have not been verified.**

## Three modes
- **Normal Rumble** — the game handles its own native XInput vibration; app offers a voluntary quick motor test.
- **Audio-to-Haptics** — opt-in WASAPI system-output capture, basic audio envelope to *standard* XInput vibration. For true Razer Sensa audio processing, use Synapse 4.
- **Native DualSense Haptics** — opt-in **experimental** 4ch/48k UAC → lossy 50Hz Sensa parametric bridge and separate source-only monitor; the product target is genuine stereo game-authored DualSense haptic PCM to the actual two Wolverine body VCMs. **High-fidelity playback remains unimplemented/unverified.**

## Run / build on Windows 11
Use the latest .NET 10 SDK:
```powershell
dotnet run --project src/RW3ProNativeDSHaptics/RW3ProNativeDSHaptics.csproj
```
On push to `main`, GitHub Actions builds a **self-contained portable ZIP** and an **Inno Setup installer** for Windows x64. On semver tag `v0.1.0`, both are published as GitHub Release assets with SHA256 manifests. See [WINDOWS_APP](docs/WINDOWS_APP.md).

## Application experience
- Modern Windows dark UI, three mode tiles and live capability/status text.
- Close (X) and minimize => notification area; tray context menu changes mode, checks updates, restores or quits.
- Short, explicit XInput rumble test; audio mode applies conservative amplitude and stops on exit.
- Startup checkbox; settings saved locally; no tracking or telemetry.
- Update manager checks GitHub Releases, downloads verified asset; installer opens on consent; portable ZIP requires user to replace files after quitting.

## Key references
- [Razer Wolverine V3 Pro](https://www.razer.com/console-controllers/razer-wolverine-v3-pro)
- [WYVRN/Razer Sensa DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit) — currently documented **events → hosted .haps**, not raw live PCM.
- [iFixit Wolverine V3 Pro actuator replacement](https://www.ifixit.com/Guide/Razer+Wolverine+V3+Pro+Voice-Coil+Actuators+Replacement/214388)
- [PadForge](https://github.com/hifihedgehog/PadForge) and [ApexSenseBridge](https://github.com/ReynArts/ApexSenseBridge) as research references (not bundled).
- [Full research sources](docs/RESEARCH_AND_REFERENCES.md)

## Source-of-truth diagnostics (added Nov 5, 2026)
The Windows status panel explicitly distinguishes (a) a chosen mode, (b) WASAPI system audio being received, (c) XInput API accepting a conventional vibration command, (d) manual confirmation of a physical test pulse, and (e) **real DualSense stereo PCM → Razer Sensa hardware playback**. (e) remains **unsupported and unverified**. "Native" selection cannot fake a success state.

The app now displays capture frames, accepted/rejected output commands, last failure, app CPU% and memory; includes a copyable status report and locally rotated error log (**no automatic audio recording**; an explicitly consented source-only 10-second rear PCM WAV export is available separately). In audio mode it samples up to 256 frames/callback, issues at most 20 XInput writes/sec, runs BelowNormal priority and stops audio processing after three consecutive 2-second CPU samples over 3% total-system CPU. These safeguards **do not guarantee zero impact on game FPS**; use actual in-game A/B frame-time measurements to confirm.

Automated provenance contract checks: `dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj`. See [Status, evidence and performance](docs/STATUS_AND_PERFORMANCE.md) and [Validation plan](docs/VALIDATION_PLAN.md).

## Where the project stands

**M0–M2 software milestones are implemented and CI-passing; the project is at M3 physical-source/actuator verification, with the critical M4 live high-fidelity stereo output still blocked.** Main: `ad2aed3b`; [successful Windows CI](https://github.com/skonyd/rw3pronativedshaptics/actions/runs/42214409530). The current Razer path is a **lossy 20ms/50Hz parametric envelope**, not DualSense waveform replay. The remaining five [GitHub issues](https://github.com/skonyd/rw3pronativedshaptics/issues) are dependency and physical acceptance gates, not simply missing buttons.

Read [the detailed implementation inventory, stage gates, blockers and next test actions](docs/IMPLEMENTATION_STATUS.md). Do not infer an overall percentage from green CI or the number of finished UI features.

## AI handoff
Start at [AGENTS.md](AGENTS.md); it contains user intent, evidence standards and what is still unproven.

## Test readiness and CI

The `main` branch Windows workflow runs contract checks, a WPF Release build, a self-contained win-x64 publish, and a non-interactive `--self-test` of the published executable before packaging the ZIP and installer. Synthetic tests run without any controller; **physical Wolverine/Interhaptics behaviour still requires manual acceptance**.

See [Windows hardware acceptance runbook](docs/DEVICE_TEST_RUNBOOK.md) for detailed steps, including stopping motors on audio silence, real 4-channel endpoint isolation and game-signal provenance. The Audio-to-Haptics backend has an independent 250ms silence watchdog plus a 100ms polling timer, and the experimental Sensa engine will not launch a second worker while an older worker is shutting down. These are source safeguards; validate on actual hardware before treating them as production proven.

## Native input/output direction, November 2026

[ADR-002](docs/ADR-002-NATIVE-BRIDGE-ROUTE.md) documents the preferred path: reuse a *user-installed, compatible* virtual DualSense 4ch source (PadForge Full composite where independently verified; DSX+ as a separate optional experiment) rather than build a new Windows audio driver. Native audio-channel evidence is separate from actual game provenance. The Interhaptics output remains an **experimental, lossy** parametric adapter. The official Wwise/Sensa WAV authoring plugin is a promising **offline fidelity comparison**, not yet a reusable real-time PCM streaming interface.

The experimental source picker now displays channel count, sample rate and PCM resolution; unsupported formats are blocked before the provider starts. The live panel separately shows speaker RMS, rear haptic L/R RMS and peaks. New deterministic PCM16/Float32 channel tests and GitHub Actions least-privilege publishing checks are included. No device drivers or proprietary SDKs are bundled.

## Nine selectable haptic methods

The app now exposes a persistent **method selector** with separate input/output descriptions, honest readiness indicators and setup links. The recommended candidate to obtain **authentic DualSense game PCM** is PadForge Full virtual DualSense 4ch/48k → RW3 experimental bridge. Razer output still uses **lossy 50Hz parametric Interhaptics** and is not demonstrated equivalent to the original DualSense.

Other selectable methods are physical USB DualSense UAC, DSX+ virtual DualSense Audio, manual 4ch UAC, official Razer Sensa HD Games, official Synapse Audio-to-Haptics, RW3 system-audio XInput rumble, normal game rumble, and **research-only** Wwise Sensa Motion. External/research choices do not run vendor apps or unsupported integrations. See [route selection and prerequisites](docs/ROUTE_SELECTION.md).

## Physical DualSense / Wolverine A/B Haptic Lab (10 scenes)

The Windows application now includes a game-free comparison laboratory: connect a physical **USB DualSense** and **Wolverine V3 Pro**, then replay ten deterministic synthetic textures (rain, sand/gravel/grass footsteps, pistol, automatic rifle, engine idle, acceleration, explosion, thunder) on either controller or both. **USB DualSense receives the reference PCM16 4ch/48k bytes without RW3 filtering, mixing or gain changes**, with user-selectable WASAPI Exclusive (strict preflight, no automatic fallback) or Shared; Windows/driver processing and physical response still need verification. **Wolverine receives a separate lossy 50Hz Interhaptics envelope**; this is not original Sony game PCM playback. Two sliders change RW3/Wolverine-generated output only; the real USB DualSense reference is fixed. Save subjective 1–5 texture, impact and stereo scores per controller as local JSONL, and STOP or exit safely. **Hardware behaviour remains unverified.** See [physical A/B operating and limitations guide](docs/AB_BEHAVIORAL_TEST_LAB.md).

### Verified device presence in the A/B lab
A/B comparison now shows **USB ✓ / not found ✕ / unverified ○** separately for the two controllers using present Windows PnP vendor/product identifiers. Wolverine's wireless receiver can share the hardware ID, so **wired-cable confirmation is separate**. A successful software playback is not evidence of physical motor motion. The updated lab requires a sensation answer for each **actually played** controller, records negative/uncertain trials without fabricated numeric scores, and summarizes only valid paired subjective impressions. See [A/B presence and evidence rules](docs/AB_BEHAVIORAL_TEST_LAB.md).

### Original-source PCM-to-WAV research path
For higher-fidelity study without claiming a supported live Razer streaming API, RW3 can now export all ten original synthetic 4ch A/B scenes as **unchanged stereo rear-channel PCM16/48k WAV**, and *optionally* passively capture up to ten seconds of a game's selected native 4ch UAC rear-channel PCM (PCM16 or Float32) in RAM, with explicit **save/discard** controls. The real USB DualSense output is **not changed**. Wwise Motion Sensa can use stereo WAV in a separately licensed setup, but its WAV path is documented as a rumble-style fallback and it is **not proof of real-time native waveform playback in Wolverine**. [WAV export / capture / Wwise experiment](docs/STEREO_WAV_CAPTURE_AND_WWISE.md).

### Full-resolution stereo PCM transport preparation (source-only; no Sensa output)
The **Native Stereo Bridge** includes an optional **48kHz original stereo PCM transport probe**. It forwards unchanged DualSense/UAC rear-channel sample bits into a bounded 120ms RAM ring, then a separate **discard-only** 20ms worker consumes complete 48k frames; UI counters show queued, dropped and zero-padded frames. This is separate from the existing **lossy 50Hz Interhaptics Razer output** and does **not** drive a controller or assert that a game authored the source PCM. Source-only monitoring needs no proprietary vendor runtime. [Transport technical contract and limitations](docs/REALTIME_PCM_TRANSPORT.md).

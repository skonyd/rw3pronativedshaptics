# Project brief / explicit requirements (2026-11-05)

**Project:** RW3 Pro Native DS Haptics. **Repository:** skonyd/rw3pronativedshaptics.

**Motivation:** User owns a DualSense and DSX+, but its shape causes hand fatigue. User already bought Razer Wolverine V3 Pro. Main use is story games on Windows 11 PC. Wants the authored DualSense vibration *texture* (rain, terrain, impacts, nuanced spatial L/R) on the ergonomic Wolverine without buying another gamepad. Adaptive trigger resistance is specifically excluded. Xbox compatibility is irrelevant to this project.

**Primary objective:** Accurate left and right body actuator playback of genuine DualSense native haptic data from supported PC games. Audio-driven effects are a fallback/different mode, not equivalent.

**Modes:**
1. **Normal Rumble:** ordinary XInput/game rumble; do not seize or overwrite normal game control. Provide an opt-in motor test.
2. **Audio-to-Haptics:** capture selected PC game/system sound via WASAPI loopback or link to Razer's first-party Sensa Audio-to-Haptics. Explain that effects are sound-derived, not authored haptics. Controls for gain and safe motor stop.
3. **Native DualSense Haptics:** virtual or physical DualSense capture of *stereo* haptic PCM, DSP, output to Wolverine's two body voice-coil actuators if a supported hardware API is discovered. Explicitly report unavailability until hardware path confirmed.

**Desktop requirements:** Double-click starts Windows 11 desktop app; closing main window hides to tray, tray right-click exposes mode selection/show/check updates/exit. Modern dark interface and clear active-mode/state badge. Portable ZIP without installer; a normal Windows installer/uninstaller; GitHub Releases channel, version view, check/download updates; optional launch at startup. No opaque silent installation.

**Safety/quality:** Safe vibration amplitudes and stop; explicit consent for audio loopback and virtual drivers; no clandestine game injection. Handle missing controller/network/driver clearly. Keep local data and explain privacy: no telemetry. Per-game profiles are future work.

**Important product distinction:** UI/build/installation can be complete while native Wolverine PCM device control remains unverified. Do not claim full bridge functionality based on offline PCM-only simulations.

**Acceptance tests:**
- Fresh Windows 11 PC: portable run and installation/clean uninstall.
- Close => tray; double-click tray restores; right-click changes mode, exits.
- Normal gamepad rumble unaffected; user can invoke 250ms test if XInput target connected.
- Audio mode user-opt-in WASAPI capture drives conventional XInput rumble with volume limit, can stop immediately (not Sensa-HD parity).
- Native mode displays correct blocker and does not silently route ordinary rumble.
- Github Actions produces installer & portable files, hashes and update metadata.
- When hardware SDK proven, independent L/R original DualSense waveform test passes without mono collapse.

## Delivery status and scope correction (2026-11-06)

The original project brief describes the **target product**, not proof that every acceptance test is already met. For the current status use [IMPLEMENTATION_STATUS](IMPLEMENTATION_STATUS.md) and [ROADMAP](ROADMAP.md).

- **M1 implemented/CI-passing:** Windows WPF tray client, persistent mode/route settings, ordinary XInput rumble, opt-in system-audio-to-XInput and package/update scaffolding.
- **M2 implemented/CI-passing:** optional real/virtual 4ch UAC source-only PCM monitor; lossy experimental HAR Sensa parametric adapter; ten original synthetic DualSense-vs-Wolverine physical A/B scenes; user-reported feel/scores and USB present-root checkmarks (not physical-motor sensors); 0–100% RW3-generated output intensity only; opt-in 10s local PCM recording and bit-preserving rear stereo WAV export.
- **M3 underway / physical validation BLOCKED:** independently prove a real game's authentic DualSense PCM, compatible Wolverine input→virtual DualSense/UAC source, two real Sensa left/right physical actuators, safety and measured performance.
- **M4 NOT IMPLEMENTED:** officially supported legal high-fidelity live stereo PCM sink and proven source-to-Wolverine fidelity with actual controllers; Wwise stereo WAV authoring is an **offline**, third-party licensed experiment, not a delivered realtime bridge.

All synthetic A/B scenes are **project-authored**, not original Sony game haptic assets. DualSense baseline is unmodified by RW3 gain/EQ. Windows mixing and physical motion must be measured separately. The user-installed/licensed HAR, Razer and Wwise runtimes are not bundled. Production acceptance remains contingent on the original objective, not a green CI.

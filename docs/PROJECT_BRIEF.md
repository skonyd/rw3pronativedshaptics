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

# Windows 11 hardware acceptance runbook

The CI pipeline verifies build, portable startup, synthetic stereo channel isolation and safety predicates. **It cannot prove real Wolverine haptic playback or native DualSense fidelity.**

## Preparation
1. Download the Windows x64 build artifact from a successful GitHub Actions run; compare the supplied SHA256 with the downloaded ZIP/EXE.
2. On a Windows 11 test machine, record OS build, Wolverine V3 Pro firmware, Synapse/Chroma version and DSX+/Steam Input versions. Keep a rollback point before any separately installed virtual driver.
3. Put legally licensed official x64 HAR.dll and Interhaptics.RazerProvider.dll beside the app only if your SDK license permits it. Do not redistribute those binaries through this repo.
4. Connect Wolverine in PC mode, select the correct XInput slot by a brief physical pulse (never trust first connected slot). Disable competing haptic apps during the isolated test.
5. Exit the app fully from its tray before replacing or uninstalling it.

## A. Desktop and safety
- [ ] Portable launched on fresh Windows without .NET runtime; tray show/restore/quit and settings path verified.
- [ ] Installer installed, starts and uninstalls cleanly. Autostart Run entry belongs only to this install and is cleared on uninstall.
- [ ] Normal mode does not take ownership of game rumble; 250 ms XInput pulse stops.
- [ ] Audio mode explicitly enabled; correct target slot; stopping mode and quit zero motors.
- [ ] Play a steady sound, then stop all audio. Motor MUST cease within 250 ms plus watchdog timer scheduling; verify independently of a new WASAPI callback.
- [ ] Switch audio endpoint, disconnect controller and reconnect. No stuck motor or false active evidence.
- [ ] With audio muted for >2.5s, UI must mark input activity stale rather than active.
- [ ] 10-minute CPU/RAM stability and demanding game A/B average FPS, 1% lows, p95/p99 frame times.

## B. Native experiment — game-free
- [ ] No native DLLs: explicit error; never silently falls back to XInput.
- [ ] Wrong sample rate or 2ch audio source: rejected.
- [ ] 4ch/48k DualSense UAC endpoint: selected intentionally; verify physical device ID and speaker/haptic mapping.
- [ ] Left parametric Sensa pulse physically felt on left only; right on right only. Log negative as well as positive results.
- [ ] Full-path synthetic 4ch left and right fixtures independently observed by WASAPI with opposite channel quiet.
- [ ] Provider render counter advanced; manually confirm physical response; do not label this a fidelity pass.
- [ ] Disconnect/hide/change device midstream: Sensa output stops and no stale success remains.
- [ ] Repeated Start → Stop → Start; never allow two HAR engine workers or a stuck vibration.

## C. Native game-authored PCM — acceptance gate
- [ ] Supported real PC game emits separately observed 4ch authored haptic PCM to selected DualSense endpoint (not system stereo or synthesized fixture).
- [ ] Verify virtual input + endpoint association when DSX+/VIIPER used. Prevent double controller input with Steam Input.
- [ ] Both channels remain independent, including negative isolation tests and device remap.
- [ ] Measure haptic end-to-end latency, frequency/transient response and compare to physical DualSense.
- [ ] **Do not mark full native success**: current 50 Hz envelope reconstruction is lossy and raw waveform sink is not demonstrated.

## Evidence record
Record: date/time; app commit; Windows build; controller firmware; Synapse/Chroma and DSX+ version; XInput slot; endpoint ID/channel format; game/scene; left/right capture block increments; opposite-channel isolation; provider ticks; physical test Yes/No; CPU/FPS/p99; error log; final pass/fail. Never upload raw game audio or credentials.

## External input adapter pilot (only after device smoke passes)

PadForge is currently the better **hypothesis** for Wolverine -> virtual DualSense Full because it maps arbitrary physical pads, while DSX+ targets its supported DualSense hardware. This must be confirmed physically, not inferred from product pages.

1. Save original working Windows/gamepad configuration. Review PadForge HIDMaestro/usbip-win2 driver requirements. External virtual controller drivers are explicitly user-installed; some USB/IP setups reset USB root hubs during installation. Do not install automatically or disable signature checks.
2. On a per-game profile, assign Wolverine inputs to ONE virtual DualSense Full slot; review raw device hiding with PadForge's supported controls to avoid double input. Verify button/stick response before attempting native haptics.
3. On games with true DualSense authored haptic output, select the matching four-channel virtual render endpoint if necessary. Leave normal speakers as default unless the game specifically requires otherwise.
4. In RW3 source picker, identify the exact 4ch/48k endpoint. Run synthetic left/right first; speaker-only front energy is not haptic evidence.
5. Disable any competing PadForge-to-Sensa or virtual haptics-to-XInput translation while measuring RW3, otherwise duplicate feedback may confound results. Independently test PadForge's own lower-fidelity baseline as a comparison.
6. Confirm actual game haptic origin with a reproducible scene and independently observe physical left/right vibration. Do not call API acknowledgment proof of equivalent tactile waveform.
7. Close RW3 and restore original input mapping and virtual device settings after each A/B trial.

## Route method choice (before testing)
Use [method selection guide](ROUTE_SELECTION.md). Select PadForge for the preferred authentic-source *hypothesis*, not for guaranteed physical equivalence. For supported Razer games test official Sensa HD Games independently as the best established Razer reference. DSX+ is an alternative only if Wolverine physical-input interoperability can be established. Wwise Motion is an **independent offline waveform experiment**, not a working route for continuous native game PCM. The bridge must be explicitly acknowledged per endpoint and no third-party driver should be installed automatically.

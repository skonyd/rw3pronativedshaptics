# Validation plan / evidence ledger

## Windows desktop UX smoke tests
- [ ] Clean Windows 11 x64: launch portable app exe without Python or .NET installed.
- [ ] Close button hides to tray; app process remains, double-click restores; tray right-click modes and explicit Exit work.
- [ ] App restarting after Exit doesn't leave stuck tray icon or motor vibration.
- [ ] Install/uninstall current-user setup; desktop/Start menu shortcut and uninstaller work.
- [ ] Portable settings reside next to executable or user-writable portable data path; installed settings under LocalAppData.
- [ ] Manual update check handles no GitHub release, timeout, old version, no asset, hash mismatch, installer and portable package.
- [ ] CI build/release package SHA256 verification; version matches tag.

## Normal Rumble smoke
- [ ] Game rumble is not intercepted in idle mode.
- [ ] Test button makes physically identified Wolverine pulse briefly, then stops; reject no gamepad.
- [ ] If multiple XInput devices connected, display first connected slot clearly; future selection is required.

## Audio-to-Haptics smoke
- [ ] Explicit user action starts WASAPI default-render loopback.
- [ ] Audio output responds as *conventional XInput rumble only*, not misrepresented as Sensa HD.
- [ ] Gain/limiter keeps amplitude conservative; silence and stop resets motors.
- [ ] Simultaneous game XInput rumble conflict explained; don't silently overlay.
- [ ] Change modes/exit always stops loopback and rumble.

## Native DualSense capture / playback — not yet done
- [ ] Establish real PCM capture from a DualSense-compatible PC game, record timestamped raw L/R frames.
- [ ] Confirm no input duplication with DSX+ and Steam Input.
- [ ] Prove both body VCM actuators can play independent signals through supported API.
- [ ] Prove bandwidth, frequency range and waveform integrity of actual hardware.
- [ ] Compare against original DualSense with side-specific rain/terrain/impact cues and latency metrics.
- [ ] Capture hardware firmware revision, Windows build, Synapse/Chroma versions and logs.
- [ ] Never switch native badge to 'supported' without all hardware tests.

## Test report template
Date | Controller firmware | Synapse/Chroma | Source game/scene | Device slot | Mode | Latency | L/R independence | Waveform fidelity | Evidence link | Result.

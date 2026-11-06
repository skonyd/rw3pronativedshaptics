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
- [x] UI and XInput source support explicit slot 0–3 selection or automatic first; [ ] validate with physical Wolverine and DSX+ virtual device connected simultaneously.

## Audio-to-Haptics smoke
- [ ] Explicit user action starts WASAPI default-render loopback.
- [ ] Audio output responds as *conventional XInput rumble only*, not misrepresented as Sensa HD.
- [ ] Gain/limiter keeps amplitude conservative; silence and stop resets motors.
- [ ] Simultaneous game XInput rumble conflict explained; don't silently overlay.
- [ ] Change modes/exit always stops loopback and rumble.

## Provenance and game-performance validation — new
- [ ] Run `dotnet run --project tests/RW3Pro.ContractTests/RW3Pro.ContractTests.csproj`: all 30 hardware-free provenance, audio safety and stereo isolation checks must pass.
- [ ] No XInput device: status warns; no false vibration or Sensa success.
- [ ] Motor test: Windows acknowledges command, only mark *physically confirmed* if the user actually feels the short pulse.
- [ ] Silent loopback: initialized vs received data vs meaningful sound are differentiated.
- [ ] Real game audio: callback/frame counters increase; XInput accepted/rejected counts distinguish command status; do not label as DualSense PCM.
- [ ] Native selection without experimental capture shows no original game signal. In experimental UAC capture, show observed source-channel counters explicitly as unverified game provenance and never declare 48 kHz Wolverine fidelity.
- [ ] Disconnect controller or change Windows audio endpoint: log/show error and gracefully stop capture.
- [ ] Capture performance for 10 minutes under a demanding game; compare FPS, 1% lows, p95/p99 frame times and audio quality with mode off/on.
- [ ] Measure idle CPU (<1% preferred), RAM stability, bounded 20/s motor calls, callback samples ≤256, log max 1MiB + 1 backup.
- [ ] Force sustained >3% normalized CPU for 3 successive polls: app should automatically stop audio mode and return Normal; report why.
- [ ] Observe game + audio output simultaneously: the current prototype may contend for the same XInput motor state; test this risk explicitly.
- [ ] Copy diagnostic status and check it contains exact source, receipt and blocker. Do not store raw game audio.
- [ ] Confirm close-to-tray and recover from UI error stop the motor.

## Game-free full-path 4ch native source experiment — not yet hardware validated
- [ ] Select physical/virtual DualSense UAC endpoint; it must be exactly 4-channel 48 kHz. Selecting ordinary 2-channel speakers MUST fail rather than silently call system audio native.
- [ ] On Windows verify user-obtained licensed HAR.dll and Interhaptics.RazerProvider.dll load and provider init succeeds.
- [ ] Run `Sol Sensa` and `Sağ Sensa` test pulses, confirm physically distinct left/right handles. Provider init alone is not proof.
- [ ] Run `Tam yol: PCM sol/sağ → Sensa` with no game running: 4ch fixture must fill only speaker-silent rear haptic channel 3 or 4; WASAPI monitor must see the selected channel and provider render must tick.
- [ ] Ask human to confirm side-by-side physical sensation, preserve negative results as evidence.
- [ ] Confirm lack of DLLs, inactive endpoint, mismatched sample rate, disconnected gamepad, STOP and close all fail safely with motors zeroed.
- [ ] Start a real DualSense-authored game, compare haptic channel energy during game effect vs silence; identify the actual writing source, not just total PCM samples.
- [ ] Compare actual DualSense force/spectral/latency vs Razer, document reconstruction loss.
- [ ] Run CPU/frame-time A/B benchmark in a demanding story game (no performance guarantee).
- [ ] Native bridge source status must NOT imply bit-perfect 48k waveform playback.

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

## Research-route acceptance checks (ADR-002)
- [ ] Source picker shows 4ch/48k PCM16/Float32 format, refuses unsupported before attempting Razer initialization.
- [ ] Speaker-only PCM raises front-channel energy but NOT rear haptic energy.
- [ ] Synthetic left/right PCM16 and Float32 frames preserve independent channels in the analysis layer; NaN/Infinity is zeroed safely.
- [ ] Compare physical USB DualSense source with PadForge virtual DualSense Full source without silently installing drivers.
- [ ] For any virtual controller, record physical Wolverine mapping and ensure only one input device is exposed to each game.
- [ ] Compare high-fidelity Wwise stereo WAV route with parametric Interhaptics route if legally and technically permitted; DO NOT infer true live PCM capability from offline WAV playback.
- [ ] Keep native-game-authorship and physical-fidelity labels BLOCKED until reproducible hardware test.

## User-selectable method paths
- [ ] Choose each of nine methods and verify input/output/provenance descriptions and support status.
- [ ] Applying PadForge/DSX+/USB route saves the preference and opens the matching source guidance in native bridge. External apps/drivers are never automatically installed.
- [ ] Unsupported or unacknowledged 4ch source cannot start; friendly-name matching must never count as source identity proof.
- [ ] Wwise research-only selection reports no RW3 live integration and does not start playback.
- [ ] Razer Sensa Games and Synapse Audio-to-Haptics selections leave RW3 capture stopped and link to official setup.
- [ ] RW3 Audio-to-Haptics still requires explicit capture permission and never resumes silently after Windows boot.
- [ ] Legacy Mode-only preferences and system tray menu remain compatible.

## Dual-device A/B acceptance (2026-11-06)
- [x] Deterministic fixtures for 10 short behavioral textures, silent speaker channels and maximum bounded PCM amplitude; assertions for repeatability and output safety.
- [x] Separate in-app reference path: no software gain/EQ/compression on DualSense's raw 4ch 48kHz reference; optional exclusive WASAPI with fail-fast on unsupported format, not silent shared fallback.
- [x] Separate Wolverine-specific additional test level with persisted RW3 global output level. Shared reference driver processing is disclosed.
- [x] Physical A/B interface: both, DualSense only, Wolverine only; user confirmation before Razer provider or physical USB playback.
- [x] Store per-device subjective 1–5 texture, impact and stereo ratings in local JSONL, do not store raw game/USB PCM or cloud upload.
- [ ] **Physical verification pending:** real USB DualSense playback, Wolverine Sensa actuator routing, simultaneous playback, stop/close cancellation and 10-scene feel comparisons.
- [ ] **Measurement pending:** hardware-sensed latency/fidelity, authentic authored game signal provenance.

# Roadmap (as of 2026-11-05)

## Phase 0 — project memory and requirements
- [x] Three modes and user intention documented
- [x] References and uncertainty classified

## Phase 1 — Windows 11 desktop shell
- [x] C# WPF application source, tray context menu, close-to-tray, mode selection
- [x] Current-user installer and standalone portable package pipelines specified
- [x] GitHub Releases check and SHA256-verified download designed
- [ ] Successful Windows CI build and packaging **requires verification after push**
- [ ] Windows 11 user device smoke test

## Phase 2 — conventional physical motor pipeline
- [x] XInput optional rumble test implemented in source
- [x] WASAPI audio → XInput prototype implemented in source
- [ ] Verify actual Wolverine connection and physical vibration, and that game input is not interrupted
- [ ] Per-device XInput slot selection and UI meter/limiter control

## Phase 3 — Razer Sensa HD output R&D
- [ ] Locate supported public API for precise independent left/right voice-coil output
- [ ] Test event + .haps capabilities and short on-the-fly effects; establish limits
- [ ] Prove stop, channel separation, timing, spectral response on hardware
- [ ] Build `IRazerSensaBackend` only after confirming device API

## Phase 4 — DualSense-native PCM capture and bridge
- [ ] Implement optional virtual DualSense/capture backend with documented interfaces
- [ ] Add two-channel PCM resampling/ringbuffer/dynamic haptic output
- [ ] Compare against real DualSense; no silent fallback to audio/rumble
- [ ] Profiles for games, DSX+ interoperability, diagnostics recording
- [ ] Windows signed release if signing certificate is available

Prioritize proving the Sensa driver output contract **before** invasive virtual device work.

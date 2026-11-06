# Bit-preserving stereo PCM / Wwise Motion research path

**Status:** In-app WAV export and opt-in 10-second source-only capture implemented; deterministic file-format and sample-identity checks required in Windows CI. **No live Wwise runtime bridge, shipped Wwise banks, shipped proprietary Razer binaries, authenticated game-origin source, or physically verified haptics are claimed.**

## What changed

The existing RW3 Sensa bridge reduces DualSense 48kHz stereo rear haptic channels to **20ms RMS amplitude steps** passed through the Interhaptics parametric effect engine. This deliberately **destroys fine spectral and transient information**. A new separate path preserves the source PCM bits for independent analysis and Wwise authoring experiments without silently replacing or altering the existing engine.

### Synthetic A/B references (offline WAV export)
1. Open **USB DualSense ↔ Wolverine • 10 sahneli A/B Haptic Lab**.
2. Choose **Seçili sahne → stereo WAV** or **10 sahneyi WAV olarak dışa aktar**. Neither button activates Razer, physical DualSense or any sound device.
3. Files appear in the application's data directory under `haptic-comparison/wwise-stereo-references`, with deterministic names `RW3-Rain-stereo-48k-pcm16.wav`, etc.
4. The same original four-channel, signed PCM16 48kHz reference used in the USB DualSense test is demultiplexed: **front channels 1+2 discarded**, **rear haptic channels 3+4 copied bit-for-bit** to standard 2-channel stereo WAV at 48kHz. No resampling, amplitude changes, filtering or normalization.

### Live game rear-channel *recording* (opt-in offline analysis)
1. Open Native Bridge and verify the game is using the selected **4-channel / 48kHz DualSense UAC source**. Use **Yalnızca 4ch PCM kaynağını izle** to start passive WASAPI loopback; do *not* activate the Razer bridge.
2. Start the target game/effect (if it sends haptic samples to this Windows render endpoint), then click **10 sn haptic PCM kaydına başla** and grant explicit consent.
3. The app accumulates at most **10 seconds of rear stereo source PCM in bounded RAM**. It copies source sample *bits* unchanged, including IEEE Float32 if the source is 4ch Float32, without writing to disk on the audio capture callback. Elapsed wall clock also stops accepting new sample frames after 10 seconds, even if the game is silent.
4. Click **Bitir ve stereo WAV kaydet** to write a locally named file in `haptic-captures/`, under the RW3 user settings/data directory. Float32 uses IEEE_FLOAT stereo WAV with `fact` chunk; signed PCM16 uses standard 44-byte stereo WAV. **Kaydı sil / vazgeç** drops and clears the in-memory buffer without saving.
5. Source/endpoint stop, disconnect, application close or bridge-mode change discards any uncommitted PCM. Files are local only; never uploaded. Keep in mind that some games' source files/audio may be copyrighted; use only where you have permission.

**What recording proves:** only that the selected audio endpoint produced 4ch PCM samples and that RW3 preserved the rear channels in an offline WAV container. A named DualSense endpoint, nonzero rear-channel samples or an exported WAV **do not prove** that the sample data came from the game's original authored DualSense feedback, nor that it will match DualSense's actuator motion when later played elsewhere. The actual USB DualSense render path is not modified by this capture or by the RW3 global intensity slider.

## Separate third-party Wwise Razer Sensa evaluation

Official documentation:
- [Razer Sensa HD Haptics Wwise Plugin / Audio Device](https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/)
- [Wwise Motion runtime integration](https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/game-engine-integration/)

In **Wwise 2025.1.6+** with the compatible **Razer Sensa HD Haptics plugin + Motion** installed, import an exported stereo WAV as a Sound SFX source. Configure **Generic Haptics 2-Channel** (NOT **Generic 2-Channel**) and map stereo channels to **Driver A/Driver B**. Route to **Motion Factory Bus → Razer_Sensa** device. To deploy in a custom game/engine, required Wwise Motion and runtime licensing, soundbanks, Sensa plugin binaries and explicit `AddOutput` setup are *not* included in this repository. The official docs describe WAV as a convenient lower-fidelity **rumble-style fallback** relative to properly authored `.haptic` clips, and note device-specific actuator behavior and fan-out. This is **not** proof of true lossless streaming from a live game into Wolverine.

Wwise is an **external licensed laboratory**, not a 10th built-in live route. The app does **not** bundle binaries, inject into games, reverse engineer proprietary actuator packets, silently install device drivers, or synthesize a false first-party Sony haptic signal.

## Verification checklist

- [x] Pure PCM16 rear channels copied byte-for-byte for ten deterministic scenarios
- [x] Float32 rear channels copied byte-for-byte with IEEE WAVE + fact frame count
- [x] Bounded, consent-driven RAM-only 10-second buffer and explicit local commit/discard
- [x] Four-channel format validation and source-only mode restriction (Razer sink inactive)
- [ ] Inspect a physical DualSense / virtual source running a supported real game; establish genuine origin and content.
- [ ] Test exported WAV in *licensed* Wwise Motion with correctly configured Razer_Sensa and real Wolverine; characterize the waveform actually reaching both VCMs.
- [ ] Compare resulting physical response to untouched physical DualSense reference using repeatable side separation, latency, spectral response and blind behavioral A/B scoring.
- [ ] Obtain a documented real-time arbitrary PCM sink if possible; offline WAV authoring is not sufficient to mark live DualSense→Wolverine solved.

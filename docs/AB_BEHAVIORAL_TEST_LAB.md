# USB DualSense ↔ Wolverine V3 Pro physical A/B lab

**Status:** Software implementation and deterministic hardware-free contract checks. **Physical haptics, fidelity, channel separation and end-to-end latency are NOT verified without both controllers.**

The comparison is designed to answer a *subjective tactile question*: when someone holds a physical wired DualSense in one hand and Wolverine V3 Pro in the other, which device conveys a synthetic texture or event more convincingly?

## Scope: exact reference vs lossy output

- **Input:** RW3 generates one ORIGINAL, reproducible four-channel signed PCM16 reference at 48,000 Hz. Channels 1/2 (speaker) are digital silence; channels 3/4 carry distinct synthetic left and right textures. A scene lasts 2.8–3.2 seconds. This is *not* Sony's game-authored haptic data and does not replicate a particular game's recording.
- **DualSense:** The exact `BehavioralHapticFixtures.Generate` byte buffer is passed to `RawSourceWaveStream` and Windows WASAPI without an RW3 gain multiplier, equalizer, pitch shifter, compressor, mixer or route through Wolverine. Use **Exclusive PCM16 4ch/48k** for the strongest unmodified-stream test; if the endpoint does not support it the app fails visibly instead of silently falling back to Shared. Shared mode can involve Windows APOs/mixing/conversion despite no RW3-level modification. Even exclusive is not *measured bit-perfect physical actuator displacement*. Physical USB DualSense is selected and confirmed by the user.
- **Wolverine:** The same *source* goes through bounded 20ms left/right RMS estimates and two Interhaptics parametric effects; this strips fine spectral and temporal detail. It is a real native Razer provider route only when a licensed, physically working SDK/provider is present. A provider tick is not proof of motor movement.
- **Timing:** Independent WASAPI and Interhaptics engines start near each other but are not hardware-clock synchronized. A user can run **both**, **DualSense only** or **Wolverine only** to compare.
- **Levels:** USB DualSense test signal is fixed at its low, predefined <=22% PCM full-scale bounded reference level. The RW3 **general output** slider (0–100%) affects RW3 Wolverine and RW3 XInput commands. The A/B **Wolverine test level** is an additional 0–100% gain on its lossy parametric output. Neither slider changes DualSense bytes. Sliders do not affect another game's native controller vibration or external Synapse effects.
- **Safety:** Short, bounded output; explicit consent; STOP cancels WASAPI playback, zeros Sensa effects; closing window or quitting while playback is in progress requests cancellation and awaits the player to stop before disposing the provider. Users should start with low Wolverine intensity and take breaks when testing repeated strong scenes.

## Ten independently generated tactile scenes

| # | Scene | Tactile design |
|---:|---|---|
| 1 | Rain / yağmur | Distributed small impulses, varying L/R droplets |
| 2 | Sand / kumda yürüyüş | Soft periodic impacts and low-frequency rubbing |
| 3 | Gravel / çakılda yürüyüş | Grainy sharper crunch impulses |
| 4 | Grass / çimde yürüyüş | Soft brushing and low impact |
| 5 | Pistol / tabanca | Three isolated right-weighted transients |
| 6 | Automatic rifle / otomatik tüfek | Short rapid repeating kicks |
| 7 | Engine idle / rölanti | Continuous low-frequency motor with mild modulation |
| 8 | Acceleration / hızlanma | Sweeping frequency and intensity |
| 9 | Explosion / patlama | Main shock transient followed by fading rumble |
| 10 | Thunder / gök gürültüsü | Slow irregular broad swell and delayed opposite channel |

These scenes were written for this project. The comparison tests the **capability of the two output pipelines** for a common stimulus, **not** whether original Sony game effects are faithfully reproduced.

## Operating procedure

1. Plug **both real controllers via USB** into a Windows 11 PC; launch the RW3 app. Connect Wolverine in PC mode, configure Synapse 4/Chroma's required Sensa option and provide correctly licensed official x64 HAR.dll and Interhaptics.RazerProvider.dll next to RW3. RW3 does not bundle/install vendor runtimes.
2. On the main screen, use **USB DualSense ↔ Wolverine • 10 sahneli A/B Haptic Lab**. If RW3's audio-to-XInput or experimental native bridge is active, it is stopped/closed so the comparison owns RW3 outputs.
3. In the lab choose the **real USB DualSense** sound endpoint in the list. It must expose exactly four channels at 48kHz. Read the full name and confirm physically. A virtual DualSense, television, speaker, or other output endpoint is not an equivalent control.
4. Keep **Exclusive PCM16** selected to attempt unmodified output. If Windows rejects it, investigate exclusive-mode endpoint support. Selecting Shared is an explicit, less stringent choice, not automatic fallback.
5. Choose one of ten scenes. Select **both**, **DualSense only**, or **Wolverine only**. Confirm Razer experimental output when applicable.
6. Hold the DualSense in one hand, Wolverine in the other (swap hands and repeat to reduce handedness bias). Adjust only the **Wolverine test intensity** if necessary; the DualSense reference stays fixed. Do not change scene/source while claiming a matched test.
7. Click Play, then assess each physical controller separately on **texture/detail**, **impact/transient** and **left/right isolation** from 1–5. Enter freeform notes and save the scene's subjective score. USB audio playback submission and provider-render activity are software observations, not verified tactile response.
8. Repeat on multiple scenes and compare ratings. Results are appended locally to `haptic-comparison/behavioral-ab-scores.jsonl` under the RW3 settings directory, no PCM uploaded. STOP, Close and application exit request source shutdown and provider zeroing.
9. For **true game-authored** DualSense PCM from actual games, use the independent *Native Bridge source-only mode* and then hardware verification, not these synthetic fixtures.

## Known gates before a production claim

- [ ] Verify the real physical DualSense 4ch endpoint format and physical output response to all ten synthetic scenes.
- [ ] Verify Wolverine's two actual Sensa VCM actuators respond independently and provider shutdown never sticks a motor.
- [ ] Measure A/B onset latency with an external sensor and output actuator frequency response. UI subjective scores are not instrumented measurements.
- [ ] Replace the Wolverine's lossy 50Hz parametric envelope route **only after** a documented/licensed higher-fidelity Razer streaming or authoring interface is available. Wwise's offline two-channel WAV plugin does not establish a real-time arbitrary-PCM API.
- [ ] Repeat as a genuine supported game's haptics are captured from actual USB or virtual DualSense. A common synthetic reference alone cannot prove native game-authored parity.

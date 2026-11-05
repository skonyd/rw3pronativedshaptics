# Experimental Native DualSense UAC → Razer Sensa bridge (2026-11-05)

## What is now implemented
The first **real endpoint-to-provider experiment** exists in the Windows WPF app. It is not yet a finished DualSense-quality bridge:

```
Game / synthetic test player
    → physically or virtually present DualSense USB Audio Class 4ch 48k render endpoint
    → WASAPI loopback from explicitly chosen render endpoint
    → Ch 3 (left haptic) + Ch 4 (right haptic), separate from speaker ch 1/2
    → bounded RMS envelope extraction (<=256 frames/callback)
    → independent L/R amplitude values, clamped <=0.33
    → Interhaptics HAR.dll: two parametric effects, one targeting Palm Left, one Palm Right
    → Interhaptics.RazerProvider.dll → Razer Synapse 4 Sensa HD (PC mode)
    → manual physical motor confirmation + diagnostic counters
```

**Important:** This is an **authored haptic CHANNEL INPUT** when a compatible game actually writes to the DualSense UAC endpoint, but the engine output is a **lossy amplitude reconstruction**, not the original 48 kHz waveform. At present it is 50 Hz-parametric, not sample-exact Sony-to-Razer PCM. Device enumeration/CPU counters/API success do not prove the game generated the PCM or that the motor faithfully reproduced it.

## Source investigation
- The PC DualSense/VIIPER USB audio implementation carries four 48 kHz audio channels: front speaker L/R and rear haptic L/R. [DS4Windows explanation](https://github.com/hbashton/DS4Windows/blob/main/docs/dualsense-bluetooth-audio-haptics.md), [VIIPER implementation docs](https://github.com/DualSenseClient/VIIPER/blob/main/docs/devices/dualsense.md).
- DSX+ virtual DualSense with audio endpoint **may** be appropriate, but the required audio endpoint must be present and the game must actually send haptic data. A virtual controller without its supported audio endpoint isn't sufficient. [DSX Steam announcements](https://steamcommunity.com/app/1812620/announcements/).
- The original DualSense plugged into USB can expose the relevant physical audio endpoint even if held only as a reference device. But the game may prefer the actual input controller and might not send native haptics to a passive DualSense. **Test game by game; never assume.**
- Razer's high-level WYVRN API plays named pre-authored effects (quantized roughly 30 Hz); it is not a live raw PCM sink. [Razer Sensa DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit).
- A usable lower-level C API is Interhaptics: `HAR.Init`, `AddParametricEffect`, `AddTargetToEventMarshal`, `SetEventIntensity`, `ComputeAllEvents`, `ProviderRenderHaptics`. [PadForge internals](https://padforge.org/docs/reference/sensa-haptics-internals/), [Interhaptics docs](https://doc.wyvrn.com/docs/interhaptics-sdk/haptics-unity-sdk/api-usage/).
- Official body mapping constants: `Operator.Plus=1`, `GroupID.Palm=400`, `LateralFlag.Left=2`, `Right=1`. [SharedTypes](https://doc.wyvrn.com/zh-Hans/docs/interhaptics-sdk/haptics-sdk-for-game-engines/shared-types/). Actual Wolverine target routing via these constants remains hardware-untested.
- Our output design is inspired by public Interhaptics bindings and PadForge's documented runtime lifecycle, **but no upstream source is vendored in our repo**. [PadForge Sensa docs](https://padforge.org/docs/reference/sensa-haptics-internals/).

## Running a safe experimental test on Windows 11
1. Razer Synapse 4 + Chroma installed. Wolverine V3 Pro firmware >=2.02. Connect via cable/dongle, switch to **PC mode (hold o + Menu + A for ~2 s)**. In Synapse enable Sensa HD Games source. For instructions see [DevKit](https://github.com/WyvrnOfficial/RazerSensa_DevKit).
2. For the standalone Interhaptics engine, obtain **HAR.dll** and **Interhaptics.RazerProvider.dll** for Windows x64 **from the licensed official WYVRN Interhaptics SDK**. Place beside the app executable only if the license allows; this GitHub repo NEVER downloads, modifies or redistributes those proprietary binaries. Required dependencies may also be needed from the SDK.
3. Open Windows app → Native mode → **Deneysel Native Stereo Bridge** → inspect source dropdown. Select only the actual 4-channel 48 kHz DualSense UAC output. Default stereo PC speakers are rejected. Do not mistake a 4ch sound device for a DualSense without manual inspection.
4. Start. The provider can initialize without verified physical channel motion. Watch the source frame counters and provider render counters.
5. **Game-free tests:** 
   - `Sol/Sağ Sensa aktüatörü`: two isolated parametric effect tests directly through Razer provider, no DualSense PCM.
   - `Tam yol: PCM sol/sağ → Sensa`: write quiet synthesized 4-channel PCM to the selected audio endpoint, capture its haptic channel back through WASAPI, feed the Sensa output, and prompt for independent physical feel confirmation. This is an end-to-end transport test, NOT evidence that any game sent a genuine DualSense haptic command.
6. For **authored game PCM**, start a PC game with verified DualSense audio haptics support, ensure it directs output to the chosen 4ch endpoint; compare input counters while effect plays versus silence. User test needed for distinct game texture and channel side. Disable Steam Input remapping if the game requires native DualSense protocol. Do not inject into the game or install virtual drivers from this app.
7. Stop: both envelopes zeroed, provider stop requested. Exit stops its thread. No background telemetry or raw audio logs.

## Evidence & blockers
| Stage | Implemented in source | Verified in actual Windows+Wolverine hardware? |
|---|---|---|
| Enumerate 4-channel render endpoints | Yes | No |
| Decode separate rear L/R 48 kHz PCM channels | Yes | No |
| Generate 4ch game-free fixture (rear channels only) | Yes | No |
| Create two independent Interhaptics parametric effects | Yes (experimental P/Invoke) | No |
| Deliver to device physical L/R coils | Provider call + manual prompt | **No** |
| Show exact real-game origin | Only source/channel counters | **No, cannot infer from counters alone** |
| Raw, lossless DualSense 48 kHz waveform playback on Razer coils | **No** | **No** |

## Known risks and limitations
- Other software may control the Razer provider, or Chroma may allow only one active app. Shut down other haptic apps while testing; do not assume safe sharing.
- Native DLLs in external SDK releases may change their ABI; failures must be reported, not downgraded silently to XInput. Real hardware tests must confirm side mapping.
- Audio render endpoint channel count, application selection and driver state can change. The app rejects mismatches and does not install devices.
- Current WASAPI callback processes <=256 sample points per block, without allocating proportional to audio length; Interhaptics runs a 20ms worker. Audio callbacks submit only atomic amplitude state. **Impact on FPS is not yet benchmarked.**
- Do not label test fixture as a true game instruction, nor provider render as physically verified motor output.
- Next R&D milestone: investigate a **supported direct waveform sink API** or material synthesis route in Interhaptics engine; compare with DualSense on actual waveform, timing and left/right differentiation.

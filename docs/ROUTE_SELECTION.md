# User-selectable haptic methods — 2026-11-06

> **2026-11-07 scope update:** See [current implementation](IMPLEMENTATION_STATUS.md). Separate A/B, Test Lab and expert bridge windows and the route selector are removed. Any instructions below using those controls describe historical workflows; underlying fixture/evidence/WAV/recorder/transport libraries remain. Current Test has 23 synthetic Wolverine/DualSense demos; DualSense reference is Exclusive-only without Shared fallback. Current Sensa v2 targets 5ms amplitude/frequency/transient updates, remains lossy and does not prove game provenance or physical HD. Hardware acceptance requirements remain open unless explicitly recorded in [HARDWARE_TEST_LOG](HARDWARE_TEST_LOG.md).

The route selected in the RW3 desktop application is a **user preference**. It never proves that external drivers, source PCM, the Razer provider or physical playback are present. All methods are selectable; only locally implemented methods can be activated inside the app.

| UI method | Original DualSense game-authored haptics | Who provides the output | State |
| --- | --- | --- | --- |
| ★ PadForge Full virtual DualSense 4ch/48k → RW3 → Sensa | **Potentially** if the actual game sends its authored haptic PCM to this virtual UAC endpoint | RW3 lossy Interhaptics 50Hz stereo envelope; requires licensed vendor SDK and physical proof | Recommended **authentic source experiment**, not a fidelity guarantee |
| Physical wired DualSense UAC → RW3 → Sensa | Potentially, if original game binds output to that USB controller | RW3 lossy Interhaptics 50Hz stereo envelope | Reference experiment, possible two-controller conflict |
| DSX+ virtual DualSense Audio → RW3 → Sensa | Potentially in compatible DSX versions and game setups; Wolverine input interoperability unverified | RW3 lossy Interhaptics | Alternative experiment |
| Manually selected 4ch/48k UAC | Unverified until real-game capture | RW3 lossy Interhaptics | Expert manual choice |
| Official Razer Sensa HD Games | **No**; uses Razer game-authored effects, not Sony's original haptics | Supported game + Synapse/Chroma | Best supported real-world Sensa path in compatible games; external setup |
| Official Synapse Audio-to-Haptics | **No**; ordinary system audio, not DualSense PCM | Synapse 4 | External system-audio haptics |
| RW3 WASAPI system audio → XInput | **No**; system audio | RW3 XInput standard rumble | Ready, not HD |
| Standard Xbox/XInput game rumble | **No**; ordinary game-controller feedback | Game + controller | Ready fallback |
| Wwise Sensa Motion offline stereo authoring | **Not a live original game stream**; can author WAV or .haptic content | Licensed Wwise Motion + WYVRN plugin | Research-only; **no active Wwise player in RW3** |

## Best choice and the two distinct quality questions

For the **highest chance of acquiring the *original DualSense-authored game data***, the current preferred experimental source is PadForge Full virtual DualSense's 4-channel UAC. But this is NOT a proven high-fidelity end-to-end solution: the current Razer output is lossy and unmeasured on hardware.

For **currently supported high-quality Wolverine Sensa output**, use a game with an official Razer Sensa integration (first-party Synapse/Chroma path). Such effects are **not** guaranteed to match Sony DualSense authored textures.

The official [WYVRN Wwise Sensa Motion plugin](https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/) can feed Driver A/B from Generic Haptics 2-Channel media; this is not evidence of an arbitrary low-latency live PCM API. Wwise Motion licensing is required for SoundBank authoring/distribution; no vendor DLL is included here.

## Applying a method in RW3
1. Open the method selector on the main window. Inspect its input, output and readiness. Select an entry and click **Seçili yöntemi uygula**.
2. RW3 saves the chosen route in local `settings.json`. Old preferences containing only `Mode` still load safely. Audio capture never resumes on startup without new permission.
3. For a native source, launch **Deneysel Native Stereo Bridge** and inspect the corresponding source guidance. Compatible 4ch/48k endpoints are suggested, but friendly names are never trusted as proof of device or game source. Check the explicit source acknowledgement only after confirming the mapping.
4. The bridge still requires user consent and correctly licensed x64 HAR/Interhaptics DLLs. It does **not** install PadForge, DSX, device drivers, Wwise or firmware. No switch from experimental native to system-audio rumble occurs silently.
5. Selecting an external Razer method keeps RW3 sound capture off; configure Synapse manually. Selecting Wwise research shows reference instructions but does not start any Wwise process or audio playback.
6. Use [the hardware acceptance checklist](DEVICE_TEST_RUNBOOK.md) before asserting original-game input, physical independent L/R actuators or true tactile fidelity.

## Sources
- [PadForge FAQ — DualSense native game audio](https://padforge.org/docs/faq/)
- [DSX v3.2 Beta virtual DualSense audio](https://steamdb.info/patchnotes/23660736/)
- [Official Razer Sensa HD Wwise guidance](https://doc.wyvrn.com/docs/interhaptics-sdk/razer-sensa-hd-haptics-wwise/)
- [Source vs output decision record](ADR-002-NATIVE-BRIDGE-ROUTE.md)

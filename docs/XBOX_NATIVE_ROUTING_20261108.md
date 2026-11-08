# Native trigger routing and audio-mode separation — 2026-11-08

Status: **software implemented and Windows-build/contract checked; limited positive user-felt Xbox demo feedback, full physical acceptance open**.
Later user feedback: trigger-only works; combined left/right/together feels apparently correct (“düzgün gibi”). Treat this as limited qualitative physical feedback, superseding the blanket unverified wording for those reported scenes only. Remaining physical checks and the current plan are in [CURRENT_PLAN_20261108.md](CURRENT_PLAN_20261108.md). Earlier failed device-discovery probes remain historical, not grounds to dismiss this later user observation.
This update follows local commit `78523d2`; it does not establish original-game or DualSense physical equivalence.

## User-requested behavior

| Selected mode | Body output | Impulse-trigger output |
| --- | --- | --- |
| Normal | Game-managed | Game-managed; no RW3 conversion |
| Audio-to-Haptics | Front/system audio → PC Sensa, or Xbox two-channel XInput body rumble | RW3 does not derive trigger output from audio or write a four-channel vibration state |
| Native DualSense virtual bridge, Xbox mode | Game's rear haptic PCM channels 3/4 → separate L/R envelopes, plus valid legacy rumble | Actual game report `0x02` trigger effect + physical L2/R2 pull → independent impulse-motor vibration approximation |
| Native bridge, Xbox, existing trigger option off | Same haptic envelopes; trigger approximation folded into body output | Zero from this bridge |
| Native bridge, PC mode | Existing lossy Sensa engine; trigger/legacy rumble on body XInput | No Xbox impulse-trigger backend in PC mode |

Native conversion uses the game's HID output, not a sound-derived trigger preset. The simulator remains explicitly synthetic and exercises the same report parser. No new driver, service, firmware, privacy setting or HidHide configuration was changed by this validation.

Audio Xbox production output now calls **only `XInputSetState` with its two-body-motor structure**, replacing the previous WGI four-channel state and audio-derived trigger pulses. This avoids explicitly clearing/replacing a game's impulse-trigger values. Cross-process coexistence still needs a real Xbox game/feedback test; API shape alone does not prove how the device driver arbitrates concurrent writers. Body feedback may compete with the game's own body output. Normal mode remains game-managed.

## Corrections

- Xbox input and output bind to the same connected GameInput device, strict `1532:0A3F`. Blocking enumeration replaces selection of the most recently used arbitrary gamepad. Multiple matching devices fail explicitly. Reads use a device filter and `GetGamepadState`, not a fixed XInput slot. The inbox System32 DLL and SDK v0 ABI are explicit; no redist ABI is substituted.
- PC Auto accepts only a unique connected XInput slot; an ambiguous set requires the existing explicit slot setting. Xbox no longer depends on that slot.
- All four bridge motor channels obey the common 0–100% output level, including live zero. Invalid numbers become zero; clamp happens before scaling.
- A separate timer checks the 250ms input-output freshness deadline. Read failure immediately clears motor output, latched effects, pending clicks and haptic envelope/history, and submits neutral virtual input. Stop rejects late writes and retains native objects if the input worker has not exited. A native API/driver stall is not a measured guarantee of physical stop within 250ms.
- Haptic PCM missing for >60ms zeros its Xbox envelope contribution; persistent trigger resistance commands do not expire merely because a game sets an effect once.
- Report validity distinguishes `HAPTICS_SELECT` from compatible rumble and accepts the compatible-vibration-v2 flag.
- Enabled zone strength bits `0` mean **1/8**, not off; the zone mask controls enablement. Weapon break uses the encoded endpoint, not the next zone. Bow `0x22` has a separate resistance/snap approximation. Simple effects no longer manufacture a minimum force for zero-strength data.
- Interpreter settings are applied before evaluating the pull. Trigger-off immediately clears pending clicks. Low-rate vibration (1–30Hz) affects the envelope; >30Hz retains amplitude rather than claiming a reproduced carrier. This is amplitude-controlled vibration, not adaptive resistance or waveform fidelity.

Supported effect interpretations: `00/05` off, `01/02/06` simple, `11/12` limited simple, `21` feedback, `22` bow, `23` galloping, `25` weapon, `26` vibration, `27` machine. Galloping uses an approximate two-beat envelope from the encoded foot positions/frequency; Machine uses its separate amplitude pair, frequency and 0.1-second-unit period rather than falsely treating it as zone feedback. Debug/unknown modes are silent for that trigger and reported in diagnostics/status. These conversions preserve usable command parameters; the original game's effect timing/physical force cannot be duplicated exactly by these motors.

## Validation and limits

### Combined demo on the Native test card

Test → Native v2 demolar → Oyun simülasyonu now defaults to **Haptik + tetik: sol / sağ / birlikte**. With the virtual bridge running, Wolverine in Xbox mode and the existing impulse-trigger option enabled, press **Oyun gibi çal** and follow the live pull instructions. Seven 5s phases: left body + L2; right body + R2; stereo body + both triggers with different effects; left body + R2; right body + L2; body only while pulling both; triggers only with silent body. Total 35s, then explicit off/zero. Stop/cancel uses the existing final off report. This is synthetic game-format PCM/HID, not an original-game recording or automatic trigger pull.

The demo writes rear PCM channels 3/4 and report `0x02` to the existing Windows endpoint/HID path; it does not bypass the production converter. Front speaker samples and legacy-rumble bytes remain zero, enabling isolation checks. Multiple matching DualSense audio endpoints now fail instead of arbitrarily selecting a physical reference; the runtime requires 4ch/48k. The dedicated demo requires Xbox/impulse settings explicitly, without changing them automatically. The full tour includes this scene and therefore has the same requirement.

Software contracts independently check each phase's expected L/R PCM and L2/R2 report activity, inactive-side exact silence, interpreter response to a pull sweep, stage boundaries, total length, front-speaker silence and final off. WPF build passes. No new physical playback was performed for this demo.

Windows x64 WPF Release build and required contract executable pass. Added independent wire-byte cases cover weak enabled zones, actual weapon break/force, L/R isolation, trigger-off with continuing body PCM, setting off, actual GameInput field mapping/device policy, invalid input, live gain zero, watchdog boundary and no post-stop writes. Windows hardware-probe `audit-xbox-safety` confirms audio creates body commands but **never trigger-channel values**, including malformed/silent input, cancellation and stale-output stop. This is command-level evidence.

Four 20-second **input-only** attempts found no Wolverine (`acquired=0`, `reads=0`, `MotorCommandsSent=0`), including retries after the user reconnected and exited the old RW3. Final GameInput enumeration was empty. Read-only PnP checks also found no present `1532:0A3F`/`0A4C` node; Bluetooth Xbox entries were not accepted as the Wolverine. These are unavailable-device results, **not valid zero-pull measurements** and not physical motor tests. Raw local reports: `TestResults/xbox-native-fix-20261108/` (ignored artifacts).

Remaining acceptance: visible wired Xbox Wolverine → L2/R2 each reach/release full range on the device-bound reader → simulator sends actual-format trigger reports and rear PCM concurrently → user verifies independent trigger/body sensation → option off/on, level zero, unplug/stop/mode-switch checks → genuine DualSense-capable game source. Separately test an Xbox game's own impulse feedback while Audio mode owns only body output. M3/M4 remain open.

## Sources

- ABI: installed Windows SDK `10.0.26100.0/um/GameInput.h`, specifically v0 device callback enumeration, device-specific reading, connected status, default focus policy, gamepad-state and rumble methods. [Microsoft GetCurrentReading](https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/interfaces/igameinput/methods/igameinput_getcurrentreading).
- Two-channel body command: [Microsoft XInputSetState](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputsetstate) and [XINPUT_VIBRATION](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_vibration).
- Report flags: [Sony-authored Linux hid-playstation source](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c). Trigger byte encodings cross-checked against [nowrep/dualsensectl](https://github.com/nowrep/dualsensectl/blob/main/main.c) and [Nielk1's original protocol factories](https://gist.github.com/Nielk1/6d54cc2c00d2201ccb8c2720ad7538db). These are community reverse-engineering references, not Sony's public SDK or calibrated physical mapping. No third-party implementation code was copied into the converter.

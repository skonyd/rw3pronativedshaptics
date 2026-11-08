# Hardware test log

## 2026-11-08 — User-felt combined-demo feedback after local app setup

User reports the trigger motor test works and the combined haptic/trigger left/right/together demo feels apparently correct (“düzgün gibi”). This is subjective scene-specific physical feedback, not an instrumented measurement or original-game/phase/fidelity pass. Classic, cross/body-only/trigger-only phases, gain zero, stop/reconnection and Xbox-game audio coexistence remain separately unconfirmed. Preserve the four earlier unavailable-device probes as historical acquisition failures. [Current remaining plan](CURRENT_PLAN_20261108.md).

## 2026-11-08 — Native Xbox routing correction: physical acceptance unavailable

Four input-only 20s attempts around reconnection and user-exit of the old RW3 found no Wolverine in GameInput (`acquired=0`, `reads=0`, no motor writes). Read-only PnP queries likewise showed no present `1532:0A3F` or `0A4C`; Bluetooth Xbox entries were not substituted. This is not a valid zero-trigger reading or a physical failure/pass of the new converter. No driver/service/HidHide/privacy configuration changed. WPF build, real-wire trigger/routing/safety contracts and Windows audio command audit pass separately; Audio no longer creates trigger output. [Implementation, retained artifacts and remaining hardware procedure](XBOX_NATIVE_ROUTING_20261108.md). Firmware and physical motor response remain unmeasured for this increment.

## 2026-11-08 — Opt-in multiband and cadence review

[Full multiband results](MULTIBAND_RESULTS_20261108.md): documented public `.haps` melodies change additional provider payload regions; three36-case static/update/generated controls repeat byte-identically per side (header excluded) and leave the opposite side unchanged. Existing one-band conclusions applied to the older parametric/multiple-event route, not the whole SDK. Live original-PCM→FFT→UpdateHM/provider tests complete, including16 microphone cases and eight real Windows loopback/source comparisons. No physical DualSense/game source or decoded/measured actuator spectrum/phase. Windows26320, Wolverine1532:0A4C, same licensed SDK, firmware unrecorded. Microphone enhancement/volume settings were left unchanged in this increment; no driver/service/firmware changed.

240 source-spectrum cases pass; a42.67ms diagnostic profile fails55 low-tone cases and is not exposed. Production opt-in history stays85.33ms, rendering33ms, with phase/short-transient/nearby-tone loss disclosed.66ms amplitude-zero curves, safe stop and explicit no-fallback errors are maintained. Native debug render threw on idle `.haps` with no material frame; normal idle rendering avoids that accessor. Failed exploratory runs are not completed evidence.

The user's33ms question triggered12 further30/33/36ms ×old/new ×two-repeat acoustic cases. All share the same canonical source hash, no microphone clipping or acquisition failure.30ms shows stronger later tail windows than33/36ms; one30ms steady window is weak/contaminated and retained as unresolved. No calibrated latency or immutable hardware ceiling is claimed. [Official source scope, tables and limitations](LATENCY_BUDGET.md).33ms remains selected. New UI checkbox defaults off; app was reopened in Normal after tests.

Each entry records **what was observed on real hardware**, under which conditions,
and what it does **not** prove. Subjective "felt" answers come from the user's hand,
recorded in-app (Test tab, A = felt / B = not felt) and in the local `rw3.log`.

---

## 2026-11-07 — First physical Wolverine V3 Pro Sensa left/right test (Issue #1)

**Setup**

| Item | Value |
| --- | --- |
| OS | Windows 11 Pro, build 10.0.26320 |
| Wolverine V3 Pro | USB cable, PC mode (○ + Menu + A), firmware updated via Synapse the same day (version not recorded) |
| Wolverine USB identity | `USB\VID_1532&PID_0A4C` "Razer Wolverine V3 Pro for Xbox" (wired PC mode). The HyperSpeed dongle / Xbox mode shows `1532:0A3F`. |
| DualSense | USB, `054C:0CE6`, render endpoint 4ch / 48 kHz / 16-bit |
| Razer software | Synapse 4.0.896, Chroma 4.0.896 |
| Interhaptics binaries | `HAR.dll` + `Interhaptics.RazerProvider.dll` from WyvrnOfficial/Interhaptics_Unity_CoreSDK **v1.8.0** `Runtime/Plugins/x64` (SHA256 `43600b03…3d82` / `d4534291…9dcf`), user-supplied, not committed |
| Reference | Razer Sensa DevKit `TechDemo_V1.1.0` vibrated the controller (sanity check of hardware + Synapse) |

**Results**

| Step | Observation |
| --- | --- |
| Palm (GroupID 400) Left/Right targets, 0.20 amplitude, single 385 ms pulse | Left felt weakly; **right never felt** |
| Hand (GroupID 302) targets, 0.38 × 800 ms | Left stronger; right still not felt; user noticed only the *first* click after start vibrated |
| Root cause found | Parametric envelope was `{0,1, 1,1}` (1 s). Per the official SDK, `PlayEvent` plays once from "now"; looping is a separate `SetEventLoop`. The effect therefore expired ~1 s after engine start, so later pulses were silent. Fixed: 600 s envelope, `SetEventLoop`, and `PlayEvent` re-trigger on every signal/test onset. |
| After fix: Hand Left / Hand Right, 3-beat pattern at full test amplitude | **Left felt, right felt, both felt — "noticeably"**; repeated twice with consistent answers (log 19:00:06, 19:00:10, 19:00:41, 19:00:45) |
| Right-motor finder (9 candidates, controller answers) | First candidate **Hand • Right (302/1) felt on the right** (19:00:26, 19:00:59) |
| Classic XInput left/right motor test | Felt ("tamam gibi") |

**What this establishes**

- The two Wolverine body actuators can be addressed **independently** through the public Interhaptics engine + Razer provider using `GroupID.Hand` + `LateralFlag.Left/Right`, matching Razer's own DevKit configs.
- The earlier "right actuator silent" result was a software timeline bug, not hardware.

**Correction after re-test (same day): strong cross-leak**

The user re-assessed: left and right pulses **leak into each other heavily**, so it is hard to tell which side
fired. "Felt on this side" was true, but channel **separation is poor**. Possible causes (not yet isolated):
mechanical coupling through the one-piece shell, both effects sharing the 65–300 Hz band at full test amplitude,
or the Razer provider mixing Hand L/R. Next step: compare against the USB DualSense playing the same 3-beat
pattern on rear channels 3/4 as a human reference for "how much leak is normal".

**What it does NOT establish**

- Clean left/right isolation — see the correction above.
- No sensor/accelerometer measurement; no latency, spectrum or amplitude-linearity data.
- The Razer path is still the **lossy 50 Hz parametric envelope**, not the DualSense 48 kHz waveform (M4 unchanged).
- No game-authored DualSense haptic source was tested yet (Issue #2/#5).

### DualSense reference path (same session)

- USB DualSense render endpoint mix format: WAVEFORMATEXTENSIBLE **Float32**, 4ch, 48 kHz, channel mask **0x33** (FL FR BL BR).
  Endpoint volume was 65 %. Rear-left/right (indices 2/3) are the haptic pair.
- A plain 4ch PCM16 WAVEFORMATEX is **rejected in both Shared and Exclusive** mode; Exclusive accepts only
  extensible PCM16 with mask 0x33 (`DualSenseAudioFormat.QuadPcm16`). The A/B lab's USB reference path used the
  plain format, so it could never have played before this fix.
- First Shared-mode attempt (Float32, amplitude 0.6, scaled by the 65 % endpoint volume): WASAPI loopback showed the
  pattern only on channel index 2 (RMS 0.33, others 0.0000), yet the user **felt nothing** on the DualSense.
- The pattern now uses Exclusive PCM16 quad at amplitude 0.8 (no Windows volume/mixer); playback confirmed
  (`exclusive=True`). Hand result pending.
- In the same round the user rated **Wolverine left and right as "only on this side"** (overriding the earlier
  "heavy leak" impression; repeat with the DualSense reference before drawing a conclusion).
- Hypothesis for the silent DualSense: the controller's haptic path was in power-save / muted. The DualSense test now
  first sends one USB HID output report 0x02 with `AllowAudioMute` (flags1 bit1) and MuteControl = 0 (HapticPowerSave,
  AudioPowerSave, HapticMute etc. cleared) and flags0 = 0 (audio haptics, no rumble emulation), modelled on Special K's
  `initialize_audio()`. Report accepted by the controller (`hid#vid_054c&pid_0ce6&mi_03`). Live controller state only;
  lost on unplug. Hand result pending.
- Single wake report: still **nothing felt**, also after unplug/replug. duaLib (DualSenseY) and DSX write output reports
  continuously; the DualSense test now repeats the wake report every ~30 ms (33 reports/s measured) from 150 ms before
  until the end of the pattern. Hand result pending.
- Keep-alive reports: still nothing felt. Automated check with the DualSense's own microphone (48 kHz, 2ch): **exact
  zeros** (142 080 samples, max 0.0) with and without wake reports, so the controller's whole audio section is down,
  not only the haptics.
- HID round-trip proves our output reports are applied: a HostTimestamp written at SetStateData[32] (0xC0FFEE42) is
  mirrored in input report byte 43. Input status: MicMuted=0, but **PowerPercent=0, PowerState=0x0F (ChargingError),
  PluggedUsbData=1, PluggedUsbPower=0**. Working hypothesis: the DualSense does not power its audio/haptic amplifier
  in a charging-error / no-USB-power state. Next: different cable/port (rear motherboard USB), let it charge, re-run
  the automatic mic test (non-zero mic noise = audio section alive).

## 2026-11-07 — Software reference/provenance correction (no new physical test)

DualSense reference playback now rejects unsupported Exclusive quad PCM16 instead of converting to the Shared mix format. Native live status reports observed rear PCM with game provenance and physical HD unverified. Prior observations retain their original setup/firmware limitations; these code changes are not new hardware evidence. Physical unsupported/busy endpoint behavior and reference response must be re-tested.

## 2026-11-07 — Game-free canonical PCM conversion lab, Wolverine only

**Conditions:** Windows 11 26320; physical wired PC-mode USB `1532:0A4C`; running Synapse/Chroma 4.0.896; the pre-existing user-supplied HAR/Razer provider binaries from the local Debug output. Full SHA256s are recorded in each local probe `setup.json`. Firmware version remains **not recorded**. A4tech FHD 1080P webcam microphone, user-reported Wolverine Xbox button uppermost and close to microphone. User deferred all DualSense physical tests; there was no Sony USB node or compatible 4ch render endpoint in the device inventory.

**Input/output:** seven deterministic channel/negative controls and all 23 procedural demos, original quad PCM16/48k through the **same `NativePcmIngress` used by live capture**, then actual Interhaptics v2. This is replay at the decoder boundary, **not a WASAPI/USB source test or authentic game capture**. Wolverine level 100%, source gain 140%; canonical reference samples unchanged.

**Executed observations:**

- User explicitly reported that vibration was coming during the channel sequence. This is general subjective motion confirmation, not a per-side isolation or per-demo quality rating.
- Original hashes matched before/after every completed scene. All 23 demos completed the provider run. Their measured engine tick rate ranged **199.9338–200.07187 Hz**; maximum observed source replay scheduling lateness **1.4171 ms**. These are software timing measurements, not physical latency.
- Initial microphone runs were muted. User confirmed mute and authorized unmuting this specific microphone. A4tech endpoint mute changed true→false; endpoint gain stayed 1.0. Muted results in local `controls-v2` are **invalid acoustic evidence**.
- Unmuted channel and demo runs collected background/active/tail levels, with no raw microphone audio retained. The final demo run excluded 750ms capture startup settling. No active-window microphone clipping was observed in the 23 demos.
- Demo active/background filtered RMS ratio ranged **0.0213–5.1834**; engine-off tail/background reached **12.329**. In a separate **0% Wolverine output** control (same stereo source hash), RMS was background **0.0006998**, active **0.0046872**, engine-off tail **0.0171692**. Therefore these acoustic windows do **not** reliably isolate the motor contribution. No physical frequency, transient fidelity, side isolation, absolute latency, quality score or stop-safety PASS is assigned from them. Background variation, microphone processing and the previously noted idle-output concern need separate investigation; these data cannot identify which cause dominates.
- Mid-playback cancellation (Rocket) returned through engine.Stop cleanup; a subsequent independent stereo run initialized and completed. This verifies software cancellation/restart behavior, not externally measured physical stopping.

**Local evidence** (ignored `TestResults`, no vendor binaries or microphone PCM in reports): `channel-lab-20261107-unmuted`, `channel-lab-20261107-demos`, `channel-lab-20261107-cancel`, `channel-lab-20261107-zero`. An earlier failed preflight report records missing SDK beside the new probe; the already-user-supplied binaries were then placed in the ignored local build folder, not downloaded or distributed.

**Acceptance:** source identity/software separation and actual Wolverine motion have useful evidence. Real-game authorship, complete USB/WASAPI capture route, physical DualSense comparison, calibrated response and full safety/reconnect remain **open**. M3/M4 unchanged. [Channel lab procedure](CHANNEL_LAB.md).

## 2026-11-08 — Audio review: no completed new physical measurement

User reported Wolverine and A4tech microphone ready in a quiet room; DualSense was not to be driven. Initial inventory: Wolverine wired PC 1532:0A4C, physical DualSense and A4tech present; Windows 26320, exact controller firmware still unknown. Existing licensed SDK SHA256s were HAR `43600B03E9B46F2B3B4810E2904C6333BCE441C19D85638E0E7E3FF39F973D82` and provider `D4534291E26D8E2FC4A3D35AF09A50A476769AC004844A707BB9F3B859D99DCF`. Synapse version was not remeasured (prior record 4.0.896).

First probe waited at microphone opening and was terminated before motor initialization. A logged retry failed in WASAPI AudioClient.Initialize with E_ACCESSDENIED 0x80070005. Registry consent reads were Allow at user/machine/desktop-app scope; cause remains unresolved. A separate explicitly selected MME capture option compiled, but its run was prevented by the wired-PC prerequisite: WolverineUsbNode=false and WolverineWiredPcNode=false; DualSense/A4tech still enumerated. The user was asked to restore the Wolverine connection and check microphone access. No privacy/volume setting or driver changed, no DualSense output played, no acoustic equivalence or physical stop result claimed.

Software builds/contracts and 4,452 finite equalized tone comparisons passed. Prepared acoustic controls/sweep/repeats and limitations are documented in [audio audit](AUDIO_AUDIT_20261108.md); local aggregate/setup/grid reports are under TestResults/acoustic-audit-20261108.

A subsequent standalone microphone-only check bypassed no hardware prerequisite (it drives no motor) and explicitly selected MME/PCM16. Windows waveInOpen returned UnspecifiedError; no microphone samples were measured. Both recording API failures remain unresolved.

## Retry after user restored Wolverine — 2026-11-08

Wolverine wired PC identity is present again. A4tech is already unmuted at 100%; user/machine/desktop and Codex microphone consent read Allow, audio services are running and PnP status is OK. Another WASAPI microphone-only attempt still returned E_ACCESSDENIED at AudioClient.Initialize. Windows Sound properties also show the A4tech endpoint enabled, 2ch PCM16/48k, generic USB audio driver 10.0.26100.9836 (2026-10-23). Voice Clarity/automatic Voice Focus were enabled. A temporary ordinary audio-enhancement-off test produced the same initialization error; Voice Clarity/automatic Voice Focus were restored and visually verified. No privacy/security setting changed, no raw microphone audio retained, no DualSense output, no completed physical motor measurement. Root cause remains unresolved; the old Wolverine-disconnected blocker is now cleared. Local retry status is TestResults/acoustic-audit-20261108/microphone-retry-status.json.

## 2026-11-08 — Completed one-microphone comparison

The launch-dependent access problem was isolated: the same test DLL captures through the explicitly selected installed .NET Host, while its apphost executable returns E_ACCESSDENIED. Windows showed .NET Host microphone permission enabled. No privacy registry/toggle changed. Windows microphone test also completed. Root Windows policy detail is not established.

Wolverine wired PC 1532:0A4C; A4tech volume 100%, fixed user-reported position. Voice Clarity was temporarily disabled for 130 controlled cases and then restored with automatic Voice Focus, visually verified. Conditions, missing firmware, SHA256s, control windows and all 41 sweep pairs are in [the results](ACOUSTIC_RESULTS_20261108.md). 8 controls + 82 sweep + 24 repeat + 16 extended cases completed without acquisition failure/microphone clipping. Eight earlier processed-mic controls are retained separately. Original source hashes agree across every sweep pair. Both source cancellations occurred as expected and late/off windows approached background; the audio cancellation's short active window was weak, so no calibrated physical-stop claim follows.

The finite software grid passes, but acoustic repeat CV about 48–73%, 17/41 sweep pairs below the descriptive background screen on at least one path, and one elevated late tail leave physical fidelity/reliability acceptance open. No physical DualSense reference or genuine game source was played. Cause of variation is unresolved; do not blame Synapse alone or tune it away by gain. HapticService 1.2.2.0 and installed RzInterHaptics 1.0.0.5 were observed during read-only dependency research. No Razer service/driver/firmware changed. [Remaining conversation plan and independence research](SYNAPSE_INDEPENDENCE.md).

## 2026-11-08 — Additional repeated-session and unity-input audit

**68 further cases completed**, separate from the previous 130. Wired PC Wolverine/A4tech unchanged; firmware remains unrecorded. 48 cases vary engine/microphone reuse, 12 repeat the first cold/cold condition at the end, eight test real loopback with new unity input at .5/.9. Voice Clarity was temporarily disabled and restored to Voice Clarity/automatic Voice Focus with visual confirmation; mic volume remains 100%. No Sony output or genuine game source, privacy/driver/firmware/service changes.

Initial cold repeat CV21.66% reference/24.45% audio falls to 4.58%/0.98% in final cold controls. Middle reused groups have CV3.05–4.15%, but fixed order confounds settling/time; reuse is not a proven cure. All cases retained. New unity audio input clips zero samples in eight live cases; existing 140% output gain still saturates envelope points at .9 (242 reference, 243 audio across two repeats each). Physical equivalence remains unverified. See [full scenario results](SCENARIO_AUDIT_20261108.md) for controls, limitations and nine failed short-pulse software comparisons.

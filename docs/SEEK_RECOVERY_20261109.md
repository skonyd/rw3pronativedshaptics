# YouTube seek/recovery investigation — 2026-11-09

Status: original bug remains OPEN. Capture-recovery safeguard implemented and installed;
user confirmed the actual foreground-dependent motor interruption still occurs.
Do not present the safeguard or a successful motor API return as a physical fix.

Latest user-requested cleanup: the unproven endpoint-meter restart heuristic and temporary
audio trace were removed from current production source, including their helper/state/tests.
The historical implementation/evidence below is retained as history.250ms stale-output
silence and normal capture/output error handling remain. [Current routing audit](HAPTIC_ROUTER_AUDIT_20261109.md).

A gap existed in AudioHapticsBridge: its independent250ms silence watchdog cleared motors,
but a capture that remained IsCapturing=true while delivering no audible PCM stayed healthy
according to the UI. The UI already retries a failed stream, but never failed this condition.
AudioCaptureRecovery now checks front-channel render-endpoint activity outside the WASAPI
callback, at most10 times/s. After startup grace2500ms, missing audible capture1500ms and
sustained endpoint activity1000ms, it marks capture failed, clears queued/motor output and
lets the existing UI retry the selected mode without an output-method change. Real silence,
intermittent noise, invalid/unavailable meters and normal fresh callbacks do not request this
recovery. It is shared by PC Sensa and Xbox body audio, not the original native PCM reference.

Validation:
- Windows Release build and full contracts passed (TestResults/seek-recovery-20261109.log).
- Real Windows WASAPI render-client replacement18/18 cases recovered, including short40ms
  and longer450ms gaps, on Soundcore Motion+44100Hz. No microphone/controller output was
  opened by that probe; this is not YouTube or physical actuator proof.
- Source recovery diagnostic: TestResults/seek-live-20261109.json.
- Published framework-dependent win-x64 package copied to existing local install after user
  gracefully exited old instance. SHA256 of installed DLL matches published DLL:
  A001DD0DB8201709DE4EEFF7FBE69929087BB625881CD35B7ED01FA7B85A7318.
- Installed --self-test exit0; app reopened. Actual log confirms Audio running on Xbox body
  sink / Soundcore / auto Bluetooth delay300ms. Present Wolverine USB identity1532:0A3F.
- User repeated YouTube seek: music continued, motor feedback disappeared for10s or more.
  Later repeated observations associated loss with Opera foreground and return with Codex.

## Foreground isolation — latest findings

- User launched installed RW3 themselves through Start Menu: Opera still interrupted output.
  A Codex parent process is therefore not a sufficient explanation.
- App was put in Normal mode for independent constant40% body tests. XInput1.4,
  inbox GameInputv0, repeated XInputEnable(true), XInput9.1.0 and WinRT WGI all
  failed the user's physical Opera-foreground check. No API substitution is a fix.
- Latest WGI probe bound RawGameController VID1532/PID0A3F, sent900 commands,
  no reported errors, and zeroed output in finally. The user still felt a cut.
  Report: TestResults/focus-wgi-20261109.json. This is a physical FAIL despite API success.
- A separate temporary Opera profile, with the historical WGI-disable and current
  GameInput-disable feature names, also cut output (1200 XInput calls,0 reported errors).
  Normal Opera profile/settings were not changed. Current Chromium source removed the
  historical WGI-to-XInput feature switch, so command-line presence does NOT prove that
  WGI was actually disabled. Do not claim this test excluded all browser gamepad access.
- Audio traces showed continuing nonzero captured PCM, live delivery, valid slot0 and
  successful writes. Thus the reported case is not explained by absent source callbacks.
  The main-process endpoint meter sometimes read0 while PCM/external metering was active;
  its zero is not proof of source silence.
- USB trace TestResults/usb-profile-study-20261108/admin/seek-xbox-20261109.pcap
  contains622 rumble reports,602 nonzero body reports, and05 state commands. Microsoft
  GIPUSB defines state05 as Quiesce, which clears feedback. This is consistent with a
  focus/ownership problem, but packet times have NOT established causal ordering across
  the complete physically silent interval. Keep root cause provisional.
- Two-channel XInput produced all-four-channel GIP enable masks with zero trigger fields.
  Therefore body-only API usage is not proof that another process's impulse output survives.
- Inbox v0 read-only CreateRawDeviceReport IDs0/1/9 returned0x80070490. No raw report
  was submitted. The installed SDK focus enum lacks the newer0x40 flag; do not copy a
  newer ABI/flag into production based only on current web documentation.
- User explicitly approved a60s temporary HidHide trial and rollback. Root-only hiding
  failed visibility isolation. Driver dc1-controller.inf10.0.26100.9739 exposes Microsoft-ID
  USB/HID IG children below Wolverine; parent and container verified they belong to it.
- Hiding root AND both verified children achieved zero XInput slots from a new
  non-allowlisted apphost and slot0 from the allowlisted dotnet host. A fresh isolated
  Opera process was launched. User still reported physical interruption in that window.
  Probe completed1200 writes/0 API errors and finally zeroed motors. Thus visibility
  isolation is NOT a proven foreground-output remedy. Evidence:
  TestResults/focus-isolation-descendants-20261109/{status,motor,before,after}.json.
  Restored=true, RestoreErrors empty; cloak/inverse/device/app entries matched the snapshot.
- Initial worker attempts stopped before motor testing on a slash-form host path and a
  PowerShell5 empty-JSON-array counting difference. Original configuration was preserved
  or restored; array parsing was regression-checked. These were not motor-test passes.
- Production HidHide inventory now selects Microsoft-ID IG descendants only beneath the
  verified Razer Xbox root, with matching nonempty/non-system container. Regressions cover
  inventory order, unrelated controllers, wrong/absent containers, cycles and Sensa exclusion.
  This fixes an inventory omission, not the foreground-output failure; no Audio cloak added.
- After the completed trial Wolverine disappeared from present-device inventory (both
  1532:0A3F and0A4C absent). Updated C# inventory returned empty; a fresh live three-node
  acceptance for the production inventory remains unavailable, not a hardware pass.

Next: establish a device-bound output route that survives foreground ownership, or test
temporary controller isolation with a concrete rollback plan. Do not change DSP gain,
keepalive frequency, browser profile, services or drivers to disguise the unresolved failure.

Primary references:
- [Microsoft GameInput fundamentals](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-fundamentals)
  documents feedback suspension on focus loss; background input is not background-output proof.
- [Microsoft GIPUSB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/e7c90904-5e21-426e-b9ad-d82adeee0dbc)
  defines Quiesce; local text extraction is retained in TestResults/ms-gipusb-reference.txt.
- [Current Chromium fetcher registration](https://raw.githubusercontent.com/chromium/chromium/main/device/gamepad/gamepad_platform_data_fetcher.h)
  chooses GameInput or WGI; no historical XInput fallback switch in current main.

No driver, firmware, service, privacy or Windows security setting was changed. Only the
explicitly approved temporary HidHide configuration changed, then was verified restored.

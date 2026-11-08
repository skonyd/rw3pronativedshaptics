# Current conversation plan — 2026-11-08

This snapshot reconciles the later Direct USB/virtual bridge work with older vendor-dependent roadmap entries. It does not replace historical negative results.

Latest active user task: [microphone-free Synapse profile USB study](SYNAPSE_USB_PROFILE_STUDY_20261108.md).
Separate elevated capture helper and six-case Controlled pilot work; corrected306-case Controlled
series started, not yet verified complete. Balanced/Dynamic/Custom capture and production profile
integration remain open. Original Synapse Custom backup must be restored after study. Do not
equate newly observed compact A1 pitch codes with the older79 frequency calibration.

## Implemented and observed

- PC Direct USB backend and own frame encoder; Standard/Hybrid/Multiband options exist. Older statements that every route requires Sensa SDK DLLs or Synapse are no longer current. Prior acoustic reports are recorded in STANDALONE_STATUS_20261108.md; they do not establish waveform equivalence.
- Native Xbox conversion uses DualSense HID trigger effects plus physical pull for impulse vibration and rear haptic PCM envelopes for body output. Audio owns body output only; Normal remains game-managed.
- Combined 35s seven-phase synthetic game-format demo, existing Classic/Combined setting, software routing/safety tests, local WPF publish, branded icon and Start Menu app are implemented.
- **New user report:** trigger-only test works; combined haptic/trigger left/right/together feels apparently correct (user wording: “düzgün gibi”). This is qualitative user-felt evidence. No sensor measurement, independent full-scale pull readback, genuine-game haptic proof, Classic/cross/off/stop acceptance or DualSense equivalence follows. Earlier four unavailable-device probes remain valid historical results, not an ongoing denial of this later observation.

## Remaining work, in practical order

### User-reported seek/recovery bug — queued after profile extraction

User reports Audio-to-Haptics works while listening to YouTube music, but seeking abruptly
back to the beginning can stop vibration indefinitely. Switching Standard/Hybrid/Multiband
restores it; sometimes it returns after a delay. Reproduction and cause are not yet confirmed.
Finish the ongoing Synapse profile extraction first, then investigate production loopback
capture health, missing/silent callbacks, delivery watchdog/reset, feature/spectrum history,
and Direct USB engine recovery. Do not assume which layer failed from this symptom alone.
Acceptance: repeated seek-to-start, rapid consecutive seeks, pause/resume, silence-to-sound,
and continuous playback recover without changing output method, on all three methods.
Preserve bounded queues, independent silence stop and no post-stop writes. Separate actual
YouTube/manual reproduction from deterministic discontinuity/lifecycle regression tests.

### User-requested mode-screen UX — queued after seek bug

Preserve this order: finish Synapse profile extraction, fix/verify YouTube seek recovery,
then implement the following UI/startup changes.

- Under the Audio-to-Haptics mode selection, use the existing content area for vertically
  stacked buttons covering both existing output choices and newly derived profiles. Keep
  profile selection distinct from Standard/Hybrid/Multiband processing where they are
  separate dimensions; do not silently discard existing choices or conflate their behavior.
  Selecting a button shows a readable explanation, intended use and evidence-qualified
  advantages for story games, competitive games and music. Do not invent measured wins
  or label Synapse-inspired approximations byte-identical without validation.
- Selecting Native/DualSense conversion should orchestrate required existing components
  automatically and expose readiness/progress/errors on the mode screen, including the
  existing administrator-required launch path where necessary. Keep Settings controls;
  users should not need to visit Settings for routine startup. Windows UAC/authentication
  must remain user-approved; do not bypass it, cache credentials, or add unsolicited
  persistent elevated tasks/services. Avoid duplicate instances, competing motor owners,
  and hiding the physical controller before virtual/input/output prerequisites are ready.
- Add coherent HidHide installation/status/start-stop controls. Inspect current installed
  driver/service configuration first; distinguish enabling/disabling cloaking, restoring
  RW3-owned hide rules, starting/stopping the bridge, and actual driver installation.
  Never label one operation as another or erase unrelated HidHide settings. Actual driver
  installation needs a concrete component/source/license and rollback plan before execution;
  presenting the requested controls does not require auto-installing a driver now.
- Visually verify the main-mode content and retained Settings, keyboard/controller focus,
  state updates, failure/retry, repeated mode selection, stop and application exit.

1. Complete physical acceptance of Classic versus Combined, crossed channels, body-only and trigger-only controls, live option changes, output-zero, stop/unplug/reconnect and mode changes. The combined demo currently requires the impulse option on; it cannot by itself validate Classic. Retain the user's approximate observation without upgrading it to every phase passing.
2. One genuine DualSense-native game with the current installed build: actual authored rear PCM and effect reports, correct Wolverine input, no double input, repeatable simultaneous output while the game is foreground. Older TLOU2 input/icon/readback observations are narrower evidence.
3. Xbox game plus Audio-to-Haptics: demonstrate that the game's original impulse feedback survives simultaneous body-only XInput writes. Command shape alone is not physical cross-process arbitration proof. Run longer sessions, device/audio endpoint changes, sleep/resume, volume/mute and stop/recovery.
4. **Concrete code gap found during this review:** MainWindow.Level_Checked updates `_rumble` and `_bridge`, but the running `_xboxAudio` owns a separate XboxAudioSink initialized with the starting level. Live global level changes therefore do not propagate to that active audio sink until recreation. Fix serialized live parameter delivery and verify it; do not claim the Native four-channel level fix also covers this separate route. Audio sensitivity and device-slot live behavior deserve the same route-specific review.
5. Return to the user's deferred latency work: validated low/high frequency hybrid plus short-transient path. 85.33ms is spectral history, not measured total game-to-motor latency. Do not enable the failed 2048 shortcut or assume 33ms is an immutable hardware limit. Physical DualSense/Wolverine repeated reference A/B and Standard/Hybrid blind feel comparison remain open; phase, nearby-tone and short-hit fidelity are not complete.
6. Publish the current uncommitted changes and run current Windows CI/release smoke when authorized, with version/changelog/hashes and current installed-copy updates. Local launch/shortcut success is not a signed installer/update acceptance pass.

## SDK files and deployment

Never put Sensa runtime DLLs into game folders for this app's architecture. The optional vendor backend loads its licensed runtime beside the RW3 executable; Direct USB does not need Sensa SDK DLLs. HIDMaestro.Core.dll is a distinct virtual-controller dependency already packaged beside RW3, with its separately installed driver prerequisite. These libraries do not add native DualSense support to an otherwise unsupported game. No injection, new driver installation or redistribution of proprietary Sensa binaries was performed in this review.

## Subsequently requested tabs — after mode-screen UX

User requested two adjacent tabs beside Settings, in this order:

1. **Kısayollar / Shortcuts:** first provide a visual inventory of the actual shipped defaults
   and saved assignments; then support adding, editing and deleting user shortcuts in a
   Synapse-like editor. Inspect Synapse and existing app input mappings before defining the
   model: distinguish controller remaps/chords, application commands and keyboard macros
   rather than assuming the user's word “shortcut” means only keyboard hotkeys. Discover
   which functions are actually supported by the device protocol versus app-side handling.
   Persist custom entries without overwriting defaults; allow explicit reset, diagnose
   conflicting assignments, and retain keyboard/controller navigation. Verify foreground
   and background scope explicitly; existing controller-driven app navigation is foreground
   only and must not silently become system-wide input injection. Read-only Synapse/device
   inspection is authorized; do not activate user-authored macros as part of mere editing.
2. **Aydınlatma / Lighting:** the next adjacent tab controls this controller's lighting in a
   Synapse-like screen. Inspect actual Synapse controls and establish supported device
   commands, defaults, persistence and PC/Xbox-mode differences before implementation.
   Do not assume full RGB/effect support from the existence of an indicator LED. Show only
   verified controls and meaningful unavailable states; no firmware/driver modification.

These are queued work, not implemented or hardware-validated features. Preserve earlier
order: profile study → seek/recovery bug → mode/profile/startup/HidHide UX → Shortcuts
inventory/editor → Lighting tab.

## 2026-11-08 live continuation

User explicitly authorizes automatic continuation without waiting for repeated “devam”.
Only actual physical-device loss or user-only Windows approvals require a handoff.
Controlled and Balanced full source series each completed306 cases. Controlled complex
and Balanced complex each completed123 cases with offline analysis exit0 and capture stop
confirmed. Dynamic complex is currently running in `dynamic-complex` (123 planned cases),
USBPcap6 address12. Elevated helper was renewed with a four-hour bound; no driver changes.
Current source runner is `TestResults/usb-profile-study-20261108/probe-next/`.
After Dynamic: inspect/test Custom, restore its original backed-up Controlled30–130/70%,
then switch Synapse Audio-to-Haptics OFF visibly before running pc-standard/pc-hybrid/
pc-multiband complex comparisons with --synapse-off-confirmed. Record/restore user state.
Do not finish each turn merely because a long series started: continue analysis and advance
profiles automatically when completion/finalization are verified.

New code: AudioStructureFixtures (41 declared complex sources), AudioStructureAudit;
HybridOutputPolicy weak-third veto fixed: second strongest now decides whether a meaningful
pair exists. Baseline audit had0/58 multi side-frames for40/.3+120/.3+300/.045; fixed58/58.
Regressions failed before fix and passed afterward. This is software evidence, not hardware
acceptance; installed copy has NOT been republished with this fix. Nearby80+84 still merges
near82 in the4096-sample analyzer. Keep this failure visible.

USB timing: old address11/2.4-labeled attachment had~29.3ms median control completion RTT;
new address12 attachment~.26ms. Same-link Controlled pilot completed6 cases/210 frames and
retained pitch code8 at80Hz; Balanced gives10. No physical motor-latency conclusion.
Legacy79 transient decoder still needs correction: archived learn.jsonl1225 rows parse
structurally with21-bit transient payload, unlike old24-bit assumption;55 transient rows.
Pitch/time semantics need independent calibration before enabling transient packing. Do not
silently ignore the one archived legacy transient decode error or hand-pack unknown fields.

DSX feature discussion is queued after Shortcuts and Lighting, not before current research.

## Latest user priority override — 2026-11-09

First fix and verify the YouTube seek/recovery bug; it is NOT fixed in this checkpoint.
Then return to the22-item feedback backlog, implementing genuine missing pieces rather
than duplicating existing rumble/trigger bridges. Remaining previously planned UX, Custom
editor, Shortcuts, Lighting and DSX discussion follow that review/work. User requested a
commit of the current work; no push was requested. Full contracts and Windows build passed
for the existing increment, including legacy-without-PCM routing; the diagnostic Xbox audit
also verifies serialized live level0/resume50/stop and body-only commands without hardware.

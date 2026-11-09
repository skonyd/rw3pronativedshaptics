# Routing audit and diagnostic cleanup — 2026-11-09

In Audio mode, enable audio body output and choose Xbox game body rumble and
LT/RT independently. Either game source uses the same opt-in virtual Xbox
bridge and physical four-motor writer. Audio plus LT/RT alone keeps game rumble
out of the body mix. Enable the bridge before launching the game.

The first Xbox option requests Windows elevation directly when needed. Approval
restarts RW3 with the selected route. Cancellation leaves the bridge disabled.
Native also offers ready-made DualSense source combinations. The updated
[remaining work](NEXT_WORK_20261109.md) tracks live game, elevation and cleanup
acceptance separately from decoder and mixer tests.

## Test tab and DualSense fallback scenarios

- **Ses + Xbox tetikleri** runs real default-endpoint WASAPI audio and synthetic
  Xbox-format reports through the production decoder, mixer and physical
  GameInput writer. Phases are body only, LT, RT, both with game rumble, trigger
  release with continuing audio, LT after audio, and all off. The test caps
  global level at 40%, uses no virtual device or HidHide, and accepts cancellation.
  It does not exercise game-to-virtual Xbox callbacks.
- **Normal rumble + adaptif tetik (PCM yok)** sends DualSense USB 0x02 reports
  to the running virtual DualSense. The first half sends compatible rumble v1
  and L2 feedback. The second sends v2 rumble and R2 weapon effect, then an
  explicit all-off report. This scene and the trigger-only scene skip audio
  endpoint playback entirely.
- Native virtual DualSense receives HID and PCM independently. A game can send
  normal rumble plus adaptive effects, PCM plus adaptive effects, trigger-only,
  or ordinary rumble alone. A manual UAC capture supplies PCM only. Trigger
  interpretation uses the physical Wolverine trigger's analog position plus
  the game's authored position and force fields. The Xbox impulse motor conveys
  a pulse or vibration; Wolverine does not generate counterforce. Classic
  and PC modes route the approximation to the body. Xbox game's own LT/RT
  amplitudes are already motor commands and do not enter the DualSense interpreter.

Standalone run on connected wired Xbox Wolverine 1532:0A3F completed seven
phases, decoded 65 synthetic reports and recorded 248 GameInput write calls.
No controller concealment or virtual device creation was used. Physical motion,
real game feedback and virtual Xbox callback acceptance are separate checks.

Follow-up on the installed test path: the user felt the body motors and reported
no trigger motion. Five UI runs decoded 65 reports each and recorded 238–247
GameInput calls. The physical Xbox device advertised motor mask 0x0F, including
both trigger bits. This is a physical trigger-test failure despite API activity.
The added **Xbox tetik çıkış testi** isolates LT and RT at up to 65% using
GameInput and then Windows.Gaming.Input. Its physical result is pending. The
production writer remains GameInput until an alternative is validated.

The user subsequently felt the isolated trigger test, while the combined test
still felt like body output only. The original combined test applied a 40%
master cap to 70% trigger commands (28% at the writer). The isolated test
reaches 65%. The revised combined diagnostic uses the same trigger ceiling
for trigger-only and audio-plus-trigger phases, with a lower audio-body ceiling
to reduce masking. The user reported feeling both the body and the trigger
motor separately in the revised test. This is subjective physical confirmation
of distinct outputs in the synthetic test. It does not isolate the cause of the
earlier failure or verify each side or phase individually. Live game gains use
the existing output level. Game-to-virtual Xbox report reception remains open.

## Experimental Xbox game feedback with system audio

General → Sesten titreşim → Xbox oyun rumble ve tetiklerini birleştir starts
an opt-in virtual Xbox Series BT profile with the installed HIDMaestro SDK.
The physical Xbox-mode Wolverine is the sole motor target, bound by GameInput.
RW3 forwards physical input to the virtual controller, reads game HID rumble
callbacks, merges game and audio body levels by per-side maximum, and sends
the game LT/RT levels in the same four-channel command. System audio contributes
zero to LT/RT. The existing audio profile and global output level apply.

The short all-motor Xbox HID format follows the HIDMaestro profile/driver and
the [SDL packet layout](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_xboxone.c).
[PadForge callback code](https://github.com/hifihedgehog/PadForge/blob/main/PadForge.App/Common/Input/HMaestroVirtualController.cs)
provides independent evidence for LT/RT followed by the two body bytes. Other
report formats, including separate legacy XUSB feedback, need verification.
To avoid holding stale commands, audio expires after 250 ms and game feedback
after one second without a new command. Long game effects may end early.

The option starts unchecked on every launch. It requires an elevated RW3 process,
installed HIDMaestro and HidHide, and one wired Xbox-mode Wolverine. Enabling it
asks for consent to temporarily hide the physical game-facing nodes. Stop, failure
and exit release the motor output and restore the saved HidHide transaction.
Restart the game after the bridge starts so it opens the virtual controller.
The app shows the number of decoded feedback reports for diagnosis.

Software contracts passed for independent LT/RT, rejected malformed reports,
combined output and trigger stop with continued audio. SDK-enabled publish and
SDK-free Windows build passed. Hardware status is untested: the Xbox Wolverine
enumerated over USB, but this session did not create the elevated virtual device,
change HidHide state, capture game feedback or measure motor motion.

## Source selection below the mode cards

The General screen now stores independent Native PCM, rumble and L2/R2 checkboxes.
Native's audio endpoint bridge can play PCM alone. Start the virtual DualSense bridge
from the same panel to receive game rumble and trigger reports. In Xbox mode the
existing four-motor writer can combine all three sources, with an impulse/body
destination checkbox and the existing gain and priority controls in Settings.
PC mode routes L2/R2 effects to the body and honors the three source switches.
The Audio panel has a system-audio switch. Combining system audio with real Xbox
game feedback still requires the four-channel feedback adapter described below.

Contracts exercised all eight Native Xbox source combinations and loaded older
saved levels with enabled-source defaults. The connected wired Wolverine enumerated
as 1532:0A3F. GameInput returned 1,222 successful reads and zero misses in 20 seconds.
The sampled triggers were at rest throughout, and the probe sent zero motor
commands. Physical effect and real-game acceptance remain open. The separate
Opera foreground defect is tracked in [issue #16](https://github.com/skonyd/rw3pronativedshaptics/issues/16).

Review of current local source; API/software routing is distinct from physical acceptance.
Follow-up on main: Native Xbox now has persisted PCM, rumble and L2/R2 levels (0 mutes
the source) and per-side body priority. Defaults preserve the previous max mixer. Audio
Xbox skips redundant idle zero writes while still stopping active output. This reduces
idle interference but leaves active Audio/game impulse arbitration open.

PadForge documents game impulse capture through virtual Xbox One/Elite/Series profiles:
[Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/). The RW3 adapter
needs verified LT/RT callbacks from a compatible local profile and one four-motor writer
before enabling this route. Physical acceptance requires separate body/LT/RT commands
and simultaneous system audio on the Wolverine; the device was absent in this session.
User's first diagram is stale for Native: genuine report rumble and adaptive-trigger paths
already exist. The second diagram is a useful target, not a delivered general-purpose router.

| Mode / physical mode | Current body path | Trigger / game feedback |
| --- | --- | --- |
| Normal | Game-managed | RW3 does not synthesize, intercept or mix normal game feedback. |
| Audio / Xbox | System front L/R audio → XboxAudioSink filters/envelopes → XInput body command | No adapter captures the Xbox game's impulse commands. Separate game output may conflict with RW3 output; no preservation guarantee. |
| Audio / PC | System audio → selected lossy Standard/Hybrid/Multiband Sensa mapping | PC trigger motors are not available through this path. |
| Native / Xbox | Virtual DualSense rear PCM envelopes + report rumble → BridgeMotorFrame → device-bound GameInput | Genuine report trigger effects + physical pull interpreted into impulse vibration; TriggerFeel can instead fold effects into body. Foreground-output failure remains open. |
| Native / PC | Rear PCM → Sensa; report rumble/trigger approximations → compatible body output | No independently driven PC impulse motors. Simultaneous Sensa/compatible-body arbitration is not universally verified. |

## Implemented router subset

`VirtualDualSenseBridge.OnOutput` parses the virtual controller's report, retains valid rumble
bytes and trigger effects, and evaluates effects against actual physical pull. Legacy rumble
works without haptic PCM. `BridgeMotorFrame.Route` uses per-side maximum for body sources;
it does not sum every source or implement configurable game/audio priority. `BridgeMotorOutput`
serializes Native motor frames, global level,250ms input watchdog and safe stop. The existing
Classic/Combined setting selects trigger fold-to-body versus Xbox impulse output.

There are no independent persistent source toggles for PCM, rumble and L2/R2, no flexible
per-source gain/priority matrix, and no general live router UI. `HapticRouteCatalog` is a retained
route-description catalog; it is not the active runtime mixer or a visible flexible routing screen.

Sensa L/R and rumble L/R must not be presented as four independent body motors. They are
different ways of driving the controller's two body actuators, dependent on physical mode.
Xbox envelope rumble is not the PC Sensa frequency-controlled path or lossless HD PCM replay.

## Audio plus normal Xbox-game impulse feedback

The application requests body vibration through XInput's two-field API, but captured USB
GIP reports enabled all four channels with zero trigger fields. API shape does not prove that
another process's impulse state survives. WGI/GameInput setters and their readback must not
be mistaken for a capture adapter for another game's outgoing feedback. Current Audio mode
does not run the virtual DualSense callback source used by Native. The unresolved Opera focus
issue is an additional independent output limitation.

Remaining work under backlog4/10: a genuine game-feedback source adapter, common ownership
of all four Xbox outputs, per-source enable/gain/priority settings, and real-game body+trigger
acceptance. Do not silently change Audio into Native, manufacture adaptive effects from music,
install interceptors, or promise arbitrary Xbox-game feedback capture without a verified source.

## Cleanup requested by user

Removed from production:10Hz endpoint-meter polling/restart heuristic, temporary120s
`--trace-audio` logging, callback diagnostic timestamps/peak state and Xbox trace-only fields.
Deleted unused AudioCaptureRecovery helper and its heuristic-specific contracts. It did not
solve the actual focus failure and the process-local endpoint meter was unreliable.
No measured CPU percentage improvement is claimed. Independent250ms stale-output silence,
bounded delivery queue, recording-stopped handling, real output-failure handling and serialized
stop remain. Hardware probe CLI tools are explicit-run diagnostics, not background app tasks.

Required validation: Windows Release contracts, WPF publish, packaged self-test and installed
DLL/EXE hash match. No new physical motor or actual-game coexistence test is implied.

Validation completed: Release contracts passed (`TestResults/router-cleanup-contracts-20261109.log`),
win-x64 WPF publish with warnings-as-errors succeeded, published and installed self-tests exited0.
After user tray Exit, previous overwritten program files were backed up under
`TestResults/router-cleanup-installed-backup-20261109`; installed DLL and EXE hashes match publish.
DLL SHA256:8660B9C3D3F99D8F4F3DB20BD011450B58F92593445CEFADF1A6F5BA66841524.
EXE SHA256:ED6E719F5533819761126C61BBD48A303F7B71A10E2A239AC96D6EFB08B29D14.
Reopened installed app minimized to tray, without trace flags. No new physical motor test.

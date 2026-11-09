# Routing audit and diagnostic cleanup — 2026-11-09

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

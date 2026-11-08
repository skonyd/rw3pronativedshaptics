# Proposed temporary HidHide isolation trial

Status: user explicitly approved; trial completed, physical FAIL, configuration restored.
Worker: `tools/test-focus-isolation.ps1`, dry-run by default; `-Apply` requires elevation.
Result: `TestResults/focus-isolation-descendants-20261109/status.json`: restored=true,
no restore errors, blocked apphost slots=[], allowed dotnet slots=[0]. User still reported
that the fresh Opera window cut output. Do not enable permanent Audio-mode hiding.

Purpose: distinguish foreground output ownership from the RW3 audio pipeline. This is
a diagnostic experiment, not a proposed permanent Audio-mode behavior or a promised fix.

## Established state

Read-only snapshot: `TestResults/hidhide-before-focus-trial-20261109.json`.
HidHide cloak OFF; inverse OFF. Existing hidden Sony entries and application entries
must be preserved. Initial Razer-ID Xbox node found:
`USB\VID_1532&PID_0A3F\0000000000000001` (XboxComposite).
Re-enumerate before applying; reject absent/ambiguous/changed identity.
The completed visibility-isolation run also included its Microsoft-ID USB IG child and HID
IG grandchild after their ancestry and common container were independently verified.

## Bounded change and rollback

1. Keep RW3 in Normal and no other motor test running. Save a fresh complete snapshot.
   Refuse inverse mode or pending RW3 restore state; do not overwrite another owner's state.
2. Temporarily allow the installed x64 `dotnet.exe` probe host if not already allowed.
   Add only the exact confirmed Wolverine Xbox node to the hidden list if absent.
   Enable cloak for the trial. Do not change inverse mode, drivers, services or browser settings.
   Note: enabling cloak also activates previously listed Sony entries during the trial;
   other applications may temporarily lose those controllers as well.
3. Launch a fresh isolated Opera test process AFTER hiding; existing open handles are not
   proof of isolation. Do not terminate the user's regular Opera or alter its profile.
   Verify the test process cannot enumerate the physical Wolverine. If isolation cannot
   be demonstrated, mark the result inconclusive rather than a failed/output-success test.
4. Run the existing strict-device XInput constant40% body probe for60s, triggers0.
   User checks output while isolated Opera is foreground. The probe zeros motors in finally.
   Do not infer physical output from successful API writes.
5. In finally remove ONLY newly added node/application entries and restore initial cloak
   state. Preserve all pre-existing entries and inverse setting. Verify against snapshot.
   Retain the snapshot and exact undo commands before the first mutation in case of crash.
6. Close only the trial browser. No production routing change follows automatically.

## Acceptance and limitations

If motors continue only under demonstrated isolation, investigate access arbitration further.
If they still cut, HidHide is not a proven remedy. It is possible hiding makes the probe or
shared GameInput service lose access; that is a visibility failure, not motor verification.
Even a positive result would not justify hiding Audio mode from every game: normal game
input and impulse-trigger feedback must still work. Native's virtual-controller isolation is
a separate already implemented route. Keep the main bug open until the real app passes
repeated Opera/YouTube checks with usable game input and stop/reconnect behavior.

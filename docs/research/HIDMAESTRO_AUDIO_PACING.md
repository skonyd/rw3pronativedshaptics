# HIDMaestro USB/IP DualSense: the audio endpoint drains in 20 ms bursts

Status (2026-11-24): cause outside RW3; not fixed. Reported 2026-11-24: https://github.com/hifihedgehog/HIDMaestro/issues/76 and https://github.com/vadimgrn/usbip-win2/issues/207 (texts posted as in `TestResults/issue-*.md`). Measurements: E-20261123-81 … -85 in
[EXPERIMENTS.md](EXPERIMENTS.md). Below is a draft issue for HIDMaestro
(https://github.com/hifihedgehog/HIDMaestro). The user posts it; RW3 does not.

## Symptom

The Last of Us Part II (PC, v1.0.10402.1014) with HIDMaestro's `dualsense-composite` over USB/IP: the game's main
output (TV) crackles. A real DualSense on the same PC, same scene: no crackle. With an Xbox output instead of the
virtual DualSense: no crackle.

## Measurements

* **The game's TV output** (WASAPI loopback, 60 s, dialogue scene): 171 holes of exactly 64 samples of digital zero,
  all on one 6720-sample (140 ms) grid, while the game also renders haptics to the virtual DualSense. Real DualSense:
  0 holes. The level while sound plays is the same (rms 0.0064 vs 0.0067).
* **The virtual endpoint's own stream** (`HMAudioOutput.FramesReceived`): 6000 blocks of 480 frames, mean spacing
  10.00 ms, one gap of 26 ms. So the URB completions are steady.
* **How Windows drains the endpoint** (shared-mode, event-driven render of silence, 1056-frame buffer, 20 s,
  `GetCurrentPadding` read at every 10 ms event):

  | endpoint | padding at each event | clock position vs a straight line |
  |---|---|---|
  | TV (HDA) | 576 every time (480 taken per period) | ±0.03 ms |
  | real DualSense (USB, `wdma_usb.inf`) | 576 every time | under 1 ms |
  | HIDMaestro DualSense (USB/IP, `wdma_usb.inf`) | **1056, 96, 1056, 96 …** (nothing, then 960 frames) | 2 to 5 ms |

  Endpoint format and periods are the same on both DualSense endpoints (4 ch, 16-bit, 48 kHz, mask 0x33, default
  period 10 ms, minimum 3 ms).

## Ruled out

* **Bus speed.** The profile rebuilt from the dump's other-speed (full-speed) configuration, isochronous interval 1:
  the same 1056/96 pattern.
* **`start_frame` in RET_SUBMIT.** `FrameAtCompletion` is the URB's due tick, the end of its last packet's window; a
  real host controller reports the first packet's frame. Reporting `FrameAtCompletion - PacketCount` (completion
  callback wrapped at run time): the same 1056/96 pattern.
* **Nagle.** The server sets `NoDelay`.

## Versions

HIDMaestro.Core 2.1.0 (68040e2), usbip-win2 host controller as embedded in it, Windows 11 Pro 26320, x64.
The 2.2.0 and 2.3.0 release notes mention no change to the audio path.

## Question for the maintainer

Steady completions on HIDMaestro's side, yet `usbaudio` (legacy UAC1 class driver) takes the client's data every
other period. Is something on the usbip-win2 side (current frame number, completion delivery, URB scheduling)
known to make `usbaudio` refill in pairs, and is a real DualSense's 10 ms cadence reachable over USB/IP?

## Could RW3 route around it? (2026-11-23, source at v2.1.0)

Idea: present the virtual DualSense's audio function as USB Audio 2.0, so Windows binds `usbaudio2.sys` (a different
timing model) instead of the legacy `usbaudio.sys`. Findings:

* **HIDMaestro speaks UAC1 only.** Class requests to an audio interface go to `UsbAudioEngine.HandleUacGet/Set`
  (`UsbipEmulatedDevice`, "UAC1 feature-unit request"): GET_CUR/MIN/MAX/RES on feature units, SET_CUR. UAC2's
  CUR/RANGE requests and its clock source entity (sampling frequency, clock valid) would stall, and `usbaudio2.sys`
  would not start the function. A profile cannot answer them: no profile-level control-request replies exist for audio
  interfaces.
* **So it needs a HIDMaestro change and our own build of `HIDMaestro.Core.dll`.** The project's build compiles the
  UMDF2 driver and signs payloads (Windows SDK + WDK + Visual Studio C++; this PC has the SDK's signtool only, no WDK,
  no Visual Studio) and downloads usbip-win2's installer. A managed-only build that embeds the payloads taken from the
  shipped 2.1.0 DLL looks possible but needs project surgery.
* **The outcome is not known in advance.** Nothing shows yet that `usbaudio2.sys` drains a USB/IP endpoint any more
  steadily; the bursts may come from the virtual host controller itself.
* Changing usbip-win2's host controller is out: a kernel driver needs Microsoft's signature (or test signing, a
  security setting RW3 does not touch).

Cheapest next step: the report above to HIDMaestro's maintainer (active; 2.3.0 on 2026-11-20), who knows the
usbip-win2 side. The UAC2 route stays a multi-session project with an uncertain result.

## Where the pairing comes from (2026-11-24)

* **HIDMaestro's side is steady** (E-20261124-86, its pending-URB list sampled every 0.5 ms): 10-packet OUT URBs,
  each completed at its due time (median 0.36 ms late), every 10 ms. But Windows submits two at once every 20 ms,
  only after both earlier ones completed; 6-20 ms queued.
* **Windows' own USB trace** (E-20261124-87, `Microsoft-Windows-USB-UCX`, both pads at the same moment, same
  driver, same 10-packet ASAP URBs of 3840 bytes):
  * real DualSense: two URBs submitted at the start, the first completes 13.3 ms later, then every completion is
    followed by one submit; always two in flight;
  * HIDMaestro DualSense: one URB at the start, completed 10.4 ms later, then two submits; the next completion
    brings none, the one after two again; one or two in flight.
* **Start-up slack does not change it** (E-20261124-88): moving HIDMaestro's OUT cursor 3.5 ms later at the stream
  start left the pattern as it was.
* No `URB_FUNCTION_GET_CURRENT_FRAME_NUMBER` appears in the trace, so `usbaudio.sys` reads bus time through the bus
  interface, which the virtual host controller (usbip-win2's UDE driver) answers. That `usbaudio` keeps only one URB
  ahead every other period points at that answer; this part is kernel code and cannot be changed from RW3 or
  HIDMaestro's managed code.

For the report: the trace comparison above is the strongest evidence; it may concern usbip-win2
(https://github.com/vadimgrn/usbip-win2) as much as HIDMaestro.

## A kernel-mode virtual DualSense drains steadily (2026-11-24)

DSX's virtual DualSense (Nefarius Virtual Gamepad Emulation Bus G2, a Microsoft-attested UDE host controller, with
Nefarius' `nssvdsflt` audio filter below the same `usbaudio.sys`), same probe: padding 576 in 1991 of 2000 periods
(480 frames every 10 ms; 5 + 4 periods of 1056 / 96 in 20 s), clock jitter -1.2 / +9.7 ms (a few hiccups). So
Windows' audio driver drains a virtual DualSense every 10 ms when the virtual host controller is a kernel driver of
its own; over usbip-win2's host controller it drains in 20 ms bursts.

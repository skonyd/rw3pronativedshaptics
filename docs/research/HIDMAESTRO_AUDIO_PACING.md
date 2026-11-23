# HIDMaestro USB/IP DualSense: the audio endpoint drains in 20 ms bursts

Status (2026-11-23): cause outside RW3; not fixed. Measurements: E-20261123-81 … -85 in
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

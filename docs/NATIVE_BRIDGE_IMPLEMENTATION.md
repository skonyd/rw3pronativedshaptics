# Native v2 bridge — 2026-11-07

**Prototype: lossy parametric output, not native waveform replay.** See [status](IMPLEMENTATION_STATUS.md), [analysis](SENSA_PIPELINE_ANALYSIS.md) and [hardware record](HARDWARE_TEST_LOG.md).

## Current UI and setup

The main window has Genel / Test / Ayarlar. The separate expert bridge, Test Lab and A/B windows and right-motor finder are removed.

1. For Sensa use Wolverine wired PC mode (`1532:0A4C` in the recorded setup), working Synapse/Chroma, and compatible legally obtained HAR.dll / Interhaptics.RazerProvider.dll. No proprietary runtimes or drivers are distributed/installed by RW3.
2. Select Native in Genel. It runs automatically and retries missing prerequisites every 5 seconds. The source is the first compatible 4ch/48k endpoint named DualSense/Wireless Controller. A name match is a hint, not device identity or game provenance.
3. Capture reads rear PCM channels 3/4, while retaining strict PCM16/Float32 format validation. Live bridge requests 10ms loopback buffering; Windows can grant more. Feature slots are 1ms; the engine targets 5ms amplitude/frequency/transient updates via `UpdateParametricEffect`, separately for Hand Left/Right.
4. Test offers side pulses with user sensation answers and 23 synthetic demos, targeting Wolverine or USB DualSense. These generated signals are never original game assets. Tests pause the selected mode and it resumes afterwards.
5. DualSense reference sends original quad PCM16 bytes in Exclusive only. Unsupported format or busy/failed initialization produces an error; Shared fallback and mix-format conversion are absent. No RW3 output gain applies to the reference. WASAPI completion alone does not prove physical playback.
6. Switch to Normal or exit from the tray to stop Native operation. X/minimize hides to tray and retains the selected mode.

## What is and is not established

Prior user-felt Hand L/R body pulses establish actuator reachability under recorded conditions. Software frame separation is not a physical cross-talk measurement. Real-game authored PCM, compatible Wolverine input-to-virtual-DualSense binding, calibrated latency/fidelity and full safety/reconnect acceptance remain open. The current UI reports rear PCM observation separately from those missing proofs.

The source-only recording/WAV/raw transport APIs and evidence fixtures remain library capabilities with contract coverage. Removed expert UI controls are not current user instructions. Source-only transport must never coexist with Sensa or recording. Wwise offline WAV is not a proven live high-fidelity route. See [ADR-002](ADR-002-NATIVE-BRIDGE-ROUTE.md) for research constraints.

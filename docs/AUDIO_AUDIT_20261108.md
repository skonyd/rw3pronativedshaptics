# Audio/native review and single-microphone audit — 2026-11-08

Latest follow-up: [repeated scenario audit](SCENARIO_AUDIT_20261108.md). Default Sensa input is now unity, replacing the historical 2.2 gain discussed below. Added partial-frame/nonfinite regressions, independent phase/multitone checks, 68 new microphone cases and Xbox diagnostic-writer safety coverage. Nine short-pulse comparisons remain outside tolerance; output ceiling and physical equivalence remain open.

## Result and scope

The local audio→Sensa implementation is a **prototype**, sharing the Native path's lossy feature extractor. Equalized synthetic inputs now pass stronger software comparison. The controlled microphone audit completed 130 cases; see [final acoustic results](ACOUSTIC_RESULTS_20261108.md). Physical equivalence and repeatability remain unverified. M3 is open; M4 is not implemented.

Initial attempts were blocked (historical; capture now works through the explicitly selected .NET Host): the first WASAPI microphone open stalled and that probe process was stopped; the next attempt failed at `AudioClient.Initialize` with `E_ACCESSDENIED (0x80070005)`. Windows microphone and desktop-app consent settings read `Allow`; this does not identify the cause. An explicitly selected MME/PCM16 microphone path was added, but its attempt stopped at the hardware prerequisite: Wolverine was no longer enumerated (`WolverineUsbNode=false`, `WolverineWiredPcNode=false`). DualSense and A4tech remained enumerated. A subsequent microphone-only MME attempt failed at `waveInOpen` with `UnspecifiedError`; this API did not produce a usable measurement either. No new microphone/motor response results are claimed. No privacy setting or driver was changed; DualSense was not driven.

## Defects fixed

- Comparison previously truncated to the shorter feature list, skipped known→unknown frequency errors and checked only the first transient amplitude. It now rejects missing/empty frames, frequency availability differences, invalid values, per-frame transient counts, and both transient amplitudes/positions.
- Audio capture health previously meant a capture object existed. RecordingStopped and processing failures now clear health, silence delivery, and expose the cause to the UI/retry loop.
- Audio delivery is serialized and bounded to two seconds of stereo samples. Queue overflow or sink failure stops output explicitly and releases pooled PCM. Stop prevents subsequent sink writes, releases endpoint notifications and joins the delivery worker.
- Independent 250ms stale-output protection runs even when WASAPI stops sending callbacks. It preserves intentional queued sync delay, then silences once the queue drains. This is not a physical motor-stop deadline.
- Sensa silence clears old feature history without resetting its published cursor; Xbox silence clears filter/partial-energy state. Audio bridge owners now dispose replaced instances.
- Resampler coefficients/history and usual conversion buffers are prepared before capture callbacks; callbacks reject bursts above 250ms. Supported audio formats are checked before start. Front L/R only is explicit; surround downmix remains open.
- Audio pre-gain saturation is counted. The fixed gain is 2.2, so arbitrary audio samples above approximately 0.455 can clip. Equalized fixtures divide the test source by 2.2 and cannot prove this clipping absent in games.

## Windows software verification

- Windows Release WPF and HardwareProbe builds pass with warnings treated as errors.
- Contract suite passes, including the comparison counterexamples, delayed-tail preservation, queue overflow, sink failure, 250ms watchdog, stop/no-late-write, silence/history and clipping diagnostics.
- Existing finite catalogue: 94 selected signals pass at 48k, 44.1k and 96k under the strengthened checks. Tests include selected levels, transients, side changes, 23 demos and sound-test patterns. Resampling is within tolerances, not byte-identical.
- Additional grid: **371 integer frequencies (30–400 Hz) × 4 levels (0.05/0.2/0.5/0.9) × 3 rates = 4,452 cases**, each 300ms with a 240ms ramped left-channel tone. No failing case at amplitude 0.01, frequency 1Hz, transient amplitude 0.02, exact transient position/count and no missing/invalid frames. Max amplitude error: 48k `6.55651e-7`, 44.1k `6.07967e-6`, 96k `6.67572e-6`; max frequency difference 0Hz in this grid.

These are equalized-input feature comparisons using shared processing, not independent proof that extraction preserves the original waveform. The finite grid is not every possible frequency, phase, level, game signal, packetization or source-device behavior. Physical output is still one dominant frequency, four amplitude points and up to two transients per side at current 33ms output pacing.

Local raw results (git-ignored): `TestResults/contracts-20261108.txt`, `TestResults/acoustic-audit-20261108/software-grid/grid-{48000,44100,96000}.json`, and attempted acoustic setup reports. CI for main `2d59f2d` remains separate from these uncommitted changes.

## Prepared acoustic procedure

Use only Wolverine wired PC identity `1532:0A4C`; A4tech in the user's fixed position, Xbox button uppermost near the microphone. Record exact firmware (still missing), Windows/Synapse/runtime versions, endpoint volume/mute and SDK hashes. Never move the controller or microphone between paired measurements.

1. Run two interleaved sets of engine-idle, audio played with engine off, synthetic Native-extractor→Wolverine, and the same equalized source through Windows loopback→audio→Sensa. Use the explicit Steam Streaming Speakers 48k virtual endpoint so a physical loudspeaker does not contaminate the microphone. Stop if controls show unrelated vibration or acoustic contamination.
2. Sweep 13 frequencies (30/40/50/60/80/100/120/150/190/250/300/350/400Hz) × three source levels (0.2/0.5/0.9), plus separate right-side 80/190Hz scenes, through both paths. This is 82 paired-path cases, not exhaustive physical coverage.
3. Repeat 80/190Hz cases three times per path. Compare background, engine-idle, driven-and-tail and engine-off windows, relative RMS, requested-tone amplitude and 10ms level bins. Extend with simultaneous stereo/transients and long-run stop/restart only after controls and repeatability pass.
4. Keep original synthetic quad PCM16 SHA256 unchanged. Audio test playback is explicitly scaled by 1/2.2 for equalized comparison; no physical DualSense reference is used. Wolverine level/gain are fixed and disclosed at 100%/140%. Delay override is zero for this test.

The probe retains **aggregate levels and a bounded 10ms timeline only**, no microphone PCM/voice recording or upload. Sample timestamps are microphone sample time, not calibrated command→actuator latency. A selected-frequency Goertzel magnitude measures microphone sound, not actuator displacement or a complete spectrum. AGC/noise gates, desk coupling, position and motor acoustics remain confounders. Do not derive automatic physical equalization or claim left/right isolation from one microphone.

## Reproduction

Build `tests/RW3Pro.HardwareProbe` in Release on Windows x64. Use the user's licensed HAR/provider DLL directory with `--sdk-dir`; SDK DLLs must also exist beside the probe, outside git/distribution. Close RW3 using tray Exit so only one HAR owner exists.

```powershell
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-software-grid TestResults/software-grid
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-controls --sdk-dir <licensed-sdk-dir> --report-dir TestResults/controls --mic A4tech --playback <explicit-48k-virtual-render-id>
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-sweep --sdk-dir <licensed-sdk-dir> --report-dir TestResults/sweep --mic A4tech --playback <explicit-48k-virtual-render-id>
dotnet <probe-output>/RW3Pro.HardwareProbe.dll audit-repeat --sdk-dir <licensed-sdk-dir> --report-dir TestResults/repeat --mic A4tech --playback <explicit-48k-virtual-render-id>
```

An optional explicit `--mic-mme` selects Windows MME PCM16 capture instead of WASAPI Float32; it never silently falls back. MME identity must uniquely match the selected endpoint's name. Both recording APIs remain subject to Windows access controls. Exceptions write a failed-case report and stop the audit. The user must resolve Windows permission/device availability; do not alter privacy settings to force a pass.

See [ordered roadmap](ROADMAP.md) for the acceptance gates after this audit.

## Retry after user restored Wolverine — 2026-11-08

Wolverine wired PC identity is present again. A4tech is already unmuted at 100%; user/machine/desktop and Codex microphone consent read Allow, audio services are running and PnP status is OK. Another WASAPI microphone-only attempt still returned E_ACCESSDENIED at AudioClient.Initialize. Windows Sound properties also show the A4tech endpoint enabled, 2ch PCM16/48k, generic USB audio driver 10.0.26100.9836 (2026-10-23). Voice Clarity/automatic Voice Focus were enabled. A temporary ordinary audio-enhancement-off test produced the same initialization error; Voice Clarity/automatic Voice Focus were restored and visually verified. No privacy/security setting changed, no raw microphone audio retained, no DualSense output, no completed physical motor measurement. Root cause remains unresolved; the old Wolverine-disconnected blocker is now cleared. Local retry status is TestResults/acoustic-audit-20261108/microphone-retry-status.json.

## Capture access resolved and measurements completed

The same built test DLL fails with E_ACCESSDENIED when started through its unsigned apphost `.exe`, but captures real A4tech samples successfully through the installed `dotnet.exe` (.NET Host, shown allowed in Windows microphone settings). This launch-dependent access difference is observed; the exact Windows policy cause is not established. Standard .NET Host was explicitly selected for the rest of this audit; no microphone privacy/security toggle or registry value was changed. Do not treat this as proof that the double-click packaged app has microphone permission on a user's PC.

The user restored Wolverine wired PC identity and replugged the microphone. Windows' own microphone test also completed. First two sets of controls were measured with Voice Clarity enabled. For the controlled frequency/repeat runs, Voice Clarity was temporarily turned off through ordinary Sound properties; mic volume stayed 100%, physical position fixed, input format 48k stereo. The previous Voice Clarity/automatic Voice Focus setting was restored and visually verified after measurement.

The unprocessed run completed 8 interleaved controls, 82 sweep cases and 24 repeats. All 41 sweep source pairs have identical canonical PCM SHA256s. None of those sweep microphone/source samples clipped, and no capture failure was reported. These are acquisition results, not a physical fidelity pass. Only 24/41 pairs had both paths at least 6dB above their own background in the selected steady window; 17 remain below this descriptive detectability screen on at least one path. Among those 24 stronger observations, audio/reference raw microphone RMS differences range within approximately 2.49dB, but that is not a calibrated acceptance limit. Repeat coefficients of variation are high (approximately 48–73% across the eight route/scene groups, N=3). At least one late-tail window was elevated. Therefore every-value equality and physical repeatability are **not established**.

The extra sequence adds stereo 80/190Hz, short bursts, alternating sides, 5s sustained source and expected source cancellation, plus diagnostic repeats. The existing engine FrameProbe observes source feature peaks, post-gain envelope ceilings, saturation and active frames without changing the normal provider rendering function. Provider-render count now uses actual EngineTicks rather than the broader software TickCount. A report still never treats that counter as measured actuator output.

The Python tool `tools/analyze-acoustic-audit.py` produces aggregate measurement-summary JSON and Markdown tables. It uses a 350ms-to-drive-end-minus-100ms steady window and command-completion+400ms late-tail window, with a conservative baseline from background/engine-idle. Microphone sample time is not synchronized to command onset, so these windows cannot establish absolute latency or a physical stop deadline. A 6dB background screen is descriptive; it is not an equality threshold. Background energy is not normalized away to force a match. Local outputs are under TestResults/acoustic-audit-20261108.

Final controlled run: **130 cases completed** (8 controls, 82 sweep, 24 repeats, 16 extended), with no acquisition failure or microphone clipping. See [full results and limitations](ACOUSTIC_RESULTS_20261108.md). Both source cancellations were expected and completed; physical equivalence and repeatability acceptance remain open. No driver, privacy setting or Razer service was changed.

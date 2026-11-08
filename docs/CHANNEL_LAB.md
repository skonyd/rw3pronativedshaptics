# DualSense → W3 Pro channel lab — 2026-11-07

**Prototype / synthetic source.** Test tab card 3 exercises the real v2 conversion without launching a game. It does not establish game authorship, USB source capture, physical fidelity or DualSense equivalence.

## Source and routes

The lab builds one deterministic, bounded 4-channel PCM16 48k source: front speaker channels 1/2, rear haptic channels 3/4. Channel controls cover isolated left/right, simultaneous 80/190Hz stereo, alternating sides, short transients, speaker-only and full silence. All 23 existing demos are selectable. These are project-authored signals in the DualSense audio-haptic format, not extracted game effects or adaptive-trigger HID commands.

- **W3 Pro • PCM dönüşümünü sına:** original 5ms/240-frame PCM blocks → `NativePcmIngress` → `HapticFeatureExtractor` → actual licensed HAR/Razer v2 engine → Wolverine. Live `NativeDualSenseCapture` uses this same ingress. This route deliberately does not claim to exercise USB/WASAPI. No direct XInput or manual side-pulse substitution is used.
- **DualSense • aynı PCM referansı:** the exact canonical source is sent via strict Exclusive quad PCM16, with no RW3 gain, EQ, conversion or Shared fallback. Only this reference route drives DualSense; it is a separate user action. A missing/incompatible source fails explicitly. Name-based endpoint selection is still not certification of physical identity.

Wolverine output level and source gain are disclosed in the report and apply only to its lossy parametric output. The SHA256 before and after replay identifies the unchanged original. Software analysis reports source channels, feature peaks and saturated amplitude-point counts. These predictions are not physical sensor readings.

The previous demo section also uses the same canonical seed/source on both outputs; random-texture references no longer have different seeds between controllers. Front/back demos remain frequency/rhythm codes on two motors.

## Microphone and stopping

Microphone measurement is opt-in; select the actual microphone, then check the box. A muted or unavailable mic fails clearly. The app never unmutes a device automatically. The separately invoked hardware-probe unmute command requires explicit user authorization.

Only aggregate 30–600Hz filtered RMS, raw peak, sample and clipping counts are retained. No microphone waveform or voice recording is stored/uploaded. Startup settling is excluded from baseline. Each run measures engine-off background, source playback plus 200ms silence timeout, then an engine-off tail. Single-microphone airborne/desk sound is affected by placement, room noise, automatic gain/noise suppression and mechanical coupling. It cannot measure left/right physical isolation, absolute physical latency or reference waveform equality. A quieter active window is not proof of a failed motor, and a louder one is not proof of correct haptics.

Durdur/B cancels the replay and stops its engine. Mode tests are exclusive and auto-run resumes after they finish. Report files are local JSON summaries in the app log folder's `channel-lab` subfolder; incomplete/cancelled UI runs are not presented as completed measurements.

## Reproducible Windows hardware probe

The repository does not distribute vendor binaries. Place legally obtained compatible HAR.dll and Interhaptics.RazerProvider.dll in the ignored local probe output directory, only if licensed. `--sdk-dir` selects the already-user-supplied originals and records their hashes. Close the app through tray Exit before a probe; its mutex prevents concurrent app/probe output.

```powershell
dotnet build tests/RW3Pro.HardwareProbe/RW3Pro.HardwareProbe.csproj -c Release -warnaserror
dotnet tests/RW3Pro.HardwareProbe/bin/Release/net10.0-windows/RW3Pro.HardwareProbe.dll inventory
# Explicit hardware output and opt-in microphone metering:
dotnet tests/RW3Pro.HardwareProbe/bin/Release/net10.0-windows/RW3Pro.HardwareProbe.dll run-controls --sdk-dir <user-sdk-directory> --mic <unique-name-or-endpoint-id> --report-dir <local-directory>
# run-demos runs all 23; run --scene <ID> runs one.
```

Probe measurements default Wolverine output to 100% (`--level 0` is the disclosed zero-output control) and source gain to 140%; reference PCM is never scaled. Setup records OS, USB presence, microphone, user-provided SDK hashes and unknown firmware explicitly. Hardware tests must remain separate from CI, which runs the pure source/decoder contracts without any real devices.

## Remaining acceptance

USB/WASAPI source validation requires a supported physical or user-installed virtual 4ch endpoint, which was absent in this session. Authentic game provenance requires independently documented game captures; generated PCM cannot supply that evidence. After the user elects the DualSense stage, compare the identical source physically with repeatable placement and proper sensors/user ratings. Never mark M3/M4 done from this lab alone. See [hardware log](HARDWARE_TEST_LOG.md) and [validation plan](VALIDATION_PLAN.md).

`run-cancel` cancels during Rocket playback, records incomplete playback separately, and checks that engine cleanup returns. A following independent run exercises restart. Physical stop still needs observation. See [the recorded results and acoustic limitations](HARDWARE_TEST_LOG.md).

## Real Windows endpoint route — added 2026-11-07

**Windows uç noktası → W3** is now a separate route. Refresh/select a format-qualified 4ch/48k endpoint. A physical USB DualSense was used for the first successful run; no new drivers were installed. An endpoint name/format is still not proof of game authorship.

1. An independent headless process plays the canonical quad fixture through **explicit Shared WASAPI**. This is a source injector, never a fallback from the strict Exclusive reference player. PCM16→Float32 representation is numerically exact; no RW3 gain, normalization, EQ or resampling is applied to the source. The original PCM hash and actual rendered-byte hash are both recorded (different representations have different hashes).
2. First pass is **source-only**, with the existing explicit 8-second RAM-bounded rear recorder. It has no Sensa/transport sink. Offline comparison finds a sample alignment and reports complete active-region coverage, exact sample equality, correlation, observed gain and raw RMS error without compensating the signal. The temporary captured WAV remains in RAM only and is cleared; finalizing the recorder also clears its private RAM. No source audio file is saved automatically.
3. The second pass replays the identical source from the separate process. `NativeDualSenseCapture` is attached to the actual Razer v2 engine; **only real WASAPI callbacks** feed it. No direct fixture push is used. Source hashes/settings and live channel activity must agree with the first pass. Source, provider and user-felt evidence remain distinct.
4. Optional microphone levels include sound from **both** controllers and the room in this route. They cannot isolate Wolverine or establish fidelity. Sample alignment is not game-to-motor latency.

The explicit checkbox **Yalnız uç nokta testi boyunca Windows kaynağını aç / %100 yap; bitince önceki ayarı geri yükle** opts into temporary unmute/unity level. The runner saves/restores both settings on success, error and cancellation. Without this option the source's mute/volume is untouched, and a muted/zero-volume endpoint fails clearly. This checkbox never affects ordinary Native mode or the Exclusive reference player. A disconnected endpoint can prevent restoration; that error is surfaced.

Hardware probe: `run-endpoint --scene <ID> --endpoint <ID>` or `run-endpoint-controls --endpoint <ID>`, plus the existing SDK/mic/report arguments. The standalone sender is `--emit-channel-fixture <endpoint> <scene> <metadata-report>` and never opens HAR or a controller mode. CLI audio-setting experiments must save and restore the original mute/volume in a single try/finally, even on failure.

[Recorded full-path observations](HARDWARE_TEST_LOG.md) now supersede the earlier statement that no endpoint was available: the user subsequently connected a USB DualSense. Authentic-game source, virtual input association and physical fidelity remain separate open gates.

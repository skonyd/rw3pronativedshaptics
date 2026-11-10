# Current status (single source of truth)

Updated 2026-11-10. This page replaces reading the dated reports in order; they stay as history
(see [docs/README.md](README.md)). When something changes, update this page in the same commit.

## What the app does

| Mode (main window) | Source | Wolverine in PC mode (1532:0A4C) | Wolverine in Xbox mode (1532:0A3F) |
| --- | --- | --- | --- |
| Normal | game's own Xbox rumble | game → body motors | game → body + trigger motors |
| Sesten titreşim | PC audio (WASAPI loopback) | Sensa palms, stereo | body motors, stereo (left motor ← left channel) |
| Oyunun DualSense titreşimi | game's DualSense output via the virtual DualSense (HIDMaestro) or a USB DualSense | haptic PCM → Sensa palms; trigger effects → body motors | haptic PCM → body motors; trigger effects → impulse-trigger motors (GameInput) |

- **Sensa without Synapse:** own frame encoder + HID feature report 4 ("Direct USB"), over the cable and the HyperSpeed dongle.
  Synapse/HAR remains an optional legacy backend, used only when Synapse runs and has attached the controller.
- **Virtual DualSense bridge:** needs administrator rights. The game sees a DualSense: buttons/sticks from the Wolverine (XInput in PC mode, GameInput in Xbox mode, live readback check), haptic PCM in, trigger reports in.
  HidHide hides the Wolverine from games while it runs and is restored on stop.
- **Controller mode:** PC → Xbox can be switched from the app (Synapse's command, replayed). Xbox → PC only with ○ + Menü + A (2 s).
- **Trigger motors** exist only in Xbox mode, and work while the game is in front only through GameInput (WGI vibrates only for the foreground window).

## Everyday use

- Start RW3 from the Start menu / shortcut, or `explorer.exe "<path>\RW3ProNativeDSHaptics.exe"`. A start from a packaged app (Claude/Codex) relaunches itself so settings and log stay in one place (`PackageContext`).
- Selecting "Oyunun DualSense titreşimi" starts the virtual bridge by itself. The first time, it offers the UAC-free task once (one UAC prompt); after that the bridge starts without a prompt, on selection and at app start. The only manual control is the start/stop button under that mode; Settings shows status only.
- Native v2 demos (Wolverine) follow the setup: through the running bridge (the bridge stays up), else Sensa in PC mode, else the body motors in Xbox mode. In Xbox mode the body motors give strength only: no 30-400 Hz pitch and weak left/right. The demos' intended feel needs PC mode (Sensa); the app says so after a demo.
- Testing without a game: Test → "Oyun simülasyonu" sends game-like haptic PCM + trigger reports and reports each stage (bridge, Sensa / Xbox motors, mic).
- Settings and log: `%LOCALAPPDATA%\RW3ProNativeDSHaptics\` (`settings.json`, `logs\rw3.log`).

## Verified on hardware (webcam mic, medians)

- Direct USB = Synapse path within ~1 dB.
- Hybrid places mixed 80+190 Hz correctly.
- Dongle works (Direct USB).
- Xbox-mode trigger motors: GameInput from the background +27/+19 dB; WGI in the background 0 dB.
- PC mode trigger motors: unreachable.
  - XInput has no trigger channel; WGI gives 0 dB.
  - GameInput's trigger channels (2026-11-09) give +12.5 dB at a separate 222 Hz actuator (body motors: 76 / 62 Hz), but the user felt it outside the triggers.
- Sensa in Xbox mode: no route.
  - The controller exposes only a GIP device: no Razer HID, and Razer's haptics service sees no device.
  - GIP vendor messaging (`GipGameControllerProvider.SendMessage`) is reported not to work and Razer's GIP commands are undocumented, so we did not probe the firmware blindly.
  - USB captures, read-only:
    - PC → Xbox switch (2026-11-08, re-read 2026-11-10): Synapse sends only reads, then the switch command. No Sensa-off command.
    - After re-enumeration only the Windows GIP driver talks to the controller (ack, power, auth, LED, rumble stop), nothing Razer-specific.
    - Synapse does not show the controller in Xbox mode at all (user, 2026-11-10). The 2026-11-10 capture has no Xbox-mode traffic from it.
  - Vibration strength command (PC mode, captured 2026-11-10, not yet sent by RW3): class 0x0C cmd 0x06, arguments [03, channel, percent].
    - Synapse writes channels 5, 6, 7 and 8 together from one slider (100/83/67/50/33/17/0 %).
    - Channel map (mic, PC mode, 2026-11-10, 2 repeats). Setting a channel to 0 % cut the matching actuators by 15-19 dB, down to the room floor; the other channels changed nothing (within ±1 dB):
      - Channel 5 = left side: big body motor and the left Sensa palm.
      - Channel 6 = right side: small body motor and the right Sensa palm.
      - Channels 7 and 8: no effect on the body motors or Sensa. Most likely the trigger motors, which are unreachable in PC mode.
    - All channels were restored to 100 % afterwards.
  - The controller's GIP headset audio (endpoint "Kulaklıklar (Xbox Controller)") does not drive Sensa. Tested 2026-11-10: tones at 40–1000 Hz, with and without a headset in the 3.5 mm jack. The audio reached the headset, but the controller did not vibrate (felt by the user, and no tone peak at the mic without a headset).
- Virtual DualSense input matched the Xbox pad in every readback report.
- Sensa overdrive check (2026-11-10, mic, both palms, steady drive):
  - Sweep: 30/50/80/120/190/300 Hz at strengths 16/32/48/63. Strength 63 is the top of the encoder scale, the same maximum Synapse/HAR uses.
  - No tick or bottoming anywhere: the 1.5-6 kHz band stayed within ±2.4 dB of the room.
  - The top end compresses gently instead: at 120-300 Hz, 48 → 63 adds 0 to +5 dB.
  - Below ~50 Hz little reaches the mic at any strength.
  - So RW3 cannot push Sensa past its range. An extra limiter is not needed for steady signals.
- Trigger motor frequency (Xbox mode, GameInput, 2026-11-10, mic; 4 good runs, 2 orientations). The pitch follows the strength:
  - Left trigger: ~130 Hz at 25 %, ~176 Hz at 50 %, ~234 Hz at 100 %.
  - Right trigger: ~108 / ~150 / ~234 Hz.
  - So the right motor turns slower at low and mid strength, and the two are equal at full.
  - Level differences flipped with the pad's orientation, so they are mic placement. Averaged, the levels are equal except the right trigger at 25 % (~2.5 dB weaker).
  - Body motors: big 76 Hz, small 188 Hz.
  - A DualSense trigger frequency cannot be reproduced: only strength is commandable.
- Xbox-mode audio option (2026-11-10): bass (< 120 Hz) → heavy motor, treble (> 300 Hz) → light motor. Mono, no left/right. Selectable and effective only while the controller is detected in Xbox mode. Unit-tested, not yet felt.

## Open / not verified

- Xbox-mode trigger effects in the bridge after the GameInput input fix: software-checked, not yet felt by the user.
- Xbox audio stereo fix (0346115): unit-tested, not yet felt by the user.
- Transient records (2026-11-10): Direct USB now writes the decoded record (strength, 24, time, tail 100) for the first hit of a frame instead of folding it into the points. This is a setting, on by default.
  Mic A/B (TestResults/transient-records-20261110, Standard, 5 repeats per scene, medians):
  - Repeated hits: same peak (+24.3 dB) and same -6 dB width (80 ms). Better contrast between hits (p90/p50 +17.9 vs +15.6 dB).
  - Character: energy moves from 120 Hz (×21 vs ×27) to 190 Hz (×5.3 vs ×2.0), a crisper click.
  - First hit after silence: ~4.5 dB weaker and ~10 ms later.
  - Mixed and stereo scenes: unchanged.
  - Not a clear mic win, so the feel decides (Ayarlar → "vuruşları darbe kaydı olarak gönder"). The `101` tail is still not reproduced.
- Spectral history: multi-resolution since 2026-11-09. Components >= 60 Hz come from a 42.7 ms window, the bass from 85.3 ms. The 240-case audit passes 240/240 (max 0.36 Hz error); the plain 2048 window failed 55 cases. Hybrid enters multiband after 21.3 ms + 2 steady frames. Mic comparison 2026-11-09, Direct USB, 45 cases (TestResults/multires-physical-20261109), medians:
  - Mixed 80+190 Hz, Hybrid: 80 Hz ×13.6, 190 Hz ×10.9, wrong band ×2.2.
  - Multiband: onset in the first 300 ms +4.1 dB (old run ≈0); short hits +9.8 dB (old run +6 dB).
  - Hits under Hybrid unchanged versus Standard.
  - No regression found.
- Mode orchestration is in `HapticOrchestrator` (decisions in `HapticModePolicy`, contract-tested).
  `MainWindow.xaml.cs` is still large (~1,860 lines). The next split candidates are the test/simulator page and the settings page.
- Audio mode level shaping (2026-11-10, both off by default; mode screen → "Seviye"). Not yet felt or mic-tested. Contract tests:
  - Auto level: follows the haptic band (< 250 Hz) over ~3.5 s toward -20 dBFS, gain ×0.5..×4. It turns down in 0.4 s and up in 3.5 s, and holds the gain in silence. A 12 dB input spread leaves 1.7 dB.
  - Hum suppression: a steady sound fades to 40 % over a few seconds (-6.4 dB on an 8 s drone). A hit 1.5× above the drone baseline opens it fully in 2 ms.
  - On-screen sound tests bypass both.
  - Mic, 2026-11-10 (TestResults/level-shaper-20261110). Real shaper → SensaAudioSink → Direct USB path. Scene: 60 Hz drone + 120 Hz hit every 0.5 s, at source -12/-6/0 dB, 2 repeats, medians:
    - Auto level works. The spread across the three volumes fell from 6.4 dB (off) to 1.4 dB. Hit contrast is unchanged (+15.9 vs +15.4 dB).
    - Auto + hum at -6/0 dB gave the best hit contrast: +18..23 dB, against +15..20 off.
    - At -12 dB, hum lowered the contrast: hum alone +6.5/+8.5 dB, auto+hum +11/+11.5, against +13/+14 off. Not explained yet.
    - The repeats differ by up to 5 dB, so the hum result is only indicative.
- YouTube seek recovery (audio mode) unresolved; see the dated reports.
- GameInput output dropouts (2026-11-10): explained. They happen while another GameInput/gamepad app (e.g. Opera) is in the foreground: GameInput then gives output focus to that window. The user has seen this before. Not a probe or app bug. It matters for the bridge only if a browser is in front during play.

## Code map

- `src/RW3ProNativeDSHaptics/`: the app.
- `src/RW3Pro.Lab/`: audit/measurement code used only by tests and the hardware probe.
- `tests/RW3Pro.ContractTests/`: device-free checks; every check runs and failures are listed at the end.
- `tests/RW3Pro.HardwareProbe/`: hardware measurements (mic, USB, Sensa frames).

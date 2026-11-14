# Current status (single source of truth)

Updated 2026-11-14. For the work history and next steps when switching AI tools, see [HANDOFF.md](HANDOFF.md). This page replaces reading the dated reports in order; they stay as history
(see [docs/README.md](README.md)). When something changes, update this page in the same commit.

## Firmware lab

An original Cortex-M4 audio-to-haptics DSP module now runs in an offline replay
alongside stock NXP routines from v2.04.00. Nine scenarios verify callback PCM
selection, stereo isolation, stream loss, a simulated runtime switch and Sensa
decoder output. The comparison currently ends at DMA arguments and decoded
RAM. Both Telink images have initial byte-range maps with explicit unknowns.
Windows wired Xbox and dongle USB captures both carry 192-byte GIP audio
payloads. The NXP's 198-byte receive branch copies PCM to its alternate I2S
ring in emulation. The captures and replay cover the host USB boundary.
The stock audio queue now runs across startup, drain, underrun and recovery
in the replay. The CTIMER2 callback and SCT duty register updates also run
with fixture state. Stock I2S queue admission and DMA0 interrupt dispatch are
covered at their boundaries. The replay also runs the stock I2S completion
handler and follows the application callback to its next PCM or silence
submission with modeled IRQ events. The repeatable build runs these checks against
the pinned stock image and emits a local hash-identified report.
Bootloader acceptance, recovery, wireless audio, PWM and integration timing
remain open. [Firmware replay and next steps](firmware/AUDIO_HAPTICS_REPLAY.md).

## What the app does

| Mode (main window) | Source | Wolverine in PC mode (1532:0A4C) | Wolverine in Xbox mode (1532:0A3F) |
| --- | --- | --- | --- |
| Normal | game's own Xbox rumble | game → body motors | game → body + trigger motors |
| Sesten titreşim | PC audio (WASAPI loopback) | Sensa palms, stereo | Sensa palms over GIP, stereo (setting "Xbox modunda Sensa", default on; off: body motors, left motor ← left channel) |
| Oyunun DualSense titreşimi | game's DualSense output via the virtual DualSense (HIDMaestro) or a USB DualSense | haptic PCM → Sensa palms; trigger effects → body motors | haptic PCM → Sensa palms and trigger effects → trigger motors, in one Sensa frame over GIP (setting; off: body motors + GameInput impulse) |

- **Main page (2026-11-14):** each mode's options sit under the mode cards.
  - Sesten titreşim: level slider (10–300 %, 100 = the sound's own level; Sensa feature gain and body-motor gain, live), "Standart (DSX gibi)", the game's own Xbox rumble/LT-RT (virtual Xbox pad), and an "advanced" expander (frequency filter, auto level, hum suppression, bass/treble split).
  - Oyunun DualSense titreşimi: palm haptic source **Oyunun HD haptiği / Oyun sesinden / Kapalı** with a level slider (game HD → native gain; sound → the same audio level), plus trigger effects and the game's rumble. "Oyun sesinden" is for games with DualSense triggers but no HD haptics: the virtual DualSense keeps the triggers and rumble, and the palms follow the PC sound through the same Sensa extractor. The bridge then pushes no game PCM and no palm trigger synth (one producer). A USB DualSense endpoint is used only for the game-HD source.
  - The native mode now auto-starts with any source on. Before, it auto-started only with HD PCM on, so the common "triggers, no HD" setup waited for a manual click.
  - Settings no longer has the Native gain / audio sensitivity presets (now the sliders).
  - Layout (second pass): the status card (what runs now, level meters) sits above the mode card and stays visible; the mode options scroll. The native mode is labelled "DualSense oyunları". The connections panel names what each part does, and its "DualSense köprüsü" line follows the bridge live (before, it kept saying "kapalı" while the bridge ran, and "Xbox modunda kullanılmaz" for Sensa although Xbox-mode Sensa works). The tray menu can switch the native palm haptic source per game.
  - Fixed: in Xbox mode with "Xbox modunda Sensa", any frequency filter other than "full band" silently moved audio mode from the palms to the body motors. The filter now applies on the palm path too.
  - Audio status shows the captured playback device and the Bluetooth sync delay.
  - Not yet felt: "Oyun sesinden" in a real game.

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
- **General calibration** (Test → "Genel kalibrasyon testi", since 2026-11-11). A blind, controller-driven test in PC or Xbox mode:
  - Trials: shuffled locate trials (one or two actuators, varied strength / pitch / texture, 3 changing choices including mixes and "nothing", plus a fixed 4th "Orta / yerini ayıramadım" since 2026-11-14; each stimulus twice) and "1 or 2: stronger / better" pairs in both orders.
  - Controls: X/A/B answer, Y = Orta (locate trials only), View (⧉) replays, Menu stops; keys 1/2/3/4, R, Esc.
  - Output: raw data and a summary (accuracy per actuator with "orta ×N", ORTA share, sent → chosen, consistency, threshold, pair verdicts) in `%LOCALAPPDATA%\RW3ProNativeDSHaptics\calibration\`.
  - Stops the running mode while it runs.
- Settings and log: `%LOCALAPPDATA%\RW3ProNativeDSHaptics\` (`settings.json`, `logs\rw3.log`).

## Verified on hardware (webcam mic, medians)

- Direct USB = Synapse path within ~1 dB.
- Hybrid places mixed 80+190 Hz correctly.
- Dongle works (Direct USB).
- Xbox-mode trigger motors: GameInput from the background +27/+19 dB; WGI in the background 0 dB.
- PC mode trigger motors: unreachable.
  - XInput has no trigger channel; WGI gives 0 dB.
  - GameInput's trigger channels (2026-11-09) give +12.5 dB at a separate 222 Hz actuator (body motors: 76 / 62 Hz), but the user felt it outside the triggers.
  - Repeated 2026-11-10 (2 runs, mic): left and right trigger channels are identical at every strength (+5.5 / +6.8 / +12 dB at 25 / 50 / 100 %, peak 68–100 Hz) and the pitch does not rise with strength as the real trigger motors do (130–234 Hz in Xbox mode). GameInput emulates trigger rumble on the body motors in PC mode; it does not reach the trigger motors.
  - Firmware 2.04.00 (read from Razer's updater without running it, 2026-11-10): the haptics MCU has two Sensa descriptors. In PC mode it reports 2 body parts (palms 216/116, 3 bands, 4 points, 30–400 Hz); otherwise (Xbox mode) 4 body parts (palms with 2 bands / 2 points, 20–300 Hz, plus 249 and 149, 0–300 Hz, resonance 20 Hz, probably the trigger motors). Selected by one config flag at boot. The descriptor is read with Razer class 0x16 cmd 0x90 (length) / 0x91 (offset, 0x4D-byte chunks). So the firmware supports palms + triggers as one haptic stream in Xbox mode, but no Windows route to that stream is known.
- Sensa in Xbox mode: **in the app since 2026-11-11 (dba5a26), setting on by default; not yet felt through a game or the simulator** (docs/research/XBOX_MODE_HAPTICS_ROUTE.md).
  - "Oyunun DualSense titreşimi" in Xbox mode: the game's haptic PCM → palms. Trigger effects → trigger motors, both in the Sensa frame over GIP and on GameInput's impulse channels (the calibration rated both together better, E-20261111-07). The game's normal rumble stays on the body motors (GameInput). Setting off = previous routing.
  - Trigger strength: values above 0 start at 16/63 (felt threshold), and clicks last 160 ms (E-20261111-06). Resistance runs at 85 % (user: 45 % too weak).
  - Weapon and bow effects (all modes): the vibration rises from 40 % at the zone start to 100 % at the break point, then a strong click, then nothing, like a stiffening trigger that breaks. No physical resistance: the Wolverine's triggers only vibrate.
  - The same setting also sends Xbox-mode Native v2 demos and the Wolverine motor test to the palms through Sensa (2026-11-11).
  - One Sensa frame drives **palms and trigger motors together**: body parts 216/116 = left/right palm, 249/149 = left/right trigger motor. Confirmed by a blind hand-held test (E-20261111-04).
  - The route: Razer packets tunnelled in GIP message 0x0F, answers in 0x10. Our app can send them through `Windows.Gaming.Input.Custom` (interface `{AAE22FB7-…}`).
  - Read-only so far: in Xbox mode the controller answers Sensa class 0x16 and serves a 4-body-part descriptor (palms + 249/149, probably the triggers; 2 bands / 2 points).
  - The 4-body-part frame layout (docs/SENSA_USB_PROTOCOL.md) works on the controller: palms play the sent pitch, and the trigger motors follow strength.
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
- Body motor ticks (2026-11-10, mic, 2-8 kHz spikes in 5 ms bins, XInput; TestResults/body-motor-ticks-20261110):
  - The source is the big (heavy, left) body motor at full power. In Xbox mode, steady 1.0 gave 24-29 ticks/s with the high band +15..17 dB, in all 3 runs. The same motor in PC mode at 1.0 ticked in 1 of 2 runs (6.5/s).
  - At 0.35-0.8: sporadic, not repeatable (0-4/s, once 8.7/s at 0.8).
  - The small motor, low levels (start threshold), the update rate (re-sent at 20 Hz or 200 Hz) and audio-like envelopes: no ticks.
  - Capping at 0.75-0.8 costs about 3-4 dB of the big motor's maximum (overall mic level -20 vs -23.4 dBFS).
  - Ceiling setting (BodyMotorLimit, default off since the hand test): up to 0.6 the command is unchanged, above it approaches 0.75.
    - Mic, controller on the desk: 1.0 gave 22-30 ticks/s; 0.74 gave 10-15/s.
    - In a second sweep, 0.45-0.7 also ticked irregularly (0-14/s), although the first sweep had them nearly clean.
    - The motor ticks reliably only at full power. In the middle the ticks are position-dependent, probably the pad touching the desk.
    - Held in the hand, next to the mic (3 runs × 1.0/0.74/0.65/0.55/0.45; the vibration reached the mic at -30..-40 dBFS against a -44 dBFS room): 1 tick in total, none at full power.
    - The ticks therefore come from the pad rattling against the surface it lies on, not from the motor itself. The ceiling only matters while the pad lies on a table, so it is off by default.
  - Why the user hears it only in Xbox mode: there, audio mode drives the body motors with gain 2.5, which often reaches 1.0. In PC mode the audio goes to Sensa instead.
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
  - Not a clear mic win, so the feel decides (Ayarlar → "vuruşları darbe kaydı olarak gönder").
  - Fixed 2026-11-11 (firmware decoder, emulated): the old record layout shifted the right hand 3 bits whenever the left hand had a hit. The A/B above used that layout and should be repeated.
- Spectral history: multi-resolution since 2026-11-09. Components >= 60 Hz come from a 42.7 ms window, the bass from 85.3 ms. The 240-case audit passes 240/240 (max 0.36 Hz error); the plain 2048 window failed 55 cases. Hybrid enters multiband after 21.3 ms + 2 steady frames. Mic comparison 2026-11-09, Direct USB, 45 cases (TestResults/multires-physical-20261109), medians:
  - Mixed 80+190 Hz, Hybrid: 80 Hz ×13.6, 190 Hz ×10.9, wrong band ×2.2.
  - Multiband: onset in the first 300 ms +4.1 dB (old run ≈0); short hits +9.8 dB (old run +6 dB).
  - Hits under Hybrid unchanged versus Standard.
  - No regression found.
- Mode orchestration is in `HapticOrchestrator` (decisions in `HapticModePolicy`, contract-tested).
  `MainWindow.xaml.cs` is still large (~1,860 lines). The next split candidates are the test/simulator page and the settings page.
- **Audio mode "Standart (DSX gibi)"** (2026-11-14, default on, also for existing settings): the audio becomes vibration as it is, with no gain, compression, auto level or hum stage. A soft noise gate on the < 250 Hz band (threshold ≈ -30 dBFS, opens in 1 ms, releases over 80 ms, expander below) stops the vibration in near-silence.
  - Why: the user reported audio mode "vibrated all the time and never stopped" while playing. Their settings had auto level + hum suppression on: auto level lifts quiet content up to ×4 toward -20 dBFS, and hum suppression only lowers a steady bed to 40 %.
  - DSX reference: its saved audio-to-haptics profile (DSX 3.1.7, user's install) has only per-motor intensity (0–10) and speaker/headset volume, no threshold or level stage.
  - Contract-tested (unity above the gate, -43 dB tone -19.7 dB, a hit after near-silence passes at 0.99, auto/hum ignored while on). Not yet felt in a game.
  - Auto level and hum suppression remain as options; they apply only with Standart off.
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

# Test game: "Şişe Atışı" (src/RW3TestRange)

A small shooting-gallery game that drives the controller **the way real games do**, so RW3 can be tested end to end
without a commercial game. It does not use RW3's code: it talks to whatever pad Windows shows it.

Start: `tools\run-test-range.cmd` (or `src\RW3TestRange\bin\Release\net10.0-windows\RW3TestRange.exe`).
Window any size; F11 or View / Create = full screen; Esc = back to the start screen.

## Quick table (start screen)

Same idea as RW3's "Oyuna görünen kol" matrix, from the game's side: pick the pad the game **supports**
(Xbox kolun kendisi, Xbox 360, Xbox Series, PS4, DualSense) and the feedback rows (Müzik, Efekt sesleri, HD haptik,
Rumble, Tetik titreşimi / efekti). The game then uses the paths a real game with that support would use:

| Column | Paths |
| --- | --- |
| **Bağlı kol (RW3'süz)** | no RW3: the real pads that are connected, each through its own path (real DualSense: report 0x02 + HD haptics; real DS4: report 0x05; Xbox pad: GameInput, else XInput). RW3's virtual Sony pads are ignored for input and output. Use it with your own DualSense or Xbox pad as the reference; the start screen warns when RW3 is emulating a pad at the same time |
| Wolverine (kolun kendisi) | follows the pad's mode, detected from the present devices (re-checked every 3 s): **Xbox mode** (1532:0A3F) = GameInput, body + trigger motors, and the game holds the pad like a Game Pass game (tests the palm focus issue); **PC mode** (1532:0A4C) = Windows sees an Xbox 360 pad: XInput body motors, no trigger row |
| Xbox Series | GameInput: body motors + LT/RT trigger motors |
| Xbox 360 | XInput: two body motors (no trigger row) |
| PS4 | DualShock 4 output report 0x05: rumble + lightbar (no trigger row) |
| DualSense | output report 0x02 (adaptive triggers, light, LEDs, compatible rumble) + HD haptics on channels 3/4 |

A cell of another column switches to it; a row the column lacks switches to the closest column that has it.
"⇄ RW3'teki seçimi kullan" reads RW3's `settings.json` (Output / Features) and sets the same column. "Gelişmiş" keeps
the manual output checkboxes (they override the table until a cell is clicked).

## Weapons (choose the loadout; LB/RB switches)

| Weapon | DualSense R2 | Palms (HD haptics) | Trigger motors (GameInput) |
| --- | --- | --- | --- |
| Tabanca | weapon 0x25, click at ~60 % | short, sharp hit | short right push |
| Tüfek | machine 0x27 (rhythm in the trigger) | recoil alternating left / right | rhythmic right |
| Pompalı | heavy, early weapon 0x25 (strength 8) | deep blast + pump double click | both body motors + both triggers |
| Yay | bow 0x22 (resistance, snap-back) | tension hum rising with the pull, snap on release | right trigger tension ramp |
| Arbalet | latest, hardest weapon click (zones 5→8) | mechanism click + heavy thump; crank ticks while reloading (L2 resistance) | right push; left ticks while cranking |

## Game settings

Tetik efekti yoğunluğu (Kapalı / Zayıf / Orta / Güçlü, scales every trigger effect like the PS5 setting), HD haptik
seviyesi (0–200 %), titreşim seviyesi, müzik / efekt sesi (also what RW3's audio-to-haptics hears), tur süresi,
ters Y, jiroskopla nişan. Saved in `%LOCALAPPDATA%\RW3TestRange\config.json`; the session summary lists them.

## Outputs (advanced, several at once)

| Checkbox | What the game sends | Typical real game |
| --- | --- | --- |
| Müzik | procedural music (bass, drums, pad, lead) on the default speakers | any; RW3 "sesten titreşim" / melody mode should play it |
| Ses efektleri | shots, glass, reload, explosion, heartbeat (panned) | any |
| Xbox · XInput | two body motors (`XInputSetState`) | most PC games |
| Xbox · GameInput | body motors + LT/RT trigger motors (`IGameInputDevice::SetRumbleState`, inbox API v0) | GDK / Game Pass |
| DualSense · adaptif tetik + ışık + LED | USB output report 0x02: trigger effects (0x21 feedback, 0x25 weapon, 0x26 vibration, 0x22 bow, 0x27 machine, 0x23 galloping), lightbar, player LEDs, mic LED; compatible rumble when HD haptics are off | PS5 ports |
| DualSense · HD haptik | PCM on channels 3/4 of the pad's 4-channel audio endpoint | PS5 ports |
| DualSense · kolun hoparlörü | effects also on channels 1/2 | PS5 ports |
| PS4 · DualShock 4 | output report 0x05: rumble + lightbar | PS4-pad games |

Input: every XInput slot, GameInput
gamepad and DualSense HID (real or RW3's virtual one) is read; the pad used last drives the game.

## Screens

- **Oyun:** bottles on three shelves, 60 s. RT/R2 fire, LT/L2 aim (L2 resistance), LB/RB weapon (pistol: weapon
  click; rifle: machine effect; shotgun: heavy weapon + strong rumble; bow: bow effect + rising tension on the right
  trigger motor and palms), X/□ reload, Y/△ grenade (all motors + deep haptic + orange light), RS melee, LS haptic
  left→right sweep, ▲▼ rumble strength, Mute = music, touchpad = aim, optional gyro aim. Glass haptics are panned to
  where the bottle was; last 10 s: heartbeat on both palms and a red pulsing light. The bottom lines say what was sent
  where.
- **Tuş testi:** every button (incl. touchpad, mute, PS/Xbox) turns green once pressed; sticks, triggers, gyro, touch.
  Pulling a trigger drives its own trigger motor as far as it is pulled (GameInput). Hold ☰ / Options 2 s to leave.
- **Geri bildirim testi (rehberli):** 22 checks (each body motor, each trigger motor, six adaptive trigger effects,
  HD haptics left / right / sweep / melody / hits, lightbar, player LEDs, mic LED, speaker left / right, music, pad
  speaker). A plays, then A felt / B not felt / X again / Y skip. Checks whose output is not selected are marked
  "çıkış kapalı".

## Logs

`%LOCALAPPDATA%\RW3TestRange\logs\session-*.jsonl` (every event and send) and `session-*.txt` (summary: outputs,
pads, guided answers, button test, counters, notes, and **RW3's own log lines from the same minutes**). Written on
"Raporu kaydet ve aç" and on exit.

## Known limits

- DualSense over Bluetooth: input is read, output (report 0x31 with CRC) is not written.
- GameInput uses the inbox v0 API; games on the redistributable use a newer one (same service).

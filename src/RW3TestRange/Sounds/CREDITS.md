# Test range sounds: sources and licences

Every file here is **CC0** (public domain, no attribution required). Credit is given anyway, as the authors ask.
The clips were cut from the originals (one shot each, start trimmed, tail faded) and converted to 16-bit WAV
(effects 48 kHz, music and glass at their original 44.1 kHz). The game picks a random variant per key
(`pistol.wav`, `pistol_2.wav`, ...); a missing key falls back to the synthesised sound in `Synth.cs`.

| Files | Source | Author | Original file |
|---|---|---|---|
| `pistol*.wav` (Walther PPQ, 1911), `rifle*.wav` (AK-47, AR-15), `shotgun*.wav` (Model 12, Mossberg), `pump.wav` (Model 12) | [The Free Firearm Sound Library](https://opengameart.org/content/the-free-firearm-sound-library) | Ben Jaszczak, Brian Nelson, Kevin Heras, Matthew Nanney | `Prepared SFX Library.7z`: X_31P, X_39P, A_42P, C_27P, D_24P, K_17P, N_26P |
| `bow*.wav`, `bowdraw.wav`, `crossbow*.wav`, `crank*.wav`, `reload-bow.wav`, `melee.wav` | [Medieval sound effects: Weapon Textures](https://opengameart.org/content/medieval-sound-effects-weapon-textures) | Ben Jaszczak, Brian Nelson (Still North Media) | English Longbow Shoot / Draw, Scythian Recurve Shoot, Crossbow Shoot / Lever Trigger / Place Bolt, Dagger Swing |
| `glass.wav` | [Glass Break](https://opengameart.org/content/glass-break) | Till Behrend | `glass_breaking.wav` |
| `music.wav` | [Fast fight / battle music](https://opengameart.org/content/fast-fight-battle-music) | Ville Nousiainen (mutkanto) | `fight.wav` |
| `music_2.wav` | [Action Music Loop](https://opengameart.org/node/98874) | AppPro | `action loop.wav` |

Not covered by these libraries and still synthesised: gun reload and empty click, explosion (grenade), UI clicks,
and every DualSense haptic waveform.

Downloaded 2026-11-22 with the user's consent.

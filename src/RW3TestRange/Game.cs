namespace RW3TestRange;

public sealed record Weapon(string Name, int Mag, double Rate, bool Auto, double Spread, byte R, byte G, byte B, double Reload, string Feel);

/// <summary>
/// "Şişe Atışı": bottles on three shelves, four weapons, grenades, a 60-second round. Every action sends the feedback a
/// real game would (sound, rumble, trigger motors, adaptive triggers, HD haptics, lightbar, player LEDs).
/// Coordinates are 0..1 of the play field.
/// </summary>
public sealed class RangeGame
{
    public sealed class Bottle { public double X, Y, Vx; public int Shelf; public bool Golden; public double Wobble; }
    public sealed class Shard { public double X, Y, Vx, Vy, Life; public bool Gold; }
    public sealed class Popup { public string Text = ""; public double X, Y, Life; }

    public static readonly Weapon[] Weapons =
    {
        new("Tabanca", 12, 0, false, .004, 40, 120, 255, 1.2,
            "R2: yolun %60'ında silah tıkı (0x25). Avuç: kısa, keskin vuruş. Tetik motoru: kısa itme."),
        new("Tüfek", 30, 10, true, .012, 40, 255, 90, 1.6,
            "R2: makineli titreşim (0x27), tetik içinde ritim. Avuç: her mermide sağ-sol sırayla geri tepme."),
        new("Pompalı", 6, 0, false, .05, 255, 70, 30, 1.0,
            "R2: sert, erken silah tıkı (0x25 güç 8). Avuç: derin patlama + pompa çift tık. İki gövde motoru."),
        new("Yay", 1, 0, false, 0, 200, 60, 255, .4,
            "L2 ya da R2 ile çek: iki tetikte de yay efekti (0x22), çektikçe direnç, bırakınca geri tepme. Gerilim çektiğin taraftaki tetik motorunda ve avuçta yükselir, bırakışta şak."),
        new("Arbalet", 1, 0, false, .002, 255, 230, 120, 2.0,
            "R2: en geç ve en sert tık (0x25, bölge 5→8). Doldururken L2 dirençli kurma, avuçta kurma tıkırtıları."),
    };

    public readonly List<Bottle> Bottles = new();
    public readonly List<Shard> Shards = new();
    public readonly List<Popup> Popups = new();
    public readonly Queue<string> FeedbackLines = new();
    private readonly FeedbackHub _fb;
    private readonly GameConfig _cfg;
    private readonly Random _rng = new();
    public double CrossX = .5, CrossY = .5, TimeLeft = 60, Flash, ExplosionAt = -1, ExplosionX, ExplosionY, ExplosionRing;
    public int WeaponIndex, Score, Shots, Hits, Grenades = 3;
    public readonly int[] Ammo = Weapons.Select(w => w.Mag).ToArray();
    public bool Aiming, Paused, Over, Reloading, GyroAim;
    private double _reloadLeft, _fireCooldown, _heartbeat, _spawn, _bowDraw, _bowMax, _bowTick, _grenadeTimer = -1;
    private bool _rtArmed = true, _lightPulse, _recoilLeft;
    private double _crankTick;

    public Weapon Current => Weapons[WeaponIndex];
    public RangeGame(FeedbackHub fb, GameConfig cfg) { _fb = fb; _cfg = cfg; GyroAim = cfg.GyroAim; Restart(); }

    public void Restart()
    {
        Bottles.Clear(); Shards.Clear(); Popups.Clear();
        TimeLeft = _cfg.RoundSeconds; Score = Shots = Hits = 0; Grenades = 3; Over = Paused = false;
        for (int i = 0; i < Ammo.Length; i++) Ammo[i] = Weapons[i].Mag;
        for (int i = 0; i < 9; i++) Spawn();
        SelectWeapon(Array.FindIndex(_cfg.Loadout, x => x) is var first && first >= 0 ? first : 0, "round start");
        SessionLog.Current?.Event("round-start");
    }

    private void Spawn()
    {
        int shelf = _rng.Next(3);
        bool fromLeft = _rng.Next(2) == 0;
        Bottles.Add(new Bottle
        {
            Shelf = shelf, Y = .3 + shelf * .22, X = fromLeft ? -.05 : 1.05,
            Vx = (fromLeft ? 1 : -1) * (.05 + _rng.NextDouble() * .09 + shelf * .02), Golden = _rng.NextDouble() < .12
        });
    }

    private void Say(string line)
    {
        FeedbackLines.Enqueue($"{DateTime.Now:HH:mm:ss.f}  {line}");
        while (FeedbackLines.Count > 7) FeedbackLines.Dequeue();
    }

    private float PanOf(double x) => (float)Math.Clamp(x * 2 - 1, -1, 1);

    private int NextInLoadout(int from, int step)
    {
        for (int i = 1; i <= Weapons.Length; i++)
        {
            int k = ((from + step * i) % Weapons.Length + Weapons.Length) % Weapons.Length;
            if (_cfg.Loadout[k]) return k;
        }
        return from;
    }

    private void SelectWeapon(int index, string why)
    {
        WeaponIndex = (index + Weapons.Length) % Weapons.Length;
        var w = Current;
        _fb.Adaptive(L2Now(), R2For(WeaponIndex, Ammo[WeaponIndex] > 0), "weapon " + w.Name);
        UpdateLights();
        _fb.Sound(Synth.Click(1200, .04, .3f), "weapon switch");
        _fb.Haptic(Synth.HPunch(220, .04, 0, .4f, 90), "weapon switch");
        Say($"Silah: {w.Name} · R2 efekti {R2For(WeaponIndex, true)} · ışık {w.R},{w.G},{w.B}");
        SessionLog.Current?.Event("weapon", new { w.Name, why });
    }

    /// <summary>The weapon's R2 effect, scaled by the trigger intensity setting (Off = no effect, like games).</summary>
    private TriggerFx R2For(int weapon, bool loaded)
    {
        if (!loaded || _cfg.Triggers == TriggerIntensity.Off) return TriggerFx.Off;
        int S(int full) => _cfg.Strength(full);
        return weapon switch
        {
            0 => TriggerFx.Weapon(3, 6, S(6)),
            1 => TriggerFx.Machine(2, 9, S(6), S(3), 25, 1),
            2 => TriggerFx.Weapon(2, 5, S(8)),
            3 => TriggerFx.Bow(1, 8, S(6), S(8)),
            _ => TriggerFx.Weapon(5, 8, S(8))
        };
    }

    /// <summary>L2: aim resistance, or the crossbow's crank while it reloads.</summary>
    private TriggerFx L2Now() =>
        _cfg.Triggers == TriggerIntensity.Off ? TriggerFx.Off
        : Reloading && WeaponIndex == 4 ? TriggerFx.Feedback(0, _cfg.Strength(6))
        : WeaponIndex == 3 ? R2For(3, true)          // the bow can be drawn with either trigger
        : Aiming ? TriggerFx.Feedback(2, _cfg.Strength(4)) : TriggerFx.Off;

    private void UpdateLights()
    {
        var w = Current;
        int ammo = Ammo[WeaponIndex], leds = (int)Math.Ceiling(5.0 * ammo / w.Mag);
        byte mask = (byte)(leds switch { 0 => 0, 1 => 0b00100, 2 => 0b01010, 3 => 0b01110, 4 => 0b11011, _ => 0b11111 });
        if (TimeLeft < 10 && _lightPulse) _fb.Light(255, 0, 0, mask, "time running out");
        else _fb.Light(w.R, w.G, w.B, mask, "weapon/ammo");
    }

    public void Update(double dt, InputHub input)
    {
        var s = input.State;
        if (input.Pressed(Btn.Menu)) { Paused = !Paused; Say(Paused ? "Duraklatıldı (☰ devam)" : "Devam"); _fb.StopAll(); }
        if (Over)
        {
            if (input.Pressed(Btn.A)) Restart();
            Particles(dt);
            return;
        }
        if (Paused) return;

        TimeLeft -= dt;
        if (TimeLeft <= 0) { TimeLeft = 0; Over = true; _fb.StopAll(); _fb.Light(255, 255, 255, 0b11111, "round over"); SessionLog.Current?.Event("round-end", new { Score, Shots, Hits }); SessionLog.Current?.Note($"Tur bitti: skor {Score}, atış {Shots}, isabet {Hits}"); return; }

        // aim: left stick fast, right stick fine, L2 = aim down sights (slower, L2 resistance on a DualSense)
        bool aimNow = s.LT > .35f && WeaponIndex != 3;   // with the bow, L2 draws the string instead of aiming
        if (aimNow != Aiming)
        {
            Aiming = aimNow;
            _fb.Adaptive(L2Now(), R2For(WeaponIndex, Ammo[WeaponIndex] > 0), Aiming ? "aim start" : "aim end");
            if (Aiming) { _fb.Haptic(Synth.HPunch(200, .03, -.6f, .35f, 120), "aim"); Say("Nişan (L2): L2 direnci açık"); }
        }
        double speed = Aiming ? .35 : .9;
        CrossX += (PadState.Dead(s.LX) * speed + PadState.Dead(s.RX) * .25) * dt;
        CrossY -= (PadState.Dead(s.LY) * speed + PadState.Dead(s.RY) * .25) * dt * (_cfg.InvertY ? -1 : 1);
        if (GyroAim && s.HasMotion) { CrossX -= s.GyroY * dt * .02; CrossY -= s.GyroX * dt * .02; }
        if (s.Touching && input.Active?.Sony == true) { CrossX = s.TouchX; CrossY = s.TouchY; }
        CrossX = Math.Clamp(CrossX, 0, 1); CrossY = Math.Clamp(CrossY, .05, .95);

        if (!Reloading && (input.Pressed(Btn.RB) || input.Pressed(Btn.Right))) SelectWeapon(NextInLoadout(WeaponIndex, 1), "next");
        if (!Reloading && (input.Pressed(Btn.LB) || input.Pressed(Btn.Left))) SelectWeapon(NextInLoadout(WeaponIndex, -1), "previous");
        if (input.Pressed(Btn.Up)) { _fb.RumbleScale = Math.Min(1, _fb.RumbleScale + .25f); Say($"Rumble gücü %{_fb.RumbleScale * 100:0}"); }
        if (input.Pressed(Btn.Down)) { _fb.RumbleScale = Math.Max(0, _fb.RumbleScale - .25f); Say($"Rumble gücü %{_fb.RumbleScale * 100:0}"); }
        if ((input.Pressed(Btn.X) || input.Pressed(Btn.Touchpad)) && !Reloading && Ammo[WeaponIndex] < Current.Mag) StartReload();
        if (input.Pressed(Btn.Y)) ThrowGrenade();
        if (input.Pressed(Btn.RS)) Melee();
        if (input.Pressed(Btn.LS)) { _fb.Haptic(Synth.HSweep(2), "pan test"); _fb.Rumble(.6f, 0, .5, why: "pan test left"); Say("LS: haptik sol→sağ süpürme"); }
        if (input.Pressed(Btn.Mute) && _fb.Speakers != null) { _fb.Speakers.MusicOn = !_fb.Speakers.MusicOn; _fb.MicLed(!_fb.Speakers.MusicOn); Say("Mute: müzik " + (_fb.Speakers.MusicOn ? "açık" : "kapalı")); }

        if (Reloading)
        {
            _reloadLeft -= dt;
            if (WeaponIndex == 4 && (_crankTick -= dt) <= 0)   // crossbow crank: ticks in the palms and the left trigger motor
            {
                _crankTick = .22;
                _fb.Haptic(Synth.HPunch(170, .03, -.5f, .5f, 110), "crossbow crank");
                _fb.Sound(SoundBank.Get("crank", -.3f, .6f) ?? Synth.Click(1100, .03, .25f), "crossbow crank");
                _fb.Rumble(0, .1f, .08, lt: .5f);
            }
            if (_reloadLeft <= 0) FinishReload();
        }
        Fire(dt, s);

        if (_grenadeTimer >= 0) { _grenadeTimer -= dt; if (_grenadeTimer < 0) Explode(); }
        if (TimeLeft < 10)
        {
            _heartbeat -= dt;
            if (_heartbeat <= 0)
            {
                _heartbeat = .85; _lightPulse = !_lightPulse;
                _fb.Sound(Synth.Heartbeat(), "heartbeat", .5f);
                _fb.Haptic(Synth.HHeartbeat(), "heartbeat");
                _fb.Rumble(.35f, 0, .18, why: "heartbeat");
                UpdateLights();
            }
        }

        _spawn -= dt;
        if (_spawn <= 0 && Bottles.Count < 12) { Spawn(); _spawn = .6 + _rng.NextDouble(); }
        foreach (var b in Bottles) { b.X += b.Vx * dt; b.Wobble = Math.Max(0, b.Wobble - dt); }
        Bottles.RemoveAll(b => b.X < -.1 || b.X > 1.1);
        Particles(dt);
        Flash = Math.Max(0, Flash - dt);
    }

    private void Particles(double dt)
    {
        foreach (var p in Shards) { p.X += p.Vx * dt; p.Y += p.Vy * dt; p.Vy += 1.4 * dt; p.Life -= dt; }
        Shards.RemoveAll(p => p.Life <= 0);
        foreach (var p in Popups) { p.Y -= .05 * dt; p.Life -= dt; }
        Popups.RemoveAll(p => p.Life <= 0);
        if (ExplosionAt >= 0) { ExplosionRing += dt * 1.5; if (ExplosionRing > .35) ExplosionAt = -1; }
    }

    private void Fire(double dt, PadState s)
    {
        _fireCooldown -= dt;
        var w = Current;
        if (WeaponIndex == 3) { Bow(dt, s); return; }
        // fire where each R2 effect breaks: pistol ~60 %, shotgun ~50 % (early, heavy), crossbow ~80 % (late)
        float breakAt = WeaponIndex switch { 2 => .55f, 4 => .85f, _ => .65f };
        bool want = w.Auto ? s.RT > .5f && _fireCooldown <= 0 : s.RT > breakAt && _rtArmed;
        if (!w.Auto && s.RT < .3f) _rtArmed = true;
        if (!want || Reloading) return;
        _rtArmed = false;
        if (Ammo[WeaponIndex] == 0)
        {
            _fb.Sound(SoundBank.Get("empty", 0, .7f) ?? Synth.Click(2600, .03, .4f), "empty");
            _fb.Haptic(Synth.HPunch(250, .025, .5f, .4f, 120), "empty");
            Say("Şarjör boş: X / □ ile doldur");
            _fireCooldown = .3;
            return;
        }
        Ammo[WeaponIndex]--;
        _fireCooldown = w.Auto ? 1 / w.Rate : .12;
        Shots++;
        Flash = .06;
        float pan = PanOf(CrossX);
        _fb.Sound(Synth.Shot(WeaponIndex, pan * .5f), "shot " + w.Name);
        switch (WeaponIndex)
        {
            case 0:
                _fb.Haptic(Synth.HShot(0), "shot pistol");
                _fb.Rumble(.25f, .6f, .12, rt: .8f, why: "pistol"); break;
            case 1:
                _recoilLeft = !_recoilLeft;   // automatic recoil walks left-right in the palms
                _fb.Haptic(Synth.HPunch(120, .07, _recoilLeft ? -.6f : .6f, .85f, 45), "shot rifle");
                _fb.Rumble(.2f, .45f, .07, rt: .55f, why: "rifle"); break;
            case 2:
                _fb.Haptic(Synth.HShot(2), "shot shotgun");
                _fb.Rumble(1f, .7f, .35, lt: .35f, rt: 1f, why: "shotgun"); break;
            case 4:
                _fb.Haptic(Synth.Mix(Synth.HPunch(240, .03, .4f, .6f, 120), Synth.HPunch(55, .3, 0, 1f, 10)), "shot crossbow");
                _fb.Rumble(.6f, .4f, .2, rt: 1f, why: "crossbow"); break;
        }
        Say($"{w.Name} ateş · rumble + sağ tetik motoru · HD haptik · ses (pan {pan:+0.0;-0.0})");
        int pellets = WeaponIndex == 2 ? 7 : 1;
        for (int p = 0; p < pellets; p++)
        {
            double x = CrossX + (_rng.NextDouble() - .5) * w.Spread * (Aiming ? .4 : 1) * (pellets > 1 ? 4 : 1);
            double y = CrossY + (_rng.NextDouble() - .5) * w.Spread * (Aiming ? .4 : 1) * (pellets > 1 ? 4 : 1);
            HitAt(x, y, .035);
        }
        if (Ammo[WeaponIndex] == 0) _fb.Adaptive(L2Now(), TriggerFx.Off, "magazine empty");
        if (WeaponIndex == 2 && Ammo[WeaponIndex] > 0) { _fb.Sound(SoundBank.Get("pump", .1f, .8f) is { } pump ? Synth.Shift(pump, .3) : Synth.Shift(Synth.Reload(), .35), "pump"); _fb.Haptic(Synth.Shift(Synth.HReload(), .35), "pump"); }
        UpdateLights();
    }

    private bool _bowLeft;

    /// <summary>Bow: drawn with whichever trigger is pulled further (both carry the bow effect); tension and release go to
    /// that side's trigger motor and palm.</summary>
    private void Bow(double dt, PadState s)
    {
        float pull = Math.Max(s.LT, s.RT);
        if (pull > .15f && !Reloading)
        {
            if (_bowMax == 0)
            {
                _bowLeft = s.LT > s.RT;
                var draw = SoundBank.Get("bowdraw", _bowLeft ? -.3f : .3f, .7f);   // recorded string draw (none in the synth set)
                if (draw != null) _fb.Sound(draw, "bow draw");
            }
            _bowDraw = pull; _bowMax = Math.Max(_bowMax, pull);
            _bowTick -= dt;
            if (_bowTick <= 0)
            {
                _bowTick = .12;
                float side = _bowLeft ? -.7f : .7f, t = (float)_bowDraw;
                _fb.Haptic(Synth.HPunch(60 + 40 * t, .12, side, .25f + .5f * t, 4), "bow tension", .6f + .4f * t);
                _fb.Rumble(_bowLeft ? .15f * t : 0, _bowLeft ? 0 : .15f * t, .14, lt: _bowLeft ? .6f * t : 0, rt: _bowLeft ? 0 : .6f * t);
            }
            return;
        }
        if (_bowMax > .6)
        {
            Shots++; Flash = .04;
            float pan = PanOf(CrossX);
            _fb.Sound(Synth.Shot(3, pan * .5f), "bow release");
            float side = _bowLeft ? -.6f : .6f;
            _fb.Haptic(Synth.Mix(Synth.HPunch(180, .15, side, .6f, 25), Synth.HPunch(260, .03, side, .7f * (float)_bowMax, 130)), "bow release");
            _fb.Rumble(_bowLeft ? .5f : .2f, _bowLeft ? .2f : .5f, .12, lt: _bowLeft ? .9f : 0, rt: _bowLeft ? 0 : .9f, why: _bowLeft ? "bow release (L2)" : "bow release (R2)");
            Say($"Yay bırakıldı (güç %{_bowMax * 100:0}) · {(_bowLeft ? "L2" : "R2")} yay efekti · {(_bowLeft ? "sol" : "sağ")} tetik motoru");
            HitAt(CrossX, CrossY, .02 + .03 * _bowMax);
        }
        _bowMax = 0; _bowDraw = 0;
    }

    private void HitAt(double x, double y, double radius)
    {
        foreach (var b in Bottles.ToList())
        {
            double dx = (b.X - x) * 1.6, dy = (b.Y - .04 - y);
            if (dx * dx + dy * dy > radius * radius * 4) continue;
            Break(b, false);
        }
    }

    private void Break(Bottle b, bool byGrenade)
    {
        Bottles.Remove(b);
        Hits++;
        int pts = b.Golden ? 5 : 1;
        Score += pts;
        Popups.Add(new Popup { Text = b.Golden ? "+5 ALTIN" : "+1", X = b.X, Y = b.Y - .08, Life = .8 });
        for (int i = 0; i < 14; i++)
            Shards.Add(new Shard { X = b.X, Y = b.Y - .03, Vx = (_rng.NextDouble() - .5) * .6, Vy = -_rng.NextDouble() * .6, Life = .6 + _rng.NextDouble() * .4, Gold = b.Golden });
        if (byGrenade) return;
        float pan = PanOf(b.X);
        _fb.Sound(Synth.Shatter(pan), "shatter");
        _fb.Haptic(Synth.HShatter(pan), "shatter");
        _fb.Rumble(pan < 0 ? .25f : 0, pan >= 0 ? .35f : .15f, .1, why: "shatter");
        if (b.Golden) { _fb.Haptic(Synth.HTone(262, .15, pan), "golden"); _fb.Light(255, 200, 0, 0b11111, "golden bottle"); }
        Say($"Şişe kırıldı ({(pan < -.3 ? "sol" : pan > .3 ? "sağ" : "orta")}) · cam haptiği o tarafta");
    }

    private void StartReload()
    {
        Reloading = true; _reloadLeft = Current.Reload; _crankTick = 0;
        if (WeaponIndex == 4) _fb.Adaptive(L2Now(), TriggerFx.Off, "crossbow crank");
        _fb.Sound(SoundBank.Get(WeaponIndex switch { 2 => "reload-shotgun", 3 or 4 => "reload-bow", _ => "reload" }, -.2f, .8f) ?? Synth.Reload(), "reload");
        _fb.Haptic(Synth.HReload(), "reload");
        _fb.Rumble(.0f, .3f, .08, why: "reload");
        _fb.Light(255, 200, 0, 0, "reloading");
        Say("Doldurma: tık-tık haptiği, ışık sarı");
    }

    private void FinishReload()
    {
        Reloading = false; Ammo[WeaponIndex] = Current.Mag;
        _fb.Adaptive(L2Now(), R2For(WeaponIndex, true), "reloaded");
        UpdateLights();
    }

    private void ThrowGrenade()
    {
        if (Grenades == 0 || _grenadeTimer >= 0) { Say("El bombası yok"); return; }
        Grenades--; _grenadeTimer = .9; ExplosionX = CrossX; ExplosionY = CrossY;
        _fb.Sound(Synth.Click(500, .1, .4f), "grenade throw");
        _fb.Haptic(Synth.HPunch(90, .08, .2f, .5f, 40), "grenade throw");
        Say("El bombası atıldı (Y / △)…");
    }

    private void Explode()
    {
        _grenadeTimer = -1; ExplosionAt = 0; ExplosionRing = 0;
        float pan = PanOf(ExplosionX);
        _fb.Sound(Synth.Explosion(pan), "explosion");
        _fb.Haptic(Synth.HExplosion(pan), "explosion");
        _fb.Rumble(1f, 1f, .9, lt: .7f, rt: .7f, why: "explosion");
        _fb.Light(255, 120, 0, 0b11111, "explosion");
        int n = 0;
        foreach (var b in Bottles.ToList())
            if (Math.Abs(b.X - ExplosionX) < .18 && Math.Abs(b.Y - ExplosionY) < .25) { Break(b, true); n++; }
        Say($"PATLAMA: {n} şişe · iki gövde + iki tetik motoru · derin haptik");
    }

    private void Melee()
    {
        _fb.Sound(Synth.Melee(), "melee");
        _fb.Haptic(Synth.HPunch(90, .15, 0, 1f, 25), "melee");
        _fb.Rumble(.8f, .2f, .15, why: "melee");
        Say("RS: dipçik darbesi (gövde + haptik)");
        HitAt(CrossX, CrossY, .05);
    }
}

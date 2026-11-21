namespace RW3TestRange;

/// <summary>One check of the guided feedback test: plays a stimulus on one output, then asks what was felt.</summary>
public sealed record FeedbackTest(string Id, string Title, string Ask, Out Needs, double Seconds, Action<FeedbackHub> Start, Action<FeedbackHub>? Stop = null);

public enum TestAnswer { None, Yes, No, Skipped, NotAvailable }

/// <summary>
/// The guided test list: every output path the game has, one at a time, with a yes/no answer per item, so a session
/// shows exactly which feedback reached the hands (and through RW3's log, along which path).
/// </summary>
public static class FeedbackTests
{
    private static Action<FeedbackHub> Adaptive(TriggerFx l, TriggerFx r) => fb => fb.Adaptive(l, r, "test");
    private static readonly Action<FeedbackHub> AdaptiveOff = fb => fb.Adaptive(TriggerFx.Off, TriggerFx.Off, "test end");

    public static readonly FeedbackTest[] All =
    {
        new("body-left", "Sol gövde motoru (büyük)", "Sol tutamaçta güçlü, derin titreşim var mı?", Out.XInput | Out.GameInput | Out.DsHid, 1.2,
            fb => fb.Rumble(1, 0, 1.2, why: "test body-left")),
        new("body-right", "Sağ gövde motoru (küçük)", "Sağ tutamaçta hafif, hızlı titreşim var mı?", Out.XInput | Out.GameInput | Out.DsHid, 1.2,
            fb => fb.Rumble(0, 1, 1.2, why: "test body-right")),
        new("trig-left", "Sol tetik motoru (LT)", "Sol TETİĞİN İÇİNDE titreşim var mı? (GameInput)", Out.GameInput, 1.2,
            fb => fb.Rumble(0, 0, 1.2, lt: 1, why: "test trigger-left")),
        new("trig-right", "Sağ tetik motoru (RT)", "Sağ TETİĞİN İÇİNDE titreşim var mı? (GameInput)", Out.GameInput, 1.2,
            fb => fb.Rumble(0, 0, 1.2, rt: 1, why: "test trigger-right")),
        new("ds-l2-res", "L2 adaptif: sürekli direnç", "L2'yi yavaşça çek: direnç/tetik titreşimi hissediliyor mu?", Out.DsHid, 6,
            Adaptive(TriggerFx.Feedback(1, 8), TriggerFx.Off), AdaptiveOff),
        new("ds-r2-weapon", "R2 adaptif: silah (tık)", "R2'yi yavaşça sonuna kadar çek: yolun ortasında tık var mı?", Out.DsHid, 6,
            Adaptive(TriggerFx.Off, TriggerFx.Weapon(2, 6, 8)), AdaptiveOff),
        new("ds-r2-vib", "R2 adaptif: titreşim", "R2'yi yarıya çekip tut: tetikte sürekli titreşim var mı?", Out.DsHid, 6,
            Adaptive(TriggerFx.Off, TriggerFx.Vibration(0, 8, 30)), AdaptiveOff),
        new("ds-r2-bow", "R2 adaptif: yay", "R2'yi sonuna kadar çekip bırak: gerilme ve bırakışta vuruş var mı?", Out.DsHid, 6,
            Adaptive(TriggerFx.Off, TriggerFx.Bow(1, 8, 6, 8)), AdaptiveOff),
        new("ds-r2-machine", "R2 adaptif: makineli", "R2'yi çekip tut: ritmik (makineli) titreşim var mı?", Out.DsHid, 6,
            Adaptive(TriggerFx.Off, TriggerFx.Machine(1, 9, 7, 3, 40, 2)), AdaptiveOff),
        new("ds-l2-gallop", "L2 adaptif: dörtnala", "L2'yi çekip tut: at nalı gibi ikili vuruşlar var mı?", Out.DsHid, 6,
            Adaptive(TriggerFx.Galloping(1, 9, 1, 4, 3), TriggerFx.Off), AdaptiveOff),
        new("hd-left", "HD haptik: sol avuç", "Yalnız SOL avuçta 120 Hz titreşim var mı?", Out.DsHaptics, 1.2,
            fb => fb.Haptic(Synth.HTone(120, 1.2, -1), "test hd-left")),
        new("hd-right", "HD haptik: sağ avuç", "Yalnız SAĞ avuçta 120 Hz titreşim var mı?", Out.DsHaptics, 1.2,
            fb => fb.Haptic(Synth.HTone(120, 1.2, 1), "test hd-right")),
        new("hd-sweep", "HD haptik: sol → sağ → sol", "Titreşim soldan sağa kayıp geri geldi mi?", Out.DsHaptics, 3,
            fb => fb.Haptic(Synth.HSweep(3), "test hd-sweep")),
        new("hd-melody", "HD haptik: ezgi", "Avuçlar bir ezgi çaldı mı? (kolu kulağına dayayınca duyulur)", Out.DsHaptics, 3.4,
            fb => fb.Haptic(Synth.HMelody(), "test hd-melody")),
        new("hd-hits", "HD haptik: vuruşlar", "Silah, cam ve patlama vuruşları ayırt ediliyor mu?", Out.DsHaptics, 3,
            fb => { fb.Haptic(Synth.HShot(0), "test hd-hits"); fb.Haptic(Synth.Shift(Synth.HShatter(0), .7), "test hd-hits"); fb.Haptic(Synth.Shift(Synth.HExplosion(0), 1.4), "test hd-hits"); }),
        new("light", "Işık çubuğu renkleri", "Işık çubuğu kırmızı → yeşil → mavi oldu mu?", Out.DsHid, 3,
            fb => { fb.Light(255, 0, 0, 0b00100, "test light"); Later(1, () => fb.Light(0, 255, 0, 0b01010, "test light")); Later(2, () => fb.Light(0, 0, 255, 0b11111, "test light")); }),
        new("leds", "Oyuncu LED'leri", "Alt LED'ler 1 → 5 doldu mu?", Out.DsHid, 3,
            fb => { byte[] m = { 0b00100, 0b01010, 0b01110, 0b11011, 0b11111 }; for (int i = 0; i < 5; i++) { byte v = m[i]; Later(i * .5, () => fb.Light(40, 120, 255, v, "test leds")); } }),
        new("mic", "Mikrofon LED'i", "Mikrofon düğmesinin ışığı yandı mı?", Out.DsHid, 2,
            fb => fb.MicLed(true), fb => fb.MicLed(false)),
        new("spk-left", "Hoparlör: sol kanal", "Ses yalnız SOLDAN geldi mi? (sesten titreşim açıksa sol avuç da)", Out.Sfx, 1.2,
            fb => fb.Sound(Synth.Tone(330, 1.2, -1), "test speaker-left")),
        new("spk-right", "Hoparlör: sağ kanal", "Ses yalnız SAĞDAN geldi mi? (sesten titreşim açıksa sağ avuç da)", Out.Sfx, 1.2,
            fb => fb.Sound(Synth.Tone(330, 1.2, 1), "test speaker-right")),
        new("music", "Müzik (RW3 sesten titreşim)", "Müzik çalarken avuçlarda bas ve melodi hissediliyor mu?", Out.Music, 8,
            fb => { if (fb.Speakers != null) fb.Speakers.MusicOn = true; }, fb => { if (fb.Speakers != null) fb.Speakers.MusicOn = false; }),
        new("pad-speaker", "DualSense hoparlörü", "Kolun kendi hoparlöründen ses geldi mi?", Out.DsSpeaker, 1.2,
            fb => { fb.HapticOut?.PadSpeaker.Play(Synth.Tone(660, 1.2, 0)); SessionLog.Current?.Event("pad-speaker", "test"); }),
    };

    private static void Later(double seconds, Action a) =>
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a));
}

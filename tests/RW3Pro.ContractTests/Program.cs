using RW3ProNativeDSHaptics;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}

var normal = HapticEvidence.Evaluate(HapticMode.Normal, false, false, false, false, false);
Check(normal.Contains("izlemiyor"), "Normal game rumble is not claimed as observed");

var waiting = HapticEvidence.Evaluate(HapticMode.Audio, false, false, false, false, false);
Check(waiting.Contains("bekleniyor"), "Audio without frames reports waiting");

var audio = HapticEvidence.Evaluate(HapticMode.Audio, true, true, false, false, false);
Check(audio.Contains("SİSTEM SESİ") && !audio.StartsWith("NATIVE"), "Audio XInput acknowledgement not labeled native");

var noCapture = HapticEvidence.Evaluate(HapticMode.Native, true, true, false, false, false);
Check(noCapture.Contains("aktif DEĞİL"), "Native selection without PCM is blocked");

var noSink = HapticEvidence.Evaluate(HapticMode.Native, true, true, true, false, false);
Check(noSink.Contains("HD çıkışı doğrulanmadı"), "Captured PCM not sufficient for native playback");

var noPhysical = HapticEvidence.Evaluate(HapticMode.Native, true, true, true, true, false);
Check(noPhysical.Contains("fiziksel HD oynatma doğrulanmadı"), "Driver acknowledgment not motor confirmation");

var valid = HapticEvidence.Evaluate(HapticMode.Native, false, false, true, true, true);
Check(valid.Contains("stereo kaynak, çıkış"), "All three independent native proofs required");


Check(NativeStereoFixture.ValidateSeparation(HapticTestChannel.Left), "Synthetic left PCM is left-only");
Check(NativeStereoFixture.ValidateSeparation(HapticTestChannel.Right), "Synthetic right PCM is right-only");
Check(!ChannelTestGate.CanRunNative(false, true), "No DualSense PCM capture means no native test");
Check(!ChannelTestGate.CanRunNative(true, false), "No Razer haptic output means no native test");
Check(!ChannelTestGate.CanConfirmNative(true, true, true, false),
    "Single-side physical feedback is not full native proof");
Check(!ChannelTestGate.CanConfirmNative(false, true, true, true),
    "Audio or synthetic input cannot masquerade as authentic game haptics");
Check(NativePcmTestPlayer.VerifyIsolatedChannel(HapticTestChannel.Left),
    "Four-channel UAC fixture has isolated left rear channel");
Check(NativePcmTestPlayer.VerifyIsolatedChannel(HapticTestChannel.Right),
    "Four-channel UAC fixture has isolated right rear channel");
Check(!HapticSafetyRules.MustZeroStaleMotor(true, true, 1000, 1249),
    "Fresh WASAPI callback does not zero motor");
Check(HapticSafetyRules.MustZeroStaleMotor(true, true, 1000, 1250),
    "Silent WASAPI callback watchdog zeros stale motor");
Check(!HapticSafetyRules.MustZeroStaleMotor(false, true, 1000, 2000),
    "Watchdog cannot write after capture stopped");
Check(!HapticSafetyRules.MustZeroStaleMotor(true, false, 1000, 2000),
    "Already stopped motor is not repeatedly commanded");
Check(HapticSafetyRules.HasRecentChannelEnergy(true, 1000, 0, 1200),
    "Recent left haptic energy identified");
Check(!HapticSafetyRules.HasRecentChannelEnergy(true, 1000, 0, 3500),
    "Old haptic energy counters are not called recent");
Check(!HapticSafetyRules.IsolatedChannelObserved(1, 2, 3, 4),
    "Bleed into opposite UAC channel fails isolation");
Check(HapticSafetyRules.IsolatedChannelObserved(1, 2, 3, 3),
    "Clean isolated UAC channel passes");
var isolatedLeft = NativePcmTestPlayer.BuildFourChannelTone(HapticTestChannel.Left, 48000, 520);
var leftMetrics = FourChannelHapticAnalyzer.Analyze(isolatedLeft, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
Check(leftMetrics.ReceivedFrames == 24960 && leftMetrics.SampledFrames <= 256,
    "Four-channel PCM analyzes bounded samples and counts input frames");
Check(leftMetrics.LeftRms > 0.01f && leftMetrics.RightRms == 0f && leftMetrics.SpeakerRms == 0f,
    "Real source decoder distinguishes rear left from speaker and rear right");
var isolatedRight = NativePcmTestPlayer.BuildFourChannelTone(HapticTestChannel.Right, 48000, 520);
var rightMetrics = FourChannelHapticAnalyzer.Analyze(isolatedRight, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
Check(rightMetrics.RightRms > 0.01f && rightMetrics.LeftRms == 0f && rightMetrics.SpeakerRms == 0f,
    "Real source decoder distinguishes rear right from speaker and rear left");
var speakerOnly = new byte[8];
speakerOnly[1] = speakerOnly[3] = 0x40;
Check(FourChannelHapticAnalyzer.Analyze(speakerOnly, FourChannelHapticAnalyzer.PcmEncoding.Pcm16).SpeakerOnly,
    "Speaker-only audio never counts as DualSense haptic energy");
var stereoFloat = new byte[16];
BitConverter.GetBytes(0.25f).CopyTo(stereoFloat, 8);
BitConverter.GetBytes(-0.5f).CopyTo(stereoFloat, 12);
var floatMetrics = FourChannelHapticAnalyzer.Analyze(stereoFloat, FourChannelHapticAnalyzer.PcmEncoding.Float32);
Check(floatMetrics.LeftRms == 0.25f && floatMetrics.RightRms == 0.5f,
    "Float32 native frame decoding preserves independent haptic levels");
BitConverter.GetBytes(float.NaN).CopyTo(stereoFloat, 8);
var sanitized = FourChannelHapticAnalyzer.Analyze(stereoFloat, FourChannelHapticAnalyzer.PcmEncoding.Float32);
Check(sanitized.LeftRms == 0f && sanitized.RightRms == 0.5f && sanitized.NonFiniteSamples == 1,
    "Malformed float samples are zeroed and counted without leaking into output");
Check(FourChannelHapticAnalyzer.IsCompatible(4, 48000, 16, FourChannelHapticAnalyzer.PcmEncoding.Pcm16) &&
      !FourChannelHapticAnalyzer.IsCompatible(2, 48000, 16, FourChannelHapticAnalyzer.PcmEncoding.Pcm16) &&
      !FourChannelHapticAnalyzer.IsCompatible(4, 44100, 16, FourChannelHapticAnalyzer.PcmEncoding.Pcm16),
    "Native source is exactly 4-channel 48k; no ordinary stereo fallback");
Check(HapticRouteCatalog.All.Count == 9, "Nine persistent route preferences exist");
Check(HapticRouteCatalog.All.Select(x => x.Route).Distinct().Count() == 9,
    "Route identifiers are unique");
Check(HapticRouteCatalog.Get(HapticRoute.NativePadForge).RecommendedForOriginalSource,
    "PadForge is recommended for original input potential only");
Check(HapticRouteCatalog.Get(HapticRoute.NativePadForge).Output.Contains("kayıplı"),
    "Recommended input still honestly labels lossy Sensa output");
Check(!HapticRouteCatalog.Get(HapticRoute.WwiseStereoMotionLab).CanStartInsideApp &&
    HapticRouteCatalog.Get(HapticRoute.WwiseStereoMotionLab).Readiness == RouteReadiness.ResearchOnly,
    "Wwise option never pretends to start live integration");
Check(!HapticRouteCatalog.Get(HapticRoute.OfficialRazerSensaGames).OriginalDualSenseSourcePossible,
    "Razer native games cannot be mislabeled authored Sony source");
Check(HapticRouteCatalog.Get(HapticRoute.OfficialRazerAudioToHaptics).Mode == HapticMode.Normal,
    "External Razer audio route does not start RW3 audio capture");
Check(HapticRouteCatalog.Get(HapticRoute.NativeDsxPlus).IsNative,
    "DSX virtual audio is an opt-in experimental native source");
Check(HapticRouteCatalog.NameMayMatch(HapticRoute.NativePadForge, "PadForge DualSense Full"),
    "PadForge candidate name hint works");
Check(!HapticRouteCatalog.NameMayMatch(HapticRoute.NativeDsxPlus, "Realtek Speakers"),
    "Obvious non-matching endpoint warns user");
Check(!HapticRouteCatalog.CanStartBridge(HapticRoute.NativePadForge, true, false),
    "No native startup without explicit source acknowledgement");
Check(!HapticRouteCatalog.CanStartBridge(HapticRoute.NativePadForge, false, true),
    "Incompatible 4ch endpoint cannot start");
Check(!HapticRouteCatalog.CanStartBridge(HapticRoute.WwiseStereoMotionLab, true, true),
    "Research-only Wwise cannot start native backend");
Check(HapticRouteCatalog.CanStartBridge(HapticRoute.NativeUsbDualSense, true, true),
    "Compatible native source passes to independent vendor SDK preflight");
Check(HapticRouteCatalog.DefaultForMode(HapticMode.Audio) == HapticRoute.LocalSystemAudioXInput &&
    HapticRouteCatalog.DefaultForMode(HapticMode.Native) == HapticRoute.NativeManualUac,
    "Legacy mode controls resolve safely");
Check(NativeCaptureHealth.Evaluate(false, 0, 0, 0, 5000) == NativeCaptureState.Stopped,
    "Native source status is stopped without capture");
Check(NativeCaptureHealth.Evaluate(true, 0, 0, 0, 5000) == NativeCaptureState.WaitingForAudio,
    "Native source awaits first WASAPI buffer");
Check(NativeCaptureHealth.Evaluate(true, 1000, 0, 0, 1010) == NativeCaptureState.AudioSilentOrSpeakerOnly,
    "Silent or front-speaker-only frames cannot assert authored haptic activity");
Check(NativeCaptureHealth.Evaluate(true, 1000, 990, 0, 1050) == NativeCaptureState.RearChannelEnergyObserved,
    "Active rear PCM is observed without claiming game authorship");
Check(NativeCaptureHealth.Evaluate(true, 1000, 990, 0, 3500) == NativeCaptureState.AudioStale,
    "Stale callback is not reported as an active native source");
Check(NativeCaptureHealth.Evaluate(true, 5000, 0, 0, 4000) == NativeCaptureState.AudioStale,
    "Invalid monotonic timestamp is treated as stale");
Check(NativeCaptureHealth.Describe(NativeCaptureState.RearChannelEnergyObserved).Contains("doğrulanmadı"),
    "Even observed rear PCM cannot claim source provenance");
Check(BehavioralHapticFixtures.All.Count == 10, "Ten reproducible tactile scenarios exist");
Check(BehavioralHapticFixtures.All.Select(s => s.Id).Distinct().Count() == 10,
    "Every behavior has one unique user-facing scenario");
foreach (var scene in BehavioralHapticFixtures.All)
{
    byte[] pcm = BehavioralHapticFixtures.Generate(scene.Id);
    int frames = scene.DurationMs * BehavioralHapticFixtures.SampleRate / 1000;
    Check(pcm.Length == frames * BehavioralHapticFixtures.BytesPerFrame,
        "Scene " + scene.Id + " exact 4ch 48k frame count");
    Check(BehavioralHapticFixtures.Generate(scene.Id).AsSpan().SequenceEqual(pcm),
        "Scene " + scene.Id + " deterministic across runs");
    bool frontSilent = true, bounded = true;
    for (int o = 0; o < pcm.Length; o += 8)
    {
        if (pcm[o] != 0 || pcm[o+1] != 0 || pcm[o+2] != 0 || pcm[o+3] != 0)
            frontSilent = false;
        if (Math.Abs(BitConverter.ToInt16(pcm, o + 4)) > 7300 ||
            Math.Abs(BitConverter.ToInt16(pcm, o + 6)) > 7300)
            bounded = false;
    }
    Check(frontSilent && bounded, "Scene " + scene.Id +
        " silent speakers and <=22% haptic PCM peak");
    var rms = BehavioralHapticFixtures.RmsWindow(pcm, frames / 3, 960);
    Check(float.IsFinite(rms.Left) && float.IsFinite(rms.Right),
        "Scene " + scene.Id + " has finite independent RMS envelopes");
    Check(pcm.AsSpan(0, 4).IndexOfAnyExcept((byte)0) < 0,
        "Scene " + scene.Id + " original reference speaker channels are silent");
}
Check(BehavioralHapticFixtures.Generate(HapticBehavior.Pistol)
    .AsSpan().IndexOf((byte)0xff) >= 0, "Procedural impulse scene contains data");
Check(BehavioralHapticFixtures.All.All(s => s.DurationMs <= 3200),
    "All USB DualSense reference scenes are deliberately bounded in duration");
Check(HapticOutputLevel.Limit(-5) == 0 && HapticOutputLevel.Limit(145) == 100,
    "RW3 level control is clamped to 0-100 percent");
Check(HapticOutputLevel.Apply(0.35f, 0) == 0 &&
      HapticOutputLevel.Apply(0.35f, 100) == 0.35f,
    "RW3 output gain has a safe zero and unity setting");
Check(HapticOutputLevel.Apply(double.NaN, 100) == 0 &&
      HapticOutputLevel.Apply(float.PositiveInfinity, 100) == 0,
    "Nonfinite output levels cannot leak to motor APIs");
Check(UsbGamepadClassifier.IsDualSenseUsbNode(@"USB\VID_054C&PID_0CE6\A"),
    "USB Sony DualSense base VID/PID recognized");
Check(UsbGamepadClassifier.IsDualSenseUsbNode(@"usb\vid_054c&pid_0df2&mi_03\A"),
    "USB Sony DualSense Edge VID/PID recognized");
Check(UsbGamepadClassifier.IsWolverineV3ProUsbNode(@"USB\VID_1532&PID_0A3F\B"),
    "Wolverine V3 Pro expected Razer USB VID/PID recognized");
Check(!UsbGamepadClassifier.IsWolverineV3ProUsbNode(@"HID\VID_1532&PID_0A3F\B") &&
      !UsbGamepadClassifier.IsDualSenseUsbNode(@"SWD\VID_054C&PID_0CE6\V"),
    "Virtual/HID-only nodes cannot certify a present USB root device");
Check(!UsbGamepadClassifier.IsWolverineV3ProUsbNode(@"USB\VID_1532&PID_0A3FF\B"),
    "PID prefix collision rejected");
Check(UsbGamepadClassifier.IsWolverineWiredPcModeNode(@"USB\VID_1532&PID_0A4C\8&EF6451E&0&6") &&
      UsbGamepadClassifier.IsWolverineV3ProUsbNode(@"USB\VID_1532&PID_0A4C&MI_02\9&X"),
    "Wired PC-mode Wolverine 1532:0A4C recognized");
Check(!UsbGamepadClassifier.IsWolverineWiredPcModeNode(@"USB\VID_1532&PID_0A3F\B"),
    "Dongle/Xbox-mode 0A3F is not reported as a wired PC-mode connection");
// ── v2 HapticFeatureExtractor: envelope / frequency / transients / isolation ──
static byte[] FourCh16(int frames, Func<int, float> left, Func<int, float> right)
{
    var b = new byte[frames * 8];
    for (int i = 0; i < frames; i++)
    {
        short l = (short)Math.Round(Math.Clamp(left(i), -1f, 1f) * 32767);
        short r = (short)Math.Round(Math.Clamp(right(i), -1f, 1f) * 32767);
        b[i * 8 + 4] = (byte)l; b[i * 8 + 5] = (byte)(l >> 8);
        b[i * 8 + 6] = (byte)r; b[i * 8 + 7] = (byte)(r >> 8);
    }
    return b;
}
static byte[] FourChF32(int frames, Func<int, float> left, Func<int, float> right)
{
    var b = new byte[frames * 16];
    for (int i = 0; i < frames; i++)
    {
        BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 8, 4), left(i));
        BitConverter.TryWriteBytes(b.AsSpan(i * 16 + 12, 4), right(i));
    }
    return b;
}
Func<int, float> Sine(double hz, double amp) => i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / 48000.0));
Func<int, float> Zero = _ => 0f;
{
    var fx = new HapticFeatureExtractor();
    long cur = 0;
    fx.Push(FourCh16(4800, Zero, Zero), FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
    int n0 = fx.Take(ref cur, out var sl, out var sr);
    Check(n0 == 100 && sl.Peak == 0 && sr.Peak == 0 && sl.FrequencyHz == 0 && sl.TransientCount == 0,
        "v2 extractor: 100 ms silence -> 100 slots, zero envelope, no frequency, no transient");
    Check(fx.Take(ref cur, out _, out _) == 0, "v2 extractor: no new data -> 0 slots (engine holds/fades)");

    fx.Push(FourCh16(9600, Sine(60, 0.5), Zero), FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
    fx.Take(ref cur, out var l60, out var r60);
    Check(Math.Abs(l60.P3 - 0.5f) < 0.06f && Math.Abs(l60.FrequencyHz - 60) <= 5,
        $"v2 extractor: 60 Hz sine 0.5 on left -> envelope {l60.P3:F2}, frequency {l60.FrequencyHz:F0} Hz");
    Check(r60.Peak == 0 && r60.FrequencyHz == 0 && r60.TransientCount == 0,
        "v2 extractor: left-only source leaves right completely silent (no cross-talk in software)");

    fx.Push(FourCh16(9600, Zero, Sine(200, 0.8)), FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
    fx.Take(ref cur, out _, out var r200);
    Check(Math.Abs(r200.FrequencyHz - 200) <= 10 && r200.P3 > 0.7f,
        $"v2 extractor: 200 Hz sine on right -> {r200.FrequencyHz:F0} Hz, envelope {r200.P3:F2}");
}
{
    var fx = new HapticFeatureExtractor();
    long cur = 0;
    fx.Push(FourCh16(4800, Zero, Zero), FourChannelHapticAnalyzer.PcmEncoding.Pcm16);   // 100 ms quiet
    fx.Take(ref cur, out _, out _);
    fx.Push(FourCh16(240, Sine(150, 0.9), Zero), FourChannelHapticAnalyzer.PcmEncoding.Pcm16); // 5 ms hit
    int nHit = fx.Take(ref cur, out var hit, out var hitR);
    Check(nHit == 5 && hit.TransientCount == 1 && hit.T0Amplitude > 0.3f && hitR.TransientCount == 0,
        $"v2 extractor: sudden 5 ms hit -> exactly one left transient (amp {hit.T0Amplitude:F2}), none on right");
    fx.Push(FourCh16(4800, Sine(150, 0.9), Zero), FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
    fx.Take(ref cur, out var steady, out _);
    Check(steady.TransientCount == 0, "v2 extractor: steady vibration after the hit is not re-reported as transients");
}
{
    var fx = new HapticFeatureExtractor();
    long cur = 0;
    fx.Push(FourChF32(9600, Sine(80, 0.4), Sine(120, 0.6)), FourChannelHapticAnalyzer.PcmEncoding.Float32);
    fx.Take(ref cur, out var fl, out var fr);
    Check(Math.Abs(fl.FrequencyHz - 80) <= 5 && Math.Abs(fr.FrequencyHz - 120) <= 6 &&
          Math.Abs(fl.P3 - 0.4f) < 0.06f && Math.Abs(fr.P3 - 0.6f) < 0.06f,
        $"v2 extractor: Float32 stereo -> L {fl.FrequencyHz:F0} Hz/{fl.P3:F2}, R {fr.FrequencyHz:F0} Hz/{fr.P3:F2}");
    var nan = FourChF32(480, _ => float.NaN, _ => float.PositiveInfinity);
    fx.Push(nan, FourChannelHapticAnalyzer.PcmEncoding.Float32);
    fx.Take(ref cur, out var nl, out var nr);
    Check(float.IsFinite(nl.P3) && float.IsFinite(nr.P3) && float.IsFinite(nl.FrequencyHz),
        "v2 extractor: NaN/Infinity samples never reach the engine");
}
{
    var fx = new HapticFeatureExtractor();
    long cur = 0;
    fx.Push(FourCh16(480, i => (float)(i / 480.0 * Math.Sin(2 * Math.PI * 100 * i / 48000.0)), Zero),
        FourChannelHapticAnalyzer.PcmEncoding.Pcm16);   // 10 ms rising ramp
    fx.Take(ref cur, out var ramp, out _);
    Check(ramp.P0 < ramp.P1 && ramp.P1 < ramp.P2 && ramp.P2 < ramp.P3,
        $"v2 extractor: 4 points follow a rising envelope inside one window ({ramp.P0:F2} {ramp.P1:F2} {ramp.P2:F2} {ramp.P3:F2})");
}
// ── v2 demo signals: bounds, energy, 8-direction encoding ──
foreach (var demo in NativeDemoSignals.All)
{
    int n = NativeDemoSignals.DurationMs(demo) * 48;
    double el = 0, er = 0, peak = 0; bool finite = true;
    for (int i = 0; i < n; i += 3)
    {
        var (l, r) = NativeDemoSignals.Sample(demo, i / 48000.0);
        if (!double.IsFinite(l) || !double.IsFinite(r)) finite = false;
        peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
        el += l * l; er += r * r;
    }
    Check(finite && peak <= 1.0 + 1e-9 && el + er > 1 && NativeDemoSignals.Title(demo).Length > 0,
        $"v2 demo {demo}: finite, peak {peak:F2} <= 1, has energy (L {el:F0} / R {er:F0})");
}
{
    // Run each 8-direction step through the real extractor and compare left/right and frequency.
    (float L, float R, float Hz) Step(int step)
    {
        var fx = new HapticFeatureExtractor(); long cur = 0;
        int start = step * NativeDemoSignals.DirectionStepMs * 48 + 60 * 48, frames = 300 * 48;
        var b = new byte[frames * 8];
        for (int i = 0; i < frames; i++)
        {
            var (l, r) = NativeDemoSignals.Sample(NativeDemo.Directions8, (start + i) / 48000.0);
            short ls = (short)(l * 32767), rs = (short)(r * 32767);
            b[i * 8 + 4] = (byte)ls; b[i * 8 + 5] = (byte)(ls >> 8); b[i * 8 + 6] = (byte)rs; b[i * 8 + 7] = (byte)(rs >> 8);
        }
        fx.Push(b, FourChannelHapticAnalyzer.PcmEncoding.Pcm16);
        fx.Take(ref cur, out var fl, out var fr);
        return (fl.P3 + fl.P2, fr.P3 + fr.P2, Math.Max(fl.FrequencyHz, fr.FrequencyHz));
    }
    var front = Step(0); var right = Step(2); var back = Step(4); var left = Step(6);
    Check(right.L < right.R * 0.05f && left.R < left.L * 0.05f,
        $"v2 demo 8 yön: 'Sağ' only right (L {right.L:F2} / R {right.R:F2}), 'Sol' only left (L {left.L:F2} / R {left.R:F2})");
    Check(Math.Abs(front.L - front.R) < 0.05f * Math.Max(front.L, front.R) && Math.Abs(back.L - back.R) < 0.05f * Math.Max(back.L, back.R),
        "v2 demo 8 yön: 'Ön' and 'Arka' are centred (equal left/right)");
    Check(front.Hz > 150 && back.Hz < 60,
        $"v2 demo 8 yön: front/back separated by frequency (Ön {front.Hz:F0} Hz, Arka {back.Hz:F0} Hz)");
    Check(NativeDemoSignals.Caption(NativeDemo.Directions8, 1300).Contains("Sağ") &&
          NativeDemoSignals.Caption(NativeDemo.Directions8, 3700).Contains("Sol"),
        "v2 demo 8 yön: on-screen caption follows the playing direction");
}
// Strict reference preflight and byte identity, without opening a hardware endpoint.
var referenceBytes = NativePcmTestPlayer.BuildFourChannelTone(HapticTestChannel.Left, 48000, 520);
var referenceCopy = (byte[])referenceBytes.Clone();
bool rejectedShared = false;
try { using var rejected = DualSenseAudioFormat.OpenReferenceStream(referenceBytes, false); }
catch (InvalidOperationException ex) { rejectedShared = ex.Message.Contains("Shared"); }
Check(rejectedShared && referenceBytes.SequenceEqual(referenceCopy),
    "Unsupported Exclusive reference fails without Shared fallback or PCM mutation");
using (var referenceStream = DualSenseAudioFormat.OpenReferenceStream(referenceBytes, true))
{
    var playedBytes = new byte[referenceBytes.Length];
    Check(referenceStream.Read(playedBytes, 0, playedBytes.Length) == playedBytes.Length &&
        playedBytes.SequenceEqual(referenceCopy), "Exclusive reference stream preserves every original PCM byte");
}
foreach (var malformedReference in new[] { Array.Empty<byte>(), new byte[9] })
{
    bool rejected = false;
    try { using var stream = DualSenseAudioFormat.OpenReferenceStream(malformedReference, true); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Reference playback rejects empty or incomplete quad PCM frames");
}

// ── A: audio → Sensa haptic band ──
{
    double Pass(double hz)
    {
        var fx = new HapticFeatureExtractor(); long cur = 0;
        var sink = new SensaAudioSink(fx);
        var buf = new float[48000 * 2];
        for (int i = 0; i < 48000; i++) { float v = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000)); buf[2 * i] = v; buf[2 * i + 1] = 0; }
        sink.Accept(buf);
        fx.Take(ref cur, out var l, out var r);
        return l.P3 + r.P3 * 1000;   // right must stay 0
    }
    double bass = Pass(60), mid = Pass(1000);
    Check(bass > 0.27 && bass < 0.31 && mid < 0.005,
        $"A audio→Sensa: 60 Hz passes ({bass:F2}), 1 kHz is cut ({mid:F3}), left-only input stays left");
}
{
    // Audio pan test: left at start, centred at 1.5 s, right at 3 s, back to left at the end.
    var a0 = AudioPanTest.Sample(0.4); var mid = AudioPanTest.Sample(1.5); var right = AudioPanTest.Sample(2.95);
    double E(double t, bool left) { double e = 0; for (int i = 0; i < 480; i++) { var s = AudioPanTest.Sample(t + i / 48000.0); e += left ? s.L * s.L : s.R * s.R; } return e; }
    Check(E(0.3, true) > 20 * E(0.3, false) && E(2.9, false) > 20 * E(2.9, true) && E(5.6, true) > 20 * E(5.6, false) &&
          Math.Abs(E(1.5, true) - E(1.5, false)) < 0.1 * E(1.5, true) &&
          AudioPanTest.Caption(200).Contains("SOL") && AudioPanTest.Caption(2900).Contains("SAĞ"),
        "A audio pan test: left -> centre -> right -> left, captions follow");
}
{
    // Extra sound tests: hard left/right hops, front/back texture, 8 directions.
    double En(AudioTestKind k, double t, bool left) { double e = 0; for (int i = 0; i < 480; i++) { var s = AudioPanTest.Sample(k, t + i / 48000.0); e += left ? s.L * s.L : s.R * s.R; } return e; }
    bool sameAsDemo = true;
    for (int i = 0; i < 48000 * 3; i += 7)
    {
        var d = NativeDemoSignals.Sample(NativeDemo.PingPong, i / 48000.0); var a = AudioPanTest.Sample(AudioTestKind.PingPong, i / 48000.0);
        if (Math.Abs(a.L * SensaAudioSink.PreGain - d.L) > 1e-5 || Math.Abs(a.R * SensaAudioSink.PreGain - d.R) > 1e-5) { sameAsDemo = false; break; }
    }
    bool pingPong = sameAsDemo && AudioPanTest.DurationOf(AudioTestKind.PingPong) == NativeDemoSignals.DurationMs(NativeDemo.PingPong) &&
                    En(AudioTestKind.PingPong, 0.1, true) > 0.01 && En(AudioTestKind.PingPong, 0.1, false) == 0 &&
                    En(AudioTestKind.PingPong, 0.35, false) > 0.01 && En(AudioTestKind.PingPong, 0.35, true) == 0 &&
                    AudioPanTest.Caption(AudioTestKind.PingPong, 100).Contains("SOL") && AudioPanTest.Caption(AudioTestKind.PingPong, 350).Contains("SAĞ");
    double Tone(double t, double hz) { double re = 0, im = 0; for (int i = 0; i < 24000; i++) { double v = AudioPanTest.Sample(AudioTestKind.FrontBack, t + i / 48000.0).L; re += v * Math.Cos(2 * Math.PI * hz * i / 48000.0); im += v * Math.Sin(2 * Math.PI * hz * i / 48000.0); } return Math.Sqrt(re * re + im * im); }
    double front = Tone(0.05, AudioPanTest.FrontHz) / Math.Max(1e-9, Tone(0.05, AudioPanTest.BackHz)),
           back = Tone(0.80, AudioPanTest.BackHz) / Math.Max(1e-9, Tone(0.80, AudioPanTest.FrontHz));
    bool frontBack = front > 5 && back > 5 &&
                     Math.Abs(En(AudioTestKind.FrontBack, 0.9, true) - En(AudioTestKind.FrontBack, 0.9, false)) < 1e-9 &&
                     AudioPanTest.Caption(AudioTestKind.FrontBack, 100).Contains("ÖN") && AudioPanTest.Caption(AudioTestKind.FrontBack, 800).Contains("ARKA");
    bool dir8 = AudioPanTest.DurationOf(AudioTestKind.Directions8) == NativeDemoSignals.DurationMs(NativeDemo.Directions8) &&
                En(AudioTestKind.Directions8, 1.3, false) > 20 * En(AudioTestKind.Directions8, 1.3, true) &&
                AudioPanTest.Caption(AudioTestKind.Directions8, 1300).Contains("Sağ");
    // Through the real audio path (2x 220 Hz low-pass + extractor): front and back both strong, distinct pitch.
    (float amp, float hz) Through(double fromS)
    {
        var fx = new HapticFeatureExtractor(); long cur = 0;
        var sink = new SensaAudioSink(fx);
        var buf = new float[21120 * 2];   // 0.44 s: ends inside a 12 Hz front tap
        for (int i = 0; i < buf.Length / 2; i++) { var sm = AudioPanTest.Sample(AudioTestKind.FrontBack, fromS + i / 48000.0); buf[2 * i] = sm.L; buf[2 * i + 1] = sm.R; }
        sink.Accept(buf);
        fx.Take(ref cur, out var l, out _);
        return (Math.Max(Math.Max(l.P0, l.P1), Math.Max(l.P2, l.P3)), l.FrequencyHz);
    }
    var (fa, fh) = Through(0.0); var (ba, bh) = Through(0.75);
    // Unity input should track the authored .4-ish front and .308 back levels, not require the old 2.2 boost.
    frontBack &= fa > .35 && fa < .45 && ba > .27 && ba < .33 && fh > 120 && bh < 70;
    Console.WriteLine($"  front through audio path: amp {fa:F2} {fh:F0} Hz | back: amp {ba:F2} {bh:F0} Hz");
    Check(pingPong && frontBack && dir8,
        $"A sound tests: ping-pong = Native v2 Sol↔Sağ demo (x PreGain), hard L/R, front/back texture (front {front:F0}x at 190 Hz, back {back:F0}x at 45 Hz), 8 directions follow the demo");
}
{
    // Audio path ≡ Native path, frame by frame, for every value in the catalogue.
    var reps = AudioPathEquivalence.Catalogue().Select(s => AudioPathEquivalence.Compare(s)).ToList();
    var worst = reps.OrderByDescending(r => r.MaxAmpError + r.MaxHzError / 100 + r.TransientAmpError).First();
    bool same = reps.All(r => r.WithinTolerance());
    Check(same && reps.Count >= 90,
        $"A audio→haptics ≡ Native: {reps.Count} signals (tones 20–400 Hz × 4 levels, hits, demos, sound tests) — worst: {worst}");
    foreach (int rate in new[] { 44100, 96000 })
    {
        var rr = AudioPathEquivalence.Catalogue().Select(sg => AudioPathEquivalence.Compare(sg, 0, rate)).ToList();
        bool ok = rr.All(r => r.WithinTolerance(.01, 1, .02));
        Check(ok, $"A audio→haptics ≡ Native from a {rate} Hz device (sinc resampled): {rr.Count(r => r.WithinTolerance(.01, 1, .02))}/{rr.Count}, worst amp {rr.Max(r => r.MaxAmpError):F4}, worst Hz {rr.Max(r => r.MaxHzError):F2}");
    }
    double flat = Enumerable.Range(3, 38).Select(i => ButterworthLowPass.Magnitude(i * 10, HapticFeatureExtractor.BandLimitHz, HapticFeatureExtractor.BandLimitOrder)).Min();
    double cut = ButterworthLowPass.Magnitude(1000, HapticFeatureExtractor.BandLimitHz, HapticFeatureExtractor.BandLimitOrder);
    var bp = new ButterworthLowPass(HapticFeatureExtractor.BandLimitHz, HapticFeatureExtractor.BandLimitOrder);
    double peak400 = 0; for (int i = 0; i < 48000; i++) { double y = bp.Process((float)Math.Sin(2 * Math.PI * 400 * i / 48000.0)); if (i > 24000) peak400 = Math.Max(peak400, Math.Abs(y)); }
    Check(flat >= 0.99 && cut <= 0.01 && peak400 >= 0.98,
        $"A haptic band limit flat 30–400 Hz (min {flat:F3}, measured 400 Hz {peak400:F3}), 1 kHz cut ({cut:F4})");
}
Check(AudioSyncDelay.Resolve(-1, true) == AudioSyncDelay.BluetoothAutoMs &&
      AudioSyncDelay.Resolve(-1, false) == 0 && AudioSyncDelay.Resolve(150, true) == 150 &&
      AudioSyncDelay.Resolve(0, true) == 0 && AudioSyncDelay.Resolve(5000, false) == AudioSyncDelay.MaxMs &&
      new UserSettings().AudioSyncDelayMs == -1,
    "A audio sync delay: auto = Bluetooth 300 ms / wired 0, manual values honoured and clamped");
var quad = DualSenseAudioFormat.QuadPcm16();
Check(quad.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && quad.Channels == 4 &&
      quad.SampleRate == 48000 && quad.BitsPerSample == 16 &&
      DualSenseAudioFormat.ChannelMask(quad) == 0x33,
    "DualSense format is extensible PCM16 4ch/48k with quad mask 0x33 (rear pair = haptics), not NAudio's default 0xF");
var wake = DualSenseHidControl.BuildWakeHapticsReport(64);
bool wakeOk = wake.Length == 64 && wake[0] == 0x02 && wake[1] == 0x00 && wake[2] == 0x02 && wake[10] == 0x00;
for (int i = 3; i < wake.Length; i++) if (i != 10 && wake[i] != 0) wakeOk = false;
Check(wakeOk, "DualSense wake report: ID 0x02, no rumble emulation, only AllowAudioMute, MuteControl cleared, nothing else touched");
var dongleOnly = UsbGamepadClassifier.Classify(new[] { @"USB\VID_1532&PID_0A3F\d" });
Check(dongleOnly.WolverineUsbNode && !dongleOnly.WolverineWiredPcNode,
    "Dongle-only scan is present but not wired");
var absent = UsbGamepadClassifier.Classify(Array.Empty<string>());
Check(absent.ScanSucceeded && !absent.DualSenseUsbNode && !absent.WolverineUsbNode,
    "Empty scan means neither known USB device found, not automatically connected");
var bothPresent = UsbGamepadClassifier.Classify(new[]
    { @"USB\VID_054C&PID_0CE6\sony", @"USB\VID_1532&PID_0A3F\razer" });
Check(bothPresent.ScanSucceeded && bothPresent.DualSenseUsbNode &&
    bothPresent.WolverineUsbNode, "Two distinct USB VID/PID nodes count as present");
Check(!UsbGamepadPresence.Unknown("WMI blocked").ScanSucceeded,
    "Enumeration failure displayed as unverified, not disconnected");
Check(BehavioralEvidenceRules.CanSave(ComparisonTarget.Both,
    PhysicalSensation.Felt, PhysicalSensation.Felt,
    new TactileRatings(4, 3, 5), new TactileRatings(3, 2, 4)),
    "Paired real-feel rating with all three dimensions can be saved");
Check(!BehavioralEvidenceRules.CanSave(ComparisonTarget.DualSenseOnly,
    PhysicalSensation.Felt, PhysicalSensation.Felt,
    new TactileRatings(4, 4, 4), TactileRatings.Empty),
    "An unplayed Wolverine cannot be claimed as felt");
Check(!BehavioralEvidenceRules.CanSave(ComparisonTarget.Both,
    PhysicalSensation.Felt, PhysicalSensation.NotEvaluated,
    new TactileRatings(4, 4, 4), TactileRatings.Empty),
    "Both-target run requires a separate physical response for both controllers");
Check(BehavioralEvidenceRules.CanSave(ComparisonTarget.Both,
    PhysicalSensation.NotFelt, PhysicalSensation.Uncertain,
    TactileRatings.Empty, TactileRatings.Empty),
    "Explicit negative and uncertain results are recordable without fabricated scores");
Check(!BehavioralEvidenceRules.CanSave(ComparisonTarget.DualSenseOnly,
    PhysicalSensation.Felt, PhysicalSensation.NotEvaluated,
    new TactileRatings(4, null, 2), TactileRatings.Empty),
    "Incomplete 1-5 rating set is rejected");
var proof = new BehavioralEvidenceEntry(2, DateTimeOffset.UtcNow, HapticBehavior.Rain,
    ComparisonTarget.Both, true, true, true, "USB DualSense", 50, 65,
    PhysicalSensation.Felt, PhysicalSensation.Felt,
    new TactileRatings(4,4,4), new TactileRatings(3,3,3), "test");
var proof2 = proof with { Scene = HapticBehavior.Thunder,
    DsRatings = new TactileRatings(5, 5, 5), RwRatings = new TactileRatings(2,2,2) };
var summary = BehavioralEvidenceReport.Summarize(new [] { proof, proof2 });
Check(summary.PairedSceneCount == 2 && summary.SceneCount == 2 &&
    summary.DsMean == 4.5 && summary.RwMean == 2.5,
    "Subjective summary aggregates only separate matched two-device scores");
Check(BehavioralEvidenceReport.Summarize(Array.Empty<BehavioralEvidenceEntry>())
    .PairedSceneCount == 0, "No prior sessions never imply fidelity");

Check(HapticStereoWave.SampleRate == BehavioralHapticFixtures.SampleRate,
    "WAV exports retain 48000 Hz reference sample rate");
foreach (var clip in BehavioralHapticFixtures.All)
{
    byte[] raw = BehavioralHapticFixtures.Generate(clip.Id);
    byte[] rear = HapticStereoWave.ExtractRear(raw, HapticPcmFormat.Signed16);
    byte[] file = HapticStereoWave.BuildWav(rear, HapticPcmFormat.Signed16);
    Check(file.Length == HapticStereoWave.PcmWaveHeaderLength + raw.Length / 2,
        "WAV export size matches 2 original haptic channels");
    Check(System.Text.Encoding.ASCII.GetString(file, 0, 4) == "RIFF" &&
          System.Text.Encoding.ASCII.GetString(file, 8, 4) == "WAVE" &&
          System.Text.Encoding.ASCII.GetString(file, 36, 4) == "data" &&
          BitConverter.ToInt16(file, 20) == 1 &&
          BitConverter.ToInt16(file, 22) == 2 &&
          BitConverter.ToInt32(file, 24) == 48000,
        "WAV exported headers represent signed PCM16 stereo 48kHz");
    bool bitIdentical = true;
    for (int i = 0; i < raw.Length / 8; i++)
        if (!rear.AsSpan(i * 4, 4).SequenceEqual(raw.AsSpan(i * 8 + 4, 4)))
        { bitIdentical = false; break; }
    Check(bitIdentical &&
        file.AsSpan(HapticStereoWave.PcmWaveHeaderLength).SequenceEqual(rear),
        "Rear L/R PCM source bits are untouched by Wwise WAV export");
}
byte[] floatSource = new byte[16 * 3];
BitConverter.GetBytes(0.71875f).CopyTo(floatSource, 8);
BitConverter.GetBytes(-0.1234f).CopyTo(floatSource, 12);
BitConverter.GetBytes(-0.999f).CopyTo(floatSource, 24);
byte[] floatRear = HapticStereoWave.ExtractRear(floatSource, HapticPcmFormat.Float32);
byte[] floatWav = HapticStereoWave.BuildWav(floatRear, HapticPcmFormat.Float32);
Check(BitConverter.ToInt16(floatWav, 20) == 3 &&
    BitConverter.ToInt16(floatWav, 34) == 32 &&
    System.Text.Encoding.ASCII.GetString(floatWav, 36, 4) == "fact" &&
    BitConverter.ToInt32(floatWav, 44) == 3 &&
    System.Text.Encoding.ASCII.GetString(floatWav, 48, 4) == "data" &&
    floatWav.AsSpan(HapticStereoWave.FloatWaveHeaderLength).SequenceEqual(floatRear),
    "Float32 stereo WAV preserves source float bits with fact chunk");
var rec = new HapticStereoRecorder(HapticPcmFormat.Signed16, 1);
byte[] tiny = new byte[8*3];
tiny[4] = 0x43; tiny[5] = 0x21;
rec.Accept(tiny.AsSpan(0, 8));
rec.Accept(tiny.AsSpan(8, 16));
Check(rec.CapturedFrames == 3 && !rec.IsFull,
    "Opt-in recorder accumulates original PCM in bounded RAM");
byte[] recorded = rec.End();
Check(recorded.AsSpan(HapticStereoWave.PcmWaveHeaderLength)
    .SequenceEqual(HapticStereoWave.ExtractRear(tiny, HapticPcmFormat.Signed16)),
    "RAM recording exports exact original stereo rear PCM");
try { rec.Accept(tiny); rec.End(); throw new Exception("Recorder finalized twice"); }
catch (InvalidOperationException) { Console.WriteLine("PASS: recorder cannot finalize twice"); }
var maxRecorder = new HapticStereoRecorder(HapticPcmFormat.Signed16, 1);
maxRecorder.Accept(new byte[48000*8+8]);
Check(maxRecorder.IsFull && maxRecorder.CapturedFrames == 48000,
    "WAV recorder clamps input to explicit one-second sample budget");
maxRecorder.Abandon();
Check(!maxRecorder.HasSamples,
    "Abandon clears recording buffers and sensitive sample material");
try { HapticStereoWave.ExtractRear(new byte[7], HapticPcmFormat.Signed16);
    throw new Exception("Incomplete frames silently accepted"); }
catch (ArgumentException) { Console.WriteLine("PASS: incomplete interleaved frames rejected"); }
try { new HapticStereoRecorder(HapticPcmFormat.Float32, 11);
    throw new Exception("Recording window can exceed hard limit"); }
catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS: capture length <=10s"); }
Console.WriteLine("Stereo 48k WAV export and bounded opt-in recording passed.");

// Explicit capture-buffer requests: legacy WASAPI loopback formerly used 100ms.
Check(WasapiLoopbackBufferPolicy.DefaultRequestMs == 20 &&
      WasapiLoopbackBufferPolicy.Validate(10) == 10 &&
      WasapiLoopbackBufferPolicy.Validate(20) == 20 &&
      WasapiLoopbackBufferPolicy.Validate(50) == 50 &&
      WasapiLoopbackBufferPolicy.Validate(100) == 100,
    "WASAPI low-latency buffer requests are explicit, validated and default to 20ms");
foreach (int unsupportedMs in new[] { 0, 1, 5, 15, 30, 40, 99, 101, 1000 })
{
    bool rejected = false;
    try { WasapiLoopbackBufferPolicy.Validate(unsupportedMs); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "Unsupported WASAPI loopback buffer value " + unsupportedMs + "ms rejected");
}

// ===== Loss-visible raw 48k stereo transport (NO Razer output or PCM DSP) =====
var ring = new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 20);
Check(ring.CapacityFrames == 960 && ring.Snapshot().CapacityMs == 20,
    "Raw PCM ring is capped to exactly 20 ms of 48k stereo source frames");
byte[] sourceFrames = new byte[1200 * 8];
for (int i = 0; i < 1200; ++i)
{
    BitConverter.GetBytes(unchecked((short)(i + 1))).CopyTo(sourceFrames, i * 8 + 4);
    BitConverter.GetBytes(unchecked((short)-(i + 1))).CopyTo(sourceFrames, i * 8 + 6);
    sourceFrames[i * 8] = 0xCA; // front/speaker must NEVER be forwarded
}
byte[] expectedStereo = HapticStereoWave.ExtractRear(
    sourceFrames, HapticPcmFormat.Signed16);
ring.WriteFourChannel(sourceFrames);
var state = ring.Snapshot();
Check(state.OfferedFrames == 1200 && state.DroppedFrames == 240 &&
      state.BufferedFrames == 960 && state.UnderrunFrames == 0,
    "Oversized input drops exactly the oldest frames and reports every lost frame");
byte[] latestStereo = new byte[960 * 4];
Check(ring.ReadStereo(latestStereo) == 960 &&
      latestStereo.AsSpan().SequenceEqual(expectedStereo.AsSpan(240 * 4)),
    "Overflow preserves the newest original PCM16 rear-channel bits and order");
byte[] zeroPadded = Enumerable.Repeat((byte)0xFD, 12 * 4).ToArray();
Check(ring.ReadStereo(zeroPadded) == 0 &&
      zeroPadded.All(b => b == 0) && ring.Snapshot().UnderrunFrames == 12,
    "Empty consumer read zero-fills instead of replaying stale original haptic frames");
state = ring.Snapshot();
Check(state.OfferedFrames == state.DeliveredFrames +
      state.DroppedFrames + state.BufferedFrames,
    "Every offered frame is accounted for as delivered, dropped or buffered");
ring.Reset();
state = ring.Snapshot();
Check(state.OfferedFrames == 0 && state.UnderrunFrames == 0 &&
      state.DroppedFrames == 0 && state.BufferedFrames == 0,
    "Disconnect/reset clears buffer and all local trace counters");
var chunkRing = new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 20);
chunkRing.WriteFourChannel(sourceFrames.AsSpan(0, 400 * 8));
byte[] firstChunk = new byte[100 * 4];
Check(chunkRing.ReadStereo(firstChunk) == 100 &&
      firstChunk.AsSpan().SequenceEqual(expectedStereo.AsSpan(0, 100 * 4)),
    "First bounded drain returns unchanged stereo frames");
chunkRing.WriteFourChannel(sourceFrames.AsSpan(400 * 8, 500 * 8));
byte[] wrappedChunk = new byte[800 * 4];
Check(chunkRing.ReadStereo(wrappedChunk) == 800 &&
      wrappedChunk.AsSpan().SequenceEqual(expectedStereo.AsSpan(100 * 4, 800 * 4)),
    "Crossing ring write/read boundaries preserves exact left/right PCM and order");
var chunkStats = chunkRing.Snapshot();
Check(chunkStats.OfferedFrames == 900 && chunkStats.DeliveredFrames == 900 &&
      chunkStats.DroppedFrames == 0 && chunkStats.BufferedMs == 0,
    "No false drops in a near-capacity burst+drain sequence");
var evictRing = new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 20);
evictRing.WriteFourChannel(sourceFrames.AsSpan(0, 700 * 8));
evictRing.WriteFourChannel(sourceFrames.AsSpan(700 * 8, 500 * 8));
Check(evictRing.Snapshot().DroppedFrames == 240 &&
      evictRing.ReadStereo(latestStereo) == 960 &&
      latestStereo.AsSpan().SequenceEqual(expectedStereo.AsSpan(240 * 4)),
    "Overflow from previously buffered frames evicts oldest without mixing speaker channels");
try { ring.WriteFourChannel(new byte[9]); throw new Exception("Partial 4ch accepted"); }
catch (ArgumentException) { Console.WriteLine("PASS: malformed 4ch frame rejected"); }
try { ring.ReadStereo(new byte[3]); throw new Exception("Partial stereo accepted"); }
catch (ArgumentException) { Console.WriteLine("PASS: malformed stereo frame rejected"); }
try { new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 510);
    throw new Exception("Unbounded ring accepted"); }
catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS: ring bounded to 500ms"); }
var floatTransport = new RealtimeStereoPcmTransport(HapticPcmFormat.Float32, 20);
var floatInputs = new byte[3 * 16];
BitConverter.GetBytes(int.MinValue).CopyTo(floatInputs, 8); // negative zero
BitConverter.GetBytes(0x7fc00001).CopyTo(floatInputs, 12); // NaN payload kept as SOURCE bytes
BitConverter.GetBytes(0x7f800000).CopyTo(floatInputs, 24); // +Inf kept as SOURCE bytes
BitConverter.GetBytes(0x3f99999a).CopyTo(floatInputs, 28);
floatTransport.WriteFourChannel(floatInputs);
byte[] floatCopied = new byte[3 * 8];
Check(floatTransport.ReadStereo(floatCopied) == 3 &&
      floatCopied.AsSpan().SequenceEqual(
          HapticStereoWave.ExtractRear(floatInputs, HapticPcmFormat.Float32)),
    "Float32 raw transport preserves exact source bit patterns including IEEE payloads");
Check(StereoPcmDiscardProbe.ChunkMs == 5 &&
      StereoPcmDiscardProbe.ChunkFrames == 240 &&
      RealtimeStereoPcmTransport.SampleRate * StereoPcmDiscardProbe.ChunkMs / 1000 == 240,
    "Event-driven diagnostic drains 240 original 48k frames per 5ms chunk, NOT a 50Hz envelope");

// Latency accounting uses high-resolution monotonic callback and dequeue ticks;
// these do NOT include game audio production, WASAPI capture, driver or motor.
long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
long FiveMsTicks = System.Diagnostics.Stopwatch.Frequency / 200;
long TenMsTicks = System.Diagnostics.Stopwatch.Frequency / 100;
var ageRing = new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 40);
ageRing.WriteFourChannel(sourceFrames.AsSpan(0, 8), tickStart);
ageRing.WriteFourChannel(sourceFrames.AsSpan(8, 8), tickStart + FiveMsTicks);
var queuedBefore = ageRing.Snapshot(tickStart + TenMsTicks);
Check(Math.Abs(queuedBefore.OldestBufferedAgeMs - 10.0) < 0.2 &&
      Math.Abs(queuedBefore.LastSourceGapMs - 5.0) < 0.2 &&
      Math.Abs(queuedBefore.SinceLastSourceMs - 5.0) < 0.2 &&
      queuedBefore.PeakBufferedFrames == 2,
    "Exact callback gaps and oldest queue residence are reported independently from capacity");
byte[] agedStereo = new byte[2 * 4];
Check(ageRing.ReadStereo(agedStereo, tickStart + TenMsTicks) == 2 &&
      agedStereo.AsSpan().SequenceEqual(expectedStereo.AsSpan(0, agedStereo.Length)),
    "Latency metrics never rewrite original source stereo sample bits");
var ageSnapshot = ageRing.Snapshot(tickStart + TenMsTicks);
Check(Math.Abs(ageSnapshot.MaxTransitMs - 10.0) < 0.2 &&
      Math.Abs(ageSnapshot.LastTransitMs - 5.0) < 0.2 &&
      Math.Abs(ageSnapshot.MeanTransitMs - 7.5) < 0.2 &&
      ageSnapshot.OldestBufferedAgeMs == 0 &&
      ageSnapshot.DeliveredFrames == 2 &&
      ageSnapshot.DroppedFrames == 0,
    "Average/peak callback-to-reader PCM residence measured with deterministic monotonic ticks");
ageRing.Reset();
var ageAfterReset = ageRing.Snapshot(tickStart + TenMsTicks);
Check(ageAfterReset.MeanTransitMs == 0 && ageAfterReset.MaxTransitMs == 0 &&
      ageAfterReset.MaxSourceGapMs == 0 &&
      ageAfterReset.PeakBufferedFrames == 0,
    "Stop/endpoint switch also removes prior latency and callback-jitter evidence");

// Event-driven delivery: no fixed 20ms timer and no artificial underruns
// from consuming silence when Windows simply has not issued a callback.
var signalRing = new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 40);
var signalProbe = new StereoPcmDiscardProbe(signalRing);
try
{
    signalRing.WriteFourChannel(sourceFrames.AsSpan(0, 240 * 8));
    signalProbe.NotifyDataArrived();
    Check(System.Threading.SpinWait.SpinUntil(
        () => signalRing.Snapshot().DeliveredFrames == 240,
        TimeSpan.FromSeconds(3)),
        "Real audio-arrival notification wakes the 5ms PCM reader without a periodic timer");
    var signalStatus = signalRing.Snapshot();
    Check(signalStatus.OfferedFrames == 240 && signalStatus.DeliveredFrames == 240 &&
          signalStatus.UnderrunFrames == 0 && signalStatus.DroppedFrames == 0 &&
          signalStatus.BufferedFrames == 0,
        "Event-driven null sink does not invent underruns or drop the arrived source PCM");
    Check(signalProbe.WakeCount >= 1 && signalProbe.DrainedChunks >= 1,
        "Notifier and source-triggered drain both executed");
}
finally { signalProbe.Dispose(); }
Check(signalRing.Snapshot().BufferedFrames == 0 &&
      signalRing.Snapshot().OfferedFrames == 0,
    "Event-driven probe cancellation cleans all in-memory stereo source data");

// Concurrency stress: independently scheduled writer/reader may incur loss but
// must not generate torn stereo pairs or unaccounted source frames.
var concurrencyRing = new RealtimeStereoPcmTransport(HapticPcmFormat.Signed16, 40);
byte[] concurrencyInput = new byte[96 * 8];
for (int i = 0; i < 96; i++)
{
    short left = (short)(100 + i);
    BitConverter.GetBytes(left).CopyTo(concurrencyInput, i * 8 + 4);
    BitConverter.GetBytes((short)-left).CopyTo(concurrencyInput, i * 8 + 6);
}
int torn = 0;
var writerTask = Task.Run(() =>
{
    for (int i = 0; i < 200; ++i)
        concurrencyRing.WriteFourChannel(concurrencyInput);
});
var readerTask = Task.Run(() =>
{
    byte[] output = new byte[96 * 4];
    for (int i = 0; i < 200; ++i)
    {
        int validFrames = concurrencyRing.ReadStereo(output);
        for (int j = 0; j < validFrames; ++j)
        {
            short left = BitConverter.ToInt16(output, j * 4);
            short right = BitConverter.ToInt16(output, j * 4 + 2);
            if (left < 100 || left > 195 || right != -left)
                Interlocked.Increment(ref torn);
        }
        if (output.AsSpan(validFrames * 4).IndexOfAnyExcept((byte)0) != -1)
            Interlocked.Increment(ref torn);
    }
});
Task.WaitAll(writerTask, readerTask);
var concurrentStats = concurrencyRing.Snapshot();
Check(torn == 0 &&
      concurrentStats.OfferedFrames == concurrentStats.DeliveredFrames +
          concurrentStats.DroppedFrames + concurrentStats.BufferedFrames,
    "Concurrent producer/consumer never tears stereo PCM samples or conceals dropped frames");
concurrencyRing.Reset();
Console.WriteLine("Event-driven low-latency 48k PCM transport, residence metering, identity and concurrency passed.");

Console.WriteLine("USB PnP, subjective A/B and high-fidelity stereo WAV contract suite passed.");

// Game-free channel source and the shared production ingress: originals are never scaled.
foreach (var scene in DualSenseChannelFixtures.Scenes)
{
    var pcm = DualSenseChannelFixtures.Build(scene);
    string hash = DualSenseChannelFixtures.Sha256(pcm);
    Check(pcm.SequenceEqual(DualSenseChannelFixtures.Build(scene)), "Channel lab deterministic source: " + scene.Id);
    var feature = new HapticFeatureExtractor();
    long cursor = 0;
    float maxLeft = 0, maxRight = 0;
    double leftHz = 0, rightHz = 0;
    for (int offset = 0; offset < pcm.Length; offset += 240 * 8)
    {
        NativePcmIngress.Push(pcm.AsSpan(offset, Math.Min(240 * 8, pcm.Length - offset)),
            FourChannelHapticAnalyzer.PcmEncoding.Pcm16, feature);
        feature.Take(ref cursor, out var left, out var right);
        maxLeft = Math.Max(maxLeft, left.Peak); maxRight = Math.Max(maxRight, right.Peak);
        if (offset / 8 is > 48000 and < 96000) { leftHz = Math.Max(leftHz, left.FrequencyHz); rightHz = Math.Max(rightHz, right.FrequencyHz); }
    }
    Check(hash == DualSenseChannelFixtures.Sha256(pcm) && feature.CompletedSlots == pcm.Length / 8 / 48,
        "Shared ingress preserves original bytes and accounts for all source slots: " + scene.Id);
    if (scene.Id is "speaker" or "silence")
        Check(maxLeft == 0 && maxRight == 0, "Negative control cannot reach either haptic channel: " + scene.Id);
    if (scene.Id == "left") Check(maxLeft > .1f && maxRight == 0, "Channel 3 drives only left features");
    if (scene.Id == "right") Check(maxRight > .1f && maxLeft == 0, "Channel 4 drives only right features");
    if (scene.Id == "stereo") Check(Math.Abs(leftHz - 80) < 5 && Math.Abs(rightHz - 190) < 5,
        "Simultaneous stereo source retains different left/right frequencies");
}
bool rejectedPartialIngress = false;
try { NativePcmIngress.Push(new byte[9], FourChannelHapticAnalyzer.PcmEncoding.Pcm16, new HapticFeatureExtractor()); }
catch (ArgumentException) { rejectedPartialIngress = true; }
Check(rejectedPartialIngress, "Shared production ingress rejects partial quad frames");
Console.WriteLine("DualSense channel lab source identity, isolation, negative controls and production ingress passed.");

var labStereoPcm = DualSenseChannelFixtures.Build(DualSenseChannelFixtures.Scenes.Single(s => s.Id == "stereo"));
var levelZeroAnalysis = DualSenseChannelFixtures.Analyze(labStereoPcm, 0, 140);
var levelFullAnalysis = DualSenseChannelFixtures.Analyze(labStereoPcm, 100, 140);
Check(levelZeroAnalysis.Frames == 144000 && levelZeroAnalysis.SpeakerRms == 0 &&
    levelZeroAnalysis.LeftRms > .1f && levelZeroAnalysis.RightRms > .1f &&
    levelZeroAnalysis.LeftRms == levelFullAnalysis.LeftRms && levelZeroAnalysis.RightRms == levelFullAnalysis.RightRms,
    "Lab analysis counts all PCM frames and never scales source measurements with Wolverine gain");
var labStrongPcm = DualSenseChannelFixtures.Build(DualSenseChannelFixtures.Scenes.Single(s => s.Id == "demo:Purr"));
Check(DualSenseChannelFixtures.Analyze(labStrongPcm, 100, 300).SaturatedFeaturePoints > 0 &&
    DualSenseChannelFixtures.Analyze(labStrongPcm, 0, 300).SaturatedFeaturePoints == 0,
    "Lab separately discloses predicted Wolverine amplitude saturation and respects zero output");

var endpointFixture = DualSenseChannelFixtures.Build(DualSenseChannelFixtures.Scenes.Single(s => s.Id == "stereo"));
var endpointOriginal = (byte[])endpointFixture.Clone();
var floatEndpointFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 4);
var renderedFloat = EndpointFixturePlayer.EncodeSource(endpointFixture, floatEndpointFormat);
Check(endpointFixture.SequenceEqual(endpointOriginal) && Enumerable.Range(0, endpointFixture.Length / 2).All(i =>
    BitConverter.ToSingle(renderedFloat, i * 4) == BitConverter.ToInt16(endpointFixture, i * 2) / 32768f),
    "Explicit Shared source represents every original PCM16 value exactly in Float32 without changing the original");
foreach (var wrongFormat in new NAudio.Wave.WaveFormat[] { new(48000, 16, 2), new(44100, 16, 4), new NAudio.Wave.WaveFormatExtensible(48000, 16, 4) })
{
    bool rejected = false;
    try { EndpointFixturePlayer.EncodeSource(endpointFixture, wrongFormat); }
    catch (NotSupportedException) { rejected = true; }
    Check(rejected, "Endpoint source rejects stereo, wrong sample rate or centre/LFE mask");
}
var expectedRearFloat = HapticStereoWave.ExtractRear(renderedFloat, HapticPcmFormat.Float32);
var laggedRear = new byte[expectedRearFloat.Length + 123 * 8 + 480 * 8];
expectedRearFloat.CopyTo(laggedRear, 123 * 8);
var exactEndpointComparison = EndpointSignalVerification.Compare(endpointFixture,
    HapticStereoWave.BuildWav(laggedRear, HapticPcmFormat.Float32), HapticPcmFormat.Float32);
Check(exactEndpointComparison.ExactActiveSamples && exactEndpointComparison.CompleteActiveRegion &&
    exactEndpointComparison.AlignmentFrames == 123 && exactEndpointComparison.RawRmsError == 0,
    "Endpoint verification finds real sample offset and checks exact original active samples");
for (int i = 0; i < laggedRear.Length; i += 4)
    BitConverter.TryWriteBytes(laggedRear.AsSpan(i, 4), BitConverter.ToSingle(laggedRear, i) * .5f);
var scaledEndpointComparison = EndpointSignalVerification.Compare(endpointFixture,
    HapticStereoWave.BuildWav(laggedRear, HapticPcmFormat.Float32), HapticPcmFormat.Float32);
Check(!scaledEndpointComparison.ExactActiveSamples && scaledEndpointComparison.Correlation > .99999 &&
    Math.Abs(scaledEndpointComparison.ObservedGain - .5) < 1e-6 && scaledEndpointComparison.RawRmsError > 0,
    "Endpoint comparison reports Windows gain instead of normalizing it away");
var droppedRight = (byte[])expectedRearFloat.Clone();
for (int i = 4; i < droppedRight.Length; i += 8) Array.Clear(droppedRight, i, 4);
Check(!EndpointSignalVerification.Compare(endpointFixture, HapticStereoWave.BuildWav(droppedRight, HapticPcmFormat.Float32),
    HapticPcmFormat.Float32).ExactActiveSamples, "Endpoint verification detects missing opposite channel rather than declaring stereo success");
var finalizedRecorder = new HapticStereoRecorder(HapticPcmFormat.Signed16, 1);
finalizedRecorder.Accept(endpointFixture.AsSpan(0, 240 * 8));
var finalizedWave = finalizedRecorder.End();
Check(finalizedRecorder.CapturedFrames == 0 && finalizedWave.Length == 44 + 240 * 4,
    "Finalizing source-only comparison clears private capture RAM while returning unchanged WAV data");
Console.WriteLine("Independent endpoint sender encoding and measured capture-comparison contracts passed.");
AudioScenarioContracts.Run(Check);
SpectralContracts.Run(Check);

{
    SideFeatures F(float hz) => new() { P0 = .2f, P1 = .2f, P2 = .2f, P3 = .2f, FrequencyHz = hz };
    var a = F(120); var b = F(0);
    var lostHz = AudioPathEquivalence.CompareFrames("lost pitch", new[] { (a, default(SideFeatures)) }, new[] { (b, default(SideFeatures)) });
    Check(!lostHz.WithinTolerance() && lostHz.FrequencyAvailabilityMismatches == 1, "Equivalence rejects a missing frequency instead of skipping its error");
    var lostFrame = AudioPathEquivalence.CompareFrames("lost frame", new[] { (a, a), (a, a) }, new[] { (a, a) });
    Check(!lostFrame.WithinTolerance() && lostFrame.MissingFrames == 1, "Equivalence rejects truncation rather than comparing only the shared prefix");
    a.TransientCount = 2; a.T0Position = .1f; a.T0Amplitude = .3f; a.T1Position = .8f; a.T1Amplitude = .2f;
    b = a; b.T0Position = .9f; b.T1Position = .1f; b.T1Amplitude = 1;
    var wrongHits = AudioPathEquivalence.CompareFrames("wrong hits", new[] { (a, a) }, new[] { (b, b) });
    Check(!wrongHits.WithinTolerance() && wrongHits.MaxTransientPositionError > .7 && wrongHits.TransientAmpError > .7,
        "Equivalence checks both transient timings and the second transient amplitude");
    b = a; b.P0 = float.NaN;
    Check(!AudioPathEquivalence.CompareFrames("invalid", new[] { (a, a) }, new[] { (b, b) }).WithinTolerance(), "Nonfinite features cannot pass equivalence");
    Check(!AudioPathEquivalence.CompareFrames("empty", Array.Empty<(SideFeatures, SideFeatures)>(),
        Array.Empty<(SideFeatures, SideFeatures)>()).WithinTolerance(), "No source frames cannot pass equivalence");
    var invalidSilent = new SideFeatures { FrequencyHz = float.NaN };
    Check(!AudioPathEquivalence.CompareFrames("invalid silent pitch", new[] { (invalidSilent, invalidSilent) },
        new[] { (default(SideFeatures), default(SideFeatures)) }).WithinTolerance(), "Silent frames cannot conceal nonfinite frequency fields");
    var ending = new AudioPathEquivalence.Signal("late ending", .05, t => t >= .04 ? (.4, 0) : (0, 0));
    var quietEnding = new AudioPathEquivalence.Signal("quiet ending", .05, _ => (0, 0));
    var tailReport = AudioPathEquivalence.CompareFrames("partial ending", AudioPathEquivalence.Frames(ending, false),
        AudioPathEquivalence.Frames(quietEnding, false));
    Check(tailReport.Frames == 2 && !tailReport.WithinTolerance(), "A partial final 33ms frame is compared rather than silently discarded");
    var polarity = HapticScenarioAudit.PhaseLoss();
    Check(polarity.RawRmsDifference > .7 && polarity.Steady.WithinTolerance(),
        "Independent PCM oracle exposes phase loss even when steady parametric features match");
}
{
    var sink = new SensaAudioSink(new HapticFeatureExtractor(), SensaAudioSink.LegacyPreGain);
    sink.Accept(new float[] { .9f, -.9f, .2f, -.2f });
    Check(sink.InputSamples == 4 && sink.ClippedSamples == 2,
        "Audio pre-gain clipping is counted rather than concealed by equalized fixtures");
    var unity = new SensaAudioSink(new HapticFeatureExtractor());
    unity.Accept(new float[] { 1, -1, .9f, -.9f });
    Check(unity.InputGain == 1 && unity.ClippedSamples == 0, "Default unity audio input accepts full scale without pre-gain clipping");
    bool partial = false; try { unity.Accept(new float[1]); } catch (ArgumentException) { partial = true; }
    Check(partial, "Audio input rejects a partial stereo frame explicitly");
    var invalidFx = new HapticFeatureExtractor(); var invalidSink = new SensaAudioSink(invalidFx);
    invalidSink.Accept(Enumerable.Repeat(new[] { float.PositiveInfinity, float.NaN }, 96).SelectMany(x => x).ToArray());
    long invalidCursor = 0; invalidFx.Take(ref invalidCursor, out var invalidL, out var invalidR);
    Check(invalidSink.NonFiniteSamples == 192 && invalidL.Peak == 0 && invalidR.Peak == 0,
        "NaN/Infinity audio becomes counted silence instead of Infinity becoming full-scale vibration");
}
{
    var sink = new TestAudioSink();
    using var delivery = new AudioSinkDelivery(sink);
    delivery.Enqueue(new float[] { .1f, 0 }, 0, 300);
    delivery.Pump(250);
    Check(sink.AcceptCalls == 0 && sink.SilenceCalls == 0, "Watchdog preserves an intentionally delayed audio tail");
    delivery.Pump(300); delivery.Pump(549);
    Check(sink.AcceptCalls == 1 && sink.SilenceCalls == 0, "Fresh output is not prematurely stopped");
    delivery.Pump(550); delivery.Pump(1000);
    Check(sink.SilenceCalls == 1, "Independent 250ms watchdog zeros a stale sink exactly once");
    delivery.Dispose(); int writes = sink.AcceptCalls;
    delivery.Enqueue(new float[] { .5f, .5f }, 1000, 0); delivery.Pump(2000);
    Check(sink.AcceptCalls == writes && delivery.QueuedSamples == 0, "Stopped delivery cannot issue a late vibration command");
}
{
    var sink = new TestAudioSink(); using var delivery = new AudioSinkDelivery(sink);
    delivery.Enqueue(new float[AudioSinkDelivery.MaximumQueuedSamples], 0, 1000);
    bool overflow = false;
    try { delivery.Enqueue(new float[2], 1, 1000); } catch (InvalidOperationException) { overflow = true; }
    Check(overflow && sink.SilenceCalls == 1 && delivery.QueuedSamples == 0 && delivery.DroppedSamples > 0,
        "Audio backlog is bounded and fails explicitly with output stopped and pooled PCM released");
}
{
    var sink = new TestAudioSink { ThrowOnAccept = true }; using var delivery = new AudioSinkDelivery(sink);
    delivery.Enqueue(new float[] { .5f, .5f }, 0, 0);
    bool failed = false; try { delivery.Pump(0); } catch (InvalidOperationException) { failed = true; }
    Check(failed && sink.SilenceCalls > 0 && delivery.QueuedSamples == 0, "Sink failure clears retained data and silences the output");
}
{
    var fx = new HapticFeatureExtractor(); var sink = new SensaAudioSink(fx); long cur = 0;
    sink.Accept(Enumerable.Repeat(.2f, 960).ToArray()); fx.Take(ref cur, out _, out _);
    sink.Silence(); fx.Take(ref cur, out var left, out var right);
    Check(left.Peak == 0 && right.Peak == 0 && left.TransientCount == 0 && right.TransientCount == 0,
        "Audio source stop publishes silence without replaying old feature history");
}
{
    // Soft limiter: unchanged at gain <= 1, smooth and ordered above.
    bool identity = Enumerable.Range(0, 101).All(i => Math.Abs(HapticLimiter.Apply(i / 100.0, 1) - i / 100.0) < 1e-12) &&
                    HapticLimiter.Apply(1.5, 1) == 1 && HapticLimiter.Apply(double.NaN, 3) == 0 && HapticLimiter.Apply(-1, 3) == 0;
    double prev = -1; bool monotonic = true, bounded = true;
    for (int i = 0; i <= 2000; i++) { double y = HapticLimiter.Apply(i / 1000.0, 3); monotonic &= y > prev || i == 0; bounded &= y < 1; prev = y; }
    double a = HapticLimiter.Apply(0.33, 3), b = HapticLimiter.Apply(0.5, 3), c = HapticLimiter.Apply(1.0, 3);
    double slope = (HapticLimiter.Apply(HapticLimiter.Knee / 3 + 1e-6, 3) - HapticLimiter.Apply(HapticLimiter.Knee / 3, 3)) / 3e-6;
    Check(identity && monotonic && bounded && b - a > 0.05 && c > b && Math.Abs(slope - 1) < 0.01 && Math.Abs(HapticLimiter.Apply(0.2, 3) - 0.6) < 1e-9,
        $"Soft limiter: identity at gain<=1, monotonic, <1, slope 1 at knee; 300%: 0.33→{a:F2}, 0.5→{b:F2}, 1.0→{c:F3}");
}
{
    // Hybrid policy: steady mixed sound -> multiband after 2 steady frames; hits and single tones stay standard.
    SideFeatures Steady(float v) => new() { P0 = v, P1 = v, P2 = v, P3 = v, FrequencyHz = 130 };
    var mixed = new HapticSpectrum(new(80, .25f, 0), new(190, .25f, 0), default, 2);
    var single = new HapticSpectrum(new(80, .5f, 0), default, default, 1);
    var weakSecond = new HapticSpectrum(new(80, .5f, 0), new(190, .05f, 0), default, 2);
    var p = new HybridOutputPolicy(); double t = 0, dt = 0.033;
    var seq = new List<bool>(); for (int i = 0; i < 6; i++) { seq.Add(p.Decide(Steady(.5f), mixed, true, t)); t += dt; }
    bool enters = !seq[0] && !seq[1] && seq[3] && seq[4] && seq[5];
    var hit = Steady(.5f); hit.TransientCount = 1; hit.T0Amplitude = .9f;
    bool leavesOnHit = !p.Decide(hit, mixed, true, t);
    var p2 = new HybridOutputPolicy(); bool singleStays = true; t = 0;
    for (int i = 0; i < 6; i++) { singleStays &= !p2.Decide(Steady(.5f), single, true, t) && !p2.Decide(Steady(.5f), weakSecond, true, t); t += dt; }
    var p3 = new HybridOutputPolicy(); var rising = new SideFeatures { P0 = .1f, P1 = .3f, P2 = .6f, P3 = .9f };
    bool risingStays = true; t = 0; for (int i = 0; i < 6; i++) { risingStays &= !p3.Decide(rising, mixed, true, t); t += dt; }
    var p4 = new HybridOutputPolicy(); bool silenceResets = true; t = 0;
    for (int i = 0; i < 4; i++) { p4.Decide(Steady(.5f), mixed, true, t); t += dt; }
    silenceResets &= !p4.Decide(default, mixed, true, t) && !p4.Decide(Steady(.5f), mixed, true, t + dt);
    var sf = HybridOutputPolicy.StandardFrame(Steady(.5f), new HapticSpectrum(new(80, .2f, 0), new(190, .3f, 0), default, 2), true);
    var sfSingle = HybridOutputPolicy.StandardFrame(Steady(.5f), single, true);
    bool standardPitch = sf.FrequencyHz == 190 && sfSingle.FrequencyHz == 130 && HybridOutputPolicy.StandardFrame(Steady(.5f), mixed, false).FrequencyHz == 130;
    var scaled = HybridOutputPolicy.ScaleToEnvelope(mixed, .8f);
    bool scaleOk = Math.Abs(scaled.AmplitudeSum - .8f) < 1e-6 && Math.Abs(scaled.A.Amplitude - scaled.B.Amplitude) < 1e-6 && scaled.A.FrequencyHz == 80 && scaled.B.FrequencyHz == 190;
    Check(enters && leavesOnHit && singleStays && risingStays && silenceResets && scaleOk && standardPitch,
        "Hybrid policy: steady 80+190 → multiband after settle + 2 steady frames, hit/rising/single/weak-second stay standard, silence resets, strength = fast envelope split by spectral share");
}
{
    // Hybrid on the real extractor + spectrum: a steady mixed 80+190 Hz scene must stay multiband (no toggling),
    // a 120 Hz hit train must stay standard.
    (int multi, int std, int switches) Run(Func<double, double> wave, double seconds)
    {
        var fx = new HapticFeatureExtractor(); fx.EnableSpectralAnalysis(true);
        var policy = new HybridOutputPolicy(); long cur = 0; int m = 0, s = 0, sw = 0; bool last = false;
        var buf = new float[1584 * 2];
        for (int frame = 0; frame * 1584 < seconds * 48000; frame++)
        {
            for (int i = 0; i < 1584; i++) { buf[2 * i] = (float)wave((frame * 1584 + i) / 48000.0); buf[2 * i + 1] = 0; }
            fx.PushStereo(buf);
            fx.Take(ref cur, out var l, out _);
            bool have = fx.TryTakeSpectrum(out var ls, out _);
            bool d = policy.Decide(l, ls, have, frame * 0.033);
            if (l.Peak > HybridOutputPolicy.ActiveThreshold) { if (d) m++; else s++; }
            if (frame > 0 && d != last) sw++; last = d;
        }
        return (m, s, sw);
    }
    var mixedRun = Run(t => t < 0.2 ? 0 : 0.25 * (Math.Sin(2 * Math.PI * 80 * t) + Math.Sin(2 * Math.PI * 190 * t)), 1.8);
    var hitsRun = Run(t => (t % .25) < .02 ? .5 * Math.Sin(2 * Math.PI * 120 * (t % .25)) : 0, 1.8);
    Console.WriteLine($"  hybrid offline: mixed multi {mixedRun.multi}/std {mixedRun.std}/switches {mixedRun.switches}; hits multi {hitsRun.multi}/std {hitsRun.std}");
    Check(mixedRun.switches <= 1 && mixedRun.multi >= mixedRun.std * 4 && hitsRun.multi == 0,
        $"Hybrid on real extractor: steady 80+190 stays multiband ({mixedRun.multi} multi / {mixedRun.std} std frames, {mixedRun.switches} switch), hit train stays standard");
}
{
    // Own Sensa frame encoder + HID report builder vs bytes captured from Synapse on the USB bus (2026-11-08).
    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
    var frame = new byte[SensaFrameEncoder.FrameLength];
    double[] one = { 1, 1, 1, 1 }, zero = { 0, 0, 0, 0 }, quarter = { .25, .25, .25, .25 };
    double p60 = SensaFrameEncoder.PitchOf(60), p50 = SensaFrameEncoder.PitchOf(50);
    SensaFrameEncoder.Encode(one, p60, zero, p50, frame);
    bool left = Hex(frame) == "79fc57e2bf15f8a800000000000040000000000001003001800c006800000000000040000000000000";
    SensaFrameEncoder.Encode(zero, p50, one, p60, frame);
    bool right = Hex(frame) == "79003001800c006800000000000040000000000001fc57e2bf15f8a800000000000040000000000000";
    SensaFrameEncoder.Encode(quarter, p60, zero, p50, frame);
    bool q = Hex(frame) == "793c51e28f1478a800000000000040000000000001003001800c006800000000000040000000000000";
    bool silence = Hex(SensaFrameEncoder.Silence()) == "79003001800c006800000000000040000000000001003001800c006800000000000040000000000000";
    var (amps, pitches) = SensaFrameEncoder.DecodeBlock(frame.AsSpan(1, 19));
    bool roundTrip = amps.All(a => a == 15) && pitches.All(p => p == 10);
    var report = new byte[SensaHidTransport.FeatureLength];
    SensaFrameEncoder.Encode(one, p60, zero, p50, frame);
    SensaHidTransport.BuildReport(frame, 3, report);
    bool packet = Hex(report) == "0400030000002a160e0179fc57e2bf15f8a800000000000040000000000001003001800c00680000000000004000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002d00";
    Check(left && right && q && silence && roundTrip && packet,
        "Direct USB: own encoder reproduces captured Synapse frames (left 1.0, right 1.0, left 0.25, silence) and a full 91-byte report incl. sequence + XOR CRC");
}
{
    // Direct path point folding: transients raise the nearest strength point; silence stays silent.
    var a = new double[4];
    var f = new SideFeatures { P0 = .2f, P1 = .2f, P2 = .2f, P3 = .2f, TransientCount = 1, T0Position = .7f, T0Amplitude = .9f };
    bool on = DirectSensaMapping.Points(f, 1, a);
    bool folded = on && a[2] > .89 && a[0] < .21 && a[3] < .21;
    bool silent = !DirectSensaMapping.Points(default, 3, a);
    Check(folded && silent, "Direct USB: transient folded into nearest point (pos .7 → point 2), silent features send nothing");
}
{
    // Synapse attachment check from Razer's log lines (2026-11-08 states).
    string S(string t) => "[2026-11-08 04:00:00.000][1] [RzInterHaptics.FFI] [info] " + t;
    bool attached = SynapseSensaCheck.Evaluate(new[] { S("Starting the RPC server"), S("New sender registered, ID: 1"), S("SenderGetMetadata device \"2636-{x}\"") }, out _);
    bool afterReboot = !SynapseSensaCheck.Evaluate(new[] { S("SenderGetMetadata device \"2636-{x}\""), S("Starting the RPC server"), S("New sender registered, ID: 2") }, out _);
    bool senderNoDevice = !SynapseSensaCheck.Evaluate(new[] { S("Starting the RPC server"), S("New sender registered, ID: 1"), S("SenderGetMetadata device \"2636-{x}\""), S("New sender registered, ID: 2") }, out _);
    Check(attached && afterReboot && senderNoDevice, "Synapse check: device line after last sender = attached; restart or newer sender without it = not attached");
}
{
    // 3-band bitstream encoder vs multiband frames rendered by HAR (.haps 80+190 Hz left, 120+300 Hz right).
    double P(double hz) => SensaFrameEncoder.PitchOf(hz);
    var frame = new byte[SensaFrameEncoder.FrameLength];
    SensaFrameEncoder.EncodeBands(
        new[] { new SensaBand(15 / 63.0, 15 / 63.0, 15 / 63.0, 15 / 63.0, (17 + .5) / 127), new SensaBand(15 / 63.0, 15 / 63.0, 15 / 63.0, 15 / 63.0, (54 + .5) / 127) },
        new[] { new SensaBand(15 / 63.0, 15 / 63.0, 15 / 63.0, 15 / 63.0, (30 + .5) / 127), new SensaBand(15 / 63.0, 15 / 63.0, 15 / 63.0, 15 / 63.0, (92 + .5) / 127) }, frame);
    bool multi = Convert.ToHexString(frame).ToLowerInvariant() == "793c89e44f227919ed8f6c7b63db400000000000013cf1e78f3c79e9f70fb87dc3ee40000000000000";
    var single = new byte[SensaFrameEncoder.FrameLength];
    SensaFrameEncoder.EncodeBands(new[] { new SensaBand(1, 1, 1, 1, P(60)) }, new[] { SensaBand.Silent(P(50)) }, single);
    var plain = new byte[SensaFrameEncoder.FrameLength];
    SensaFrameEncoder.Encode(new double[] { 1, 1, 1, 1 }, P(60), new double[4], P(50), plain);
    // Silent band 2/3 in a single-band frame carry pitch 0, while EncodeBands' silent right hand uses the given pitch.
    bool same = single.AsSpan(0, 21).SequenceEqual(plain.AsSpan(0, 21));
    Check(multi && same, "3-band encoder: reproduces HAR multiband frame (L 80+190, R 120+300) byte-exact; band 1 equals the plain encoder");
}
{
    // DualSense output report 0x02 (bytes after the report ID) and trigger-effect interpretation.
    var data = new byte[47];
    data[0] = 0x0F;                       // rumble + both triggers valid
    data[2] = 40; data[3] = 200;          // right (light), left (heavy) rumble
    data[10] = 0x26; data[11] = 0xF0; data[12] = 0x03;   // R2 vibration, zones 4..9
    data[13] = 0xFF; data[14] = 0xFF; data[15] = 0xFF; data[16] = 0xFF;
    data[21] = 0x25; data[22] = 0x04; data[23] = 0x01; data[24] = 0x07;   // L2 weapon: zones 2..8, strength 8
    bool parsed = DualSenseOutputReport.TryParse(data, out var rep) && rep.RumbleValid && rep.RumbleLeft == 200 && rep.RumbleRight == 40
        && rep.RightTriggerValid && rep.RightTrigger.Mode == 0x26 && rep.LeftTriggerValid && rep.LeftTrigger.Mode == 0x25;
    bool shortRejected = !DualSenseOutputReport.TryParse(new byte[10], out _);

    // Weapon: light click at the start zone, strong click at the break, nothing in between/after.
    var w = new TriggerEffectInterpreter(); w.SetEffect(rep.LeftTrigger);
    double maxStart = 0, maxBreak = 0, mid = 0, after = 0;
    for (int i = 0; i <= 100; i++)
    {
        double pull = i / 100.0, v = w.Update(pull, 0.004);
        if (pull >= .2 && pull < .24) maxStart = Math.Max(maxStart, v);
        if (pull >= .9 && pull < .94) maxBreak = Math.Max(maxBreak, v);
        if (pull >= .5 && pull < .6) mid = Math.Max(mid, v);
    }
    for (int i = 0; i < 30; i++) after = w.Update(1.0, 0.004);
    bool weapon = maxStart > .2 && maxStart < .5 && maxBreak > .9 && mid == 0 && after == 0;

    // Vibration zones: vibrates only while the trigger is inside the active zones.
    var vib = new TriggerEffectInterpreter(); vib.SetEffect(rep.RightTrigger);
    double low = vib.Update(.1, .004), high = vib.Update(.7, .004);
    bool vibration = low == 0 && high > .9;

    // Off effect stays silent.
    var off = new TriggerEffectInterpreter(); off.SetEffect(new TriggerEffect(0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    bool silent = Enumerable.Range(0, 50).All(i => off.Update(i / 50.0, .004) == 0);
    Check(parsed && shortRejected && weapon && vibration && silent,
        $"DualSense report 0x02 parse + trigger interpreter: weapon start {maxStart:F2} / break {maxBreak:F2}, vibration zones {low:F2}→{high:F2}, off silent");
}

// Virtual DualSense input: the physical Xbox pad (XInput) is the reference.
{
    var rest = DualSenseInputMapping.Expected(new XInputPad(0, 0, 0, 0, 0, 0, 0));
    bool centred = rest.LX == 128 && rest.LY == 128 && rest.RX == 128 && rest.RY == 128 && rest.L2 == 0 && rest.R2 == 0 && rest.Hat == DualSenseInputState.HatNone;
    var up = DualSenseInputMapping.Expected(new XInputPad(0, 0, 0, 0, short.MaxValue, 0, short.MinValue));
    var right = DualSenseInputMapping.Expected(new XInputPad(0, 255, 255, short.MaxValue, 0, short.MinValue, 0));
    bool directions = up.LY == 0 && up.RY == 255 && right.LX == 255 && right.RX == 0 && right.L2 == 255 && right.R2 == 255;
    var face = DualSenseInputMapping.Expected(new XInputPad((ushort)(XInputPad.A | XInputPad.Y | XInputPad.Back | XInputPad.Guide), 0, 0, 0, 0, 0, 0));
    bool buttons = face.Cross && face.Triangle && !face.Circle && !face.Square && face.Create && !face.Options && face.PS;
    bool hats = DualSenseInputMapping.Hat(XInputPad.DpadUp) == 0 && DualSenseInputMapping.Hat((ushort)(XInputPad.DpadUp | XInputPad.DpadRight)) == 1
        && DualSenseInputMapping.Hat(XInputPad.DpadDown) == 4 && DualSenseInputMapping.Hat((ushort)(XInputPad.DpadLeft | XInputPad.DpadUp)) == 7
        && DualSenseInputMapping.Hat((ushort)(XInputPad.DpadUp | XInputPad.DpadDown)) == DualSenseInputState.HatNone;
    Check(centred && directions && buttons && hats,
        "XInput → DualSense: rest = centre 128, up = LY 0, right = LX 255, A/Y/View/Guide = Cross/Triangle/Create/PS, D-pad hat");

    // Report 0x01 parse: Cross + hat E + L1 + Options + PS.
    var report = new byte[64]; report[0] = 0x01; report[1] = 10; report[2] = 20; report[3] = 30; report[4] = 40; report[5] = 50; report[6] = 60;
    report[8] = 0x20 | 2; report[9] = 0x01 | 0x20; report[10] = 0x01;
    bool parsed = DualSenseInputState.TryParseUsb(report, out var st) && st.LX == 10 && st.LY == 20 && st.RX == 30 && st.RY == 40 && st.L2 == 50 && st.R2 == 60
        && st.Hat == 2 && st.Cross && !st.Square && st.L1 && !st.R1 && st.Options && !st.Create && st.PS;
    bool wrongId = !DualSenseInputState.TryParseUsb(new byte[64], out _);
    Check(parsed && wrongId, "DualSense USB input report 0x01 parse");

    // The 2026-11-08 bug: sticks sent as -1..1 → rest encodes as 0 (up/left). The verifier must flag it.
    var bad = rest with { LX = DualSenseInputMapping.AxisByte(0f), LY = DualSenseInputMapping.AxisByte(0f) };
    var v = new VirtualPadVerifier();
    v.RecordExpected(0, rest);
    string? problem = null;
    for (int i = 0; i < VirtualPadVerifier.MismatchesToReport; i++) problem ??= v.Observe(10 + i * 4, bad);
    bool flagged = problem != null && problem.Contains("LY beklenen 128 görülen 0") && problem.Contains("LX");
    v.Observe(40, rest);
    bool clears = v.CurrentProblem == null && v.FirstProblem != null;

    // Latency and jitter: a report showing the state from 30 ms ago still matches; ±2 counts tolerated.
    var lat = new VirtualPadVerifier();
    var pushed = DualSenseInputMapping.Expected(new XInputPad(XInputPad.A, 0, 0, 0, short.MaxValue, 0, 0));
    lat.RecordExpected(0, rest); lat.RecordExpected(30, pushed);
    bool stale = lat.Observe(35, rest) == null && lat.Observe(36, pushed with { LY = 2 }) == null && lat.CurrentProblem == null && lat.Matches == 2;
    // Old states beyond the window do not excuse a wrong report.
    var old = new VirtualPadVerifier();
    old.RecordExpected(0, rest); old.RecordExpected(1000, pushed);
    string? late = null;
    for (int i = 0; i < VirtualPadVerifier.MismatchesToReport; i++) late ??= old.Observe(1400 + i, rest);
    Check(flagged && clears && stale && late != null,
        $"Live input verifier: flags the -1..1 stick bug ({problem}), tolerates latency/jitter, window bounded");
}

// HidHide: parse CLI output, pick only the pad's game nodes, map the virtual pad's HID path.
{
    string devList = "--dev-hide \"HID\\VID_054C&PID_0CE6&MI_03\\8&1A69D1E7&0&0000\"\r\n--dev-hide \"HID\\VID_054C&PID_0CE6&MI_03\\A&2B11C806&0&0000\"\r\n";
    var hidden = HidHideControl.ParseQuoted(devList, "--dev-hide");
    bool parse = hidden.Count == 2 && hidden[1] == "HID\\VID_054C&PID_0CE6&MI_03\\A&2B11C806&0&0000"
        && HidHideControl.ParseQuoted("--cloak-off\r\n", "--dev-hide").Count == 0;
    const string xna = "{d61ca365-5af4-4486-998b-9db4734c6ca3}", hid = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";
    bool nodes = HidHideControl.IsPadGameNode("USB\\VID_1532&PID_0A4C&MI_00\\A&A454866&0&0000", xna)
        && HidHideControl.IsPadGameNode("HID\\VID_1532&PID_0A4C&IG_03\\C&230D5F02&0&0000", hid)
        && !HidHideControl.IsPadGameNode("HID\\VID_1532&PID_0A4C&MI_01&COL04\\A&27B00EE5&0&0003", hid)   // Sensa control: never hidden
        && !HidHideControl.IsPadGameNode("USB\\VID_1532&PID_0A4C\\9&10AA6111&0&2", "{36fc9e60-c465-11cf-8056-444553540000}")
        && !HidHideControl.IsPadGameNode("USB\\VID_045E&PID_028E&MI_00\\1&2&3", xna);                    // other vendors' pads untouched
    bool path = HidHideControl.InstanceFromHidPath(@"\\?\hid#vid_054c&pid_0ce6&mi_03#4&bcf55c7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")
        == "HID\\VID_054C&PID_0CE6&MI_03\\4&BCF55C7&0&0000" && HidHideControl.InstanceFromHidPath(null) == null;
    bool errors = HidHideControl.LooksLikeError("The command is not recognized.") && !HidHideControl.LooksLikeError("--cloak-on\r\n");
    Check(parse && nodes && path && errors, "HidHide: CLI parse, only the pad's XInput/IG nodes (never Sensa COL04), virtual path → instance, CLI error text");
}

// Game signal simulator: scenes carry real signal on the right sides; reports drive the same parser/interpreter.
{
    double Rms(float[] b, double from, double to)
    {
        int s = (int)(from * GameSignalSimulator.Rate), e = Math.Min(b.Length, (int)(to * GameSignalSimulator.Rate));
        double sum = 0; for (int i = s; i < e; i++) sum += b[i] * b[i];
        return e > s ? Math.Sqrt(sum / (e - s)) : 0;
    }
    bool audible = GameSignalSimulator.Scenes.Where(s => s.Id != "all").All(s =>
    {
        var (l, r) = GameSignalSimulator.Haptics(s.Id);
        return Math.Max(Rms(l, 0, s.Seconds), Rms(r, 0, s.Seconds)) > 0.02 && l.All(float.IsFinite) && r.All(float.IsFinite)
            && l.All(v => Math.Abs(v) <= 1f) && r.All(v => Math.Abs(v) <= 1f);
    });
    var (sl, sr) = GameSignalSimulator.Haptics("steps");            // step 1 (0.30 s) left only, step 2 (0.75 s) right only
    bool steps = Rms(sl, 0.30, 0.40) > 10 * Rms(sr, 0.30, 0.40) && Rms(sr, 0.75, 0.85) > 10 * Rms(sl, 0.75, 0.85);
    var (pl, pr) = GameSignalSimulator.Haptics("pan");              // starts left, middle right
    bool pan = Rms(pl, 0.3, 0.5) > 4 * Rms(pr, 0.3, 0.5) && Rms(pr, 1.9, 2.1) > 4 * Rms(pl, 1.9, 2.1);
    var (al, _) = GameSignalSimulator.Haptics("all");
    bool all = Math.Abs(al.Length / (double)GameSignalSimulator.Rate - GameSignalSimulator.Duration("all")) < 0.01;
    Check(audible && steps && pan && all, "Game simulator scenes: audible, bounded, left/right steps and pan on the right sides, full tour length");

    double Pull(TriggerEffect eff)
    {
        var it = new TriggerEffectInterpreter(); it.SetEffect(eff);
        double max = 0; for (int i = 0; i <= 250; i++) max = Math.Max(max, it.Update(i / 250.0, 0.004));
        return max;
    }
    var weaponData = GameSignalSimulator.Outputs("weapon")[0].Data;
    bool weapon = DualSenseOutputReport.TryParse(weaponData, out var wr) && wr.RightTriggerValid && wr.RightTrigger.Mode == 0x25
        && wr.LeftTriggerValid && wr.LeftTrigger.IsOff && Pull(wr.RightTrigger) >= 0.7;
    var bowData = GameSignalSimulator.Outputs("bow")[0].Data;
    bool bow = DualSenseOutputReport.TryParse(bowData, out var br) && br.RightTrigger.Mode == 0x26 && br.LeftTrigger.Mode == 0x21
        && Pull(br.RightTrigger) >= 0.7 && Pull(br.LeftTrigger) >= 0.6;
    var boom = GameSignalSimulator.Outputs("explosion");
    bool rumble = boom.Any(s => DualSenseOutputReport.TryParse(s.Data, out var x) && x.RumbleValid && x.RumbleLeft == 255 && x.RumbleRight == 200)
        && DualSenseOutputReport.TryParse(boom[^1].Data, out var last) && last.RumbleValid && last.RumbleLeft == 0 && last.RightTrigger.IsOff;
    bool tour = GameSignalSimulator.Outputs("all").Count == GameSignalSimulator.Scenes.Where(s => s.Id != "all").Sum(s => GameSignalSimulator.Outputs(s.Id).Count)
        && GameSignalSimulator.Outputs("all").Zip(GameSignalSimulator.Outputs("all").Skip(1)).All(p => p.First.At <= p.Second.At);
    Check(weapon && bow && rumble && tour && weaponData.Length == GameSignalSimulator.OutputDataLength,
        "Game simulator reports: weapon 0x25 / bow 0x21+0x26 parse and drive the interpreter, explosion rumble on and off, ordered tour");

    string ok = GameSimVerdict.Describe(120, 0.6f, 0.8f, 90, 40, 40, null, 12.5);
    string deadEngine = GameSimVerdict.Describe(120, 0.6f, 0.8f, 0, 40, 40, null, 0.2);
    string silent = GameSimVerdict.Describe(120, 0.0f, 0.0f, 0, 40, 0, null, null);
    string noAudio = GameSimVerdict.Describe(0, 0, 0, 0, 0, 0, "HID açılamadı (5)", null);
    Check(!ok.Contains('✗') && deadEngine.Contains("Sensa'ya hiç kare gitmedi") && deadEngine.Contains("✗ Mikrofon")
          && silent.Contains("sessiz") && silent.Contains("köprüye ulaşmadı") && noAudio.Contains("almadı") && noAudio.Contains("yazılamadı"),
        "Game simulator verdict names the failing stage (audio, Sensa engine, triggers, mic)");
}

// Wolverine mode switch: our packets equal Synapse's (USB capture 2026-11-08, PC → Xbox), CRC included.
{
    string prepare = "040000000000030c950102" + new string('0', 2 * 78) + "9900";
    string sw = "0400010000000100530100" + new string('0', 2 * 78) + "5300";
    string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    bool same = Hex(RazerCommand.XboxModePrepare()) == prepare && Hex(RazerCommand.XboxModeSwitch()) == sw
        && RazerCommand.Build(0x1d, 0x0C, 0x95, [0x01, 0x01, 0x00])[89] == 0x9a;
    Check(same && prepare.Length == 2 * SensaHidTransport.FeatureLength, "Razer mode commands byte-identical to Synapse's (0C95 01 02 00 → 00 53 01), checksums included");
    string xOk = GameSimVerdict.Describe(100, 0.6f, 0.6f, 0, 40, 40, null, null, 120, "Xbox modu");
    string xBad = GameSimVerdict.Describe(100, 0.6f, 0.6f, 0, 40, 40, null, null, 0, "GameInput kolu bulamadı");
    Check(xOk.Contains("✓ Xbox modu") && !xOk.Contains("Sensa") && xBad.Contains("✗ Xbox modu") && xBad.Contains("GameInput"),
        "Simulator verdict in Xbox mode reports the trigger/body motor commands instead of Sensa");
}

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
var absent = UsbGamepadClassifier.Classify(Array.Empty<string>());
Check(absent.ScanSucceeded && !absent.DualSenseUsbNode && !absent.WolverineUsbNode,
    "Empty scan means neither known USB device found, not automatically connected");
var bothPresent = UsbGamepadClassifier.Classify(new[]
    { @"USB\VID_054C&PID_0CE6\sony", @"USB\VID_1532&PID_0A3F\razer" });
Check(bothPresent.ScanSucceeded && bothPresent.DualSenseUsbNode &&
    bothPresent.WolverineUsbNode, "Two distinct USB VID/PID nodes count as present");
Check(!UsbGamepadPresence.Unknown("WMI blocked").ScanSucceeded,
    "Enumeration failure displayed as unverified, not disconnected");
Console.WriteLine("USB PnP identity checks and behavioral fixtures passed.");

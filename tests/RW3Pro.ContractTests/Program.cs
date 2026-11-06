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
Console.WriteLine("23 evidence, watchdog and channel-isolation checks passed.");

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

Console.WriteLine("7 evidence contract checks passed.");

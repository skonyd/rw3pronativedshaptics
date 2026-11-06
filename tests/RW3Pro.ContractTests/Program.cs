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
Check(StereoPcmDiscardProbe.ChunkMs == 20 &&
      RealtimeStereoPcmTransport.SampleRate * StereoPcmDiscardProbe.ChunkMs / 1000 == 960,
    "20ms consume chunks contain 960 genuine PCM frames, NOT one 50Hz envelope value");

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
Console.WriteLine("Bounded 48k stereo PCM realtime transport identity, zero-fill and concurrency passed.");

Console.WriteLine("USB PnP, subjective A/B and high-fidelity stereo WAV contract suite passed.");

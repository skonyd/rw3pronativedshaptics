using RW3ProNativeDSHaptics;

internal sealed class TestAudioSink : IAudioHapticSink
{
    public string Description => "Test-only sink; no hardware";
    public int AcceptCalls, SilenceCalls;
    public bool ThrowOnAccept;
    public Action? AcceptAction;
    public void Accept(ReadOnlySpan<float> stereo) { AcceptCalls++; AcceptAction?.Invoke(); if (ThrowOnAccept) throw new InvalidOperationException("Injected output failure"); }
    public void Silence() => SilenceCalls++;
}

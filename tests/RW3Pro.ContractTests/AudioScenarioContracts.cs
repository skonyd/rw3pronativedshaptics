using RW3ProNativeDSHaptics;

internal static class AudioScenarioContracts
{
    public static void Run(Action<bool, string> check)
    {
        var multitone = HapticScenarioAudit.MultitoneLoss();
        check(Math.Abs(multitone.Original80HzAmplitude - .25) < 1e-9 &&
            Math.Abs(multitone.Original190HzAmplitude - .25) < 1e-9 &&
            multitone.AvailableCarrierFrequenciesPerSide == 1,
            "Independent original-sample projections retain two tones; a one-carrier feature match cannot prove spectral fidelity");
        var f = new SideFeatures { P0 = .1f, P1 = .2f, P2 = .3f, P3 = .4f, FrequencyHz = 120,
            TransientCount = 2, T0Position = .1f, T0Amplitude = .4f, T1Position = .8f, T1Amplitude = .3f };
        for (int side = 0; side < 2; side++)
        for (int field = 0; field < 9; field++)
        {
            var altered = f;
            switch (field)
            {
                case 0: altered.P0 += .1f; break;
                case 1: altered.P1 += .1f; break;
                case 2: altered.P2 += .1f; break;
                case 3: altered.P3 += .1f; break;
                case 4: altered.FrequencyHz = 0; break;
                case 5: altered.T0Position += .1f; break;
                case 6: altered.T0Amplitude += .1f; break;
                case 7: altered.T1Position -= .1f; break;
                case 8: altered.T1Amplitude += .1f; break;
            }
            var report = AudioPathEquivalence.CompareFrames("mutation", new[] { (f, f) },
                new[] { side == 0 ? (altered, f) : (f, altered) });
            check(!report.WithinTolerance(), $"Comparison rejects side {side} field {field} mutation");
        }
        var none = f; none.TransientCount = 0;
        check(!AudioPathEquivalence.CompareFrames("moved hits", new[] { (f, f), (none, none) },
            new[] { (none, none), (f, f) }).WithinTolerance(), "Equal total hit counts cannot hide hits moved between frames");

        foreach (int delay in new[] { 0, 10, 300, 1000 })
        {
            var sink = new TestAudioSink(); using var queue = new AudioSinkDelivery(sink);
            queue.Enqueue(new float[] { .4f, .2f }, 100, delay);
            if (delay > 0) queue.Pump(100 + delay - 1);
            check(sink.AcceptCalls == 0, $"Delay {delay}: no early sink delivery");
            queue.Pump(100 + delay); queue.Pump(100 + delay + 249);
            check(sink.AcceptCalls == 1 && sink.SilenceCalls == 0, $"Delay {delay}: output held within stale deadline");
            queue.Pump(100 + delay + 250);
            check(sink.SilenceCalls == 1, $"Delay {delay}: missing callbacks zero the sink after queue drain");
        }
        {
            var sink = new TestAudioSink(); using var queue = new AudioSinkDelivery(sink);
            queue.Enqueue(new float[] { .4f, .2f }, 0, 1000);
            queue.Dispose(); queue.Pump(2000);
            check(sink.AcceptCalls == 0 && sink.SilenceCalls == 1 && queue.QueuedSamples == 0,
                "Stop discards a future scheduled tail instead of playing it later");
        }
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var sink = new TestAudioSink { AcceptAction = () => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); } };
            using var queue = new AudioSinkDelivery(sink);
            queue.Enqueue(new float[] { .4f, .2f }, 0, 0);
            var pump = Task.Run(() => queue.Pump(0));
            bool accepting = entered.Wait(2000);
            var stop = Task.Run(queue.Dispose);
            try { check(accepting && !stop.Wait(20), "Concurrent Stop cannot return while an older sink write is in progress"); }
            finally { release.Set(); }
            check(Task.WaitAll(new[] { pump, stop }, 3000), "Concurrent write/Stop cleanup terminates");
            int writes = sink.AcceptCalls;
            queue.Enqueue(new float[] { .4f, .2f }, 1000, 0); queue.Pump(1000);
            check(sink.AcceptCalls == writes && sink.SilenceCalls > 0, "No sink write can occur after concurrent Stop completes");
        }
    }
}

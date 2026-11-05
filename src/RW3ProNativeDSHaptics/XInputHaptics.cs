using NAudio.Wave;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

/// <summary>Conventional XInput rumble only. Does not address Razer's two HD haptic voice-coils directly.</summary>
public sealed class XInputHaptics : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);

    private WasapiLoopbackCapture? _capture;
    private bool _capturing;
    private long _lastUpdate;
    private int _selectedIndex = -1;

    public static int DetectIndex()
    {
        try
        {
            for (uint i = 0; i < 4; i++)
                if (XInputGetState(i, out _) == 0) return (int)i;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return -1;
    }

    private static void SetMotor(int index, double strong, double weak)
    {
        if (index < 0) return;
        var vibration = new XInputVibration
        {
            LeftMotorSpeed = (ushort)(Math.Clamp(strong, 0, 1) * ushort.MaxValue),
            RightMotorSpeed = (ushort)(Math.Clamp(weak, 0, 1) * ushort.MaxValue)
        };
        XInputSetState((uint)index, ref vibration);
    }

    public async Task<bool> TestAsync()
    {
        if (_capturing) return false;
        var index = DetectIndex();
        if (index < 0) return false;
        try
        {
            SetMotor(index, 0.30, 0.20);
            await Task.Delay(250);
        }
        finally { SetMotor(index, 0, 0); }
        return true;
    }

    public void StartAudio()
    {
        StopAudio();
        int index = DetectIndex();
        if (index < 0)
            throw new InvalidOperationException("XInput cihazı bulunamadı. Wolverine'i PC moduna alın; diğer gamepad'leri geçici ayırın.");
        var capture = new WasapiLoopbackCapture();
        bool floating = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                        && capture.WaveFormat.BitsPerSample == 32;
        bool pcm16 = capture.WaveFormat.Encoding == WaveFormatEncoding.Pcm
                     && capture.WaveFormat.BitsPerSample == 16;
        if (!floating && !pcm16)
        {
            capture.Dispose();
            throw new NotSupportedException("Ses çıkış formatı desteklenmiyor. 32-bit float veya 16-bit PCM Windows çıkışı gerekiyor.");
        }
        _selectedIndex = index;
        _capture = capture;
        _capturing = true;
        _lastUpdate = 0;
        capture.DataAvailable += OnAudioData;
        try { capture.StartRecording(); }
        catch { StopAudio(); throw; }
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        if (!_capturing || _capture == null) return;
        var fmt = _capture.WaveFormat;
        var bytes = fmt.BitsPerSample / 8;
        int frameBytes = Math.Max(1, fmt.Channels) * bytes;
        var count = e.BytesRecorded / frameBytes;
        if (count < 1) return;
        int step = Math.Max(1, count / 512);
        double energy = 0, change = 0, previous = 0;
        int n = 0;
        for (int frame = 0; frame < count; frame += step)
        {
            int offset = frame * frameBytes;
            double sample = fmt.BitsPerSample == 16 ?
                BitConverter.ToInt16(e.Buffer, offset) / 32768.0 :
                BitConverter.ToSingle(e.Buffer, offset);
            if (!double.IsFinite(sample)) continue;
            sample = Math.Clamp(sample, -1, 1);
            energy += sample * sample;
            change += Math.Abs(sample - previous);
            previous = sample;
            n++;
        }
        if (n == 0) return;
        var rms = Math.Sqrt(energy / n);
        var transient = change / n;
        long now = Environment.TickCount64;
        if (now - _lastUpdate < 45) return;
        _lastUpdate = now;
        // Conservative, intentionally conventional low-fidelity rumble; never call it native HD.
        var strong = Math.Min(0.40, rms * 2.6);
        var weak = Math.Min(0.28, transient * 2.1);
        try { if (_capturing) SetMotor(_selectedIndex, strong, weak); } catch { }
    }

    public void StopAudio()
    {
        _capturing = false;
        var capture = _capture;
        _capture = null;
        int index = _selectedIndex;
        _selectedIndex = -1;
        if (capture != null)
        {
            capture.DataAvailable -= OnAudioData;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        try { SetMotor(index, 0, 0); } catch { }
    }

    public void Dispose() => StopAudio();
}

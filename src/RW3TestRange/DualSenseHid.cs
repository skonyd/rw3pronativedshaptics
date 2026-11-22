using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RW3TestRange;

/// <summary>DualSense adaptive-trigger effect: mode + 10 parameter bytes, as games write it (report 0x02 bytes 10/21).</summary>
public readonly record struct TriggerFx(byte Mode, byte[] P)
{
    public static readonly TriggerFx Off = new(0x05, new byte[10]);

    private static (byte, byte, ulong) Zones(int from, int to, Func<int, int> strength)
    {
        int mask = 0; ulong packed = 0;
        for (int z = from; z <= to; z++) { mask |= 1 << z; packed |= (ulong)Math.Clamp(strength(z) - 1, 0, 7) << (3 * z); }
        return ((byte)mask, (byte)(mask >> 8), packed);
    }

    /// <summary>Continuous resistance from zone <paramref name="from"/> (0..9), strength 1..8.</summary>
    public static TriggerFx Feedback(int from, int strength)
    {
        var (m0, m1, p) = Zones(from, 9, _ => strength);
        return new(0x21, new byte[] { m0, m1, (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24), 0, 0, 0, 0 });
    }

    /// <summary>Resistance rising from <paramref name="from"/> to the end (bow string / heavy pull).</summary>
    public static TriggerFx Ramp(int from, int s0, int s1)
    {
        var (m0, m1, p) = Zones(from, 9, z => s0 + (s1 - s0) * (z - from) / Math.Max(1, 9 - from));
        return new(0x21, new byte[] { m0, m1, (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24), 0, 0, 0, 0 });
    }

    /// <summary>Weapon: resistance between zones start..end, then a break (click), strength 1..8.</summary>
    public static TriggerFx Weapon(int start, int end, int strength)
    {
        int mask = (1 << start) | (1 << end);
        return new(0x25, new byte[] { (byte)mask, (byte)(mask >> 8), (byte)Math.Clamp(strength - 1, 0, 7), 0, 0, 0, 0, 0, 0, 0 });
    }

    /// <summary>Vibration from zone <paramref name="from"/> with amplitude 1..8 at <paramref name="hz"/>.</summary>
    public static TriggerFx Vibration(int from, int amplitude, int hz)
    {
        var (m0, m1, p) = Zones(from, 9, _ => amplitude);
        return new(0x26, new byte[] { m0, m1, (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24), 0, 0, (byte)hz, 0 });
    }

    /// <summary>Bow: resistance from start to end zone, snap-back strength when released past the end.</summary>
    public static TriggerFx Bow(int start, int end, int strength, int snap)
    {
        int mask = (1 << start) | (1 << end);
        return new(0x22, new byte[] { (byte)mask, (byte)(mask >> 8), (byte)((Math.Clamp(strength - 1, 0, 7)) | (Math.Clamp(snap - 1, 0, 7) << 3)), 0, 0, 0, 0, 0, 0, 0 });
    }

    /// <summary>Machine (automatic weapon): vibration with two alternating amplitudes, carrier hz, period in 0.1 s.</summary>
    public static TriggerFx Machine(int start, int end, int a, int b, int hz, int period)
    {
        int mask = (1 << start) | (1 << end);
        return new(0x27, new byte[] { (byte)mask, (byte)(mask >> 8), (byte)((a & 7) | ((b & 7) << 3)), (byte)hz, (byte)period, 0, 0, 0, 0, 0 });
    }

    /// <summary>Galloping (horse feet): two foot positions in an eight-part cycle, cycles per second.</summary>
    public static TriggerFx Galloping(int start, int end, int first, int second, int hz)
    {
        int mask = (1 << start) | (1 << end);
        return new(0x23, new byte[] { (byte)mask, (byte)(mask >> 8), (byte)((second & 7) | ((first & 7) << 3)), (byte)hz, 0, 0, 0, 0, 0, 0 });
    }

    public override string ToString() => $"0x{Mode:X2}[{Convert.ToHexString(P)}]";
}

/// <summary>
/// A DualSense on USB HID (real, or RW3's virtual one: 054C:0CE6). Input report 0x01 (also Bluetooth 0x31 for reading),
/// output report 0x02: compatible rumble, adaptive triggers, lightbar, player LEDs, mic LED. Bluetooth output (0x31 + CRC)
/// is not written.
/// </summary>
public sealed class DualSenseHid : IPadSource
{
    public string Path { get; }
    public bool Virtual { get; }
    private SafeFileHandle? _read, _write;
    private Thread? _thread;
    private volatile bool _stop;
    private PadState _state;
    private long _lastInputTick;
    private readonly object _gate = new();
    private int _outLen = 48;
    public bool Bluetooth { get; private set; }
    public string ParentIds { get; private init; } = "";
    /// <summary>Device container: the pad's audio endpoint (HD haptics) has the same one.</summary>
    public Guid? Container { get; private init; }
    public long InputReports { get; private set; }
    public long OutputReports { get; private set; }
    public string? Error { get; private set; }

    // output state, sent whole on each change (games resend their full state)
    public TriggerFx LeftTrigger = TriggerFx.Off, RightTrigger = TriggerFx.Off;
    public byte RumbleLeft, RumbleRight, Red = 0, Green = 60, Blue = 255, PlayerLeds = 0b00100, MicLed;
    /// <summary>HD haptics in use: no rumble emulation (it would mute the haptic audio channels).</summary>
    public bool AudioHaptics;
    /// <summary>Which feature reports the pad answered at open ("0x05:ok 0x09:ok 0x20:-").</summary>
    public string Features { get; private set; } = "";
    /// <summary>The last input report as hex (first 48 bytes) and its length, for the session log.</summary>
    public string LastRaw { get; private set; } = "";
    public int LastRawLength { get; private set; }

    private DualSenseHid(string path, bool isVirtual) { Path = path; Virtual = isVirtual; }

    public string Name => (Virtual ? "DualSense (sanal, RW3)" : "DualSense") + (Bluetooth ? " Bluetooth" : " USB");
    public string Kind => "dualsense";
    public bool Sony => true;
    public bool Connected => _read != null && !_read.IsInvalid && Error == null;

    public static List<DualSenseHid> FindAll()
    {
        var list = new List<DualSenseHid>();
        foreach (var path in Hid.Paths())
        {
            using var h = Hid.Open(path, 0);
            if (h.IsInvalid) continue;
            if (!Hid.Attributes(h, out ushort vid, out ushort pid) || vid != 0x054C || pid is not (0x0CE6 or 0x0DF2)) continue;
            if (!Hid.Caps(h, out ushort usagePage, out ushort usage, out _, out _) || usagePage != 1 || usage != 5) continue;
            // RW3's virtual DualSense (HIDMaestro, USB/IP) sits on a software USB bus rooted at ROOT\USB; a real one on the
            // PC's USB controller (PCI) or Bluetooth. Both have "USB" parents, so the root of the chain decides.
            string parent = Hid.ParentChain(path);
            list.Add(new DualSenseHid(path, Hid.IsSoftwareDevice(parent)) { ParentIds = parent, Container = Hid.ContainerOf(path) });
        }
        return list;
    }

    public bool Open()
    {
        _read = Hid.Open(Path, Hid.GenericRead | Hid.GenericWrite);
        _write = Hid.Open(Path, Hid.GenericRead | Hid.GenericWrite);
        if (_read.IsInvalid || _write.IsInvalid) { Error = "HID açılamadı (" + Marshal.GetLastWin32Error() + ")"; return false; }
        if (Hid.Caps(_write, out _, out _, out ushort inLen, out ushort outLen)) { _outLen = Math.Max(48, (int)outLen); Bluetooth = inLen > 64 && inLen != 64; }
        // Like games and Steam: read the calibration (0x05), pairing (0x09) and firmware (0x20) feature reports first.
        // A real pad does not care; RW3's virtual DualSense (HIDMaestro) switches from its short report layout to the
        // full one only after such a read, and the short layout has no touch data, which reads as a finger at (0,0)
        // and pinned the crosshair to the top-left corner (2026-11-23, E-20261123-69).
        Features = string.Join(" ", new byte[] { 0x05, 0x09, 0x20 }.Select(id => $"0x{id:X2}:{(Hid.GetFeature(_write, id) ? "ok" : "-")}"));
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "DualSense input" };
        _thread.Start();
        return true;
    }

    private void ReadLoop()
    {
        var buf = new byte[128];
        while (!_stop)
        {
            if (!Hid.ReadFile(_read!, buf, buf.Length, out int n, IntPtr.Zero) || n <= 0) { if (!_stop) { Error = "okuma kesildi (" + Marshal.GetLastWin32Error() + ")"; } return; }
            int o = buf[0] == 0x01 ? 1 : buf[0] == 0x31 ? 2 : -1;
            if (o < 0 || n < o + 40) continue;
            if (buf[0] == 0x31) Bluetooth = true;
            var s = new PadState
            {
                LX = (buf[o] - 128) / 127f, LY = -(buf[o + 1] - 128) / 127f, RX = (buf[o + 2] - 128) / 127f, RY = -(buf[o + 3] - 128) / 127f,
                LT = buf[o + 4] / 255f, RT = buf[o + 5] / 255f
            };
            byte b0 = buf[o + 7], b1 = buf[o + 8], b2 = buf[o + 9];
            Btn b = (b0 & 0x0F) switch
            {
                0 => Btn.Up, 1 => Btn.Up | Btn.Right, 2 => Btn.Right, 3 => Btn.Right | Btn.Down, 4 => Btn.Down,
                5 => Btn.Down | Btn.Left, 6 => Btn.Left, 7 => Btn.Left | Btn.Up, _ => Btn.None
            };
            if ((b0 & 0x10) != 0) b |= Btn.X; if ((b0 & 0x20) != 0) b |= Btn.A; if ((b0 & 0x40) != 0) b |= Btn.B; if ((b0 & 0x80) != 0) b |= Btn.Y;
            if ((b1 & 0x01) != 0) b |= Btn.LB; if ((b1 & 0x02) != 0) b |= Btn.RB; if ((b1 & 0x10) != 0) b |= Btn.View; if ((b1 & 0x20) != 0) b |= Btn.Menu;
            if ((b1 & 0x40) != 0) b |= Btn.LS; if ((b1 & 0x80) != 0) b |= Btn.RS;
            if ((b2 & 0x01) != 0) b |= Btn.Guide; if ((b2 & 0x02) != 0) b |= Btn.Touchpad; if ((b2 & 0x04) != 0) b |= Btn.Mute;
            s.Buttons = b;
            s.DeriveTriggerButtons();
            if (n >= o + 27)
            {
                s.HasMotion = true;
                s.GyroX = BitConverter.ToInt16(buf, o + 15) / 1024f; s.GyroY = BitConverter.ToInt16(buf, o + 17) / 1024f; s.GyroZ = BitConverter.ToInt16(buf, o + 19) / 1024f;
                s.AccX = BitConverter.ToInt16(buf, o + 21) / 8192f; s.AccY = BitConverter.ToInt16(buf, o + 23) / 8192f; s.AccZ = BitConverter.ToInt16(buf, o + 25) / 8192f;
            }
            if (n >= o + 36)
            {
                byte c = buf[o + 32];
                s.Touching = (c & 0x80) == 0;
                int x = buf[o + 33] | ((buf[o + 34] & 0x0F) << 8), y = (buf[o + 34] >> 4) | (buf[o + 35] << 4);
                s.TouchX = x / 1919f; s.TouchY = y / 1079f;
            }
            lock (_gate) { _state = s; _lastInputTick = Environment.TickCount64; InputReports++; LastRaw = Convert.ToHexString(buf, 0, Math.Min(n, 48)); LastRawLength = n; }
        }
    }

    public bool TryRead(out PadState s)
    {
        lock (_gate) { s = _state; return InputReports > 0 && Environment.TickCount64 - _lastInputTick < 2000; }
    }

    /// <summary>Writes the whole output state (USB report 0x02).</summary>
    public bool Send()
    {
        if (_write == null || _write.IsInvalid || Bluetooth) return false;
        var d = new byte[_outLen];
        d[0] = 0x02;
        // flags0: bit0 rumble emulation + bit1 "rumble instead of haptics" make the pad IGNORE the haptic audio channels
        // (2026-11-21: a real DualSense stayed silent). With HD haptics both stay 0 and rumble is played as haptic audio.
        d[1] = (byte)((AudioHaptics ? 0 : 0x01 | 0x02) | 0x04 | 0x08);
        d[2] = 0x01 | 0x02 | 0x04 | 0x10;       // mic LED, power-save / mute control, lightbar, player LEDs
        if (!AudioHaptics) { d[3] = RumbleRight; d[4] = RumbleLeft; }
        d[9] = MicLed;
        d[10] = 0x00;                            // MuteControl: haptic / audio power-save and mute bits cleared (Special K, RW3)
        Write(d, 11, RightTrigger); Write(d, 22, LeftTrigger);
        d[39] = 0x02;                            // lightbar setup valid
        d[42] = 0x02;                            // light out (take over from the boot animation)
        d[44] = PlayerLeds; d[45] = Red; d[46] = Green; d[47] = Blue;
        bool ok = Hid.WriteFile(_write, d, d.Length, out _, IntPtr.Zero);
        if (ok) OutputReports++; else Error ??= "yazılamadı (" + Marshal.GetLastWin32Error() + ")";
        return ok;
    }

    private static void Write(byte[] d, int at, TriggerFx fx) { d[at] = fx.Mode; fx.P.AsSpan(0, 10).CopyTo(d.AsSpan(at + 1)); }

    public void Dispose()
    {
        _stop = true;
        try { RumbleLeft = RumbleRight = 0; LeftTrigger = RightTrigger = TriggerFx.Off; Send(); } catch { }
        _read?.Dispose(); _write?.Dispose();
    }
}

internal static class Hid
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    [StructLayout(LayoutKind.Sequential)] private struct Attr { public int Size; public ushort Vid, Pid, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct HidpCaps { public ushort Usage, UsagePage, InLen, OutLen, FeatLen; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Res; public ushort a, b, c, d, e, f, g, h, i, j; }
    [StructLayout(LayoutKind.Sequential)] private struct IfData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attr a);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr p, ref HidpCaps c);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int i, ref IfData d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref IfData d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte[] b, int len, out int read, IntPtr o);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte[] b, int len, out int written, IntPtr o);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNode(out uint node, string id, int flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint node, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID(uint node, char[] buf, int len, int flags);

    /// <summary>Instance IDs of the device and its parents ("HID\… ← USB\… ← …"), from a HID interface path.</summary>
    public static string ParentChain(string path)
    {
        try
        {
            string p = path.StartsWith(@"\\?\") ? path[4..] : path;
            int g = p.LastIndexOf("#{", StringComparison.Ordinal);
            if (g > 0) p = p[..g];
            string id = p.Replace('#', '\\');
            if (CM_Locate_DevNode(out uint node, id, 0) != 0) return id;
            var parts = new List<string> { id };
            var buf = new char[400];
            for (int i = 0; i < 8 && CM_Get_Parent(out uint parent, node, 0) == 0; i++)
            {
                if (CM_Get_Device_ID(parent, buf, buf.Length, 0) != 0) break;
                parts.Add(new string(buf).TrimEnd((char)0));
                node = parent;
            }
            return string.Join(" ← ", parts);
        }
        catch { return path; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DevInfo { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(IntPtr g, IntPtr e, IntPtr w, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int i, ref DevInfo d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfo d, char[] buf, int size, out int req);

    /// <summary>Instance IDs of every present device whose ID contains <paramref name="needle"/>.</summary>
    public static bool AnyPresent(params string[] needles)
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x4 | 0x2);   // all classes, present
        if (set == new IntPtr(-1)) return false;
        try
        {
            var buf = new char[512];
            for (int i = 0; ; i++)
            {
                var d = new DevInfo { Size = Marshal.SizeOf<DevInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref d)) return false;
                if (!SetupDiGetDeviceInstanceId(set, ref d, buf, buf.Length, out _)) continue;
                string id = new string(buf).TrimEnd((char)0);
                foreach (var n in needles) if (id.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DevPropKey { public Guid Fmt; public uint Pid; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    /// <summary>The device container (DEVPKEY_Device_ContainerId) of a HID interface path's device.</summary>
    public static Guid? ContainerOf(string path)
    {
        try
        {
            string p = path.StartsWith(@"\\?\") ? path[4..] : path;
            int g = p.LastIndexOf("#{", StringComparison.Ordinal);
            if (g > 0) p = p[..g];
            if (CM_Locate_DevNode(out uint node, p.Replace('#', '\\'), 0) != 0) return null;
            var key = new DevPropKey { Fmt = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Pid = 2 };
            uint size = 16; var buf = new byte[16];
            return CM_Get_DevNode_PropertyW(node, ref key, out _, buf, ref size, 0) == 0 && size == 16 ? new Guid(buf) : null;
        }
        catch { return null; }
    }

    /// <summary>True when the device chain ends at a software root (virtual bus) rather than hardware (PCI / ACPI / BTH).</summary>
    public static bool IsSoftwareDevice(string chain)
    {
        var ids = chain.Split(" ← ");
        if (ids.Any(id => id.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase))) return true;
        bool hardware = ids.Any(id => id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)
                                       || id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase));
        bool software = ids.Any(id => id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase));
        return software && !hardware;
    }

    public static SafeFileHandle Open(string path, uint access) => CreateFile(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

    public static IEnumerable<string> Paths()
    {
        HidD_GetHidGuid(out var g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new IfData { Size = Marshal.SizeOf<IfData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, 8);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                    {
                        string? p = Marshal.PtrToStringUni(buf + 4);
                        if (p != null) yield return p;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static bool Attributes(SafeFileHandle h, out ushort vid, out ushort pid)
    {
        var a = new Attr { Size = Marshal.SizeOf<Attr>() };
        bool ok = HidD_GetAttributes(h, ref a); vid = a.Vid; pid = a.Pid; return ok;
    }

    /// <summary>Get_Feature for one report id (buffer = the device's longest feature report).</summary>
    public static bool GetFeature(SafeFileHandle h, byte id)
    {
        int len = 64;
        if (HidD_GetPreparsedData(h, out IntPtr p))
            try { var c = new HidpCaps { Res = new ushort[17] }; if (HidP_GetCaps(p, ref c) == 0x110000 && c.FeatLen > 0) len = c.FeatLen; }
            finally { HidD_FreePreparsedData(p); }
        var buf = new byte[len]; buf[0] = id;
        return HidD_GetFeature(h, buf, len);
    }

    public static bool Caps(SafeFileHandle h, out ushort page, out ushort usage, out ushort inLen, out ushort outLen)
    {
        page = usage = inLen = outLen = 0;
        if (!HidD_GetPreparsedData(h, out IntPtr p)) return false;
        try
        {
            var c = new HidpCaps { Res = new ushort[17] };
            if (HidP_GetCaps(p, ref c) != 0x110000) return false;
            page = c.UsagePage; usage = c.Usage; inLen = c.InLen; outLen = c.OutLen; return true;
        }
        finally { HidD_FreePreparsedData(p); }
    }
}

/// <summary>
/// A DualShock 4 on USB HID (real 054C:05C4 / 09CC, or RW3's virtual PS4 pad). Input report 0x01, output report 0x05:
/// [1] flags (0x01 rumble, 0x02 light), [4] right (weak) motor, [5] left (strong) motor, [6..8] lightbar RGB.
/// </summary>
public sealed class DualShock4Hid : IPadSource
{
    public string Path { get; }
    private SafeFileHandle? _read, _write;
    private volatile bool _stop;
    private PadState _state;
    private long _lastTick;
    private readonly object _gate = new();
    private int _outLen = 32;
    public long InputReports { get; private set; }
    public long OutputReports { get; private set; }
    public string? Error { get; private set; }
    public byte RumbleLeft, RumbleRight, Red = 0, Green = 60, Blue = 255;
    public bool Virtual { get; private init; }
    public string ParentIds { get; private init; } = "";

    private DualShock4Hid(string path) => Path = path;
    public string Name => Virtual ? "DualShock 4 (sanal, RW3)" : "DualShock 4";
    public string Kind => "dualshock4";
    public bool Sony => true;
    public bool Connected => _read != null && !_read.IsInvalid && Error == null;

    public static List<DualShock4Hid> FindAll()
    {
        var list = new List<DualShock4Hid>();
        foreach (var path in Hid.Paths())
        {
            using var h = Hid.Open(path, 0);
            if (h.IsInvalid) continue;
            if (!Hid.Attributes(h, out ushort vid, out ushort pid) || vid != 0x054C || pid is not (0x05C4 or 0x09CC or 0x0BA0)) continue;
            if (!Hid.Caps(h, out ushort page, out ushort usage, out _, out _) || page != 1 || usage != 5) continue;
            string parent = Hid.ParentChain(path);
            list.Add(new DualShock4Hid(path) { ParentIds = parent, Virtual = Hid.IsSoftwareDevice(parent) });
        }
        return list;
    }

    public bool Open()
    {
        _read = Hid.Open(Path, Hid.GenericRead | Hid.GenericWrite);
        _write = Hid.Open(Path, Hid.GenericRead | Hid.GenericWrite);
        if (_read.IsInvalid || _write.IsInvalid) { Error = "HID açılamadı (" + Marshal.GetLastWin32Error() + ")"; return false; }
        if (Hid.Caps(_write, out _, out _, out _, out ushort outLen) && outLen > 0) _outLen = outLen;
        new Thread(ReadLoop) { IsBackground = true, Name = "DS4 input" }.Start();
        return true;
    }

    private void ReadLoop()
    {
        var buf = new byte[128];
        while (!_stop)
        {
            if (!Hid.ReadFile(_read!, buf, buf.Length, out int n, IntPtr.Zero) || n <= 0) { if (!_stop) Error = "okuma kesildi"; return; }
            if (buf[0] != 0x01 || n < 40) continue;
            var s = new PadState
            {
                LX = (buf[1] - 128) / 127f, LY = -(buf[2] - 128) / 127f, RX = (buf[3] - 128) / 127f, RY = -(buf[4] - 128) / 127f,
                LT = buf[8] / 255f, RT = buf[9] / 255f
            };
            byte b0 = buf[5], b1 = buf[6], b2 = buf[7];
            Btn b = (b0 & 0x0F) switch
            {
                0 => Btn.Up, 1 => Btn.Up | Btn.Right, 2 => Btn.Right, 3 => Btn.Right | Btn.Down, 4 => Btn.Down,
                5 => Btn.Down | Btn.Left, 6 => Btn.Left, 7 => Btn.Left | Btn.Up, _ => Btn.None
            };
            if ((b0 & 0x10) != 0) b |= Btn.X; if ((b0 & 0x20) != 0) b |= Btn.A; if ((b0 & 0x40) != 0) b |= Btn.B; if ((b0 & 0x80) != 0) b |= Btn.Y;
            if ((b1 & 0x01) != 0) b |= Btn.LB; if ((b1 & 0x02) != 0) b |= Btn.RB; if ((b1 & 0x10) != 0) b |= Btn.View; if ((b1 & 0x20) != 0) b |= Btn.Menu;
            if ((b1 & 0x40) != 0) b |= Btn.LS; if ((b1 & 0x80) != 0) b |= Btn.RS;
            if ((b2 & 0x01) != 0) b |= Btn.Guide; if ((b2 & 0x02) != 0) b |= Btn.Touchpad;
            s.Buttons = b; s.DeriveTriggerButtons();
            s.HasMotion = true;
            s.GyroX = BitConverter.ToInt16(buf, 13) / 1024f; s.GyroY = BitConverter.ToInt16(buf, 15) / 1024f; s.GyroZ = BitConverter.ToInt16(buf, 17) / 1024f;
            s.AccX = BitConverter.ToInt16(buf, 19) / 8192f; s.AccY = BitConverter.ToInt16(buf, 21) / 8192f; s.AccZ = BitConverter.ToInt16(buf, 23) / 8192f;
            s.Touching = (buf[35] & 0x80) == 0;
            s.TouchX = (buf[36] | ((buf[37] & 0x0F) << 8)) / 1919f; s.TouchY = ((buf[37] >> 4) | (buf[38] << 4)) / 941f;
            lock (_gate) { _state = s; _lastTick = Environment.TickCount64; InputReports++; }
        }
    }

    public bool TryRead(out PadState s) { lock (_gate) { s = _state; return InputReports > 0 && Environment.TickCount64 - _lastTick < 2000; } }

    public bool Send()
    {
        if (_write == null || _write.IsInvalid) return false;
        var d = new byte[_outLen];
        d[0] = 0x05; d[1] = 0x07; d[4] = RumbleRight; d[5] = RumbleLeft; d[6] = Red; d[7] = Green; d[8] = Blue;
        bool ok = Hid.WriteFile(_write, d, d.Length, out _, IntPtr.Zero);
        if (ok) OutputReports++; else Error ??= "yazılamadı (" + Marshal.GetLastWin32Error() + ")";
        return ok;
    }

    public void Dispose() { _stop = true; try { RumbleLeft = RumbleRight = 0; Send(); } catch { } _read?.Dispose(); _write?.Dispose(); }
}

using Windows.Gaming.Input; using Windows.Gaming.Input.Custom;
// Razer packets over GIP (Xbox mode): request = GIP message 0x0F, response = 0x10, 60-byte Razer packet
// [status, tx, rem hi, rem lo, proto, size, class, cmd, args..., xor(2..57) at 58, 0]. Format taken from the
// Razer Controller Setup for Xbox capture (2026-11-10). Sends ONLY read commands given on the command line
// as class:cmd:size[:argshex], one at a time, and logs every message the controller returns.
var log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "result.txt")) { AutoFlush = true };
void W(string s) { lock (log) log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
var allowedReads = new HashSet<(byte, byte)> { (0x00, 0x87), (0x00, 0x82), (0x16, 0x90), (0x16, 0x91), (0x0C, 0x86), (0x0C, 0x85) };
GipGameControllerProvider provider = null; var got = new SemaphoreSlim(0);
var form = new Form { Text = "RW3 GIP tunnel read probe", TopMost = true, Width = 420, Height = 120 };
form.Shown += async (_, _) =>
{
    form.Activate();
    try
    {
        GameControllerFactoryManager.RegisterCustomFactoryForGipInterface(new Factory(p => { provider = p; }, W, got), new Guid("AAE22FB7-E167-4908-9A40-FF9C687FA986"));
        for (int i = 0; i < 30 && provider == null; i++) await Task.Delay(100);
        if (provider == null) { W("no provider"); form.Close(); return; }
        byte tx = 0x40;
        foreach (var spec in args)
        {
            var f = spec.Split(':'); byte cls = Convert.ToByte(f[0], 16), cmd = Convert.ToByte(f[1], 16), size = Convert.ToByte(f[2], 16);
            var a = f.Length > 3 ? Convert.FromHexString(f[3]) : Array.Empty<byte>();
            if (!allowedReads.Contains((cls, cmd))) { W($"refused {spec}: not in the read-only allow list"); continue; }
            var pkt = new byte[60]; pkt[1] = tx++; pkt[5] = size; pkt[6] = cls; pkt[7] = cmd; a.CopyTo(pkt, 8);
            byte x = 0; for (int i = 2; i < 58; i++) x ^= pkt[i]; pkt[58] = x;
            W($"send 0x0F {Convert.ToHexString(pkt)}");
            try { provider.SendMessage(GipMessageClass.Command, 0x0F, pkt); } catch (Exception ex) { W($"SendMessage failed: 0x{ex.HResult:X8} {ex.Message}"); }
            await Task.Delay(150);
        }
    }
    catch (Exception ex) { W("ERR " + ex); }
    W("done"); form.Close();
};
Application.Run(form);

sealed class Factory(Action<GipGameControllerProvider> keep, Action<string> w, SemaphoreSlim got) : ICustomGameControllerFactory
{
    public object CreateGameController(IGameControllerProvider provider) { keep((GipGameControllerProvider)provider); w("provider " + provider.HardwareVendorId.ToString("X4") + ":" + provider.HardwareProductId.ToString("X4")); return new Sink(w, got); }
    public void OnGameControllerAdded(IGameController value) { }
    public void OnGameControllerRemoved(IGameController value) { }
}
sealed class Sink(Action<string> w, SemaphoreSlim got) : IGipGameControllerInputSink
{
    public void OnInputResumed(ulong t) { } public void OnInputSuspended(ulong t) { }
    public void OnKeyReceived(ulong t, byte k, bool p) { }
    public void OnMessageReceived(ulong t, GipMessageClass c, byte id, byte seq, byte[] b)
    { w($"recv class {c} id 0x{id:X2} len {b.Length}: {Convert.ToHexString(b)}"); if (id == 0x10) got.Release(); }
}

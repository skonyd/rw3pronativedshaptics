using Windows.Gaming.Input; using Windows.Gaming.Input.Custom; 
// PASSIVE GIP probe (sends nothing to the controller). Registers custom factories for the two unknown GIP
// interfaces in Wolverine firmware 2.04.00 metadata and logs: whether Windows hands us a provider, its
// firmware/hardware info, which interfaces LegacyGipGameControllerProvider reports, and every message the
// controller sends (class, id, bytes). Runs ~25 s with a foreground window, then writes result.txt.
var log = new System.IO.StreamWriter(Path.Combine(AppContext.BaseDirectory, "result.txt")) { AutoFlush = true };
void W(string s) { lock (log) log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
var guids = new Dictionary<string, Guid> {
    ["razer?"] = new("AAE22FB7-E167-4908-9A40-FF9C687FA986"),
    ["unknown"] = new("9776FF56-9BFD-4581-AD45-B645BBA526D6"),
    ["gamepad"] = new("082E402C-07DF-45E1-A5AB-A3127AF197B5"),
    ["navigation"] = new("B8F31FE7-7386-40E9-A9F8-2F21263ACFB7") };
var form = new Form { Text = "RW3 passive GIP probe", TopMost = true, Width = 420, Height = 120 };
form.Shown += async (_, _) =>
{
    form.Activate();
    try
    {
        foreach (var (name, g) in guids.Where(k => k.Key is "razer?" or "unknown"))
        {
            try { GameControllerFactoryManager.RegisterCustomFactoryForGipInterface(new Factory(name, W), g); W($"registered factory for {name} {g}"); }
            catch (Exception ex) { W($"register {name} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }
        }
        await Task.Delay(1500);
        foreach (var raw in RawGameController.RawGameControllers)
        {
            W($"raw controller {raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4} '{raw.DisplayName}' axes {raw.AxisCount} buttons {raw.ButtonCount} switches {raw.SwitchCount}");
        }
        W("listening 20 s; press buttons / pull triggers if you like");
        await Task.Delay(20000);
    }
    catch (Exception ex) { W("ERR " + ex); }
    W("done"); form.Close();
};
Application.Run(form);

sealed class Factory(string name, Action<string> w) : ICustomGameControllerFactory
{
    public object CreateGameController(IGameControllerProvider provider)
    {
        w($"[{name}] CreateGameController: provider {provider.GetType().Name} {provider.HardwareVendorId:X4}:{provider.HardwareProductId:X4} connected {provider.IsConnected} fw {provider.FirmwareVersionInfo.Major}.{provider.FirmwareVersionInfo.Minor}.{provider.FirmwareVersionInfo.Build}.{provider.FirmwareVersionInfo.Revision}");
        return new Sink(name, w);
    }
    public void OnGameControllerAdded(IGameController value) => w($"[{name}] added");
    public void OnGameControllerRemoved(IGameController value) => w($"[{name}] removed");
}
sealed class Sink(string name, Action<string> w) : IGipGameControllerInputSink
{
    public void OnInputResumed(ulong timestamp) => w($"[{name}] input resumed");
    public void OnInputSuspended(ulong timestamp) => w($"[{name}] input suspended");
    public void OnKeyReceived(ulong timestamp, byte keyCode, bool isPressed) => w($"[{name}] key 0x{keyCode:X2} {isPressed}");
    public void OnMessageReceived(ulong timestamp, GipMessageClass messageClass, byte messageId, byte sequenceId, byte[] messageBuffer) =>
        w($"[{name}] msg class {messageClass} id 0x{messageId:X2} seq {sequenceId} len {messageBuffer.Length}: {Convert.ToHexString(messageBuffer.AsSpan(0, Math.Min(64, messageBuffer.Length)))}");
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RW3ProNativeDSHaptics;

internal static class BodyFocusProbe
{
    [StructLayout(LayoutKind.Sequential)]private struct Vibration{public ushort Left,Right;}
    [StructLayout(LayoutKind.Sequential)]private struct PadState{public uint Packet;public ushort Buttons;public byte LT,RT;public short LX,LY,RX,RY;}
    [DllImport("xinput1_4.dll")]private static extern uint XInputGetState(uint index,out PadState state);
    public static int[] VisibleSlots()=>Enumerable.Range(0,4).Where(i=>XInputGetState((uint)i,out _)==0).ToArray();
    [DllImport("xinput1_4.dll")]private static extern uint XInputSetState(uint index,ref Vibration vibration);
    [DllImport("xinput1_4.dll")]private static extern void XInputEnable([MarshalAs(UnmanagedType.Bool)]bool enabled);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput9_1_0.dll",EntryPoint="XInputSetState")]private static extern uint LegacySetState(uint index,ref Vibration vibration);
    public static async Task RunAsync(string backend,string path,int durationSeconds=20)
    {
        if(durationSeconds is <1 or >120)throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if(backend is not("xinput" or "xinput-enabled" or "xinput-legacy" or "gameinput" or "wgi"))throw new ArgumentException("Unknown body focus backend");
        var usb=UsbGamepadMonitor.Read();
        if(!usb.ScanSucceeded || !usb.WolverineUsbNode || usb.WolverineWiredPcNode)throw new InvalidOperationException("Xbox-mode Wolverine required.");
        var slots=VisibleSlots();
        if(slots.Length!=1)throw new InvalidOperationException("Unique XInput slot required.");
        int index=slots[0];
        using var gi=new GameInputRumble();
        Windows.Gaming.Input.Gamepad? wgi=null;
        int writes=0,errors=0;var start=DateTimeOffset.UtcNow;
        try{
            if(backend=="gameinput" && !gi.TryAcquire())throw new InvalidOperationException(gi.LastError);
            if(backend=="wgi"){
                // WinRT enumeration may arrive asynchronously. Bind by actual VID/PID, not list order.
                for(int attempt=0;attempt<20;attempt++){
                    var candidates=Windows.Gaming.Input.Gamepad.Gamepads.Where(p=>{
                        var raw=Windows.Gaming.Input.RawGameController.FromGameController(p);
                        return raw.HardwareVendorId==0x1532 && raw.HardwareProductId==0x0A3F;
                    }).ToArray();
                    if(candidates.Length>1)throw new InvalidOperationException("Multiple WGI Wolverines: ambiguous.");
                    if(candidates.Length==1){wgi=candidates[0];break;}
                    await Task.Delay(100);
                }
                if(wgi==null)throw new InvalidOperationException("WGI Wolverine 1532:0A3F unavailable.");
            }
            for(int i=0;i<durationSeconds*20;i++){
                if(backend.StartsWith("xinput")) {if(backend=="xinput-enabled")XInputEnable(true);var v=new Vibration{Left=(ushort)(65535*.4),Right=(ushort)(65535*.4)};uint result=backend=="xinput-legacy"?LegacySetState((uint)index,ref v):XInputSetState((uint)index,ref v);if(result!=0)errors++;else writes++;}
                else if(wgi!=null){wgi.Vibration=new Windows.Gaming.Input.GamepadVibration{LeftMotor=.4,RightMotor=.4};writes++;}
                else {gi.Set(.4,.4,0,0);writes=(int)gi.Writes;if(!gi.HasDevice)throw new InvalidOperationException(gi.LastError);}
                await Task.Delay(50);
            }
        }finally{
            if(backend.StartsWith("xinput")){var zero=new Vibration();if(backend=="xinput-legacy")LegacySetState((uint)index,ref zero);else XInputSetState((uint)index,ref zero);}else if(wgi!=null)wgi.Vibration=default;else gi.Set(0,0,0,0);
            File.WriteAllText(path,JsonSerializer.Serialize(new {Backend=backend,StartUtc=start,EndUtc=DateTimeOffset.UtcNow,DurationSeconds=durationSeconds,BodyLevel=.4,TriggerLevels=0,Slot=index,Writes=writes,Errors=errors,PhysicalResult="User feedback required"},new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine($"{backend} completed{durationSeconds}s writes={writes} errors={errors}");
    }
}

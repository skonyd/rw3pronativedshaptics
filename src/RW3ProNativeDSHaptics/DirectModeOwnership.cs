using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Who drives the direct-mode pad: RW3 in the user's session, or the login bridge (SYSTEM, started at boot, so the pad
/// works on the Windows sign-in screen; docs/WINUSB_DIRECT_MODE.md). WinUSB opens a device only once, so they hand it
/// over through two Global objects, readable by SYSTEM and administrators only:
/// <list type="bullet">
/// <item>the owner mutex: whoever holds it may open the pad;</item>
/// <item>the release event: RW3 sets it to ask the login bridge to let go.</item>
/// </list>
/// RW3 holds the mutex on a background thread until the process ends (exit, crash or sign-out all release it, an
/// abandoned mutex counts as released), and the login bridge takes the pad back then.
/// </summary>
public static class DirectModeOwnership
{
    public const string MutexName = @"Global\RW3ProNativeDSHaptics.DirectMode.Owner";
    public const string ReleaseName = @"Global\RW3ProNativeDSHaptics.DirectMode.Release";
    /// <summary>SYSTEM and administrators only: both sides run elevated, nothing else may block or steal the pad.</summary>
    public const string Sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)";

    public const uint WaitObject0 = 0, WaitAbandoned = 0x80, WaitTimeout = 0x102;

    private static readonly object Gate = new();
    private static volatile bool s_held;
    private static long s_nextTry;

    /// <summary>RW3 holds the pad for this process's lifetime.</summary>
    public static bool HeldBySession => s_held;

    /// <summary>
    /// RW3 in the user's session: asks the login bridge (if it runs) to let go and takes the owner mutex for the rest of
    /// this process. Returns at once when already held; waits at most <paramref name="timeoutMs"/>, and after a failure
    /// does not wait again for 5 s (callers retry every second).
    /// </summary>
    public static bool AcquireForSession(int timeoutMs = 8000)
    {
        if (s_held) return true;
        lock (Gate)
        {
            if (s_held) return true;
            if (Environment.TickCount64 < s_nextTry) return false;
            var done = new ManualResetEventSlim();
            bool ok = false;
            var holder = new Thread(() =>
            {
                IntPtr mutex = Create(isMutex: true), release = Create(isMutex: false);
                if (mutex == IntPtr.Zero) { done.Set(); return; }
                if (release != IntPtr.Zero) SetEvent(release);
                uint r = WaitForSingleObject(mutex, (uint)timeoutMs);
                ok = r is WaitObject0 or WaitAbandoned;
                if (release != IntPtr.Zero) { ResetEvent(release); CloseHandle(release); }
                done.Set();
                if (!ok) { CloseHandle(mutex); return; }
                Thread.Sleep(Timeout.Infinite);   // owned until the process ends
            }) { IsBackground = true, Name = "RW3 direct-mode owner" };
            holder.Start();
            done.Wait(timeoutMs + 1000);
            if (ok) s_held = true; else s_nextTry = Environment.TickCount64 + 5000;
            return ok;
        }
    }

    /// <summary>Creates (or opens) the owner mutex or the release event (auto reset) with <see cref="Sddl"/>; zero on failure.</summary>
    public static IntPtr Create(bool isMutex) => isMutex ? CreateNamed(MutexName, mutex: true, manualReset: false) : CreateNamed(ReleaseName, mutex: false, manualReset: false);

    /// <summary>Another Global event with the same SYSTEM/administrators-only DACL (LoginBridgeTask's stop event).</summary>
    public static IntPtr CreateNamedEvent(string name, bool manualReset) => CreateNamed(name, mutex: false, manualReset);

    private static IntPtr CreateNamed(string name, bool mutex, bool manualReset)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out IntPtr sd, IntPtr.Zero)) return IntPtr.Zero;
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd, Inherit = false };
            return mutex ? CreateMutexW(ref sa, false, name) : CreateEventW(ref sa, manualReset, false, name);
        }
        finally { LocalFree(sd); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMutexW(ref SecurityAttributes sa, bool initialOwner, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(ref SecurityAttributes sa, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReleaseMutex(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
}

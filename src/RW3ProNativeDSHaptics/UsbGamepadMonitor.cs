using System;
using System.Collections.Generic;
using System.Management;

namespace RW3ProNativeDSHaptics;

/// <summary>
/// Windows PnP snapshot; no kernel driver, registry mutation or external process.
/// Only *present* USB enumerator nodes count; XInput virtual controllers are not
/// treated as physically connected Sony/Razer hardware.
/// </summary>
public static class UsbGamepadMonitor
{
    public static UsbGamepadPresence Read()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return UsbGamepadPresence.Unknown("USB PnP Windows üzerinde kullanılabilir");
            var ids = new List<string>();
            using var search = new ManagementObjectSearcher(
                "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE Present = TRUE");
            using var results = search.Get();
            foreach (ManagementBaseObject? result in results)
            {
                if (result?["PNPDeviceID"] is string id &&
                    id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase))
                    ids.Add(id);
            }
            return UsbGamepadClassifier.Classify(ids);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or InvalidOperationException or
            System.Runtime.InteropServices.ExternalException)
        {
            return UsbGamepadPresence.Unknown("Windows PnP sorgusu okunamadı: " + ex.Message);
        }
    }
}

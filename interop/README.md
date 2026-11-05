# Optional vendor Interhaptics native libraries

The experimental `SensaStereoPrototype` uses the officially documented Interhaptics engine and Razer provider. The project **does not distribute** `HAR.dll`, `Interhaptics.RazerProvider.dll` or `RzInterHaptics.dll`.

Obtain compatible x64 libraries legitimately from the [WYVRN Interhaptics SDK](https://github.com/WyvrnOfficial/Interhaptics_Unity_CoreSDK) according to its license. If licensed to use them outside a Unity application, copy HAR.dll and Interhaptics.RazerProvider.dll to the deployed application directory and ensure official runtime prerequisites are installed. Otherwise use Razer's TechDemo for hardware-only testing.

These libraries and driver bits are third-party dependencies and not assumed to exist on a normal user's Windows installation. In their absence the Native bridge reports a clear failure and does not fall back to ordinary rumble.

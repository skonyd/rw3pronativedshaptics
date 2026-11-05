using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace RW3ProNativeDSHaptics;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd";
        _singleInstance = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            MessageBox.Show("RW3 Pro Haptics zaten çalışıyor. Sistem tepsisindeki simgesini açın.", "RW3 Pro Haptics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

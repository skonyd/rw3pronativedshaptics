using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RW3ProNativeDSHaptics;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Local\\RW3ProNativeDSHaptics-86c4b6ce-01e6-4f34-95aa-e9f43bd073fd";
        _singleInstance = new Mutex(true, mutexName, out bool created);
        if (!created)
        {
            System.Windows.MessageBox.Show("RW3 Pro Haptics zaten çalışıyor. Sistem tepsisindeki simgesini açın.", "RW3 Pro Haptics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // CI / packaged-app smoke check: no device, window or vendor DLL needed.
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            bool valid = NativeStereoFixture.ValidateSeparation(HapticTestChannel.Left) &&
                NativeStereoFixture.ValidateSeparation(HapticTestChannel.Right) &&
                NativePcmTestPlayer.VerifyIsolatedChannel(HapticTestChannel.Left) &&
                NativePcmTestPlayer.VerifyIsolatedChannel(HapticTestChannel.Right);
            Shutdown(valid ? 0 : 1);
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsService.Error("Unhandled UI exception", args.Exception);
            try
            {
                if (MainWindow is MainWindow main) main.RecoverFromUnexpectedError(args.Exception);
                System.Windows.MessageBox.Show(
                    "Beklenmeyen hata kaydedildi. Güvenlik için titreşim işlemi durduruldu.\n" + args.Exception.Message,
                    "RW3 Pro · Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            }
            catch
            {
                args.Handled = false;
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsService.Error("Unobserved background task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                DiagnosticsService.Error("Fatal unhandled exception", ex);
        };
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

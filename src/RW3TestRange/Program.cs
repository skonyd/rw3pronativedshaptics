using System.Windows;

namespace RW3TestRange;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            SessionLog.Current?.Event("crash", new { error = e.Exception.ToString() });
            MessageBox.Show(e.Exception.Message, "Şişe Atışı", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        app.Run(new MainWindow(args));
    }
}

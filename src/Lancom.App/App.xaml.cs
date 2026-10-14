using System.Windows;

namespace Lancom.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static bool _hasMutexOwnership;

    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            LogAndShowFatalError("WPF Dispatcher Error", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogAndShowFatalError("Fatal Application Error", ex);
            }
        };
    }

    private static void LogAndShowFatalError(string title, Exception ex)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDir = System.IO.Path.Combine(localAppData, "Programs", "Lancom");
            System.IO.Directory.CreateDirectory(appDir);
            string logPath = System.IO.Path.Combine(appDir, "crash.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{title}] {ex}\n\n");
        }
        catch { }

        MessageBox.Show(
            $"{title}:\n{ex.Message}\n\nDetails have been logged to %LOCALAPPDATA%\\Programs\\Lancom\\crash.log",
            "Lancom Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Local\\Lancom_SingleInstance";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);
        _hasMutexOwnership = isNewInstance;

        if (!isNewInstance)
        {
            MessageBox.Show("Lancom is already running on this machine.", "Lancom", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Ensure user settings folder exists in %LOCALAPPDATA%\Programs\Lancom
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDir = System.IO.Path.Combine(localAppData, "Programs", "Lancom");
            if (!System.IO.Directory.Exists(appDir))
            {
                System.IO.Directory.CreateDirectory(appDir);
            }
        }
        catch { }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hasMutexOwnership)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

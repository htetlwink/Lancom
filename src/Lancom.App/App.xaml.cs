using System.Windows;

namespace Lancom.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static bool _hasMutexOwnership;

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

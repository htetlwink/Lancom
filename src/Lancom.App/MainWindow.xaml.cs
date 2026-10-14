using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Lancom.Core.Network;
using Lancom.Windows.UI;

namespace Lancom.App;

public partial class MainWindow : Window
{
    private TrayIconManager? _trayManager;
    private bool _isExplicitExit;
    private bool _hasShownTrayBalloon;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (source != null)
        {
            _trayManager = new TrayIconManager();
            source.AddHook(_trayManager.ProcessWindowMessage);
            _trayManager.Initialize(hwnd, "Lancom (Online)");
            _trayManager.OnTrayLeftClick += RestoreWindow;
            _trayManager.OnTrayRightClick += ShowTrayContextMenu;
        }

        if (ViewModel != null)
        {
            ViewModel.OnPageAlertReceived += (senderName, msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _trayManager?.ShowBalloon($"📢 {senderName} paged you!", msg);
                });
            };
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        if (!_hasShownTrayBalloon)
        {
            _hasShownTrayBalloon = true;
            _trayManager?.ShowBalloon("Lancom Minimized", "Lancom is running in the background. Hold Caps Lock or Mouse 4 to talk anytime!");
        }
    }

    public void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Focus();
    }

    private void ShowTrayContextMenu(int x, int y)
    {
        var menu = new ContextMenu();

        var itemShow = new MenuItem { Header = "🎙️ Show Lancom HUD", FontWeight = FontWeights.Bold };
        itemShow.Click += (s, e) => RestoreWindow();
        menu.Items.Add(itemShow);

        var itemSettings = new MenuItem { Header = "⚙️ Settings" };
        itemSettings.Click += (s, e) =>
        {
            RestoreWindow();
            if (ViewModel != null) ViewModel.IsSettingsOpen = true;
        };
        menu.Items.Add(itemSettings);

        menu.Items.Add(new Separator());

        var itemExit = new MenuItem { Header = "🛑 Quit Lancom Completely" };
        itemExit.Click += (s, e) => ExitApplication();
        menu.Items.Add(itemExit);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = x;
        menu.VerticalOffset = y;
        menu.IsOpen = true;
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        _trayManager?.Dispose();
        _trayManager = null;
        Close();
        Application.Current?.Shutdown();
    }

    private void PttButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is IInputElement element)
        {
            element.CaptureMouse();
        }
        ViewModel.TriggerPttManual(true);
    }

    private void PttButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is IInputElement element && element.IsMouseCaptured)
        {
            element.ReleaseMouseCapture();
        }
        ViewModel.TriggerPttManual(false);
    }

    private void Channel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag != null)
        {
            if (byte.TryParse(btn.Tag.ToString(), out byte channel))
            {
                ViewModel.SwitchChannel(channel);
            }
        }
    }

    private void PagePeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PeerInfo peer)
        {
            ViewModel.PagePeer(peer);
        }
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ReplayLast();
    }

    private void PlayVoicemail_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PlayLatestVoicemail();
    }

    private void DismissVoicemail_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissVoicemails();
    }

    private void QuitApp_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void RecordKey_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StartRebindingKey();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSettings();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSettingsOpen = false;
    }

    private void SettingsDrawer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = false;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel != null && ViewModel.IsSettingsOpen)
        {
            if (SettingsDrawer != null && SettingsToggleButton != null)
            {
                Point drawerPos = e.GetPosition(SettingsDrawer);
                Point btnPos = e.GetPosition(SettingsToggleButton);

                bool insideDrawer = drawerPos.X >= 0 && drawerPos.X <= SettingsDrawer.ActualWidth &&
                                    drawerPos.Y >= 0 && drawerPos.Y <= SettingsDrawer.ActualHeight;

                bool insideBtn = btnPos.X >= 0 && btnPos.X <= SettingsToggleButton.ActualWidth &&
                                 btnPos.Y >= 0 && btnPos.Y <= SettingsToggleButton.ActualHeight;

                if (!insideDrawer && !insideBtn)
                {
                    ViewModel.IsSettingsOpen = false;
                }
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel != null && ViewModel.IsSettingsOpen)
        {
            ViewModel.IsSettingsOpen = false;
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayManager?.Dispose();
        _trayManager = null;
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}

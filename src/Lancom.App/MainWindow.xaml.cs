using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Lancom.Core.Messaging;
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

            ViewModel.DisplayMessages.CollectionChanged += (s, ev) =>
            {
                ScrollChatToEnd();
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

    private void WhisperPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is PeerInfo peer)
        {
            ViewModel.SetWhisperPeer(peer);
        }
    }

    private void ClearWhisper_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearWhisper();
    }

    private void RosterTab_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedDeckTab = 0;
    }

    private void ChatTab_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedDeckTab = 1;
        ScrollChatToEnd();
    }

    private void SendMessage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SendTextMessage();
        ChatInputTextBox.Focus();
        ScrollChatToEnd();
    }

    private void ChatInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            ViewModel.SendTextMessage();
            ScrollChatToEnd();
        }
    }

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select File, Photo, or Video to Send",
            Filter = "All Files (*.*)|*.*|Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Videos (*.mp4;*.mkv;*.avi;*.mov;*.webm)|*.mp4;*.mkv;*.avi;*.mov;*.webm",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.SelectedDeckTab = 1;
            foreach (var file in dlg.FileNames)
            {
                ViewModel.SendFile(file);
            }
            ScrollChatToEnd();
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ChatMessage msg)
        {
            ViewModel.OpenDownloadedFile(msg);
        }
    }

    private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ChatMessage msg)
        {
            ViewModel.OpenDownloadedFile(msg);
        }
    }

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenDownloadsFolder();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                ViewModel.SelectedDeckTab = 1;
                foreach (var file in files)
                {
                    if (File.Exists(file))
                    {
                        ViewModel.SendFile(file);
                    }
                }
                ScrollChatToEnd();
            }
        }
    }

    private void ScrollChatToEnd()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            ChatScrollViewer?.ScrollToEnd();
        });
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
            return;
        }

        // Ctrl+V paste clipboard screenshot / image
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V)
        {
            try
            {
                if (Clipboard.ContainsImage() && ViewModel != null)
                {
                    ViewModel.SelectedDeckTab = 1;
                    ViewModel.SendClipboardImage();
                    ScrollChatToEnd();
                    e.Handled = true;
                    return;
                }
            }
            catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayManager?.Dispose();
        _trayManager = null;
        ViewModel?.Dispose();
        base.OnClosed(e);
    }
}

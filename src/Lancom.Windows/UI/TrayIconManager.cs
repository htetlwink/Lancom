using System.Runtime.InteropServices;

namespace Lancom.Windows.UI;

/// <summary>
/// Provides zero-privilege, native Win32 system tray (notification area) icon management
/// using pure Shell_NotifyIconW via P/Invoke. Requires ZERO administrator access.
/// </summary>
public class TrayIconManager : IDisposable
{
    #region Win32 Constants
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETFOCUS = 0x00000003;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_STATE = 0x00000008;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_GUID = 0x00000020;
    private const uint NIF_REALTIME = 0x00000040;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NIIF_NONE = 0x00000000;
    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_ERROR = 0x00000003;
    private const uint NIIF_USER = 0x00000004;
    private const uint NIIF_NOSOUND = 0x00000010;
    private const uint NIIF_LARGE_ICON = 0x00000020;

    private const uint WM_USER = 0x0400;
    public const uint WM_TRAYICON = WM_USER + 101; // 0x0465 (1125 decimal)

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_CONTEXTMENU = 0x007B;

    private static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);
    #endregion

    #region Win32 P/Invoke & Structures
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
    #endregion

    #region Private Fields
    private readonly object _syncLock = new();
    private readonly uint _iconId;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private string _tooltip = string.Empty;
    private bool _isCreated;
    private bool _disposed;
    private long _lastRightClickTimeMs;
    #endregion

    #region Events
    /// <summary>
    /// Raised when the user left-clicks the tray icon.
    /// </summary>
    public event Action? OnTrayLeftClick;

    /// <summary>
    /// Raised when the user right-clicks the tray icon, passing current screen coordinates (x, y)
    /// suitable for displaying a context menu.
    /// </summary>
    public event Action<int, int>? OnTrayRightClick;
    #endregion

    #region Public Properties
    /// <summary>
    /// Indicates whether the tray icon has been created in the Windows notification area.
    /// </summary>
    public bool IsCreated => _isCreated;

    /// <summary>
    /// The icon identifier passed to Shell_NotifyIcon.
    /// </summary>
    public uint IconId => _iconId;

    /// <summary>
    /// The associated window handle.
    /// </summary>
    public IntPtr Hwnd => _hwnd;
    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManager"/> class.
    /// </summary>
    /// <param name="iconId">Unique icon identifier (defaults to 1001).</param>
    public TrayIconManager(uint iconId = 1001)
    {
        _iconId = iconId;
    }

    /// <summary>
    /// Initializes and displays the system tray icon for the specified window handle.
    /// </summary>
    /// <param name="hwnd">The window handle receiving notification messages (WM_USER + 101).</param>
    /// <param name="tooltip">Tooltip string displayed on mouse hover (up to 127 characters).</param>
    /// <param name="hIcon">Custom icon handle, or IntPtr.Zero to use the default application icon.</param>
    public void Initialize(IntPtr hwnd, string tooltip, IntPtr hIcon = default)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("Window handle cannot be zero.", nameof(hwnd));
        }

        lock (_syncLock)
        {
            if (_isCreated)
            {
                Remove();
            }

            _hwnd = hwnd;
            _tooltip = tooltip ?? string.Empty;

            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
            }
            _hIcon = hIcon;

            var data = CreateNotifyIconData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYICON;
            data.hIcon = _hIcon;
            data.szTip = TruncateString(_tooltip, 127);

            bool success = Shell_NotifyIcon(NIM_ADD, ref data);
            if (success)
            {
                _isCreated = true;
            }
        }
    }

    /// <summary>
    /// Updates the tooltip text of the system tray icon.
    /// </summary>
    /// <param name="tooltip">The updated tooltip text.</param>
    public void UpdateTooltip(string tooltip)
    {
        lock (_syncLock)
        {
            _tooltip = tooltip ?? string.Empty;
            if (!_isCreated || _hwnd == IntPtr.Zero) return;

            var data = CreateNotifyIconData();
            data.uFlags = NIF_TIP;
            data.szTip = TruncateString(_tooltip, 127);

            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// Updates the icon image of the system tray icon.
    /// </summary>
    /// <param name="hIcon">Custom icon handle, or IntPtr.Zero for default application icon.</param>
    public void UpdateIcon(IntPtr hIcon)
    {
        lock (_syncLock)
        {
            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
            }
            _hIcon = hIcon;

            if (!_isCreated || _hwnd == IntPtr.Zero) return;

            var data = CreateNotifyIconData();
            data.uFlags = NIF_ICON;
            data.hIcon = _hIcon;

            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// Shows a standard Windows balloon notification from the tray icon.
    /// </summary>
    /// <param name="title">Balloon notification title (up to 63 characters).</param>
    /// <param name="message">Balloon notification text body (up to 255 characters).</param>
    /// <param name="timeoutMs">Timeout in milliseconds (typically 1000 to 30000).</param>
    public void ShowBalloon(string title, string message, int timeoutMs = 3000)
    {
        lock (_syncLock)
        {
            if (!_isCreated || _hwnd == IntPtr.Zero) return;

            var data = CreateNotifyIconData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = TruncateString(title ?? string.Empty, 63);
            data.szInfo = TruncateString(message ?? string.Empty, 255);
            data.uTimeoutOrVersion = (uint)Math.Clamp(timeoutMs, 1000, 30000);
            data.dwInfoFlags = NIIF_INFO;

            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// Removes the tray icon from the notification area.
    /// </summary>
    public void Remove()
    {
        lock (_syncLock)
        {
            if (!_isCreated || _hwnd == IntPtr.Zero) return;

            var data = CreateNotifyIconData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _isCreated = false;
        }
    }

    /// <summary>
    /// Message processing hook for WM_USER + 101 and shell restart events.
    /// Wire into HwndSource hook: hwndSource.AddHook(trayManager.ProcessWindowMessage);
    /// </summary>
    public IntPtr ProcessWindowMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            uint eventMsg = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
            if (eventMsg == 0)
            {
                eventMsg = unchecked((uint)lParam.ToInt64());
            }

            switch (eventMsg)
            {
                case WM_LBUTTONUP:
                    handled = true;
                    OnTrayLeftClick?.Invoke();
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    handled = true;
                    long now = Environment.TickCount64;
                    // Debounce closely spaced right-click events
                    if (now - _lastRightClickTimeMs > 150)
                    {
                        _lastRightClickTimeMs = now;
                        SetForegroundWindow(hwnd);
                        if (GetCursorPos(out POINT pt))
                        {
                            OnTrayRightClick?.Invoke(pt.X, pt.Y);
                        }
                        else
                        {
                            OnTrayRightClick?.Invoke(0, 0);
                        }
                    }
                    return IntPtr.Zero;
            }
        }
        else if (WM_TASKBARCREATED != 0 && msg == WM_TASKBARCREATED)
        {
            // Restore icon if explorer.exe crashed and restarted
            lock (_syncLock)
            {
                if (_isCreated && _hwnd != IntPtr.Zero)
                {
                    var data = CreateNotifyIconData();
                    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                    data.uCallbackMessage = WM_TRAYICON;
                    data.hIcon = _hIcon;
                    data.szTip = TruncateString(_tooltip, 127);
                    Shell_NotifyIcon(NIM_ADD, ref data);
                }
            }
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Overload for WPF HwndSourceHook delegate compatibility (takes signed int msg).
    /// </summary>
    public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        return ProcessWindowMessage(hwnd, unchecked((uint)msg), wParam, lParam, ref handled);
    }

    #region Helper Methods
    private NOTIFYICONDATA CreateNotifyIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _iconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length > maxLength ? value.Substring(0, maxLength) : value;
    }
    #endregion

    #region IDisposable
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            try
            {
                Remove();
            }
            catch
            {
                // Suppress unmanaged cleanup failures during disposal
            }
            _disposed = true;
        }
    }

    ~TrayIconManager()
    {
        Dispose(false);
    }
    #endregion
}

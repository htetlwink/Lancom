using System.Runtime.InteropServices;

namespace Lancom.Windows.Input;

public enum PttTriggerType
{
    KeyboardKey,
    MouseButton4,
    MouseButton5
}

public class RawInputPttHook : IDisposable
{
    #region Win32 Native Constants & Structs
    private const int WM_INPUT = 0x00FF;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RID_INPUT = 0x10000003;

    private const int RIM_TYPEKEYBOARD = 1;
    private const int RIM_TYPEMOUSE = 0;

    private const ushort RI_KEY_MAKE = 0;
    private const ushort RI_KEY_BREAK = 1;

    private const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
    private const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
    private const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
    private const ushort RI_MOUSE_BUTTON_5_UP = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public uint ulButtons;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    #endregion

    private enum ActiveTrigger
    {
        None,
        Keyboard,
        Mouse4,
        Mouse5
    }

    private IntPtr _hwndHidden;
    private WndProcDelegate? _wndProcDelegate;
    private bool _isTransmitting;
    private readonly Timer _watchdogTimer;
    private int _targetVKey = 0x14; // Default: Caps Lock (0x14)
    private PttTriggerType _triggerType = PttTriggerType.KeyboardKey;
    private ActiveTrigger _activeTrigger = ActiveTrigger.None;

    public event Action<bool>? OnPttStateChanged; // true = Pressed, false = Released
    public event Action<int>? OnRebindKeyCaptured;
    public bool IsTransmitting => _isTransmitting;
    public bool IsListeningForRebind { get; set; }

    public int TargetVKey
    {
        get => _targetVKey;
        set => _targetVKey = value;
    }

    public PttTriggerType TriggerType
    {
        get => _triggerType;
        set => _triggerType = value;
    }

    public RawInputPttHook()
    {
        InitializeMessageWindow();
        RegisterRawDevices();

        // 50ms stuck-transmit watchdog: polls hardware key state while transmitting
        _watchdogTimer = new Timer(WatchdogCheck, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void InitializeMessageWindow()
    {
        try
        {
            _wndProcDelegate = CustomWndProc;
            string className = "LancomPttHiddenWindow_" + Guid.NewGuid().ToString("N");

            WNDCLASSEX wcx = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProcDelegate,
                hInstance = GetModuleHandle(null),
                lpszClassName = className
            };

            RegisterClassEx(ref wcx);
            // HWND_MESSAGE is (IntPtr)(-3)
            _hwndHidden = CreateWindowEx(0, className, "LancomPttMsgReceiver", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wcx.hInstance, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RawInput HWND Init Error] {ex.Message}");
        }
    }

    private void RegisterRawDevices()
    {
        if (_hwndHidden == IntPtr.Zero) return;

        RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[2];

        // Keyboard
        rid[0].usUsagePage = 0x01; // Generic Desktop
        rid[0].usUsage = 0x06;     // Keyboard
        rid[0].dwFlags = RIDEV_INPUTSINK;
        rid[0].hwndTarget = _hwndHidden;

        // Mouse (for Mouse 4 / Mouse 5 side buttons)
        rid[1].usUsagePage = 0x01; // Generic Desktop
        rid[1].usUsage = 0x02;     // Mouse
        rid[1].dwFlags = RIDEV_INPUTSINK;
        rid[1].hwndTarget = _hwndHidden;

        bool success = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!success)
        {
            Console.WriteLine($"[RawInput Register Error] Win32 Error: {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            ProcessRawInput(lParam);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint dwSize = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dwSize, headerSize);
        if (dwSize == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref dwSize, headerSize) == dwSize)
            {
                RAWINPUTHEADER header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                IntPtr payloadPtr = IntPtr.Add(buffer, (int)headerSize);

                if (header.dwType == RIM_TYPEKEYBOARD)
                {
                    RAWKEYBOARD keyboard = Marshal.PtrToStructure<RAWKEYBOARD>(payloadPtr);

                    if (IsListeningForRebind)
                    {
                        if ((keyboard.Flags & RI_KEY_BREAK) == 0 && keyboard.VKey > 0)
                        {
                            IsListeningForRebind = false;
                            int vkey = keyboard.VKey;
                            // Normalize left/right modifiers if desired
                            if (vkey == 0xA2 || vkey == 0xA3) vkey = 0x11; // Ctrl
                            if (vkey == 0xA4 || vkey == 0xA5) vkey = 0x12; // Alt
                            if (vkey == 0xA0 || vkey == 0xA1) vkey = 0x10; // Shift
                            TargetVKey = vkey;
                            OnRebindKeyCaptured?.Invoke(vkey);
                            return;
                        }
                    }

                    bool isTargetKey = (_targetVKey > 0) && (
                        keyboard.VKey == _targetVKey ||
                        (_targetVKey == 0x11 && (keyboard.VKey == 0xA2 || keyboard.VKey == 0xA3)) ||
                        (_targetVKey == 0x12 && (keyboard.VKey == 0xA4 || keyboard.VKey == 0xA5)) ||
                        (_targetVKey == 0x10 && (keyboard.VKey == 0xA0 || keyboard.VKey == 0xA1))
                    );

                    if (isTargetKey)
                    {
                        bool isUp = (keyboard.Flags & RI_KEY_BREAK) != 0;
                        if (!isUp)
                        {
                            _activeTrigger = ActiveTrigger.Keyboard;
                            SetPttState(true);
                        }
                        else if (_activeTrigger == ActiveTrigger.Keyboard)
                        {
                            _activeTrigger = ActiveTrigger.None;
                            SetPttState(false);
                        }
                    }
                }
                else if (header.dwType == RIM_TYPEMOUSE)
                {
                    RAWMOUSE mouse = Marshal.PtrToStructure<RAWMOUSE>(payloadPtr);
                    ushort flags = mouse.usButtonFlags;

                    // Mouse Button 4 (XBUTTON1)
                    if ((flags & RI_MOUSE_BUTTON_4_DOWN) != 0)
                    {
                        _activeTrigger = ActiveTrigger.Mouse4;
                        SetPttState(true);
                    }
                    else if ((flags & RI_MOUSE_BUTTON_4_UP) != 0 && _activeTrigger == ActiveTrigger.Mouse4)
                    {
                        _activeTrigger = ActiveTrigger.None;
                        SetPttState(false);
                    }
                    // Mouse Button 5 (XBUTTON2)
                    else if ((flags & RI_MOUSE_BUTTON_5_DOWN) != 0)
                    {
                        _activeTrigger = ActiveTrigger.Mouse5;
                        SetPttState(true);
                    }
                    else if ((flags & RI_MOUSE_BUTTON_5_UP) != 0 && _activeTrigger == ActiveTrigger.Mouse5)
                    {
                        _activeTrigger = ActiveTrigger.None;
                        SetPttState(false);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void SetPttState(bool pressed)
    {
        if (_isTransmitting == pressed) return;

        _isTransmitting = pressed;

        if (pressed)
        {
            // Start watchdog polling
            _watchdogTimer.Change(50, 50);
        }
        else
        {
            _activeTrigger = ActiveTrigger.None;
            // Stop watchdog
            _watchdogTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        OnPttStateChanged?.Invoke(_isTransmitting);
    }

    private void WatchdogCheck(object? state)
    {
        if (!_isTransmitting) return;

        bool physicallyHeld = false;

        if (_activeTrigger == ActiveTrigger.Keyboard)
        {
            short keyState = GetAsyncKeyState(_targetVKey);
            physicallyHeld = (keyState & 0x8000) != 0;
        }
        else if (_activeTrigger == ActiveTrigger.Mouse4)
        {
            short mouse4State = GetAsyncKeyState(0x05); // VK_XBUTTON1
            physicallyHeld = (mouse4State & 0x8000) != 0;
        }
        else if (_activeTrigger == ActiveTrigger.Mouse5)
        {
            short mouse5State = GetAsyncKeyState(0x06); // VK_XBUTTON2
            physicallyHeld = (mouse5State & 0x8000) != 0;
        }
        else
        {
            // If triggered manually by UI or unknown, keep transmitting
            return;
        }

        if (!physicallyHeld)
        {
            // Lost release event (e.g. Focus changed or Alt-Tab)! Force release.
            SetPttState(false);
        }
    }

    public void Dispose()
    {
        _watchdogTimer.Dispose();
        if (_hwndHidden != IntPtr.Zero)
        {
            DestroyWindow(_hwndHidden);
            _hwndHidden = IntPtr.Zero;
        }
    }
}

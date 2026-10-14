using System.Runtime.InteropServices;

namespace Lancom.Windows.Input;

/// <summary>
/// Provides zero-privilege, native Win32 idle and AFK (Away From Keyboard) detection
/// using the standard user32!GetLastInputInfo API. Needs ZERO administrator access.
/// </summary>
public class IdleTracker
{
    #region Win32 P/Invoke & Structures
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    #endregion

    /// <summary>
    /// Gets the current idle time in seconds, calculated from the elapsed system ticks
    /// since the user's last keyboard or mouse input: (Environment.TickCount - lii.dwTime) / 1000.
    /// </summary>
    /// <returns>Idle time in seconds, or 0 if retrieval fails.</returns>
    public int GetIdleTimeSeconds()
    {
        var lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>();

        if (!GetLastInputInfo(ref lii))
        {
            return 0;
        }

        // Environment.TickCount is 32-bit millisecond system uptime.
        // Unchecked unsigned subtraction correctly handles the 49.7 day rollover.
        uint currentTicks = unchecked((uint)Environment.TickCount);
        uint elapsedTicks = unchecked(currentTicks - lii.dwTime);

        return (int)(elapsedTicks / 1000u);
    }

    /// <summary>
    /// Determines whether the user has been idle for at least the specified threshold in seconds.
    /// </summary>
    /// <param name="thresholdSeconds">Threshold in seconds (default: 300s / 5 minutes).</param>
    /// <returns>True if the system has been idle for >= thresholdSeconds, otherwise false.</returns>
    public bool IsIdle(int thresholdSeconds = 300)
    {
        return GetIdleTimeSeconds() >= thresholdSeconds;
    }

    /// <summary>
    /// Gets the current idle time in whole minutes.
    /// </summary>
    /// <returns>Idle time in whole minutes.</returns>
    public int GetIdleMinutes()
    {
        return GetIdleTimeSeconds() / 60;
    }

    #region Static Convenience Helpers
    /// <summary>
    /// Shared static instance for convenient application-wide access.
    /// </summary>
    public static IdleTracker Default { get; } = new();

    /// <summary>
    /// Gets the current idle time in seconds using the default tracker.
    /// </summary>
    public static int CurrentIdleSeconds => Default.GetIdleTimeSeconds();

    /// <summary>
    /// Gets the current idle time in minutes using the default tracker.
    /// </summary>
    public static int CurrentIdleMinutes => Default.GetIdleMinutes();

    /// <summary>
    /// Determines whether the user is currently idle using the default tracker.
    /// </summary>
    public static bool CheckIsIdle(int thresholdSeconds = 300) => Default.IsIdle(thresholdSeconds);
    #endregion
}

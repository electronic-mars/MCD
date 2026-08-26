using Windows.Win32;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Mcd.Interop.Display;

/// <summary>Minimal monitor lookups. The full topology service lives in MCD.Core.</summary>
public static class Displays
{
    /// <summary>Full bounds of the primary monitor, in physical pixels.</summary>
    public static RECT PrimaryBounds()
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return PInvoke.GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
    }

    /// <summary>Full bounds of the monitor a window sits on, in physical pixels.</summary>
    public static RECT BoundsFor(nint hwnd)
    {
        HMONITOR monitor = PInvoke.MonitorFromWindow(new HWND(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return PInvoke.GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
    }
}

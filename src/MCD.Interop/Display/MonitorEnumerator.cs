using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Mcd.Interop.Display;

/// <summary>Lists the monitors GDI currently reports.</summary>
public static unsafe class MonitorEnumerator
{
    private const uint MonitorInfoPrimary = 0x00000001;

    public static IReadOnlyList<RawMonitor> Enumerate()
    {
        var found = new List<RawMonitor>(4);
        GCHandle handle = GCHandle.Alloc(found);

        try
        {
            PInvoke.EnumDisplayMonitors(
                default,
                (RECT?)null,
                &Callback,
                new LPARAM(GCHandle.ToIntPtr(handle)));
        }
        finally
        {
            handle.Free();
        }

        return found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static BOOL Callback(HMONITOR monitor, HDC _, RECT* __, LPARAM data)
    {
        var list = (List<RawMonitor>?)GCHandle.FromIntPtr(data.Value).Target;
        if (list is null)
        {
            return false;
        }

        var info = new MONITORINFOEXW
        {
            monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) },
        };

        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            // Skip rather than abort: one unreadable monitor must not hide the rest,
            // and MonitorService rejects the snapshot when the count comes up short.
            return true;
        }

        uint dpiX = 96;
        uint dpiY = 96;
        PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY);

        list.Add(new RawMonitor(
            info.szDevice.ToString(),
            info.monitorInfo.rcMonitor,
            info.monitorInfo.rcWork,
            dpiX,
            (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0));

        return true;
    }
}

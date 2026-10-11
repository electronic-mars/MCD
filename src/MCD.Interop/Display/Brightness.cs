using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Mcd.Interop.Machine;

namespace Mcd.Interop.Display;

/// <summary>One screen whose brightness can be changed, and how bright it is, nought to a hundred.</summary>
/// <param name="Gdi"><c>\.\DISPLAY2</c> - the number Windows shows in its own display settings.</param>
/// <param name="Percent">How bright, as a percentage of what the screen can do.</param>
/// <param name="Embedded">True for a laptop's own panel.</param>
public readonly record struct ScreenBrightness(string Gdi, int Percent, bool Embedded);

/// <summary>
/// The brightness of every screen that will say, and a way to change it.
/// </summary>
/// <remarks>
/// <para>
/// Two different doors. A laptop's own panel is the display driver's: the
/// legacy brightness requests on <c>\\.\LCD</c>, which answer at once and take
/// a hundred levels. Any other monitor is its own: DDC/CI, a conversation over
/// the cable, in which every question takes a tenth of a second or more and a
/// monitor that is asleep or busy does not answer at all.
/// </para>
/// <para>
/// So nothing here waits for a monitor. <see cref="Snapshot"/> answers from
/// what it last heard and, when that is old, sends one reader off to ask
/// again; <see cref="Set"/> remembers the newest value for each screen and one
/// writer per screen drains it, so a slider that sends a value per pixel
/// reaches the cable as the few that the cable can carry, ending on the last.
/// A screen that does not answer once is kept as it was rather than dropped:
/// a list of sliders that loses one now and then is worse than a stale one.
/// </para>
/// </remarks>
public static unsafe class Brightness
{
    /// <summary>Raised on a thread of its own when a screen would not take what it was told.</summary>
    public static event Action<string>? Refused;

    /// <summary>What is known about each screen, from the last time anyone asked it. Never waits.</summary>
    public static IReadOnlyList<ScreenBrightness> Snapshot()
    {
        long now = Environment.TickCount64;

        if (now - _asked > PanelEvery && Interlocked.CompareExchange(ref _reading, 1, 0) == 0)
        {
            bool monitors = now - _heard > MonitorEvery;

            Task.Run(() =>
            {
                try
                {
                    Refresh(monitors);
                }
                finally
                {
                    Volatile.Write(ref _reading, 0);
                }
            });
        }

        lock (Gate)
        {
            return [.. _known.Values.OrderBy(s => s.Gdi, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Asks the screen to change, and remembers that it was asked. The change
    /// happens a moment later on a thread of its own.
    /// </summary>
    public static void Set(string gdi, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        lock (Gate)
        {
            if (_known.TryGetValue(gdi, out ScreenBrightness screen))
            {
                _known[gdi] = screen with { Percent = percent };
            }
        }

        Wanted[gdi] = percent;

        if (!Writing.TryAdd(gdi, true))
        {
            return;
        }

        Task.Run(() => Drain(gdi));
    }

    /// <summary>A step of a screen's own range, from a percentage.</summary>
    public static uint Scale(int percent, uint minimum, uint maximum) =>
        maximum <= minimum
            ? minimum
            : minimum + (uint)Math.Round(Math.Clamp(percent, 0, 100) * (double)(maximum - minimum) / 100);

    /// <summary>A percentage, from a step of a screen's own range.</summary>
    public static int Percent(uint current, uint minimum, uint maximum) =>
        maximum <= minimum
            ? 0
            : (int)Math.Round(Math.Clamp(current - (double)minimum, 0, maximum - minimum) * 100 / (maximum - minimum));

    // A panel answers in a millisecond; a monitor over the cable in a tenth of a
    // second or four, and asking it every second is how one is made to flicker.
    private const long PanelEvery = 900;
    private const long MonitorEvery = 20_000;

    /// <summary>Between two writes to one monitor: the cable has to finish the first.</summary>
    private const int Gap = 80;

    private static readonly Lock Gate = new();
    private static Dictionary<string, ScreenBrightness> _known = new(StringComparer.OrdinalIgnoreCase);
    private static long _asked = -PanelEvery;
    private static long _heard = -MonitorEvery;
    private static int _reading;

    private static readonly ConcurrentDictionary<string, int> Wanted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> Writing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (uint Minimum, uint Maximum)> Ranges = new(StringComparer.OrdinalIgnoreCase);

    private static void Refresh(bool monitors)
    {
        IReadOnlyList<(HMONITOR Handle, string Gdi)> live = Monitors();
        IReadOnlySet<string> embedded = DisplayConfigApi.EmbeddedPanels();

        Dictionary<string, ScreenBrightness> before;

        lock (Gate)
        {
            before = new Dictionary<string, ScreenBrightness>(_known, StringComparer.OrdinalIgnoreCase);
        }

        var next = new Dictionary<string, ScreenBrightness>(StringComparer.OrdinalIgnoreCase);

        foreach ((HMONITOR handle, string gdi) in live)
        {
            bool panel = embedded.Contains(gdi);
            before.TryGetValue(gdi, out ScreenBrightness known);
            bool had = before.ContainsKey(gdi);

            // A screen being written to is not asked: what it says halfway
            // through is the old value, and the slider would jump back to it.
            int? percent = Writing.ContainsKey(gdi) && had ? known.Percent
                : panel ? ReadPanel()
                : monitors || !had ? ReadMonitor(handle, gdi)
                : known.Percent;

            if (percent is null && had)
            {
                percent = known.Percent;
            }

            if (percent is { } value)
            {
                next[gdi] = new ScreenBrightness(gdi, value, panel);
            }
        }

        lock (Gate)
        {
            _known = next;
            _asked = Environment.TickCount64;

            if (monitors)
            {
                _heard = _asked;
            }
        }
    }

    private static void Drain(string gdi)
    {
        try
        {
            while (Wanted.TryRemove(gdi, out int percent))
            {
                if (!Write(gdi, percent))
                {
                    Refused?.Invoke($"{gdi} would not take {percent}");
                }

                Thread.Sleep(Gap);
            }
        }
        catch (Exception e)
        {
            Refused?.Invoke($"{gdi}: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            Writing.TryRemove(gdi, out _);

            // Something arrived between the last look and letting go.
            if (Wanted.TryGetValue(gdi, out int late))
            {
                Set(gdi, late);
            }
        }
    }

    private static bool Write(string gdi, int percent)
    {
        IReadOnlySet<string> embedded = DisplayConfigApi.EmbeddedPanels();

        if (embedded.Contains(gdi))
        {
            return WritePanel(percent);
        }

        foreach ((HMONITOR handle, string name) in Monitors())
        {
            if (name.Equals(gdi, StringComparison.OrdinalIgnoreCase))
            {
                return WriteMonitor(handle, gdi, percent);
            }
        }

        return false;
    }

    private static IReadOnlyList<(HMONITOR Handle, string Gdi)> Monitors()
    {
        var found = new List<(HMONITOR, string)>(4);
        GCHandle handle = GCHandle.Alloc(found);

        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT?)null, &Collect, new LPARAM(GCHandle.ToIntPtr(handle)));
        }
        finally
        {
            handle.Free();
        }

        return found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static BOOL Collect(HMONITOR monitor, HDC _, RECT* __, LPARAM data)
    {
        var list = (List<(HMONITOR, string)>?)GCHandle.FromIntPtr(data.Value).Target;

        var info = new MONITORINFOEXW
        {
            monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) },
        };

        if (list is not null && PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            list.Add((monitor, info.szDevice.ToString()));
        }

        return true;
    }

    // --- a monitor, over DDC/CI -------------------------------------------------

    private static int? ReadMonitor(HMONITOR monitor, string gdi)
    {
        int? percent = null;

        WithPhysical(monitor, physical =>
        {
            uint minimum;
            uint current;
            uint maximum;

            if (PInvoke.GetMonitorBrightness(physical, &minimum, &current, &maximum) != 0)
            {
                Ranges[gdi] = (minimum, maximum);
                percent = Percent(current, minimum, maximum);
                return true;
            }

            return false;
        });

        return percent;
    }

    private static bool WriteMonitor(HMONITOR monitor, string gdi, int percent) =>
        WithPhysical(monitor, physical =>
        {
            if (!Ranges.TryGetValue(gdi, out (uint Minimum, uint Maximum) range))
            {
                uint minimum;
                uint current;
                uint maximum;

                if (PInvoke.GetMonitorBrightness(physical, &minimum, &current, &maximum) == 0)
                {
                    return false;
                }

                range = (minimum, maximum);
                Ranges[gdi] = range;
            }

            return PInvoke.SetMonitorBrightness(physical, Scale(percent, range.Minimum, range.Maximum)) != 0;
        });

    /// <summary>Runs the work on each physical monitor behind a handle until one says it did it.</summary>
    private static bool WithPhysical(HMONITOR monitor, Func<HANDLE, bool> work)
    {
        if (PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) == 0 || count == 0)
        {
            return false;
        }

        var physical = new PHYSICAL_MONITOR[count];

        fixed (PHYSICAL_MONITOR* first = physical)
        {
            if (PInvoke.GetPhysicalMonitorsFromHMONITOR(monitor, count, first) == 0)
            {
                return false;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (work(first[i].hPhysicalMonitor))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                PInvoke.DestroyPhysicalMonitors(count, first);
            }
        }
    }

    // --- a laptop's own panel, through the display driver -----------------------

    private const uint QuerySupported = 0x230494;
    private const uint QueryCurrent = 0x230498;
    private const uint SetCurrent = 0x23049C;

    /// <summary>What the panel's driver offers, as percentages; asked once.</summary>
    private static byte[]? _levels;

    private static HANDLE OpenPanel()
    {
        fixed (char* name = @"\\.\LCD")
        {
            return PInvoke.CreateFile(
                name,
                0xC0000000,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                0,
                default);
        }
    }

    private static bool IsOpen(HANDLE handle) => (nint)handle.Value != 0 && (nint)handle.Value != -1;

    private static int? ReadPanel()
    {
        HANDLE panel = OpenPanel();

        if (!IsOpen(panel))
        {
            return null;
        }

        try
        {
            byte* now = stackalloc byte[3];
            uint returned;

            if (!PInvoke.DeviceIoControl(panel, QueryCurrent, null, 0, now, 3, &returned, null))
            {
                return null;
            }

            // Two readings, one per power source; the one in force is the one
            // for the source the laptop is on.
            return Power.Read() is { Present: true, Plugged: false } ? now[2] : now[1];
        }
        finally
        {
            PInvoke.CloseHandle(panel);
        }
    }

    private static bool WritePanel(int percent)
    {
        HANDLE panel = OpenPanel();

        if (!IsOpen(panel))
        {
            return false;
        }

        try
        {
            byte level = Nearest(panel, percent);

            // Policy 3: both the mains and the battery setting, so that it
            // still holds when the cable is pulled.
            byte* request = stackalloc byte[3] { 3, level, level };
            uint returned;

            return PInvoke.DeviceIoControl(panel, SetCurrent, request, 3, null, 0, &returned, null);
        }
        finally
        {
            PInvoke.CloseHandle(panel);
        }
    }

    /// <summary>The level the panel supports that is closest to the one asked for.</summary>
    private static byte Nearest(HANDLE panel, int percent)
    {
        byte[]? levels = _levels;

        if (levels is null)
        {
            byte* buffer = stackalloc byte[256];
            uint returned;

            levels = PInvoke.DeviceIoControl(panel, QuerySupported, null, 0, buffer, 256, &returned, null)
                ? new ReadOnlySpan<byte>(buffer, (int)returned).ToArray()
                : [];

            _levels = levels;
        }

        if (levels.Length == 0)
        {
            return (byte)percent;
        }

        byte best = levels[0];

        foreach (byte level in levels)
        {
            if (Math.Abs(level - percent) < Math.Abs(best - percent))
            {
                best = level;
            }
        }

        return best;
    }
}

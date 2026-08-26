using System.Collections.Immutable;
using Mcd.Core.Monitors;
using Windows.Win32.Foundation;

namespace Mcd.Tests.Monitors;

/// <summary>Builds monitor snapshots for the reconciler tests.</summary>
internal static class Topology
{
    /// <summary>The laptop panel this program is developed on: 2560x1600 at 125%.</summary>
    public const string LaptopPath = @"\\?\DISPLAY#BOE0AFB#5&11131be0&4&UID4353#{e6f07b5f}";

    /// <summary>
    /// The two external screens on the same desk. They report the same EDID and
    /// differ only in the UID, which is exactly the ambiguity the reconciler must
    /// refuse to guess through.
    /// </summary>
    public const string LeftPath = @"\\?\DISPLAY#RTK2555#5&11131be0&4&UID4354#{e6f07b5f}";

    public const string RightPath = @"\\?\DISPLAY#RTK2555#5&11131be0&4&UID4356#{e6f07b5f}";

    public static MonitorInfo Laptop(bool primary = true) => Make(
        LaptopPath, "BOE 0AFB", "BOE:0AFB", @"\\.\DISPLAY1", 0, 0, 2560, 1600, 120, primary);

    public static MonitorInfo Left(bool primary = false) => Make(
        LeftPath, "RTK 2555", "RTK:2555", @"\\.\DISPLAY2", 2560, -261, 4480, 819, 96, primary);

    public static MonitorInfo Right(bool primary = false) => Make(
        RightPath, "RTK 2555", "RTK:2555", @"\\.\DISPLAY3", 2560, 819, 4480, 1899, 96, primary);

    public static MonitorInfo Make(
        string devicePath,
        string friendlyName,
        string edidKey,
        string gdiName,
        int left,
        int top,
        int right,
        int bottom,
        uint dpi,
        bool primary)
    {
        var bounds = new RECT { left = left, top = top, right = right, bottom = bottom };

        return new MonitorInfo(
            new MonitorIdentity(
                MonitorStableId.FromDevicePath(devicePath),
                devicePath,
                edidKey,
                friendlyName,
                gdiName),
            bounds,
            bounds,
            dpi,
            primary);
    }

    public static AuthoritativeSnapshot Snapshot(params MonitorInfo[] monitors) =>
        new([.. monitors], DateTimeOffset.UnixEpoch);
}

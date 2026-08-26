using Windows.Win32.Foundation;

namespace Mcd.Core.Monitors;

/// <summary>One monitor, identified and measured.</summary>
public sealed record MonitorInfo(
    MonitorIdentity Identity,
    RECT Bounds,
    RECT WorkArea,
    uint Dpi,
    bool IsPrimary)
{
    public MonitorStableId StableId => Identity.StableId;

    public double Scale => Dpi / 96.0;

    public int Width => Bounds.right - Bounds.left;

    public int Height => Bounds.bottom - Bounds.top;

    /// <summary>
    /// Last-resort match when both the device path and EDID have changed: same
    /// name, same size, same scale is almost certainly the same panel.
    /// </summary>
    public string ShapeKey => $"{Identity.FriendlyName}|{Width}x{Height}@{Dpi}";
}

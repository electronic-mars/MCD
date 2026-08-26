using Windows.Win32.Foundation;

namespace Mcd.Interop.AppBar;

/// <summary>
/// Where an auto-hiding bar sits, part-way between hidden and shown.
/// </summary>
/// <remarks>
/// <para>
/// The window never leaves its own monitor. Sliding it off the edge would be
/// simpler, and is what the first version did, but a screen edge is only empty
/// space when there is no second screen against it: on a stacked pair the
/// hidden bar shows along the bottom of the monitor above and swallows the
/// pointer over a strip of it.
/// </para>
/// <para>
/// So the window shrinks against the edge instead, and the bar's contents are
/// pushed out through the near side by <see cref="Offset"/>. The contents keep
/// their full size and are clipped by the window, which is what stops every
/// widget being laid out again on every frame of the slide.
/// </para>
/// </remarks>
public static class AutoHideGeometry
{
    /// <summary>
    /// How much of the bar is on screen, in pixels.
    /// </summary>
    /// <param name="shown">0 for hidden, 1 for fully out.</param>
    /// <param name="margin">
    /// What stays behind when hidden. That sliver is the only thing the pointer
    /// can reach to bring the bar back, so it never comes out as nothing.
    /// </param>
    public static int Visible(int thickness, double shown, int margin)
    {
        int least = Math.Clamp(Math.Max(1, margin), 1, Math.Max(1, thickness));
        double reach = least + (Math.Clamp(shown, 0, 1) * (thickness - least));

        return Math.Clamp((int)Math.Round(reach), least, Math.Max(least, thickness));
    }

    /// <summary>The window's rectangle: against the edge, and never outside the monitor.</summary>
    public static RECT At(AppBarEdge edge, RECT monitor, int thickness, double shown, int margin)
    {
        int visible = Visible(thickness, shown, margin);

        return edge switch
        {
            AppBarEdge.Top => monitor with { bottom = monitor.top + visible },
            AppBarEdge.Bottom => monitor with { top = monitor.bottom - visible },
            AppBarEdge.Left => monitor with { right = monitor.left + visible },
            AppBarEdge.Right => monitor with { left = monitor.right - visible },
            _ => monitor,
        };
    }

    /// <summary>
    /// How far the bar's contents are pushed past the near side of the window,
    /// in pixels, so that the part still on screen is the part nearest the
    /// middle of the desktop.
    /// </summary>
    /// <remarks>
    /// A bar along the top slides down into view, so its lower edge arrives
    /// first and everything above it is off the top of the window. A bar along
    /// the bottom arrives the other way up and needs no offset at all.
    /// </remarks>
    public static int Offset(AppBarEdge edge, int thickness, double shown, int margin) =>
        edge is AppBarEdge.Top or AppBarEdge.Left
            ? thickness - Visible(thickness, shown, margin)
            : 0;
}

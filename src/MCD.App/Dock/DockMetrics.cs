using Mcd.Core.Settings;
using Mcd.Interop.AppBar;

namespace Mcd.App.Dock;

/// <summary>
/// Every number that makes the dock look and feel like the PowerToys one, in a
/// single file so that comparing against upstream is a single diff.
/// </summary>
/// <remarks>
/// Values taken from microsoft/PowerToys,
/// src/modules/cmdpal/Microsoft.CmdPal.UI/Dock/DockSettingsToViews.cs and
/// DockItemControl.xaml (MIT). Copyright (c) Microsoft Corporation.
/// See licenses/PowerToys-MIT.txt.
/// </remarks>
public static class DockMetrics
{
    public const double HorizontalHeightDefault = 38;
    public const double HorizontalHeightCompact = 24;

    /// <summary>A vertical dock has no compact form: 86 DIP is already the minimum readable width.</summary>
    public const double VerticalWidth = 86;

    public const double ItemCornerRadius = 4;

    // PowerToys' own item metrics - MinWidth 32, icon 16, title 12, subtitle 10,
    // text MaxWidth 100 - are deliberately not kept here. They were, unused,
    // beside the sizes actually in force, which made this file read as though
    // the dock used them. What the dock uses is below.

    /// <summary>
    /// How big a reading is drawn, by how thick the bar is.
    /// </summary>
    /// <remarks>
    /// Larger than the sixteen-pixel icon and twelve-point text of the PowerToys
    /// dock. Their sizes were copied here first and the result was legibly worse
    /// than theirs at a glance, because a reading on this bar is a number to be
    /// read across a room rather than a label beside an icon.
    /// </remarks>
    public static double ReadingIcon(DockDensity density) =>
        density == DockDensity.Compact ? 18 : 22;

    public static double ReadingFont(DockDensity density) =>
        density == DockDensity.Compact ? 13 : 14;
    public const double PressScale = 0.81;
    public static readonly TimeSpan HoverCrossfade = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan PressScaleDuration = TimeSpan.FromMilliseconds(90);

    public static readonly TimeSpan AutoHideCollapseDelay = TimeSpan.FromMilliseconds(250);
    /// <summary>How often the pointer is checked against a bar that is away.</summary>
    /// <remarks>
    /// Fast, because this is the delay before the bar reacts to being reached
    /// for, and anything slower feels like the bar is deciding whether to bother.
    /// </remarks>
    public static readonly TimeSpan AutoHideRevealPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>How often it is checked against a bar that is already out.</summary>
    /// <remarks>
    /// Three times slower: all it has to notice is the pointer leaving, and that
    /// is followed by a quarter-second wait anyway. Twenty wakes a second for the
    /// whole time a bar happens to be open is a poor trade on a laptop.
    /// </remarks>
    public static readonly TimeSpan AutoHideLeavePoll = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan SlideReveal = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan SlideCollapse = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan SlideFrameInterval = TimeSpan.FromMilliseconds(8);
    public const int RevealHitTestMargin = 1;

    public static bool IsHorizontal(AppBarEdge edge) =>
        edge is AppBarEdge.Top or AppBarEdge.Bottom;

    /// <summary>Thickness in DIP, before the monitor's scale factor is applied.</summary>
    public static double ThicknessDips(AppBarEdge edge, DockDensity density)
    {
        if (!IsHorizontal(edge))
        {
            return VerticalWidth;
        }

        return density == DockDensity.Compact ? HorizontalHeightCompact : HorizontalHeightDefault;
    }

    /// <summary>Thickness in physical pixels for a window on a monitor with this scale.</summary>
    public static int ThicknessPixels(AppBarEdge edge, DockDensity density, double scale) =>
        (int)Math.Round(ThicknessDips(edge, density) * scale);
}

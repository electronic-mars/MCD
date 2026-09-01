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
    public static double ReadingIcon(DockDensity density, string size = "large") => size switch
    {
        "small" => density == DockDensity.Compact ? 14 : 16,
        "medium" => density == DockDensity.Compact ? 17 : 20,
        _ => density == DockDensity.Compact ? 20 : 24,
    };

    public static double ReadingFont(DockDensity density, string size = "large") => size switch
    {
        "small" => 12,
        "medium" => 13,
        _ => density == DockDensity.Compact ? 14 : 15,
    };

    /// <summary>
    /// What a HoverChip adds around its content, both sides together.
    /// </summary>
    /// <remarks>
    /// Every widget that declares its length has to count this in, or the ink
    /// is wider than the box and the chip's rounded-corner clip shaves the
    /// icon's edge - which is exactly what happened, on every chip whose
    /// arithmetic forgot it, and was found with a ruler on a 96-DPI bar.
    /// </remarks>
    public const double ChipPadding = 6;
    /// <summary>
    /// How far a chip gives under the finger.
    /// </summary>
    /// <remarks>
    /// Barely. A fifth of the chip was a figure moving two points sideways on
    /// a bar whose whole job is to hold figures still; the press has to be
    /// felt, not watched.
    /// </remarks>
    public const double PressScale = 0.96;
    /// <summary>How fast a chip lights under the pointer, and how slowly it lets go.</summary>
    /// <remarks>
    /// Not the same both ways. A pointer crossing a row of seven chips at the
    /// same speed in and out draws a string of lights behind it; a slower
    /// release lets the row settle instead.
    /// </remarks>
    public static readonly TimeSpan HoverCrossfade = TimeSpan.FromMilliseconds(120);

    public static readonly TimeSpan HoverFade = TimeSpan.FromMilliseconds(250);
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
    /// <summary>
    /// One frame of the slide.
    /// </summary>
    /// <remarks>
    /// A screen refreshes every 16.7 ms; asking for a step every 8 put two
    /// steps into some frames and one into others, so the bar arrived at an
    /// even speed by an uneven route - and moved the window 125 times a
    /// second to do it.
    /// </remarks>
    public static readonly TimeSpan SlideFrameInterval = TimeSpan.FromMilliseconds(16);
    public const int RevealHitTestMargin = 1;

    /// <summary>
    /// How long one slot of the bar is.
    /// </summary>
    /// <remarks>
    /// The bar is a row of slots of exactly this size, and every widget occupies
    /// a whole number of them - one for a pinned icon, more for a reading or the
    /// player. That is what makes a slot a place: a widget can be put wherever
    /// there are enough free ones, and nowhere else.
    /// </remarks>
    public static double CellDips(AppBarEdge edge, DockDensity density, string size = "large")
    {
        double cell = IsHorizontal(edge) && density == DockDensity.Compact ? 26 : 30;

        // The slot shrinks with the readings. A widget occupies whole slots,
        // so slots sized for the large readings leave every smaller widget
        // swimming in its own footprint - the row read as gappy exactly when
        // the person had asked for a tighter bar.
        return size switch
        {
            "small" => cell - 8,
            "medium" => cell - 4,
            _ => cell,
        };
    }

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

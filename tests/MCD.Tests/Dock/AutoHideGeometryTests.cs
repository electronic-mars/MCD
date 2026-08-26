using Mcd.Interop.AppBar;
using Shouldly;
using Windows.Win32.Foundation;

namespace Mcd.Tests.Dock;

/// <summary>
/// Where the bar sits while it is hiding.
/// </summary>
/// <remarks>
/// Two rules, both learned the hard way. A sliver always stays on screen -
/// without it there is nothing for the pointer to touch and the dock is gone
/// until somebody edits config.json. And the window never leaves its own
/// monitor: the first version slid it off the edge, which on a stacked pair of
/// screens left it showing along the bottom of the one above and swallowing the
/// pointer over a strip of it.
/// </remarks>
public sealed class AutoHideGeometryTests
{
    /// <summary>The lower screen of a stacked pair; there is another directly above it.</summary>
    private static readonly RECT Screen = new() { left = 2560, top = 819, right = 4480, bottom = 1899 };

    private const int Thickness = 24;
    private const int Margin = 1;

    private static readonly AppBarEdge[] Edges =
        [AppBarEdge.Top, AppBarEdge.Bottom, AppBarEdge.Left, AppBarEdge.Right];

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void FullyShownIsTheWholeBar(AppBarEdge edge)
    {
        RECT rc = AutoHideGeometry.At(edge, Screen, Thickness, shown: 1, Margin);

        Across(edge, rc).ShouldBe(Thickness);
        AutoHideGeometry.Offset(edge, Thickness, shown: 1, Margin).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void HiddenLeavesExactlyTheMargin(AppBarEdge edge)
    {
        Across(edge, AutoHideGeometry.At(edge, Screen, Thickness, shown: 0, Margin)).ShouldBe(Margin);
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void TheWindowNeverLeavesItsMonitor(AppBarEdge edge)
    {
        for (double shown = 0; shown <= 1.0001; shown += 0.05)
        {
            RECT rc = AutoHideGeometry.At(edge, Screen, Thickness, shown, Margin);

            rc.left.ShouldBeGreaterThanOrEqualTo(Screen.left);
            rc.top.ShouldBeGreaterThanOrEqualTo(Screen.top);
            rc.right.ShouldBeLessThanOrEqualTo(Screen.right);
            rc.bottom.ShouldBeLessThanOrEqualTo(Screen.bottom);
        }
    }

    [Theory]
    [MemberData(nameof(AllEdges))]
    public void WhatIsOnScreenPlusWhatIsPushedOutIsTheWholeBar(AppBarEdge edge)
    {
        for (double shown = 0; shown <= 1.0001; shown += 0.1)
        {
            int visible = Across(edge, AutoHideGeometry.At(edge, Screen, Thickness, shown, Margin));
            int pushed = AutoHideGeometry.Offset(edge, Thickness, shown, Margin);

            // On the far edges the bar arrives the right way up and needs no
            // push; on the near ones the two have to add up, or the sliver on
            // screen shows a slice out of the middle of the bar.
            if (edge is AppBarEdge.Top or AppBarEdge.Left)
            {
                (visible + pushed).ShouldBe(Thickness);
            }
            else
            {
                pushed.ShouldBe(0);
            }
        }
    }

    [Fact]
    public void TheSlideOnlyEverGrows()
    {
        int last = 0;

        for (double shown = 0; shown <= 1.0001; shown += 0.05)
        {
            int visible = Across(
                AppBarEdge.Bottom, AutoHideGeometry.At(AppBarEdge.Bottom, Screen, Thickness, shown, Margin));

            visible.ShouldBeGreaterThanOrEqualTo(last);
            last = visible;
        }

        last.ShouldBe(Thickness);
    }

    [Fact]
    public void AskingForNothingToBeLeftStillLeavesAPixel()
    {
        // A caller asking for no sliver is asking for the dock to become
        // unreachable. It gets a pixel anyway.
        AutoHideGeometry.Visible(Thickness, shown: 0, margin: 0).ShouldBe(1);
        AutoHideGeometry.Visible(Thickness, shown: 0, margin: -5).ShouldBe(1);
    }

    [Fact]
    public void ShownIsClampedRatherThanTrusted()
    {
        AutoHideGeometry.Visible(Thickness, shown: 4, Margin).ShouldBe(Thickness);
        AutoHideGeometry.Visible(Thickness, shown: -3, Margin).ShouldBe(Margin);
    }

    [Fact]
    public void AMarginWiderThanTheBarIsNotAllowedToInvertIt()
    {
        AutoHideGeometry.Visible(thickness: 4, shown: 0, margin: 40).ShouldBe(4);
    }

    public static TheoryData<AppBarEdge> AllEdges() => [.. Edges];

    /// <summary>The rectangle's size along the axis the bar has thickness in.</summary>
    private static int Across(AppBarEdge edge, RECT rc) =>
        edge is AppBarEdge.Top or AppBarEdge.Bottom ? rc.bottom - rc.top : rc.right - rc.left;
}

using Mcd.Interop.AppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Dock;

/// <summary>Which of a bar's three regions something sits in.</summary>
public enum Band
{
    Start,
    Center,
    End,
}

/// <summary>
/// Turns the three regions of a bar to match the edge it sits on.
/// </summary>
/// <remarks>
/// <para>
/// On a bar down the side of a screen "start" means the top and "end" the
/// bottom, and the widgets inside run downwards. Left horizontal, everything
/// after the first reading lands past the right-hand edge of an 86-point-wide
/// window, where it is simply not drawn.
/// </para>
/// <para>
/// Shared by the real bar and by the picture of it in the settings, so that the
/// picture cannot quietly stop being a picture of the bar. A preview that lies
/// is worse than no preview.
/// </para>
/// </remarks>
public static class DockLayout
{
    public static void Apply(AppBarEdge edge, Panel start, Panel centre, Panel end)
    {
        bool horizontal = DockMetrics.IsHorizontal(edge);
        Orientation flow = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        Thickness inset = horizontal ? new Thickness(10, 0, 10, 0) : new Thickness(0, 10, 0, 10);

        Turn(start, flow);
        Turn(centre, flow);
        Turn(end, flow);

        start.Margin = inset;
        end.Margin = inset;

        start.HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        start.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Top;

        centre.HorizontalAlignment = HorizontalAlignment.Center;
        centre.VerticalAlignment = VerticalAlignment.Center;

        end.HorizontalAlignment = horizontal ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        end.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Bottom;
    }

    private static void Turn(Panel panel, Orientation flow)
    {
        if (panel is StackPanel stack)
        {
            stack.Orientation = flow;
        }
    }
}

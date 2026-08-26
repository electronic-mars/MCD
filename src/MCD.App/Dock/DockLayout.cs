using Mcd.App.Widgets;
using Mcd.Core.Settings;
using Mcd.Interop.AppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Dock;

/// <summary>
/// Lays one run of widgets along a bar.
/// </summary>
/// <remarks>
/// <para>
/// A grid with one track per widget: widgets take the length they need, and
/// spacers take a share of whatever is left. That is the whole placement model
/// - no regions, no coordinates, just the order of the run and where the
/// stretchy parts sit in it. When something on the bar hides itself, its track
/// collapses and the spacers absorb the difference; nothing else moves.
/// </para>
/// <para>
/// Shared by the real bar and by the picture of it in the settings, so that the
/// picture cannot quietly stop being a picture of the bar. A preview that lies
/// is worse than no preview.
/// </para>
/// </remarks>
public static class DockLayout
{
    /// <summary>The bar's own inset at each end, as PowerToys sets it.</summary>
    private const double EndInset = 4;

    /// <summary>How much of the bar's length one entry takes.</summary>
    public static GridLength LengthOf(WidgetConfig entry, WidgetViewModel widget) =>
        widget is SpacerWidget spacer ? spacer.Length : GridLength.Auto;

    /// <summary>Puts the elements into the strip, one track each.</summary>
    public static void Arrange(
        AppBarEdge edge,
        Grid strip,
        IReadOnlyList<(FrameworkElement Element, GridLength Length)> items)
    {
        bool horizontal = DockMetrics.IsHorizontal(edge);

        strip.Children.Clear();
        strip.ColumnDefinitions.Clear();
        strip.RowDefinitions.Clear();

        strip.Margin = horizontal
            ? new Thickness(EndInset, 0, EndInset, 0)
            : new Thickness(0, EndInset, 0, EndInset);

        for (int i = 0; i < items.Count; i++)
        {
            (FrameworkElement element, GridLength length) = items[i];

            if (horizontal)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
                Grid.SetColumn(element, i);
            }
            else
            {
                strip.RowDefinitions.Add(new RowDefinition { Height = length });
                Grid.SetRow(element, i);
            }

            strip.Children.Add(element);
        }
    }

    /// <summary>How a widget sits across the bar, and apart from its neighbours.</summary>
    public static void Dress(FrameworkElement host, AppBarEdge edge, bool spacer)
    {
        bool horizontal = DockMetrics.IsHorizontal(edge);

        if (spacer)
        {
            // A spacer fills its whole track, or there would be nothing to
            // stretch and nothing to grab during a drag.
            host.Margin = new Thickness(0);
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        // 2 a side here and 2 a side on each reading: widgets sit 8 apart while
        // their own readings sit 4 apart, which is what makes them read as
        // groups. The margin vanishes with a hidden widget, so no holes.
        host.Margin = horizontal ? new Thickness(2, 0, 2, 0) : new Thickness(0, 2, 0, 2);
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.VerticalAlignment = VerticalAlignment.Center;
    }
}

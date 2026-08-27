using Mcd.Core.Settings;
using Mcd.Interop.AppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Dock;

/// <summary>
/// Lays a bar out as a row of equal slots.
/// </summary>
/// <remarks>
/// <para>
/// The bar is divided into slots of one size, and every widget occupies a whole
/// number of them at the slot it was put on. Empty slots are empty - there is
/// nothing invisible holding them open - so a widget can be dropped wherever
/// enough of them are free, and the outlines drawn during a drag are the real
/// places rather than a picture of them.
/// </para>
/// <para>
/// Shared by the real bar and by anything that needs to know its shape, so the
/// arithmetic cannot quietly differ between what is drawn and what is allowed.
/// </para>
/// </remarks>
public static class DockLayout
{
    /// <summary>The bar's own inset at each end, as PowerToys sets it.</summary>
    public const double EndInset = 4;

    /// <summary>How many slots fit along a bar of this length.</summary>
    public static int Capacity(double length, double cell) =>
        Math.Max(0, (int)Math.Floor((length - (2 * EndInset)) / cell));

    /// <summary>How many slots a widget of this size needs.</summary>
    public static int SpanOf(double desired, double cell) =>
        Math.Max(1, (int)Math.Ceiling((desired - 0.5) / cell));

    /// <summary>Puts the elements onto the strip, each across the slots it holds.</summary>
    public static void Arrange(
        AppBarEdge edge,
        Grid strip,
        int capacity,
        IReadOnlyList<(FrameworkElement Element, Placement Where)> items)
    {
        bool horizontal = DockMetrics.IsHorizontal(edge);

        strip.Children.Clear();
        strip.ColumnDefinitions.Clear();
        strip.RowDefinitions.Clear();

        strip.Margin = horizontal
            ? new Thickness(EndInset, 0, EndInset, 0)
            : new Thickness(0, EndInset, 0, EndInset);

        for (int i = 0; i < capacity; i++)
        {
            if (horizontal)
            {
                strip.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                strip.RowDefinitions.Add(
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
        }

        foreach ((FrameworkElement element, Placement where) in items)
        {
            if (horizontal)
            {
                Grid.SetColumn(element, where.Cell);
                Grid.SetColumnSpan(element, where.Span);
            }
            else
            {
                Grid.SetRow(element, where.Cell);
                Grid.SetRowSpan(element, where.Span);
            }

            strip.Children.Add(element);
        }
    }

    /// <summary>How a widget sits inside the slots it holds.</summary>
    public static void Dress(FrameworkElement host, AppBarEdge edge)
    {
        // Centred in its own run of slots, taking no margin of its own: the slot
        // is the spacing, uniformly, widget to widget along the whole bar.
        host.Margin = new Thickness(0);
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.VerticalAlignment = VerticalAlignment.Center;
    }
}

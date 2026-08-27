using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// One key of the media transport: a rubber cap in a recessed rail, the way
/// Master Audio Switcher draws its player block.
/// </summary>
/// <remarks>
/// <para>
/// Its own control rather than a styled Button, for the same reason
/// <see cref="HoverChip"/> is: the Button template answers hover and press
/// with its own theme brushes, and a local background only survives at rest.
/// The cap needs all three states painted by hand - a vertical gradient that
/// lightens under the pointer and inverts when pressed, with the glyph
/// travelling down a pixel like a cap on a real deck. The ink itself never
/// changes: on an instrument the glyph is printed, not backlit.
/// </para>
/// </remarks>
public sealed partial class TransportKey : ContentControl
{
    private bool _down;

    public TransportKey()
    {
        Template = (Microsoft.UI.Xaml.Controls.ControlTemplate)
            Application.Current.Resources["BareContent"];
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        BorderThickness = new Thickness(1);

        Paint(Rest);

        Loaded += (_, _) =>
        {
            Paint(Rest);

            if (ToolTipService.GetToolTip(this) is ToolTip tip)
            {
                tip.XamlRoot = XamlRoot;
            }
        };

        ActualThemeChanged += (_, _) => Paint(_down ? Down : Rest);

        PointerEntered += (_, _) =>
        {
            if (!_down)
            {
                Paint(Hovered);
            }
        };

        PointerExited += (_, _) => { _down = false; Paint(Rest); Travel(0); };
        PointerCaptureLost += (_, _) => { _down = false; Paint(Rest); Travel(0); };
        PointerCanceled += (_, _) => { _down = false; Paint(Rest); Travel(0); };

        PointerPressed += (_, e) =>
        {
            _down = true;
            Paint(Down);
            Travel(1);
            e.Handled = true;
        };

        PointerReleased += (_, e) =>
        {
            bool fire = _down;
            _down = false;
            Paint(Hovered);
            Travel(0);
            e.Handled = true;

            if (fire && Command?.CanExecute(null) == true)
            {
                Command.Execute(null);
            }
        };
    }

    public ICommand? Command { get; set; }

    private bool Dark => ActualTheme != ElementTheme.Light;

    private (Windows.UI.Color Top, Windows.UI.Color Bottom) Rest => Dark
        ? (C(0xFF2B3037), C(0xFF20242B))
        : (C(0xFFFBFAF7), C(0xFFE6E3DC));

    private (Windows.UI.Color Top, Windows.UI.Color Bottom) Hovered => Dark
        ? (C(0xFF333941), C(0xFF262B33))
        : (C(0xFFFFFFFF), C(0xFFEDEAE3));

    /// <summary>Inverted on purpose: the cap tilts inward under the finger.</summary>
    private (Windows.UI.Color Top, Windows.UI.Color Bottom) Down => Dark
        ? (C(0xFF1D2128), C(0xFF232830))
        : (C(0xFFDDD9D1), C(0xFFE9E6DF));

    private void Paint((Windows.UI.Color Top, Windows.UI.Color Bottom) state)
    {
        Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5, 0),
            EndPoint = new Windows.Foundation.Point(0.5, 1),
            GradientStops =
            {
                new GradientStop { Color = state.Top, Offset = 0 },
                new GradientStop { Color = state.Bottom, Offset = 1 },
            },
        };

        BorderBrush = new SolidColorBrush(Dark ? C(0x12FFFFFF) : C(0x1A000000));
    }

    private void Travel(double y)
    {
        if (Content is UIElement glyph)
        {
            glyph.RenderTransform = new TranslateTransform { Y = y };
        }
    }

    private static Windows.UI.Color C(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

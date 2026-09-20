using System.Numerics;
using Mcd.App.Dock;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Mcd.App.Widgets;

/// <summary>
/// One thing on the bar, with the bar's own response to the pointer.
/// </summary>
/// <remarks>
/// <para>
/// This wraps a single reading rather than a whole widget, because that is what
/// a person points at. A widget showing four readings is four things to the eye,
/// and lighting up all four when the pointer is over one of them looks like a
/// fault.
/// </para>
/// <para>
/// It does not handle the tap. The click still reaches whatever the widget put
/// it on, so a reading can open a panel without this knowing anything about it.
/// </para>
/// </remarks>
public sealed partial class HoverChip : ContentControl
{
    private readonly SolidColorBrush _fill = new(Colors.Transparent);

    public HoverChip()
    {
        // A control with no background takes part in no hit testing at all, so
        // the pointer would pass straight through to the bar behind it.
        Template = (Microsoft.UI.Xaml.Controls.ControlTemplate)
            Application.Current.Resources["BareContent"];
        Background = _fill;
        CornerRadius = new CornerRadius(DockMetrics.ItemCornerRadius);
        Padding = new Thickness(2, 2, 2, 2);
        IsTabStop = false;
        VerticalContentAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Center;

        // A tooltip has to be told which window it belongs to. With one window
        // per monitor it has no way to work that out, and the one that guesses
        // wrong throws rather than appearing.
        Loaded += (_, _) =>
        {
            if (ToolTipService.GetToolTip(this) is ToolTip tip)
            {
                tip.XamlRoot = XamlRoot;
            }
        };

        if (Environment.GetEnvironmentVariable("MCD_PAINT") == "2")
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 120, 255));
            BorderThickness = new Thickness(1);
        }

        Loaded += (_, _) => _fill.Color = Rest;

        PointerEntered += (_, _) => Light(Hover);
        PointerExited += OnGone;
        PointerCanceled += OnGone;
        PointerCaptureLost += OnGone;
        PointerPressed += OnPressed;
        PointerReleased += (_, _) => Light(Hover);
    }

    /// <summary>
    /// True for a chip that does something when pressed.
    /// </summary>
    /// <remarks>
    /// Those wear a faint plate all the time and answer the pointer; a
    /// reading wears nothing and answers with its tooltip only. Lighting a
    /// number up under the pointer, and squeezing it under a press, promised
    /// an action it never had - and nothing on the bar said, before the
    /// pointer got there, which chips were buttons.
    /// </remarks>
    public bool Button { get; set; }

    private Windows.UI.Color Rest => !Button ? Colors.Transparent
        : ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    private void Light(Windows.UI.Color to)
    {
        if (Button)
        {
            Fade(to);
        }
    }

    /// <summary>
    /// The colours the bar uses under the pointer.
    /// </summary>
    /// <remarks>
    /// From microsoft/PowerToys, DockItemControl.xaml (MIT), so that a dock of
    /// ours beside a dock of theirs answers the pointer the same way. White at a
    /// low alpha over the dark theme, near-opaque white over the light one.
    /// </remarks>
    private Windows.UI.Color Hover => ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

    private Windows.UI.Color Pressed => ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);

    private void OnGone(object sender, PointerRoutedEventArgs e) => Cool();

    /// <summary>
    /// Puts the light out, whether or not the pointer said goodbye.
    /// </summary>
    /// <remarks>
    /// A right click opens a menu, and the menu takes the pointer with it:
    /// the release never arrives and the chip stays lit for the rest of the
    /// session. The bar puts its own lights out when the pointer leaves it,
    /// when a menu opens, and whenever it rebuilds.
    /// </remarks>
    public void Cool() => Fade(Rest, lighting: false);

    // No squeeze any more: scaling a chip this small blurred its text for
    // the length of the press. The plate getting brighter says as much.
    private void OnPressed(object sender, PointerRoutedEventArgs e) => Light(Pressed);

    /// <summary>Crossfades the background rather than switching it.</summary>
    /// <param name="lighting">
    /// True on the way in. Lighting up is quick and letting go is slow: a
    /// pointer crossing a row of chips at one speed both ways leaves a string
    /// of lights behind it.
    /// </param>
    private void Fade(Windows.UI.Color to, bool lighting = true)
    {
        var colour = new ColorAnimation
        {
            To = to,
            Duration = lighting ? DockMetrics.HoverCrossfade : DockMetrics.HoverFade,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(colour, _fill);
        Storyboard.SetTargetProperty(colour, "Color");

        var story = new Storyboard();
        story.Children.Add(colour);
        story.Begin();
    }
}

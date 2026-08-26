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
        Background = _fill;
        CornerRadius = new CornerRadius(DockMetrics.ItemCornerRadius);
        Padding = new Thickness(6, 2, 6, 2);
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

        PointerEntered += (_, _) => Fade(Hover);
        PointerExited += OnGone;
        PointerCanceled += OnGone;
        PointerCaptureLost += OnGone;
        PointerPressed += OnPressed;
        PointerReleased += (_, _) => { Fade(Hover); Squeeze(1f); };
        SizeChanged += (_, _) => Centre();
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
        ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

    private Windows.UI.Color Pressed => ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF);

    private void OnGone(object sender, PointerRoutedEventArgs e)
    {
        Fade(Colors.Transparent);
        Squeeze(1f);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Fade(Pressed);
        Squeeze((float)DockMetrics.PressScale);
    }

    /// <summary>Crossfades the background rather than switching it.</summary>
    private void Fade(Windows.UI.Color to)
    {
        var colour = new ColorAnimation
        {
            To = to,
            Duration = DockMetrics.HoverCrossfade,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(colour, _fill);
        Storyboard.SetTargetProperty(colour, "Color");

        var story = new Storyboard();
        story.Children.Add(colour);
        story.Begin();
    }

    /// <summary>
    /// Shrinks the chip while it is held down.
    /// </summary>
    /// <remarks>
    /// Through the compositor rather than a XAML transform: this runs on every
    /// press, on up to three bars at once, and a scale animated on the interface
    /// thread stutters whenever that thread is busy - which, on a bar that
    /// redraws once a second, it regularly is.
    /// </remarks>
    private void Squeeze(float to)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(this);
        Centre();

        Vector3KeyFrameAnimation scale = visual.Compositor.CreateVector3KeyFrameAnimation();

        scale.InsertKeyFrame(
            1f,
            new Vector3(to, to, 1f),
            visual.Compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.33f, 1f), new Vector2(0.68f, 1f)));

        scale.Duration = DockMetrics.PressScaleDuration;
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Scales about the middle, so a press does not shift the chip sideways.</summary>
    private void Centre() =>
        ElementCompositionPreview.GetElementVisual(this).CenterPoint =
            new Vector3((float)(ActualWidth / 2), (float)(ActualHeight / 2), 0);
}

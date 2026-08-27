using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Mcd.App.Widgets;

/// <summary>
/// The control that a widget's own template is rendered into.
/// </summary>
/// <remarks>
/// It exists so the widget can stay a plain view model with no idea that it is
/// clickable, hoverable or inside a bar. A failure inside one widget's tick is
/// caught here, so a bad widget cannot take the dock down with it.
/// </remarks>
public sealed partial class WidgetHost : ContentControl, IDisposable
{
    private readonly ILogger _log;
    private readonly WidgetViewModel _widget;
    private int _failures;

    private readonly SolidColorBrush _fill = new(Colors.Transparent);

    public WidgetHost(ILogger log, WidgetViewModel widget, DataTemplate template)
    {
        _log = log;
        _widget = widget;

        Content = widget;
        ContentTemplate = template;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        CornerRadius = new CornerRadius(4);

        // A control with no Background takes part in no hit testing at all, so
        // the click falls through to the bar behind and nothing happens. The
        // brush doubles as the whole-widget hover: the area under the pointer
        // lights up the way a PowerToys item does, and the reading chips add
        // their own brighter response on top.
        Background = _fill;

        PointerEntered += (_, _) => Fade(Hover);
        PointerExited += OnGone;
        PointerCanceled += OnGone;
        PointerCaptureLost += OnGone;
    }

    /// <summary>
    /// True for a widget that should answer the pointer with nothing - the
    /// spacers, whose whole point is to look like bare bar.
    /// </summary>
    public bool Quiet { get; set; }

    private Windows.UI.Color Hover => ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

    private void OnGone(object sender, PointerRoutedEventArgs e) => Fade(Colors.Transparent);

    private void Fade(Windows.UI.Color to)
    {
        if (Quiet)
        {
            return;
        }

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
    /// The press-and-hold outline: this element is in hand and can be dragged,
    /// or dragged off the bar to be removed.
    /// </summary>
    public void Outline(bool on)
    {
        BorderBrush = on
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF2, 0x6A, 0x21))
            : null;
        BorderThickness = new Thickness(on ? 1 : 0);
    }

    /// <summary>Half-gone: the pointer is off the bar, and letting go removes it.</summary>
    public void Doomed(bool on) => Opacity = on ? 0.25 : (Opacity < 1 ? 0.4 : 1);

    /// <summary>The settings entry this was built from, so the bar can rearrange itself.</summary>
    public WidgetConfig Entry => _widget.Entry;

    /// <summary>The view model inside, for whoever arranged the bar.</summary>
    public WidgetViewModel Widget => _widget;

    public void Tick(SensorSnapshot snapshot)
    {
        // Three failures in a row and the widget is left alone. One broken
        // widget must cost its own square of the bar, not the whole bar.
        if (_failures >= 3)
        {
            return;
        }

        try
        {
            _widget.Tick(snapshot);
            _failures = 0;
        }
        catch (Exception e)
        {
            _failures++;
            _log.LogError(e, "widget.tick typeId={TypeId} failed", _widget.TypeId);
        }
    }

    public void Attach()
    {
        try
        {
            _widget.Attach();
        }
        catch (Exception e)
        {
            _log.LogError(e, "widget.attach typeId={TypeId} failed", _widget.TypeId);
        }
    }

    public void Dispose()
    {
        _widget.Dispose();
    }

}

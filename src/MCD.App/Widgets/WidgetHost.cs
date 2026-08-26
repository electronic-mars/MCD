using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

        // A control with no Background takes part in no hit testing at all, so
        // the click falls through to the bar behind and nothing happens. This is
        // the whole reason a widget can be clicked. What lights up under the
        // pointer is the individual reading, not this - see HoverChip.
        Background = new SolidColorBrush(Colors.Transparent);

    }

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

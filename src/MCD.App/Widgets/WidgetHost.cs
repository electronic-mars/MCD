using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        Template = (ControlTemplate)Application.Current.Resources["BareContent"];
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

        // One storyboard, made here and reused. A fresh one per pointer event
        // is a running animation holding a widget that may already have been
        // taken off the bar, and the pointer crosses a bar of a dozen widgets
        // many times a minute.
        Storyboard.SetTarget(_colour, _fill);
        Storyboard.SetTargetProperty(_colour, "Color");
        _fade.Children.Add(_colour);

        PointerEntered += OnHere;
        PointerExited += OnGone;
        PointerCanceled += OnGone;
        PointerCaptureLost += OnGone;
    }

    private readonly ColorAnimation _colour = new()
    {
        Duration = DockMetrics.HoverCrossfade,
        EnableDependentAnimation = true,
    };

    private readonly Storyboard _fade = new();

    /// <summary>
    /// True for a widget that should answer the pointer with nothing - the
    /// spacers, whose whole point is to look like bare bar.
    /// </summary>
    public bool Quiet { get; set; }

    private Windows.UI.Color Hover => ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
        : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

    private void OnHere(object sender, PointerRoutedEventArgs e)
    {
        if (_widget.Pressable || _widget.OwnButtons)
        {
            Fade(Hover);
        }
    }

    private void OnGone(object sender, PointerRoutedEventArgs e) => Fade(Colors.Transparent);

    private void Fade(Windows.UI.Color to)
    {
        if (Quiet || _gone)
        {
            return;
        }

        _fade.Stop();
        _colour.To = to;
        _fade.Begin();
    }

    /// <summary>
    /// Does what a click on this widget does, without a pointer.
    /// </summary>
    /// <remarks>
    /// The bar takes the pointer away from its widgets to watch for a drag,
    /// so a press that turned out not to be one has to be handed back.
    /// </remarks>
    public void Press() => _widget.Press();

    /// <summary>
    /// The press-and-hold outline: this element is in hand and can be dragged,
    /// or dragged off the bar to be removed.
    /// </summary>
    public void Outline(bool on)
    {
        // On the chip, not on the host. The host is as wide as the length
        // the widget declared - reservation included - and an outline drawn
        // there showed a box wider than the ink, unevenly: exactly what a
        // person sees the moment they pick a widget up. The chip is what the
        // hover pill lights, so the pickup outline and the pill agree.
        Brush? brush = on
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
            : null;

        var line = new Thickness(on ? 1 : 0);

        switch (Chip())
        {
            case Control control:
                control.BorderBrush = brush;
                control.BorderThickness = line;
                break;

            case Grid grid:
                grid.BorderBrush = brush;
                grid.BorderThickness = line;
                break;

            case StackPanel stack:
                stack.BorderBrush = brush;
                stack.BorderThickness = line;
                break;

            default:
                BorderBrush = brush;
                BorderThickness = line;
                break;
        }
    }

    /// <summary>The template's root element - the thing the person sees as the widget.</summary>
    private FrameworkElement? Chip()
    {
        DependencyObject at = this;

        for (int depth = 0; depth < 4 && VisualTreeHelper.GetChildrenCount(at) > 0; depth++)
        {
            at = VisualTreeHelper.GetChild(at, 0);

            if (at is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0)
            {
                return VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement;
            }
        }

        return null;
    }

    /// <summary>Puts out whatever light the pointer left on this chip.</summary>
    public void Cool()
    {
        if (Chip() is HoverChip chip)
        {
            chip.Cool();
        }
    }

    /// <summary>Half-gone: the pointer is off the bar, and letting go removes it.</summary>
    public void Doomed(bool on) => Opacity = on ? 0.25 : (Opacity < 1 ? 0.4 : 1);

    /// <summary>
    /// Present but about nothing right now: drawn dim, the way the player's
    /// keys are while nothing plays, so the widget can be seen, aimed at and
    /// dragged rather than being a hole in the bar.
    /// </summary>
    public void Sleeping(bool asleep)
    {
        _asleep = asleep;

        // Not while a drag is dressing the opacity its own way.
        if (Opacity is 1 or Asleep)
        {
            Opacity = asleep ? Asleep : 1;
        }
    }

    private bool _asleep;

    // The same resting brightness the player's keys use while nothing
    // plays: one dimness for one meaning. Fainter, the Wi-Fi read as a
    // rendering fault beside them.
    private const double Asleep = 0.55;

    /// <summary>The settings entry this was built from, so the bar can rearrange itself.</summary>
    public WidgetConfig Entry => _widget.Entry;

    /// <summary>The view model inside, for whoever arranged the bar.</summary>
    public WidgetViewModel Widget => _widget;

    /// <summary>
    /// Tells a screen reader what this widget is and what it says.
    /// </summary>
    /// <remarks>
    /// Without it the whole bar is a row of unnamed boxes: the host is a
    /// ContentControl whose content is a view model, so what gets read out is
    /// the name of a class. Set from what the widget already writes for its
    /// own summary, and only when that changes, because this is on the path
    /// that runs once a second for every widget on every bar.
    /// </remarks>
    private void Announce()
    {
        string said = _widget.Summarise();

        if (said.Length == 0 || said == _announced)
        {
            return;
        }

        _announced = said;
        AutomationProperties.SetName(this, said);
    }

    private string? _announced;

    public void Tick(SensorSnapshot snapshot)
    {
        // A tick can already be sitting in the dispatcher's queue when the
        // window is torn down; delivered late, it walks freed XAML - which
        // logged E_UNEXPECTED when it was lucky and killed the process when
        // it was not. A dead host answers nothing.
        if (_gone)
        {
            return;
        }

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
            Announce();
        }
        catch (Exception e)
        {
            _failures++;
            _log.LogError(
                e,
                "widget.tick typeId={TypeId} failed host={Host} rooted={Rooted} in={Window}",
                _widget.TypeId,
                GetHashCode(),
                XamlRoot is not null,
                Mcd.App.Dock.DockWindow.Refreshing);
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
        if (_gone)
        {
            return;
        }

        _gone = true;

        // The handlers and the animation go before the view model does. A
        // storyboard still running against a disposed host keeps it alive,
        // and a pointer leaving the bar raises PointerExited on a host that
        // has already been taken off it.
        PointerEntered -= OnHere;
        PointerExited -= OnGone;
        PointerCanceled -= OnGone;
        PointerCaptureLost -= OnGone;

        _fade.Stop();
        Content = null;
        _widget.Dispose();
    }

    /// <summary>True once this has been taken off the bar.</summary>
    private bool _gone;

}

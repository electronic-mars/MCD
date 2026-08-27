using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// A widget drawn outside a dock, for the picture of a bar in the settings.
/// </summary>
/// <remarks>
/// <para>
/// The real template and the real view model, reading the real sensors: what
/// the settings show is a dock's worth of widgets, not a drawing of one. The
/// point of a preview is that it cannot disagree with the thing it previews,
/// and a hand-drawn approximation would start disagreeing the first time either
/// was touched.
/// </para>
/// <para>
/// Deliberately not a <see cref="WidgetHost"/>. That one answers the pointer,
/// opens panels anchored in screen coordinates and belongs to the window it was
/// made in - none of which means anything inside a settings window that may be
/// on a different screen from the dock being shown.
/// </para>
/// </remarks>
public sealed partial class WidgetPreview : ContentControl, IDisposable
{
    private readonly WidgetViewModel _widget;

    public WidgetPreview(WidgetViewModel widget, DataTemplate template)
    {
        _widget = widget;

        Content = widget;
        ContentTemplate = template;
        IsTabStop = false;
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(4, 2, 4, 2);

        // It mirrors the three lists below it, so a screen reader that walked
        // into it would read every widget's name twice.
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    /// <summary>The view model inside, for the picture that edits.</summary>
    public WidgetViewModel Widget => _widget;

    public void Tick(SensorSnapshot snapshot)
    {
        try
        {
            _widget.Tick(snapshot);
        }
        catch (Exception)
        {
            // A widget that throws while being previewed must not take the
            // settings window with it. The bar's own host counts failures and
            // gives up; here the next tick simply tries again.
        }
    }

    public void Dispose() => _widget.Dispose();
}

using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;

namespace Mcd.App.Widgets;

/// <summary>
/// A widget that is one drawing and one press: the microphone, the cup.
/// </summary>
/// <remarks>
/// <para>
/// What they share is everything but the meaning of the press. A drawing
/// that changes with the state, dimmer when nothing is going on and red when
/// something wants attention, a tooltip that says what the press will do,
/// and one slot of bar. The template is written once and both are drawn
/// with it.
/// </para>
/// <para>
/// Red is a second drawing laid over the first rather than a colour swapped
/// in: a brush held here would be picked once, from the app's resources,
/// while the template's own resources follow whatever theme the bar is in.
/// </para>
/// </remarks>
public abstract partial class GlyphWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    /// <summary>The drawing, by name in the icon library.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; } = "Gear";

    [ObservableProperty]
    public partial double IconSize { get; set; } = 16;

    /// <summary>The stroke that lands on the same weight as every other icon on the bar.</summary>
    [ObservableProperty]
    public partial double Stroke { get; set; } = 1.5;

    /// <summary>How solid the drawing is: quiet things are drawn fainter, the way an idle device is.</summary>
    [ObservableProperty]
    public partial double Faded { get; set; } = 1;

    /// <summary>1 while the drawing is red, 0 otherwise.</summary>
    [ObservableProperty]
    public partial double Alert { get; set; }

    /// <summary>The hover: what it says, and what a press does.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility Shown { get; set; } = Visibility.Visible;

    /// <summary>Whether the machine has anything for this widget to be about.</summary>
    public override bool Matters => Shown == Visibility.Visible;

    public override bool Pressable => true;

    /// <summary>One slot, the same room a pinned icon takes.</summary>
    public override double Length() => ReadingIcon + 6;

    public override void Attach() => Tick(SensorSnapshot.Empty);

    /// <summary>Draws the widget: which icon, how solid, whether red, and what the hover says.</summary>
    protected void Draw(string icon, double faded, bool alert, string detail)
    {
        Shown = Visibility.Visible;
        IconSize = ReadingIcon;
        Stroke = 36 / Math.Max(1, IconSize);
        Icon = icon;
        Alert = alert ? 1 : 0;
        Faded = alert ? 0 : faded;
        Detail = detail;
    }

    /// <summary>Draws nothing: there is nothing on this machine for it to be about.</summary>
    protected void Vanish() => Shown = Visibility.Collapsed;
}

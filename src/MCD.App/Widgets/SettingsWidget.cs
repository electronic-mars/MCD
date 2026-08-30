using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;

namespace Mcd.App.Widgets;

/// <summary>
/// The way in, drawn on the bar.
/// </summary>
/// <remarks>
/// <para>
/// A door that can be seen. Everything else about this program is reachable
/// only by knowing something first - that a strip of figures answers a right
/// click, or that starting the shortcut again opens the settings rather than
/// a second copy. Somebody who knows neither has a picture on their screen
/// and no way into it, and the first thing they look for is the one thing
/// every bar they have ever used has: a small toothed wheel at the end.
/// </para>
/// <para>
/// It is a widget rather than a fixture, so it can be taken off by anybody
/// who has learnt the right click and would rather have the slot back.
/// </para>
/// </remarks>
public sealed class SettingsWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.settings";

    public override string TypeId => Type;

    /// <summary>The wheel, as a property so the template can bind to it.</summary>
    public string Icon => "Gear";

    public double IconSize { get; private set; } = 16;

    /// <summary>Same drawn weight as everything else on the bar.</summary>
    public double Stroke => 36 / Math.Max(1, IconSize);

    public override void Attach() => Tick(SensorSnapshot.Empty);

    /// <summary>Nothing here follows the readings; it is a door.</summary>
    public override void Tick(SensorSnapshot snapshot) =>
        IconSize = ReadingIcon;

    public override void Press() => WantSettings();

    public override string Summarise() => Loc.Tr("WidgetSettingsName", "Settings");

    /// <summary>
    /// One slot, whichever way the bar runs.
    /// </summary>
    /// <remarks>
    /// The same room a pinned program's icon takes, and for the same picture.
    /// Fourteen points of padding rounded it up to two slots - sixty points of
    /// bar for a drawing twenty-four across, on a bar where slots are counted.
    /// </remarks>
    public override double Length() => ReadingIcon + 6;

    public override FrameworkElement? CreateEditor(System.Action<System.Text.Json.JsonElement?> changed) => null;
}

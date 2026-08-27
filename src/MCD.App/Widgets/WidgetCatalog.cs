using System.Collections.Immutable;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;

namespace Mcd.App.Widgets;

/// <summary>
/// One kind of widget: what it is called, what it does, and how to build one.
/// </summary>
public sealed record WidgetType(
    string TypeId,
    string Name,
    string Description,
    string Icon,
    Func<WidgetContext, WidgetConfig, WidgetViewModel> Build);

/// <summary>
/// One thing that can be put on a bar: a widget already configured to show it.
/// </summary>
/// <remarks>
/// Types and offers are different lists on purpose. A type is a kind of code -
/// the gauge, the temperature. An offer is a thing a person chooses - "CPU",
/// "Memory", "the drive's temperature" - and one type serves many offers. The
/// gallery, the bar's own menu and the drag-to-add all read this list.
/// </remarks>
/// <param name="Matches">
/// Whether an entry already on the bar shows this same thing, so the offer can
/// say so instead of quietly making a duplicate. Always false for things worth
/// having twice, like spacers.
/// </param>
public sealed record WidgetOffer(
    string Name,
    string Description,
    string Icon,
    Func<WidgetConfig> Make,
    Func<WidgetConfig, bool> Matches);

/// <summary>
/// Every widget this build knows about, in the order they are offered.
/// </summary>
/// <remarks>
/// One list rather than a switch statement in the factory and a second list in
/// the settings. Adding a kind of widget is one entry here, and everything that
/// offers, names, draws or configures widgets reads it from this one place.
/// </remarks>
public static class WidgetCatalog
{
    public static ImmutableArray<WidgetType> All { get; } =
    [
        new(
            GaugeWidget.Type,
            Loc.Tr("WidgetGaugeName", "Reading"),
            Loc.Tr("WidgetGaugeDescription", "One figure: how busy a part of the machine is."),
            "Activity",
            (context, entry) => new GaugeWidget(context, entry)),

        new(
            TempWidget.Type,
            Loc.Tr("WidgetTemperatureName", "Temperature"),
            Loc.Tr("WidgetTemperatureDescription", "Temperatures of the parts that report one."),
            "Temperature",
            (context, entry) => new TempWidget(context, entry)),

        new(
            IconWidget.Type,
            Loc.Tr("WidgetIconName", "Pinned icon"),
            Loc.Tr("WidgetIconDescription", "A program, folder or page, one press away."),
            "Rocket",
            (context, entry) => new IconWidget(context, entry)),

        new(
            MediaWidget.Type,
            Loc.Tr("WidgetMediaName", "Media"),
            Loc.Tr("WidgetMediaDescription", "Whatever is playing, with buttons for it. Hidden while nothing is."),
            "Music",
            (context, entry) => new MediaWidget(context, entry)),
    ];

    /// <summary>
    /// Everything that can be added to a bar right now: the five readings, a
    /// temperature per sensor worth watching, the player, a spacer.
    /// </summary>
    /// <remarks>
    /// The temperature offers list the prominent sensors - the per-part
    /// headline readings - rather than all of them; a processor's fourteen
    /// per-core sensors would drown the list. Any sensor at all can still be
    /// chosen on the widget itself once it is on the bar.
    /// </remarks>
    public static IEnumerable<WidgetOffer> Offers(SensorHub sensors)
    {
        foreach (GaugeReading reading in GaugeWidget.Known)
        {
            yield return new WidgetOffer(
                reading.Label,
                Loc.Tr("OfferGauge", "A live figure on the bar."),
                reading.Icon,
                () => DockContents.Gauge(reading.Id),
                entry => entry.TypeId == GaugeWidget.Type
                    && (WidgetOptions.Text(entry.Config, "reading") ?? "cpu") == reading.Id);
        }

        yield return new WidgetOffer(
            Loc.Tr("OfferHottest", "Temperature — the hottest"),
            Loc.Tr("OfferHottestDescription", "Whichever part is closest to its limit right now."),
            "Temperature",
            () => WidgetConfig.New(TempWidget.Type),
            entry => entry.TypeId == TempWidget.Type
                && string.IsNullOrEmpty(WidgetOptions.Text(entry.Config, "sensor")));

        foreach (SensorDescriptor sensor in sensors.Catalog
            .Where(d => d.Kind == SensorKind.Temperature && d.Prominent)
            .OrderBy(d => d.Group)
            .ThenBy(d => d.Label, StringComparer.CurrentCulture))
        {
            string key = sensor.Key.Value;

            yield return new WidgetOffer(
                sensor.Label,
                Loc.Tr("OfferSensor", "This part's temperature."),
                TempWidget.IconFor(sensor.Group),
                () => WidgetConfig.New(TempWidget.Type) with
                {
                    Config = WidgetJson.Object(("sensor", key)),
                },
                entry => entry.TypeId == TempWidget.Type
                    && WidgetOptions.Text(entry.Config, "sensor") == key);
        }

        yield return new WidgetOffer(
            Loc.Tr("WidgetMediaName", "Media"),
            Loc.Tr("WidgetMediaDescription", "Whatever is playing, with buttons for it. Hidden while nothing is."),
            "Music",
            () => WidgetConfig.New(MediaWidget.Type),
            entry => entry.TypeId == MediaWidget.Type);
    }

    public static WidgetType? Find(string typeId) =>
        All.FirstOrDefault(w => w.TypeId == typeId);

    /// <summary>
    /// Builds the widget an entry asks for, or null if this build does not know
    /// the kind.
    /// </summary>
    /// <remarks>
    /// Null is not a failure. A settings file written by a later version may
    /// name a widget that does not exist here yet, and skipping it beats
    /// refusing to show the dock. Everything that edits the layout must work
    /// from the settings entries rather than from what was built successfully,
    /// or the first rearrangement writes the unknown entry out of existence.
    /// </remarks>
    public static WidgetViewModel? Create(WidgetContext context, WidgetConfig entry) =>
        Find(entry.TypeId)?.Build(context, entry);
}

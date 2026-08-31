using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// One temperature - a single slot of bar.
/// </summary>
/// <remarks>
/// The drive's temperature and the graphics chip's temperature are two
/// different widgets: each sensor worth watching gets a slot of its own.
/// Without a chosen sensor the widget shows whichever prominent reading is
/// closest to its own limit right now, so a machine with nothing set up still
/// says something useful.
/// </remarks>
public sealed class TempWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.temp";

    public override string TypeId => Type;

    /// <summary>One entry, so the metric chip template serves unchanged.</summary>
    public ObservableCollection<Metric> Metrics { get; } = [];

    /// <summary>The chosen sensor's key, or null for the hottest reading.</summary>
    private SensorKey? Chosen =>
        WidgetOptions.Text(Options, "sensor") is { Length: > 0 } key ? new SensorKey(key) : null;

    public override void Attach()
    {
        Metrics.Clear();

        SensorKey? key = Chosen;

        var metric = new Metric(
            "temp",
            Icons.For("temp", "Temperature"),
            Name(key),
            "°C",
            key is { } k
                ? sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(k))
                : Warmest)
        {
            Spacing = GaugeWidget.Gap(Orientation),
            Accent = Context.Accent,
            Braun = Context.Backdrop == "braun",
            Sample = "100 °C",

            // With no sensor named, this widget is about a question rather
            // than a part, and the answer is meant to move: the sensor is
            // chosen again on every tick, and the name under the figure
            // follows it.
            Roving = key is null,

            // This widget's own limits, when the person set any. Null falls
            // back to what the part itself declares or the ordinary defaults.
            WarnAt = WidgetOptions.Number(Options, "warn"),
            CritAt = WidgetOptions.Number(Options, "crit"),

            // The sensor may not be there yet - a graphics driver still loading,
            // an external source starting after the dock did - so the icon that
            // belongs to its hardware is chosen when it turns up rather than
            // guessed from the key now.
            IconFound = sensor => Icons.For("temp", IconFor(sensor.Group)),

            NameFound = sensor => Names.Renamed(sensor.Key)
                ? Names.For(sensor)
                : GroupName(sensor.Group),
        };

        metric.SizeFor(
            ReadingIcon,
            ReadingFont,
            narrow: Orientation == Orientation.Vertical,

            // Not down the side of a screen. The bar is 86 points wide there,
            // and a name measured against nothing at all was drawn straight
            // through the edge: "Receive" fits, "Получение" does not, and the
            // cut is exactly as wide as the language.
            subtitle: Density == DockDensity.Default && Orientation == Orientation.Horizontal);
        Metrics.Add(metric);
    }

    public override void Tick(SensorSnapshot snapshot)
    {
        foreach (Metric metric in Metrics)
        {
            metric.Update(Sensors, snapshot);
        }

        if (Metrics.Count > 0 && Metrics[0].Sensor is null && _waited < Patience)
        {
            _waited++;
        }
    }

    /// <summary>How many ticks a missing sensor is given before giving up.</summary>
    /// <remarks>
    /// A graphics driver finishing its load, or an outside program starting
    /// after the dock did, fills the chip in a few seconds late. Handing the
    /// slot back at once would make the bar shuffle twice on every start.
    /// </remarks>
    private const int Patience = 10;

    private int _waited;

    /// <summary>
    /// Whether this machine has the thing this reading is about.
    /// </summary>
    /// <remarks>
    /// A chip with no sensor behind it hides itself, but hiding the chip left
    /// the slot: an empty rectangle that lit up under the pointer, pressed
    /// like a button and showed nothing, and was named by neither of the two
    /// rows that explain what is not on the bar.
    /// </remarks>
    public override bool Matters =>
        Metrics.Count == 0 || Metrics[0].Sensor is not null || _waited < Patience;

    /// <summary>
    /// Which temperature this is, for a list of what is on the bar.
    /// </summary>
    /// <remarks>
    /// With no part named it is the roving one, and its name says so rather
    /// than repeating the word twice - the part it happens to be showing this
    /// second is not what it is called.
    /// </remarks>
    public override string Called => Chosen is null
        ? Loc.Tr("OfferHottest", "Temperature - the hottest")
            + (Metrics.Count > 0 && Metrics[0].Sensor is { } watching
                ? " · " + Names.For(watching)
                : string.Empty)
        : Loc.Tr("WidgetTemperatureName", "Temperature") + " · " + Name(Chosen);

    public override string Summarise() => Name(Chosen);

    /// <summary>Along the bar: the chip's width across it, its height down it.</summary>
    public override double Length() =>
        Metrics.Count == 0
            ? 60
            : Orientation == Orientation.Vertical ? Metrics[0].Height() : Metrics[0].Width();

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var panel = new StackPanel { Spacing = 14 };

        SensorDescriptor[] found = [.. Everything()];

        // The first entry is the roving one; each sensor after it is itself.
        List<string> names =
        [
            Loc.Tr("TempHottestSummary", "The hottest reading"),
            .. found.Select(Describe),
        ];

        SensorKey? chosen = Chosen;

        panel.Children.Add(Mcd.App.Settings.Braun.Field(
            Loc.Tr("TempWhich", "Which sensor"),
            Mcd.App.Settings.Braun.Choice(
                names,
                chosen is { } key ? Array.FindIndex(found, d => d.Key.Equals(key)) + 1 : 0,
                i => changed(WidgetOptions.Merge(
                    Options,
                    ("sensor", i == 0 ? null : JsonValue.Create(found[i - 1].Key.Value)))))));

        if (found.Length > 0 && found.All(s => s.Group != HardwareGroup.Cpu))
        {
            panel.Children.Add(Explain());
        }

        // The widget's own limits. Left empty, each part keeps the point its
        // maker - or the ordinary default for its kind - declares.
        var limits = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };

        NumberBox Limit(string header, string key)
        {
            var number = new NumberBox
            {
                Header = header,
                Width = 150,
                Minimum = 30,
                Maximum = 120,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                PlaceholderText = Loc.Tr("TempLimitAuto", "the part's own"),
                Value = WidgetOptions.Number(Options, key) ?? double.NaN,
            };

            number.ValueChanged += (_, _) => changed(WidgetOptions.Merge(
                Options,
                (key, double.IsNaN(number.Value) ? null : JsonValue.Create((int)number.Value))));

            return number;
        }

        limits.Children.Add(Limit(Loc.Tr("TempWarnAt", "Warning at, °C"), "warn"));
        limits.Children.Add(Limit(Loc.Tr("TempCritAt", "Critical at, °C"), "crit"));
        panel.Children.Add(limits);

        return panel;
    }

    private string Name(SensorKey? key) =>
        key is { } k
            ? Sensors.Catalog.FirstOrDefault(d => d.Key.Equals(k)) is { } sensor
                ? Names.For(sensor)
                : k.Value
            : Loc.Tr("LabelTemperature", "Temperature");

    /// <summary>
    /// The caption for a part nobody has renamed, in the interface language.
    /// </summary>
    /// <remarks>
    /// The driver's own name for the chip is whatever alphabet the driver
    /// ships - "GPU" beside a localized "ЦП" made one bar speak two
    /// languages. The group is ours to name; the hardware's full name stays
    /// in the hover, where it is the answer to a different question.
    /// </remarks>
    private static string GroupName(HardwareGroup group) => group switch
    {
        HardwareGroup.Cpu => Loc.Tr("LabelCpu", "CPU"),
        HardwareGroup.Gpu => Loc.Tr("LabelGpu", "GPU"),
        HardwareGroup.Memory => Loc.Tr("LabelMemory", "Memory"),
        HardwareGroup.Storage => Loc.Tr("GroupStorage", "Drive"),
        HardwareGroup.Motherboard => Loc.Tr("GroupBoard", "Board"),
        _ => Loc.Tr("WidgetTemperatureName", "Temperature"),
    };

    internal static string IconFor(HardwareGroup group) => group switch
    {
        HardwareGroup.Cpu => "Cpu",
        HardwareGroup.Gpu => "Gpu",
        HardwareGroup.Storage => "Disk",
        HardwareGroup.Memory => "Memory",
        HardwareGroup.Motherboard => "Computer",
        _ => "Temperature",
    };

    private string Describe(SensorDescriptor sensor) =>
        Names.For(sensor) == sensor.Hardware
            ? Names.For(sensor)
            : $"{Names.For(sensor)} — {sensor.Hardware}";

    private static TextBlock Explain() => new()
    {
        Text = Loc.Tr(
            "TempExplain",
            "Processor and motherboard temperatures live behind a driver, "
            + "which a Store app cannot contain. Run HWiNFO with its Shared "
            + "Memory Support switched on, then turn it on under Sensors in "
            + "settings, and they will appear here."),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.7,
        MaxWidth = 360,
    };

    /// <summary>
    /// Every temperature the machine reports, hardware first.
    /// </summary>
    /// <remarks>
    /// Not filtered to the prominent ones. This is the list somebody goes
    /// looking through to choose from, and a processor's per-core readings
    /// belong in it even though none of them belongs on the bar unasked.
    /// </remarks>
    private IEnumerable<SensorDescriptor> Everything() =>
        Sensors.Catalog
            .Where(d => d.Kind == SensorKind.Temperature)
            .OrderBy(d => d.Group)
            .ThenByDescending(d => d.Prominent)
            .ThenBy(d => d.Label, StringComparer.CurrentCulture);

    private SensorDescriptor? Warmest(SensorHub sensors)
    {
        SensorSnapshot snapshot = sensors.Current;

        return sensors.Catalog
            .Where(d => d.Kind == SensorKind.Temperature && d.Prominent && snapshot[d.Key].HasValue)

            // Ranked by how close each is to its own limit, not by raw degrees.
            // A drive at 60 of 85 is calmer than a graphics chip at 80 of 83,
            // and the raw numbers say the opposite.
            .OrderByDescending(d => Pressure(snapshot[d.Key].Value, d))
            .FirstOrDefault();
    }

    private static double Pressure(double value, SensorDescriptor sensor) =>
        sensor.Critical is { } critical and > 0 ? value / critical : value / 100.0;
}

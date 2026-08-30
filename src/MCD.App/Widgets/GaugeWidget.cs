using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// One figure about how busy the machine is - a single slot of bar.
/// </summary>
/// <remarks>
/// The processor, the memory, each direction of the network and the graphics
/// chip are separate widgets on purpose: every reading is its own slot, moved,
/// added and removed on its own, the way an icon is.
/// </remarks>
public sealed class GaugeWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.gauge";

    /// <summary>Every reading a gauge can be, in the order they are offered.</summary>
    public static readonly GaugeReading[] Known =
    [
        new("cpu", "Cpu", Loc.Tr("LabelCpu", "CPU"), "%",
            SensorKey.Make("pdh", "cpu", SensorKind.Load, "total")),
        new("ram", "Memory", Loc.Tr("LabelMemory", "Memory"), "%",
            SensorKey.Make("mem", "ram", SensorKind.Load, "used")),

        // Sent and received are separate readings, each with the arrow that
        // says which way it goes.
        new("up", "ArrowUp", Loc.Tr("LabelSend", "Send"), "B/s",
            SensorKey.Make("pdh", "net", SensorKind.BytesPerSecond, "up")),
        new("down", "ArrowDown", Loc.Tr("LabelReceive", "Receive"), "B/s",
            SensorKey.Make("pdh", "net", SensorKind.BytesPerSecond, "down")),

        new("gpu", "Gpu", Loc.Tr("LabelGpu", "GPU"), "%",
            SensorKey.Make("pdh", "gpu", SensorKind.Load, "total")),
    ];

    public override string TypeId => Type;

    /// <summary>One entry, so the metric chip template serves unchanged.</summary>
    public ObservableCollection<Metric> Metrics { get; } = [];

    /// <summary>Which of the known readings this gauge is.</summary>
    public GaugeReading Reading =>
        Known.FirstOrDefault(r => r.Id == WidgetOptions.Text(Options, "reading")) ?? Known[0];

    public override void Attach()
    {
        Metrics.Clear();

        GaugeReading reading = Reading;

        var metric = new Metric(
            reading.Id,
            Icons.For(reading.Id, reading.Icon),
            reading.Label,
            reading.Unit,
            sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(reading.Key)))
        {
            Spacing = Gap(Orientation),
            Accent = Context.Accent,
            Braun = Context.Backdrop == "braun",

            // As long as this reading ever gets: a rate runs to "888 MB/s",
            // and everything else to a three-figure percentage.
            Sample = reading.Unit == "B/s" ? "888 MB/s" : "100 %",
        };

        metric.SizeFor(
            DockMetrics.ReadingIcon(Density),
            DockMetrics.ReadingFont(Density),
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

    public override string Summarise() => Reading.Label;

    /// <summary>Along the bar: the chip's width across it, its height down it.</summary>
    public override double Length() =>
        Metrics.Count == 0
            ? 60
            : Orientation == Orientation.Vertical ? Metrics[0].Height() : Metrics[0].Width();

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed) =>
        Mcd.App.Settings.Braun.Field(
            Loc.Tr("GaugeWhich", "Which reading"),
            Mcd.App.Settings.Braun.Choice(
                [.. Known.Select(r => r.Label)],
                Array.FindIndex(Known, r => r.Id == Reading.Id),
                i => changed(WidgetOptions.Merge(
                    Options, ("reading", JsonValue.Create(Known[i].Id))))));

    /// <summary>The gap between readings, on whichever side the next one sits.</summary>
    internal static Thickness Gap(Orientation orientation) =>
        orientation == Orientation.Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
}

/// <summary>One thing a gauge can show, and where its figure comes from.</summary>
public sealed record GaugeReading(string Id, string Icon, string Label, string Unit, SensorKey Key);

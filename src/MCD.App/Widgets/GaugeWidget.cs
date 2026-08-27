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
            subtitle: Density == DockDensity.Default);
        Metrics.Add(metric);
    }

    public override void Tick(SensorSnapshot snapshot)
    {
        foreach (Metric metric in Metrics)
        {
            metric.Update(Sensors, snapshot);
        }
    }

    public override string Summarise() => Reading.Label;

    /// <summary>Along the bar: the chip's width across it, its height down it.</summary>
    public override double Length() =>
        Metrics.Count == 0
            ? 60
            : Orientation == Orientation.Vertical ? Metrics[0].Height() : Metrics[0].Width();

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var box = new ComboBox
        {
            Header = Loc.Tr("GaugeWhich", "Which reading"),
            SelectedIndex = Array.FindIndex(Known, r => r.Id == Reading.Id),
        };

        foreach (GaugeReading reading in Known)
        {
            box.Items.Add(reading.Label);
        }

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                changed(WidgetOptions.Merge(
                    Options, ("reading", JsonValue.Create(Known[box.SelectedIndex].Id))));
            }
        };

        return box;
    }

    /// <summary>The gap between readings, on whichever side the next one sits.</summary>
    internal static Thickness Gap(Orientation orientation) =>
        orientation == Orientation.Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
}

/// <summary>One thing a gauge can show, and where its figure comes from.</summary>
public sealed record GaugeReading(string Id, string Icon, string Label, string Unit, SensorKey Key);

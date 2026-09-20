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
/// One part of the machine: how busy it is, how warm, or both.
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

    /// <summary>
    /// The one chip, for the template to hold directly.
    /// </summary>
    /// <remarks>
    /// Not through an ItemsControl: one measures to nothing before its
    /// containers are generated, so the host stood two points wide and the
    /// chip painted itself in overflow from the middle of its span - the
    /// lopsided margins visible the moment anything was pressed.
    /// </remarks>
    public Metric? Front => Metrics.Count > 0 ? Metrics[0] : null;

    /// <summary>
    /// The second figure, when the part shows two: its temperature after its
    /// load, under the one icon.
    /// </summary>
    /// <remarks>
    /// One widget rather than two side by side. Two chips wearing the same
    /// processor had to be told apart, and the bar's slots put a gap of their
    /// own between them; one icon with two figures is a group by construction.
    /// </remarks>
    public Metric? Second => Metrics.Count > 1 ? Metrics[1] : null;

    public Visibility SecondShown => Second is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Which part's temperature goes with a reading, for the ones that have a part.</summary>
    private static HardwareGroup? PartOf(string readingId) => readingId switch
    {
        "cpu" => HardwareGroup.Cpu,
        "ram" => HardwareGroup.Memory,
        "gpu" => HardwareGroup.Gpu,
        _ => null,
    };

    private bool WithTemp => PartOf(Reading.Id) is not null && WidgetOptions.Number(Options, "temp") is 1;

    /// <summary>The load is shown unless it was switched off and the temperature is there instead.</summary>
    private bool WithLoad => !WithTemp || WidgetOptions.Number(Options, "load") is not 0;

    /// <summary>Which of the known readings this gauge is.</summary>
    public GaugeReading Reading =>
        Known.FirstOrDefault(r => r.Id == WidgetOptions.Text(Options, "reading")) ?? Known[0];

    public override void Attach()
    {
        Metrics.Clear();

        GaugeReading reading = Reading;
        bool subtitle = Density == DockDensity.Default && Orientation == Orientation.Horizontal;
        bool pair = WithLoad && WithTemp;
        Thickness gap = Gap(Orientation);

        if (WithTemp)
        {
            HardwareGroup part = PartOf(reading.Id)!.Value;

            var warmth = new Metric(
                reading.Id + "-temp",
                Icons.For(reading.Id, reading.Icon),
                pair ? string.Empty : reading.Label,
                "°C",
                sensors => sensors.Catalog
                    .Where(d => d.Kind == SensorKind.Temperature && d.Prominent && d.Group == part)
                    .OrderBy(d => d.Label, StringComparer.CurrentCulture)
                    .FirstOrDefault())
            {
                // Eight points from the figure before it: close enough to be
                // one group, far enough to be two numbers.
                Spacing = pair && Orientation == Orientation.Horizontal
                    ? new Thickness(0, 0, gap.Right, 0)
                    : gap,
                Accent = Context.Accent,
                Braun = Context.Backdrop == "braun",
                Thermal = true,
                Bare = pair,
                Sample = "99 °C",
            };

            warmth.SizeFor(ReadingIcon, ReadingFont, narrow: Orientation == Orientation.Vertical, subtitle: subtitle);

            if (!pair)
            {
                Metrics.Add(warmth);
                OnPropertyChanged(nameof(Front));
                OnPropertyChanged(nameof(Second));
                OnPropertyChanged(nameof(SecondShown));
                return;
            }

            _warmth = warmth;
        }

        var metric = new Metric(
            reading.Id,
            Icons.For(reading.Id, reading.Icon),
            reading.Label,
            reading.Unit,
            sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(reading.Key)))
        {
            Spacing = pair && Orientation == Orientation.Horizontal
                ? new Thickness(gap.Left, 0, 0, 0)
                : gap,
            Accent = Context.Accent,
            Braun = Context.Backdrop == "braun",

            // A name given on the readings page wins over the built-in
            // caption; without one the caption stays the short word ("CPU"),
            // not the sensor's own longer label.
            NameFound = found => Context.Names.Renamed(found.Key) ? Context.Names.For(found) : null,

            // As long as this reading ever gets: a rate runs to "888 MB/s",
            // and everything else to a three-figure percentage.
            Sample = reading.Unit == "B/s" ? "888 MB/s" : "99 %",
        };

        metric.SizeFor(
            ReadingIcon,
            ReadingFont,
            narrow: Orientation == Orientation.Vertical,

            // Not down the side of a screen. The bar is 86 points wide there,
            // and a name measured against nothing at all was drawn straight
            // through the edge: "Receive" fits, "Получение" does not, and the
            // cut is exactly as wide as the language.
            subtitle: subtitle);
        Metrics.Add(metric);

        if (_warmth is not null)
        {
            Metrics.Add(_warmth);
            _warmth = null;
        }

        OnPropertyChanged(nameof(Front));
        OnPropertyChanged(nameof(Second));
        OnPropertyChanged(nameof(SecondShown));
    }

    private Metric? _warmth;

    public override void Tick(SensorSnapshot snapshot)
    {
        foreach (Metric metric in Metrics)
        {
            metric.Update(Sensors, snapshot);
        }

        // The memory hover answers in gigabytes, not only per cent: "36 %"
        // says how full, "11.4 / 32 GB" says of what. Rebuilt only when the
        // rounded figure moves, so an idle machine builds no strings.
        if (Reading.Id == "ram"
            && Metrics.Count > 0
            && Metrics[0].Id == "ram"
            && Metrics[0].Sensor is not null
            && snapshot[UsedBytes] is { HasValue: true } used
            && snapshot[TotalBytes] is { HasValue: true } total)
        {
            double usedGb = Math.Round(used.Value / Gb, 1);

            if (Math.Abs(usedGb - _saidGb) >= 0.1)
            {
                _saidGb = usedGb;

                Metrics[0].Detail = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc.Tr("RamDetail", "{0} · {1} / {2} GB"),
                    Loc.Tr("LabelMemory", "Memory"),
                    usedGb,
                    Math.Round(total.Value / Gb));
            }
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

    private const double Gb = 1024.0 * 1024 * 1024;

    private double _saidGb = double.MinValue;

    private static readonly SensorKey UsedBytes =
        SensorKey.Make("mem", "ram", SensorKind.Bytes, "used");

    private static readonly SensorKey TotalBytes =
        SensorKey.Make("mem", "ram", SensorKind.Bytes, "total");

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
    /// Whether the reading this names is on this machine at all.
    /// </summary>
    /// <remarks>
    /// A chip whose sensor nobody reports draws nothing - and until now it
    /// went on holding the slots it was drawing nothing in: a stretch of bar
    /// that could not be dropped into and could not be explained. Naming a
    /// reading that has gone (a drive taken out, modules the readings now
    /// group differently) hands the slots back, and the settings list it
    /// among the ones with nothing to say. The moment the reading answers
    /// again it takes its place back.
    /// </remarks>
    public override bool Possible => Matters;

    public override string Summarise() => Reading.Label;

    public override string Called =>
        Metrics.Count > 0 ? Metrics[0].Label : Reading.Label;

    /// <summary>Along the bar: the chip's width across it, its height down it.</summary>
    /// <remarks>
    /// The second figure counts once its sensor is there: a temperature this
    /// machine does not report is not drawn, and keeps no room.
    /// </remarks>
    public override double Length() =>
        Metrics.Count == 0
            ? 60
            : Metrics
                .Where((m, i) => i == 0 || m.Sensor is not null)
                .Sum(m => Orientation == Orientation.Vertical ? m.Height() : m.Width());

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var editor = new StackPanel { Spacing = 12 };
        editor.Children.Add(Which(changed));

        if (PartOf(Reading.Id) is not null)
        {
            editor.Children.Add(Mcd.App.Settings.Braun.Field(
                Loc.Tr("GaugeShowLoad", "Load"),
                Mcd.App.Settings.Braun.Switch(
                    WithLoad,
                    on => changed(WidgetOptions.Merge(
                        Options,
                        ("load", JsonValue.Create(on ? 1 : 0)),

                        // Switching the last figure off would leave an icon
                        // with nothing to say; the other comes on instead.
                        ("temp", JsonValue.Create(on ? (WithTemp ? 1 : 0) : 1)))))));

            editor.Children.Add(Mcd.App.Settings.Braun.Field(
                Loc.Tr("GaugeShowTemp", "Temperature"),
                Mcd.App.Settings.Braun.Switch(
                    WithTemp,
                    on => changed(WidgetOptions.Merge(
                        Options,
                        ("temp", JsonValue.Create(on ? 1 : 0)),
                        ("load", JsonValue.Create(on ? (WithLoad ? 1 : 0) : 1))))),
                Loc.Tr("GaugeShowTempHint", "Beside the load, under the same icon.")));
        }

        return editor;
    }

    private FrameworkElement Which(Action<JsonElement?> changed) =>
        Mcd.App.Settings.Braun.Field(
            Loc.Tr("GaugeWhich", "Which reading"),
            Mcd.App.Settings.Braun.Choice(
                [.. Known.Select(r => r.Label)],
                Array.FindIndex(Known, r => r.Id == Reading.Id),
                i => changed(WidgetOptions.Merge(
                    Options, ("reading", JsonValue.Create(Known[i].Id))))));

    /// <summary>The gap between readings, on whichever side the next one sits.</summary>
    internal static Thickness Gap(Orientation orientation) =>
        orientation == Orientation.Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(7, 0, 7, 0);
}

/// <summary>One thing a gauge can show, and where its figure comes from.</summary>
public sealed record GaugeReading(string Id, string Icon, string Label, string Unit, SensorKey Key);

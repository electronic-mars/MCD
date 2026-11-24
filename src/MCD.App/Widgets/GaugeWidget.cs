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

    /// <summary>
    /// The third figure: the temperature, when the processor's load is shown
    /// as two figures, one per kind of core.
    /// </summary>
    public Metric? Third => Metrics.Count > 2 ? Metrics[2] : null;

    public Visibility ThirdShown => Third is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The part's name, drawn by the widget rather than by either chip.
    /// </summary>
    /// <remarks>
    /// On the full-size bar it goes in a line of its own, beside a small
    /// icon, with the figures underneath: "CPU" over "1 % 58 °C". That is
    /// narrower than the same thing in one line - the name sits over the
    /// figures rather than beside them - and it says what the part is before
    /// it says how it is doing. Left to a chip, the name ends up under the
    /// figure that owns it, which reads as a caption for half a widget.
    /// </remarks>
    public string Caption { get; private set; } = string.Empty;

    public Visibility CaptionShown { get; private set; } = Visibility.Collapsed;

    /// <summary>A size down from the figures: the name is read once, the figures at a glance.</summary>
    public double CaptionFontSize { get; private set; } = 11;

    /// <summary>The part's icon, drawn by the widget while the name is in the same line.</summary>
    public string HeadIcon { get; private set; } = string.Empty;

    /// <summary>
    /// Larger than a chip's own icon: it stands beside two lines, and one
    /// sized for a single line reads as a speck next to them.
    /// </summary>
    public double HeadIconSize { get; private set; } = 20;

    public double HeadStroke => 36 / Math.Max(1, HeadIconSize);

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

    /// <summary>
    /// Whether the processor's load is shown per kind of core - "P 34 % E 12 %"
    /// - on a processor that has two kinds.
    /// </summary>
    private bool Split => Reading.Id == "cpu" && WithLoad && Mcd.Interop.Machine.Cores.Here.Hybrid
        && WidgetOptions.Number(Options, "cores") is 1;

    private static readonly SensorKey LoadP = SensorKey.Make("pdh", "cpu", SensorKind.Load, "p");
    private static readonly SensorKey LoadE = SensorKey.Make("pdh", "cpu", SensorKind.Load, "e");
    private static readonly SensorKey SpeedP = SensorKey.Make("pdh", "cpu", SensorKind.Clock, "p");
    private static readonly SensorKey SpeedE = SensorKey.Make("pdh", "cpu", SensorKind.Clock, "e");

    /// <summary>Which of the known readings this gauge is.</summary>
    public GaugeReading Reading =>
        Known.FirstOrDefault(r => r.Id == WidgetOptions.Text(Options, "reading")) ?? Known[0];

    public override void Attach()
    {
        Metrics.Clear();

        GaugeReading reading = Reading;
        bool stacked = Density == DockDensity.Default && Orientation == Orientation.Horizontal;
        bool pair = WithLoad && WithTemp;
        Thickness gap = Gap(Orientation);

        // The name and the icon are the widget's own up there, so no chip
        // draws either.
        Caption = reading.Label;
        CaptionShown = stacked ? Visibility.Visible : Visibility.Collapsed;
        HeadIcon = Icons.For(reading.Id, reading.Icon);
        HeadIconSize = Math.Round(ReadingIcon * 1.25);
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(CaptionShown));
        OnPropertyChanged(nameof(HeadIcon));
        OnPropertyChanged(nameof(HeadIconSize));
        OnPropertyChanged(nameof(HeadStroke));

        if (WithTemp)
        {
            HardwareGroup part = PartOf(reading.Id)!.Value;

            var warmth = new Metric(
                reading.Id + "-temp",
                Icons.For(reading.Id, reading.Icon),
                string.Empty,
                "°C",
                sensors => sensors.Catalog
                    .Where(d => d.Kind == SensorKind.Temperature && d.Prominent && d.Group == part)
                    .OrderBy(d => d.Label, StringComparer.CurrentCulture)
                    .FirstOrDefault())
            {
                // Eight points from the figure before it: close enough to be
                // one group, far enough to be two numbers.
                // Nothing on the left while the widget's own icon is there:
                // the gap from icon to figure has to be the same here as on a
                // chip that carries its own icon.
                Spacing = Orientation == Orientation.Horizontal && (pair || stacked)
                    ? new Thickness(0, 0, gap.Right, 0)
                    : gap,
                Accent = Context.Accent,
                Braun = Context.Backdrop == "braun",
                    Bare = pair || stacked,
                Sample = "99 °C",
            };

            warmth.SizeFor(ReadingIcon, ReadingFont, narrow: Orientation == Orientation.Vertical);

            if (!pair)
            {
                Metrics.Add(warmth);
                Announce();
                return;
            }

            _warmth = warmth;
        }

        if (Split)
        {
            // One figure per kind of core under the one icon: the first
            // carries the icon, the second follows it as closely as a
            // temperature does.
            Metrics.Add(Kind("p", LoadP, new Thickness(stacked ? 0 : gap.Left, 0, 0, 0), bare: stacked));
            Metrics.Add(Kind("e", LoadE, new Thickness(0, 0, pair ? 0 : gap.Right, 0), bare: true));

            Metric Kind(string slot, SensorKey key, Thickness along, bool bare)
            {
                var kind = new Metric(
                    "cpu-" + slot,
                    Icons.For(reading.Id, reading.Icon),
                    reading.Label,
                    reading.Unit,
                    sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(key)))
                {
                    Prefix = slot.ToUpperInvariant(),
                    Spacing = Orientation == Orientation.Horizontal ? along : gap,
                    Accent = Context.Accent,
                    Braun = Context.Backdrop == "braun",
                    Bare = bare,

                    // Its own name in the hover, in the reader's language:
                    // "P-cores", not the caption the widget shares.
                    NameFound = found => Context.Names.For(found),
                    Sample = "99 %",
                };

                kind.SizeFor(ReadingIcon, ReadingFont, narrow: Orientation == Orientation.Vertical, subtitle: false);
                return kind;
            }

            if (_warmth is not null)
            {
                Metrics.Add(_warmth);
                _warmth = null;
            }

            Announce();
            return;
        }


        var metric = new Metric(
            reading.Id,
            Icons.For(reading.Id, reading.Icon),
            reading.Label,
            reading.Unit,
            sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(reading.Key)))
        {
            Spacing = Orientation == Orientation.Horizontal && (pair || stacked)
                ? new Thickness(stacked ? 0 : gap.Left, 0, pair ? 0 : gap.Right, 0)
                : gap,
            Accent = Context.Accent,
            Braun = Context.Backdrop == "braun",
            Bare = stacked,

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
            subtitle: false);

        Metrics.Add(metric);

        if (_warmth is not null)
        {
            Metrics.Add(_warmth);
            _warmth = null;
        }

        Announce();
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(Front));
        OnPropertyChanged(nameof(Second));
        OnPropertyChanged(nameof(SecondShown));
        OnPropertyChanged(nameof(Third));
        OnPropertyChanged(nameof(ThirdShown));
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

    /// <remarks>
    /// The reading's own name when the first chip has none to give: a gauge
    /// showing only the temperature carries an unlabelled chip, and its line
    /// in the settings was left blank.
    /// </remarks>
    public override string Called =>
        Metrics.Count > 0 && Metrics[0].Label.Length > 0 ? Metrics[0].Label : Reading.Label;

    /// <summary>Along the bar: the chip's width across it, its height down it.</summary>
    /// <remarks>
    /// The second figure counts once its sensor is there: a temperature this
    /// machine does not report is not drawn, and keeps no room.
    /// </remarks>
    public override double Length()
    {
        if (Metrics.Count == 0)
        {
            return 60;
        }

        double along = Metrics
            .Where((m, i) => i == 0 || m.Sensor is not null)
            .Sum(m => Orientation == Orientation.Vertical ? m.Height() : m.Width());

        // Beside the icon stand two lines, and the widget is as wide as the
        // icon plus the wider of them: the name is the widget's own, and the
        // chips no longer carry an icon of their own to pay for.
        return CaptionShown == Visibility.Visible && Orientation == Orientation.Horizontal
            ? HeadIconSize + 4
                + Math.Max(along, Metric.Wide(Caption, CaptionFontSize))
                + Dock.DockMetrics.ChipPadding
            : along;
    }

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var editor = new StackPanel { Spacing = 12 };
        editor.Children.Add(Which(changed));

        if (PartOf(Reading.Id) is null)
        {
            return editor;
        }

        // Kept here, not read back from the settings on every press: the
        // page does not rebuild this editor when a switch writes, so the
        // options it was built from are one press out of date from then on -
        // and each switch also has to say where the other one stands.
        bool load = WithLoad;
        bool temp = WithTemp;
        bool cores = WidgetOptions.Number(Options, "cores") is 1;

        void Write() => changed(WidgetOptions.Merge(
            Options,
            ("load", JsonValue.Create(load ? 1 : 0)),
            ("temp", JsonValue.Create(temp ? 1 : 0)),
            ("cores", JsonValue.Create(cores ? 1 : 0))));

        Microsoft.UI.Xaml.Controls.Button? warmth = null;

        Microsoft.UI.Xaml.Controls.Button busy = Mcd.App.Settings.Braun.Switch(
            load,
            on =>
            {
                load = on;

                // Switching the last figure off would leave an icon with
                // nothing to say; the other comes on instead, and its own
                // switch has to show that.
                temp = on ? temp : true;

                if (!on && warmth is not null)
                {
                    Mcd.App.Settings.Braun.Flip(warmth, true);
                }

                Write();
            });

        warmth = Mcd.App.Settings.Braun.Switch(
            temp,
            on =>
            {
                temp = on;
                load = on ? load : true;

                if (!on)
                {
                    Mcd.App.Settings.Braun.Flip(busy, true);
                }

                Write();
            });

        editor.Children.Add(Mcd.App.Settings.Braun.Field(Loc.Tr("GaugeShowLoad", "Load"), busy));

        // Only where there are two kinds of core to tell apart.
        if (Reading.Id == "cpu" && Mcd.Interop.Machine.Cores.Here.Hybrid)
        {
            editor.Children.Add(Mcd.App.Settings.Braun.Field(
                Loc.Tr("GaugeShowCores", "P- and E-cores separately"),
                Mcd.App.Settings.Braun.Switch(cores, on =>
                {
                    cores = on;
                    Write();
                }),
                Loc.Tr("GaugeShowCoresHint", "Two figures instead of one: the performance cores and the efficient ones.")));
        }

        editor.Children.Add(Mcd.App.Settings.Braun.Field(
            Loc.Tr("GaugeShowTemp", "Temperature"),
            warmth,
            Loc.Tr("GaugeShowTempHint", "Beside the load, under the same icon.")));

        return editor;
    }

    private FrameworkElement Which(Action<JsonElement?> changed) =>
        Mcd.App.Settings.Braun.Field(
            Loc.Tr("GaugeWhich", "Reading"),
            Mcd.App.Settings.Braun.Choice(
                [.. Known.Select(r => r.Label)],
                Array.FindIndex(Known, r => r.Id == Reading.Id),
                i => changed(WidgetOptions.Merge(
                    Options, ("reading", JsonValue.Create(Known[i].Id))))));

    /// <summary>The gap between readings, on whichever side the next one sits.</summary>
    internal static Thickness Gap(Orientation orientation) =>
        orientation == Orientation.Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(1, 0, 1, 0);

    /// <summary>A press opens the last hour of what this widget reads, so it answers the pointer.</summary>
    public override bool Pressable => true;

    /// <summary>A press opens the last hour of what this widget reads.</summary>
    /// <remarks>
    /// The processor's profiles are not offered here: they change the power
    /// plan, and that is chosen on the Processor page, next to what each
    /// profile does and what it can cost - not by a press on the bar.
    /// </remarks>
    public override Microsoft.UI.Xaml.FrameworkElement? Details()
    {
        List<(string, SensorDescriptor)> lines =
        [
            .. Metrics.Where(m => m.Sensor is not null).Select(m => (Names.For(m.Sensor!), m.Sensor!)),
        ];

        // Each kind's speed goes with its load: the figure that says whether
        // a ceiling or the heat is holding it back.
        if (Split)
        {
            foreach (SensorKey key in new[] { SpeedP, SpeedE })
            {
                if (Sensors.Catalog.FirstOrDefault(d => d.Key.Equals(key)) is { } speed)
                {
                    lines.Add((Names.For(speed), speed));
                }
            }
        }

        return lines.Count > 0 ? ReadingHistory.Panel(lines) : null;
    }
}

/// <summary>One thing a gauge can show, and where its figure comes from.</summary>
public sealed record GaugeReading(string Id, string Icon, string Label, string Unit, SensorKey Key);

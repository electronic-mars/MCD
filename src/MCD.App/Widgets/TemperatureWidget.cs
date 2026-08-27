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
/// The temperatures this machine will admit to.
/// </summary>
/// <remarks>
/// Out of the box it shows the single hottest reading, because a machine with
/// nothing set up should say something useful without being configured. Once
/// somebody chooses which sensors they care about, every one of them is drawn on
/// the bar side by side: a temperature you have to click to see is a temperature
/// you will not look at.
/// </remarks>
public sealed class TemperatureWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.temperature";

    private const string Hottest = "hottest";
    private const string Chosen = "chosen";

    public override string TypeId => Type;

    public ObservableCollection<Metric> Metrics { get; } = [];

    /// <summary>"hottest" or "chosen".</summary>
    private string Mode => WidgetOptions.Text(Options, "mode") == Chosen ? Chosen : Hottest;

    private IReadOnlyList<string> Wanted => WidgetOptions.Strings(Options, "sensors") ?? [];

    public override void Attach()
    {
        Metrics.Clear();

        if (Mode == Chosen && Wanted.Count > 0)
        {
            foreach (string key in Wanted)
            {
                Add(new SensorKey(key));
            }

            return;
        }

        // One chip, showing whichever sensor is hottest at this moment. Which
        // one that is changes as the machine works, and that is the point: the
        // number worth glancing at is the worst one.
        Add(key: null);
    }

    public override void Tick(SensorSnapshot snapshot)
    {
        foreach (Metric metric in Metrics)
        {
            metric.Update(Sensors, snapshot);
        }
    }

    public override string Summarise()
    {
        if (Mode != Chosen || Wanted.Count == 0)
        {
            return Loc.Tr("TempHottestSummary", "The hottest reading");
        }

        string[] names = [.. Wanted.Select(Name)];

        return names.Length <= 3
            ? string.Join(", ", names)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc.Tr("TempManySummary", "{0} readings · {1}…"),
                names.Length,
                string.Join(", ", names.Take(3)));
    }

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var panel = new StackPanel { Spacing = 10 };
        List<string> wanted = [.. Wanted];

        var auto = new RadioButton
        {
            Content = Loc.Tr("TempAutoOption", "The hottest reading, whichever it is"),
            GroupName = InstanceId,
        };
        var pick = new RadioButton
        {
            Content = Loc.Tr("TempPickOption", "The readings I choose, side by side"),
            GroupName = InstanceId,
        };

        auto.IsChecked = Mode != Chosen;
        pick.IsChecked = Mode == Chosen;

        panel.Children.Add(auto);
        panel.Children.Add(pick);

        var boxes = new StackPanel { Spacing = 2 };

        // A panel cannot be disabled - only a control can - and the whole list
        // greys out together while the hottest reading is in charge.
        var list = new ContentControl
        {
            Content = boxes,
            Margin = new Thickness(24, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        panel.Children.Add(list);

        SensorDescriptor[] found = [.. Everything()];

        if (found.Length == 0)
        {
            boxes.Children.Add(Explain());
        }

        foreach (SensorDescriptor sensor in found)
        {
            var box = new CheckBox
            {
                Content = Describe(sensor),
                IsChecked = wanted.Contains(sensor.Key.Value),
                MinWidth = 0,
            };

            box.Checked += (_, _) => Choose(sensor.Key.Value, on: true);
            box.Unchecked += (_, _) => Choose(sensor.Key.Value, on: false);
            boxes.Children.Add(box);
        }

        // The list is shown either way rather than hidden with the mode, so a
        // person can see what choosing would offer before they choose it.
        list.IsEnabled = pick.IsChecked == true;

        auto.Checked += (_, _) => { list.IsEnabled = false; Save(Hottest); };
        pick.Checked += (_, _) => { list.IsEnabled = true; Save(Chosen); };

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
            Margin = new Thickness(0, 6, 0, 0),
        };

        NumberBox Limit(string header, string key)
        {
            var box = new NumberBox
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

            box.ValueChanged += (_, _) => changed(WidgetOptions.Merge(
                Options,
                (key, double.IsNaN(box.Value) ? null : JsonValue.Create((int)box.Value))));

            return box;
        }

        limits.Children.Add(Limit(Loc.Tr("TempWarnAt", "Warning at, °C"), "warn"));
        limits.Children.Add(Limit(Loc.Tr("TempCritAt", "Critical at, °C"), "crit"));
        panel.Children.Add(limits);

        return panel;

        void Choose(string key, bool on)
        {
            if (on)
            {
                if (!wanted.Contains(key))
                {
                    wanted.Add(key);
                }
            }
            else
            {
                wanted.Remove(key);
            }

            Save(Chosen);
        }

        void Save(string mode) => changed(WidgetOptions.Merge(
            Options,
            ("mode", JsonValue.Create(mode)),
            ("sensors", WidgetOptions.Array(wanted))));
    }

    /// <summary>Adds one chip: a named sensor, or the hottest of them all.</summary>
    private void Add(SensorKey? key)
    {
        SensorDescriptor? known = key is { } wanted
            ? Sensors.Catalog.FirstOrDefault(d => d.Key.Equals(wanted))
            : null;

        var metric = new Metric(
            "temp",
            Icons.For("temp", known is null ? "Temperature" : IconFor(known.Group)),
            known?.Label ?? Loc.Tr("LabelTemperature", "Temperature"),
            "°C",
            key is { } k
                ? sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(k))
                : Warmest)
        {
            Spacing = LoadWidget.Gap(Orientation),
            Accent = Context.Accent,
            Braun = Context.Backdrop == "braun",

            // This widget's own limits, when the person set any. Null falls
            // back to what the part itself declares or the ordinary defaults.
            WarnAt = WidgetOptions.Number(Options, "warn"),
            CritAt = WidgetOptions.Number(Options, "crit"),

            // The sensor may not be there yet - a graphics driver still loading,
            // an external source starting after the dock did - so the icon that
            // belongs to its hardware is chosen when it turns up rather than
            // guessed from the key now.
            IconFound = sensor => Icons.For("temp", IconFor(sensor.Group)),
        };

        metric.SizeFor(
            DockMetrics.ReadingIcon(Density),
            DockMetrics.ReadingFont(Density),
            narrow: Orientation == Orientation.Vertical,
            subtitle: Density == DockDensity.Default);
        Metrics.Add(metric);
    }

    private static string IconFor(HardwareGroup group) => group switch
    {
        HardwareGroup.Cpu => "Cpu",
        HardwareGroup.Gpu => "Gpu",
        HardwareGroup.Storage => "Disk",
        HardwareGroup.Memory => "Memory",
        HardwareGroup.Motherboard => "Computer",
        _ => "Temperature",
    };

    private string Name(string key) =>
        Sensors.Catalog.FirstOrDefault(d => d.Key.Value == key)?.Label ?? key;

    private static string Describe(SensorDescriptor sensor) =>
        sensor.Label == sensor.Hardware ? sensor.Label : $"{sensor.Label} — {sensor.Hardware}";

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
        Margin = new Thickness(4, 8, 4, 4),
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

        return Everything()
            .Where(d => d.Prominent && snapshot[d.Key].HasValue)

            // Ranked by how close each is to its own limit, not by raw degrees.
            // A drive at 60 of 85 is calmer than a graphics chip at 80 of 83,
            // and the raw numbers say the opposite.
            .OrderByDescending(d => Pressure(snapshot[d.Key].Value, d))
            .FirstOrDefault();
    }

    private static double Pressure(double value, SensorDescriptor sensor) =>
        sensor.Critical is { } critical and > 0 ? value / critical : value / 100.0;
}

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Mcd.Sensors.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// The charge of one wireless device - a mouse, a headset - in one chip.
/// </summary>
/// <remarks>
/// Which device is a sensor key, chosen in the gallery or in the chip's own
/// settings. The chip draws the kind of device, with a battery under the
/// figure; whose battery it is, the hover says.
/// </remarks>
public sealed class DeviceWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.device";

    public override string TypeId => Type;

    /// <summary>One entry, so the metric chip template serves unchanged.</summary>
    public ObservableCollection<Metric> Metrics { get; } = [];

    /// <summary>The one chip, for the template to hold directly - see GaugeWidget.Front.</summary>
    public Metric? Front => Metrics.Count > 0 ? Metrics[0] : null;

    private SensorKey? Chosen =>
        WidgetOptions.Text(Options, "sensor") is { Length: > 0 } key ? new SensorKey(key) : null;

    /// <summary>The drawing for a kind of device.</summary>
    public static string IconFor(SensorKey key) => DeviceBatteryProvider.FamilyOf(key) switch
    {
        "mouse" => "Mouse",
        "headset" => "Headset",
        "keyboard" => "Keyboard",
        _ => "Bluetooth",
    };

    public override void Attach()
    {
        Metrics.Clear();

        SensorKey? key = Chosen;

        var metric = new Metric(
            "device",
            key is { } k ? IconFor(k) : "Bluetooth",
            Loc.Tr("WidgetDeviceName", "Device battery"),
            "%",
            sensors => key is { } wanted ? sensors.Catalog.FirstOrDefault(d => d.Key.Equals(wanted)) : null)
        {
            Spacing = GaugeWidget.Gap(Orientation),
            Accent = Context.Accent,
            Braun = Context.Backdrop == "braun",
            Badged = true,
            Falls = true,
            WarnAt = 10,
            CritAt = 5,
            NameFound = sensor => Names.Renamed(sensor.Key) ? Names.For(sensor) : sensor.Hardware,
        };

        metric.SizeFor(
            ReadingIcon,
            ReadingFont,
            narrow: Orientation == Orientation.Vertical,
            subtitle: Density == DockDensity.Default && Orientation == Orientation.Horizontal);

        Metrics.Add(metric);
        OnPropertyChanged(nameof(Front));
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

    /// <summary>A device that has not turned up within this many ticks hands its slots back.</summary>
    private const int Patience = 10;

    private int _waited;

    public override bool Matters =>
        Metrics.Count == 0 || Metrics[0].Sensor is not null || _waited < Patience;

    /// <summary>A device that is not on this machine now takes no room; see TempWidget.Possible.</summary>
    public override bool Possible => Matters;

    public override string Called =>
        Metrics.Count > 0 && Metrics[0].Sensor is { } sensor
            ? Names.For(sensor)
            : Loc.Tr("WidgetDeviceName", "Device battery");

    public override string Summarise() => Called;

    public override double Length() =>
        Metrics.Count == 0
            ? 60
            : Orientation == Orientation.Vertical ? Metrics[0].Height() : Metrics[0].Width();

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        SensorDescriptor[] found =
        [
            .. Sensors.Catalog.Where(d => d.Group == HardwareGroup.Peripheral)
        ];

        return Mcd.App.Settings.Braun.Field(
            Loc.Tr("DeviceWhich", "Which device"),
            Mcd.App.Settings.Braun.Choice(
                [.. found.Select(d => d.Hardware)],
                Chosen is { } key ? Array.FindIndex(found, d => d.Key.Equals(key)) : -1,
                i => changed(WidgetOptions.Merge(Options, ("sensor", JsonValue.Create(found[i].Key.Value))))));
    }

    /// <summary>A press opens the last hour of what this widget reads, so it answers the pointer.</summary>
    public override bool Pressable => true;

    /// <summary>A press opens the last hour of what this widget reads.</summary>
    public override Microsoft.UI.Xaml.FrameworkElement? Details() =>
        Metrics.Any(m => m.Sensor is not null)
            ? ReadingHistory.Panel(Metrics.Where(m => m.Sensor is not null).Select(m => (Names.For(m.Sensor!), m.Sensor!)))
            : null;
}

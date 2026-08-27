using System.Collections.ObjectModel;
using System.Text.Json;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>How busy the machine is: processor, graphics, memory, network.</summary>
public sealed class LoadWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.load";

    /// <summary>
    /// Everything this widget is able to show, in the order it shows it.
    /// </summary>
    /// <remarks>
    /// The order is fixed here rather than taken from the settings. Which
    /// readings appear is worth choosing; which order four numbers sit in is
    /// not worth a second list to keep in step.
    /// </remarks>
    private static readonly Reading[] Known =
    [
        new("cpu", "Cpu", Loc.Tr("LabelCpu", "CPU"), "%",
            SensorKey.Make("pdh", "cpu", SensorKind.Load, "total")),
        new("ram", "Memory", Loc.Tr("LabelMemory", "Memory"), "%",
            SensorKey.Make("mem", "ram", SensorKind.Load, "used")),

        // Sent and received next to each other, each with the arrow that says
        // which way it goes. One figure with a globe beside it does not say
        // whether the machine is downloading or uploading.
        new("up", "ArrowUp", Loc.Tr("LabelSend", "Send"), "B/s",
            SensorKey.Make("pdh", "net", SensorKind.BytesPerSecond, "up")),
        new("down", "ArrowDown", Loc.Tr("LabelReceive", "Receive"), "B/s",
            SensorKey.Make("pdh", "net", SensorKind.BytesPerSecond, "down")),

        new("gpu", "Gpu", Loc.Tr("LabelGpu", "GPU"), "%",
            SensorKey.Make("pdh", "gpu", SensorKind.Load, "total")),
    ];

    public override string TypeId => Type;

    public ObservableCollection<Metric> Metrics { get; } = [];

    /// <summary>Which readings the user asked for; all of them until they say otherwise.</summary>
    /// <remarks>
    /// "net" is what the download reading was called before send and receive
    /// were separated. A settings file that still names it keeps its network
    /// figure rather than quietly losing it.
    /// </remarks>
    private IReadOnlyList<string> Chosen =>
        WidgetOptions.Strings(Options, "readings") is { } chosen
            ? [.. chosen.Select(id => id == "net" ? "down" : id)]
            : [.. Known.Select(r => r.Id)];

    public override void Attach()
    {
        Metrics.Clear();

        IReadOnlyList<string> chosen = Chosen;

        foreach (Reading reading in Known.Where(r => chosen.Contains(r.Id)))
        {
            var metric = new Metric(
                reading.Id,
                Icons.For(reading.Id, reading.Icon),
                reading.Label,
                reading.Unit,
                sensors => sensors.Catalog.FirstOrDefault(d => d.Key.Equals(reading.Key)))
            {
                Spacing = Gap(Orientation),
                Accent = Context.Accent,
            };

            metric.SizeFor(
                DockMetrics.ReadingIcon(Density),
                DockMetrics.ReadingFont(Density),
                narrow: Orientation == Orientation.Vertical,
                subtitle: Density == DockDensity.Default);
            Metrics.Add(metric);
        }
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
        IReadOnlyList<string> chosen = Chosen;
        string[] names = [.. Known.Where(r => chosen.Contains(r.Id)).Select(r => r.Label)];

        return names.Length == 0 ? Loc.Tr("LoadNothingChosen", "Nothing chosen") : string.Join(", ", names);
    }

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var panel = new StackPanel { Spacing = 6 };
        List<string> chosen = [.. Chosen];

        foreach (Reading reading in Known)
        {
            var box = new CheckBox
            {
                Content = reading.Label,
                IsChecked = chosen.Contains(reading.Id),
                MinWidth = 0,
            };

            box.Checked += (_, _) => Toggle(reading.Id, on: true);
            box.Unchecked += (_, _) => Toggle(reading.Id, on: false);
            panel.Children.Add(box);
        }

        return panel;

        void Toggle(string id, bool on)
        {
            if (on)
            {
                if (!chosen.Contains(id))
                {
                    chosen.Add(id);
                }
            }
            else
            {
                chosen.Remove(id);
            }

            // Written back in the canonical order, so the file does not record
            // an ordering that nothing reads.
            changed(WidgetOptions.Merge(
                Options,
                ("readings", WidgetOptions.Array(Known.Where(r => chosen.Contains(r.Id)).Select(r => r.Id)))));
        }
    }

    /// <summary>The gap between readings, on whichever side the next one sits.</summary>
    internal static Thickness Gap(Orientation orientation) =>
        orientation == Orientation.Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);

    private sealed record Reading(string Id, string Icon, string Label, string Unit, SensorKey Key);
}

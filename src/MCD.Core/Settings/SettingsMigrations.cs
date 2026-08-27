using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mcd.Core.Settings;

/// <summary>
/// Brings a settings file written by an older version up to date.
/// </summary>
/// <remarks>
/// Each step moves the file forward by exactly one version and is kept for good.
/// Someone who skips five releases runs all five steps in order; a step that
/// were ever rewritten to "do the right thing overall" would break for them and
/// for nobody the author could test against.
/// </remarks>
public static class SettingsMigrations
{
    public static SettingsModel Apply(SettingsModel model, ILogger log)
    {
        SettingsModel current = model;

        while (current.SchemaVersion < SettingsDefaults.SchemaVersion)
        {
            int from = current.SchemaVersion;

            current = from switch
            {
                1 => ToTwo(current),
                2 => ToThree(current),
                3 => ToFour(current),
                4 => ToFive(current),
                5 => ToSix(current),
                _ => current with { SchemaVersion = SettingsDefaults.SchemaVersion },
            };

            log.LogInformation("settings.migrated from={From} to={To}", from, current.SchemaVersion);
        }

        return current;
    }

    /// <summary>
    /// Version 2 adds the temperature widget.
    /// </summary>
    /// <remarks>
    /// Appended to every dock that does not already have one rather than left to
    /// the defaults, which only ever apply to a monitor being seen for the first
    /// time. Without this, everyone who ran version 1 would have to delete their
    /// settings - and with them their monitor layout - to get the feature the
    /// release is about.
    /// </remarks>
    private static SettingsModel ToTwo(SettingsModel model) => model with
    {
        SchemaVersion = 2,
        Monitors = [.. model.Monitors.Select(WithTemperature)],
    };

    /// <summary>Version 3 adds the launcher, at the opposite end from the readings.</summary>
    private static SettingsModel ToThree(SettingsModel model) => model with
    {
        SchemaVersion = 3,
        Monitors = [.. model.Monitors.Select(WithLauncher)],
    };

    /// <summary>
    /// Version 4 flattens the three regions into one run of widgets, with
    /// spacers standing where the free stretches of bar used to be.
    /// </summary>
    /// <remarks>
    /// The spacers are placed so the bar looks exactly as it did: one before
    /// the centre group keeps it centred, one before the end group pushes it to
    /// the far end, and a centre group with nothing after it gets a trailing
    /// spacer so it stays in the middle rather than sliding to the end.
    /// </remarks>
    private static SettingsModel ToFour(SettingsModel model) => model with
    {
        SchemaVersion = 4,
        Monitors = [.. model.Monitors.Select(Flatten)],
    };

    private static MonitorConfig Flatten(MonitorConfig monitor)
    {
        if (monitor.Bands is not { } bands)
        {
            // A fresh entry, already living on the defaults.
            return monitor;
        }

        List<WidgetConfig> flat = [.. bands.Start];

        if (!bands.Center.IsEmpty)
        {
            flat.Add(WidgetConfig.Spacer());
            flat.AddRange(bands.Center);
        }

        if (!bands.End.IsEmpty)
        {
            flat.Add(WidgetConfig.Spacer());
            flat.AddRange(bands.End);
        }
        else if (!bands.Center.IsEmpty)
        {
            flat.Add(WidgetConfig.Spacer());
        }

        return monitor with { Widgets = [.. flat], Bands = null };
    }

    /// <summary>
    /// Version 5 puts the launcher on every dock that lacks one, at the start
    /// of the run. Pinned programs live inside the launcher widget, and a dock
    /// without one silently showed none of them - which read as pinning being
    /// broken, not as a widget being absent. Anyone who does not want it
    /// removes it in the settings like any other widget.
    /// </summary>
    private static SettingsModel ToFive(SettingsModel model) => model with
    {
        SchemaVersion = 5,
        Monitors = [.. model.Monitors.Select(WithLauncherAnywhere)],
    };

    /// <summary>
    /// Version 6 breaks the composite widgets into atoms: one widget per
    /// reading, per temperature sensor, per pinned icon. Every slot on the bar
    /// becomes a thing of its own that can be dragged, added and removed alone.
    /// </summary>
    /// <remarks>
    /// The order of the run is preserved exactly - each composite is replaced
    /// in place by its parts, in the order it drew them. The launcher expands
    /// into the shared pinned list as it stood, which from here on lives on
    /// each dock rather than in one list for all of them.
    /// </remarks>
    private static SettingsModel ToSix(SettingsModel model) => model with
    {
        SchemaVersion = 6,
        Monitors =
        [
            .. model.Monitors.Select(m => m with
            {
                Widgets = [.. m.Widgets.SelectMany(w => Atoms(w, model.App.Launcher))],
            })
        ],
    };

    private static IEnumerable<WidgetConfig> Atoms(
        WidgetConfig entry, ImmutableArray<LaunchItem> pinned)
    {
        switch (entry.TypeId)
        {
            case "mcd.load":
                foreach (string reading in Readings(entry))
                {
                    yield return DockContents.Gauge(reading);
                }

                break;

            case "mcd.temperature":
                int? warn = Number(entry, "warn");
                int? crit = Number(entry, "crit");

                string[] sensors = Text(entry, "mode") == "chosen"
                    ? [.. Strings(entry, "sensors")]
                    : [];

                if (sensors.Length == 0)
                {
                    // The hottest reading, whichever it is - one widget, no key.
                    yield return Temp(sensor: null, warn, crit);
                    break;
                }

                foreach (string sensor in sensors)
                {
                    yield return Temp(sensor, warn, crit);
                }

                break;

            case "mcd.launcher":
                foreach (LaunchItem item in pinned)
                {
                    yield return WidgetConfig.New("mcd.icon") with
                    {
                        Config = WidgetJson.Object(
                            ("target", item.Target),
                            ("name", item.Name.Length > 0 ? item.Name : null),
                            ("icon", item.Icon.Length > 0 ? item.Icon : null)),
                    };
                }

                break;

            default:
                yield return entry;
                break;
        }
    }

    private static WidgetConfig Temp(string? sensor, int? warn, int? crit) =>
        WidgetConfig.New("mcd.temp") with
        {
            // Each object gets values of its own: a JsonNode belongs to one
            // parent, so nothing here may be shared between two configs.
            Config = WidgetJson.Object(
                ("sensor", sensor),
                ("warn", warn is { } w ? JsonValue.Create(w) : null),
                ("crit", crit is { } c ? JsonValue.Create(c) : null)),
        };

    /// <summary>The readings the load widget was showing; all of them when unset.</summary>
    private static IEnumerable<string> Readings(WidgetConfig entry)
    {
        if (Property(entry, "readings") is { ValueKind: JsonValueKind.Array } list)
        {
            // "net" is what the download reading was called before send and
            // receive were separated.
            return list.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() == "net" ? "down" : v.GetString()!);
        }

        return ["cpu", "ram", "up", "down", "gpu"];
    }

    private static string? Text(WidgetConfig entry, string name) =>
        Property(entry, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Number(WidgetConfig entry, string name) =>
        Property(entry, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out int n)
            ? n
            : null;

    private static IEnumerable<string> Strings(WidgetConfig entry, string name = "sensors") =>
        Property(entry, name) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
            : [];

    private static JsonElement? Property(WidgetConfig entry, string name) =>
        entry.Config is { ValueKind: JsonValueKind.Object } config
        && config.TryGetProperty(name, out JsonElement value)
            ? value
            : null;

    private static MonitorConfig WithLauncherAnywhere(MonitorConfig monitor) =>
        monitor.Widgets.Any(w => w.TypeId == "mcd.launcher")
            ? monitor
            : monitor with { Widgets = [WidgetConfig.New("mcd.launcher"), .. monitor.Widgets] };

    private static MonitorConfig WithTemperature(MonitorConfig monitor) =>
        Add(monitor, "mcd.temperature", atStart: false);

    private static MonitorConfig WithLauncher(MonitorConfig monitor) =>
        Add(monitor, "mcd.launcher", atStart: true);

    /// <summary>The dock with one more widget, unless it already had one.</summary>
    private static MonitorConfig Add(MonitorConfig monitor, string typeId, bool atStart)
    {
        DockBands bands = monitor.Bands ?? new DockBands();

        if (All(bands).Any(w => w.TypeId == typeId))
        {
            return monitor;
        }

        return monitor with
        {
            Bands = atStart
                ? bands with { Start = [.. bands.Start, WidgetConfig.New(typeId)] }
                : bands with { End = [.. bands.End, WidgetConfig.New(typeId)] },
        };
    }

    private static IEnumerable<WidgetConfig> All(DockBands bands) =>
        bands.Start.Concat(bands.Center).Concat(bands.End);
}

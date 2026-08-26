using System.Collections.Immutable;
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

    private static MonitorConfig WithTemperature(MonitorConfig monitor) =>
        Add(monitor, "mcd.temperature", atStart: false);

    private static MonitorConfig WithLauncher(MonitorConfig monitor) =>
        Add(monitor, "mcd.launcher", atStart: true);

    /// <summary>The dock with one more widget, unless it already had one.</summary>
    private static MonitorConfig Add(MonitorConfig monitor, string typeId, bool atStart)
    {
        if (All(monitor.Bands).Any(w => w.TypeId == typeId))
        {
            return monitor;
        }

        DockBands bands = monitor.Bands;

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

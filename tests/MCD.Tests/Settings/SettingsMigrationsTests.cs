using Mcd.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Mcd.Tests.Settings;

public sealed class SettingsMigrationsTests
{
    [Fact]
    public void AVersionOneFileGainsTheTemperatureWidget()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 1,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Bands = new DockBands { End = [WidgetConfig.New("mcd.load")] },
                },
            ],
        };

        SettingsModel migrated = SettingsMigrations.Apply(old, NullLogger.Instance);

        migrated.SchemaVersion.ShouldBe(SettingsDefaults.SchemaVersion);

        // Defaults only reach a monitor being seen for the first time. Without a
        // migration, everyone who ran the previous version would have to delete
        // their settings - and their monitor layout with them - to get the
        // feature the release is about.
        migrated.Monitors.Single().Bands.End
            .Select(w => w.TypeId)
            .ShouldBe(["mcd.load", "mcd.temperature"]);
    }

    [Fact]
    public void AWidgetIsNotAddedToADockThatAlreadyHasOne()
    {
        var already = new SettingsModel
        {
            SchemaVersion = 1,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Bands = new DockBands { Start = [WidgetConfig.New("mcd.temperature")] },
                },
            ],
        };

        SettingsModel migrated = SettingsMigrations.Apply(already, NullLogger.Instance);
        DockBands bands = migrated.Monitors.Single().Bands;

        // Someone who moved the widget to another band, or who ran a build that
        // already added it, must not end up with two of them.
        All(bands).Count(t => t == "mcd.temperature").ShouldBe(1);
    }

    [Fact]
    public void AVersionTwoFileGainsTheLauncher()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 2,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Bands = new DockBands { End = [WidgetConfig.New("mcd.load")] },
                },
            ],
        };

        SettingsModel migrated = SettingsMigrations.Apply(old, NullLogger.Instance);
        DockBands bands = migrated.Monitors.Single().Bands;

        bands.Start.Select(w => w.TypeId).ShouldBe(["mcd.launcher"]);
        bands.End.Select(w => w.TypeId).ShouldBe(["mcd.load"]);
    }

    [Fact]
    public void AVersionOneFileGainsBothInOneGo()
    {
        // Someone who skips a release runs both steps, in order, and ends up
        // with what a fresh install would have had.
        var old = new SettingsModel
        {
            SchemaVersion = 1,
            Monitors = [new MonitorConfig { StableId = "abc", Bands = new DockBands() }],
        };

        DockBands bands = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single().Bands;

        All(bands).ShouldBe(["mcd.launcher", "mcd.temperature"], ignoreOrder: true);
    }

    private static IEnumerable<string> All(DockBands bands) =>
        bands.Start.Concat(bands.Center).Concat(bands.End).Select(w => w.TypeId);

    [Fact]
    public void AnUpToDateFileIsNotTouched()
    {
        SettingsModel current = SettingsDefaults.Model;

        SettingsMigrations.Apply(current, NullLogger.Instance).ShouldBe(current);
    }

    [Fact]
    public void AFileFromTheFutureIsNotDowngraded()
    {
        // Someone who runs a newer build and then an older one must not have
        // their settings quietly rewritten by the older one's idea of the schema.
        var ahead = SettingsDefaults.Model with { SchemaVersion = SettingsDefaults.SchemaVersion + 3 };

        SettingsMigrations.Apply(ahead, NullLogger.Instance).SchemaVersion
            .ShouldBe(SettingsDefaults.SchemaVersion + 3);
    }
}

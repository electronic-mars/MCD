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
        migrated.Monitors.Single().Widgets
            .Select(w => w.TypeId)
            .ShouldBe(["mcd.launcher", "mcd.spacer", "mcd.load", "mcd.temperature"]);
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

        // Someone who moved the widget elsewhere, or who ran a build that
        // already added it, must not end up with two of them.
        migrated.Monitors.Single().Widgets
            .Count(w => w.TypeId == "mcd.temperature").ShouldBe(1);
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

        migrated.Monitors.Single().Widgets
            .Select(w => w.TypeId)
            .ShouldBe(["mcd.launcher", "mcd.spacer", "mcd.load"]);
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

        SettingsModel migrated = SettingsMigrations.Apply(old, NullLogger.Instance);

        migrated.Monitors.Single().Widgets
            .Select(w => w.TypeId)
            .Where(t => t != "mcd.spacer")
            .ShouldBe(["mcd.launcher", "mcd.temperature"], ignoreOrder: true);
    }

    [Fact]
    public void FlatteningKeepsTheOldAlignment()
    {
        // Start stays at the start, a spacer before the centre keeps it centred,
        // a spacer before the end pushes it to the far end.
        var old = new SettingsModel
        {
            SchemaVersion = 3,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Bands = new DockBands
                    {
                        Start = [WidgetConfig.New("mcd.launcher")],
                        Center = [WidgetConfig.New("mcd.media")],
                        End = [WidgetConfig.New("mcd.load")],
                    },
                },
            ],
        };

        MonitorConfig flat = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single();

        flat.Widgets.Select(w => w.TypeId).ShouldBe(
            ["mcd.launcher", "mcd.spacer", "mcd.media", "mcd.spacer", "mcd.load"]);
        flat.Bands.ShouldBeNull();
    }

    [Fact]
    public void ACentreWithNothingAfterItStaysCentred()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 3,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Bands = new DockBands { Center = [WidgetConfig.New("mcd.media")] },
                },
            ],
        };

        MonitorConfig flat = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single();

        // Schema 5 then puts the launcher at the front of every dock.
        flat.Widgets.Select(w => w.TypeId)
            .ShouldBe(["mcd.launcher", "mcd.spacer", "mcd.media", "mcd.spacer"]);
    }

    [Fact]
    public void AVersionFourFileGainsTheLauncherOnEveryDock()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 4,
            Monitors =
            [
                new MonitorConfig { StableId = "a", Widgets = [WidgetConfig.New("mcd.load")] },
                new MonitorConfig { StableId = "b", Widgets = [WidgetConfig.New("mcd.launcher")] },
            ],
        };

        SettingsModel migrated = SettingsMigrations.Apply(old, NullLogger.Instance);

        migrated.Monitors[0].Widgets.Select(w => w.TypeId)
            .ShouldBe(["mcd.launcher", "mcd.load"]);

        // A dock that already has one is left exactly as it was.
        migrated.Monitors[1].Widgets.Select(w => w.TypeId).ShouldBe(["mcd.launcher"]);
    }

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

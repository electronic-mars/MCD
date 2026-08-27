using System.Text.Json;
using Mcd.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Mcd.Tests.Settings;

public sealed class SettingsMigrationsTests
{
    [Fact]
    public void AVersionOneFileEndsUpAtomised()
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

        // The whole ladder in one go: the temperature is added, the launcher is
        // added and then dissolves (nothing was pinned), the regions flatten,
        // and finally every composite becomes its atoms - one widget per
        // reading, one per temperature.
        migrated.Monitors.Single().Widgets
            .Select(w => w.TypeId)
            .ShouldBe([
                "mcd.spacer",
                "mcd.gauge", "mcd.gauge", "mcd.gauge", "mcd.gauge", "mcd.gauge",
                "mcd.temp",
            ]);
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
            .Count(w => w.TypeId == "mcd.temp").ShouldBe(1);
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
            ["mcd.spacer", "mcd.media", "mcd.spacer",
             "mcd.gauge", "mcd.gauge", "mcd.gauge", "mcd.gauge", "mcd.gauge"]);
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

        flat.Widgets.Select(w => w.TypeId)
            .ShouldBe(["mcd.spacer", "mcd.media", "mcd.spacer"]);
    }

    [Fact]
    public void TheLoadWidgetBecomesOneGaugePerChosenReading()
    {
        // "net" is what the download reading was called before send and receive
        // were separated; it must keep its figure rather than quietly lose it.
        var old = new SettingsModel
        {
            SchemaVersion = 5,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Widgets =
                    [
                        WidgetConfig.New("mcd.load") with
                        {
                            Config = WidgetJson.Object(
                                ("readings", new System.Text.Json.Nodes.JsonArray("cpu", "net"))),
                        },
                    ],
                },
            ],
        };

        MonitorConfig dock = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single();

        dock.Widgets.Select(w => w.TypeId).ShouldBe(["mcd.gauge", "mcd.gauge"]);
        dock.Widgets.Select(w => w.Config!.Value.GetProperty("reading").GetString())
            .ShouldBe(["cpu", "down"]);
    }

    [Fact]
    public void ChosenTemperaturesBecomeOneWidgetPerSensorWithTheLimits()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 5,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Widgets =
                    [
                        WidgetConfig.New("mcd.temperature") with
                        {
                            Config = WidgetJson.Object(
                                ("mode", "chosen"),
                                ("sensors", new System.Text.Json.Nodes.JsonArray("hwinfo/ssd", "pdh/gpu")),
                                ("warn", 70),
                                ("crit", 90)),
                        },
                    ],
                },
            ],
        };

        MonitorConfig dock = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single();

        dock.Widgets.Select(w => w.TypeId).ShouldBe(["mcd.temp", "mcd.temp"]);
        dock.Widgets.Select(w => w.Config!.Value.GetProperty("sensor").GetString())
            .ShouldBe(["hwinfo/ssd", "pdh/gpu"]);

        foreach (WidgetConfig widget in dock.Widgets)
        {
            widget.Config!.Value.GetProperty("warn").GetInt32().ShouldBe(70);
            widget.Config!.Value.GetProperty("crit").GetInt32().ShouldBe(90);
        }
    }

    [Fact]
    public void TheHottestModeStaysOneWidgetWithNoSensor()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 5,
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Widgets = [WidgetConfig.New("mcd.temperature")],
                },
            ],
        };

        MonitorConfig dock = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single();

        WidgetConfig widget = dock.Widgets.Single();
        widget.TypeId.ShouldBe("mcd.temp");
        widget.Config!.Value.TryGetProperty("sensor", out _).ShouldBeFalse();
    }

    [Fact]
    public void TheLauncherExpandsIntoOneIconPerPinnedItemInOrder()
    {
        var old = new SettingsModel
        {
            SchemaVersion = 5,
            App = new AppSettings
            {
                Launcher =
                [
                    new LaunchItem { Id = "1", Name = "Notepad", Target = @"C:\notepad.exe" },
                    new LaunchItem { Id = "2", Target = "https://example.com", Icon = "Globe" },
                ],
            },
            Monitors =
            [
                new MonitorConfig
                {
                    StableId = "abc",
                    Widgets = [WidgetConfig.New("mcd.launcher"), WidgetConfig.New("mcd.media")],
                },
            ],
        };

        MonitorConfig dock = SettingsMigrations.Apply(old, NullLogger.Instance).Monitors.Single();

        dock.Widgets.Select(w => w.TypeId).ShouldBe(["mcd.icon", "mcd.icon", "mcd.media"]);

        JsonElement first = dock.Widgets[0].Config!.Value;
        first.GetProperty("target").GetString().ShouldBe(@"C:\notepad.exe");
        first.GetProperty("name").GetString().ShouldBe("Notepad");

        JsonElement second = dock.Widgets[1].Config!.Value;
        second.GetProperty("target").GetString().ShouldBe("https://example.com");
        second.GetProperty("icon").GetString().ShouldBe("Globe");
        second.TryGetProperty("name", out _).ShouldBeFalse();
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

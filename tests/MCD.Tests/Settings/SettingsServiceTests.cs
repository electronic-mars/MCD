using Mcd.Core.Settings;
using Mcd.Interop.AppBar;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Mcd.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mcd-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SettingsSurviveARoundTrip()
    {
        using (SettingsService first = Open())
        {
            first.Commit(
                first.Current with
                {
                    App = first.Current.App with { Theme = "dark", Autostart = true },
                    Monitors =
                    [
                        new MonitorConfig
                        {
                            StableId = "abc123",
                            FriendlyName = "LG ULTRAGEAR",
                            Edge = AppBarEdge.Left,
                            Density = DockDensity.Compact,
                            LastSeenUtc = DateTimeOffset.UnixEpoch,
                        },
                    ],
                },
                WriteReason.UserAction);

            first.Flush();
        }

        using SettingsService second = Open();

        second.Current.App.Theme.ShouldBe("dark");
        second.Current.App.Autostart.ShouldBeTrue();
        second.Current.Monitors.Single().Edge.ShouldBe(AppBarEdge.Left);
        second.Current.Monitors.Single().Density.ShouldBe(DockDensity.Compact);
    }

    [Fact]
    public void EnumsAreStoredByNameSoTheFileStaysReadable()
    {
        using (SettingsService service = Open())
        {
            service.Commit(
                service.Current with
                {
                    Monitors = [new MonitorConfig { StableId = "x", Edge = AppBarEdge.Right }],
                },
                WriteReason.UserAction);

            service.Flush();
        }

        // Numbers here would mean that reordering the enum silently moves every
        // user's dock to a different edge.
        Config().ShouldContain("\"Right\"");
    }

    [Fact]
    public void KeysWeDoNotRecogniseAreDropped()
    {
        Write("""
        {
          "schemaVersion": 1,
          "app": { "theme": "dark", "somethingFromTheFuture": 42 },
          "leftoverSection": { "anything": true }
        }
        """);

        using SettingsService service = Open();
        service.Current.App.Theme.ShouldBe("dark");

        service.Commit(service.Current with { App = service.Current.App with { Theme = "light" } },
            WriteReason.UserAction);
        service.Flush();

        Config().ShouldNotContain("leftoverSection");
        Config().ShouldNotContain("somethingFromTheFuture");
    }

    [Fact]
    public void AnUnreadableFileIsSetAsideAndTheDockStillStarts()
    {
        Write("{ this is not json");

        using SettingsService service = Open();

        service.Current.ShouldBe(SettingsDefaults.Model);
        Directory.GetFiles(_dir, "config.corrupt-*.json").ShouldNotBeEmpty();
    }

    [Fact]
    public void AnUnreadableFileFallsBackToTheBackup()
    {
        using (SettingsService service = Open())
        {
            service.Commit(service.Current with { App = service.Current.App with { Theme = "dark" } },
                WriteReason.UserAction);
            service.Flush();

            // A second write rolls the first file into config.bak.
            service.Commit(service.Current with { App = service.Current.App with { Theme = "light" } },
                WriteReason.UserAction);
            service.Flush();
        }

        Write("truncated");

        using SettingsService recovered = Open();
        recovered.Current.App.Theme.ShouldBe("dark");
    }

    [Fact]
    public void CommittingTheSameValueTwiceDoesNotWriteTwice()
    {
        using SettingsService service = Open();

        SettingsModel model = service.Current with { App = service.Current.App with { Theme = "dark" } };
        service.Commit(model, WriteReason.UserAction);
        service.Flush();

        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(_dir, "config.json"));

        service.Commit(model with { }, WriteReason.TopologyReconcile);
        service.Flush();

        File.GetLastWriteTimeUtc(Path.Combine(_dir, "config.json")).ShouldBe(written);
    }

    [Fact]
    public void AChosenIconSurvivesARestart()
    {
        using (SettingsService service = Open())
        {
            service.Commit(
                service.Current with
                {
                    App = service.Current.App with
                    {
                        Icons = service.Current.App.Icons.SetItem("cpu", "Rocket"),
                    },
                },
                WriteReason.UserAction);

            service.Flush();
        }

        using SettingsService again = Open();

        again.Current.App.Icons["cpu"].ShouldBe("Rocket");
    }

    [Fact]
    public void UndoTouchesOnlyWhatTheChangeTouched()
    {
        using SettingsService service = Open();

        service.Commit(
            service.Current with
            {
                Monitors =
                [
                    new MonitorConfig { StableId = "one", Widgets = [] },
                    new MonitorConfig { StableId = "two", Widgets = [] },
                ],
            },
            WriteReason.UserAction);

        // A person changes the first screen's bar...
        service.Commit(
            service.Current with
            {
                Monitors =
                [
                    service.Current.Monitors[0] with { Edge = AppBarEdge.Left },
                    service.Current.Monitors[1],
                ],
            },
            WriteReason.UserAction,
            "the edge");

        // ...and afterwards the second bar writes down where it settled its
        // widgets, which nobody asked for and nobody can undo.
        service.Commit(
            service.Current with
            {
                Monitors =
                [
                    service.Current.Monitors[0],
                    service.Current.Monitors[1] with
                    {
                        Widgets = [WidgetConfig.New("mcd.cpu") with { Cell = 4 }],
                    },
                ],
            },
            WriteReason.WidgetConfig);

        service.Undo().ShouldBeTrue();

        // The edge is back, and the second bar's own bookkeeping survived.
        service.Current.Monitors[0].Edge.ShouldBe(new MonitorConfig().Edge);
        service.Current.Monitors[1].Widgets.Single().Cell.ShouldBe(4);
    }

    [Fact]
    public void AForgottenScreenComesBackWithItsBar()
    {
        using SettingsService service = Open();

        service.Commit(
            service.Current with
            {
                Monitors =
                [
                    new MonitorConfig { StableId = "one" },
                    new MonitorConfig { StableId = "gone", Edge = AppBarEdge.Top },
                ],
            },
            WriteReason.UserAction);

        service.Commit(
            service.Current with
            {
                Monitors = [.. service.Current.Monitors.Where(m => m.StableId != "gone")],
            },
            WriteReason.UserAction,
            "a screen forgotten");

        service.Undo().ShouldBeTrue();

        service.Current.Monitors
            .Single(m => m.StableId == "gone").Edge.ShouldBe(AppBarEdge.Top);
    }

    [Fact]
    public void UndoLeavesAScreenThatArrivedAfterwardsAlone()
    {
        using SettingsService service = Open();

        service.Commit(
            service.Current with { Monitors = [new MonitorConfig { StableId = "one" }] },
            WriteReason.UserAction);

        service.Commit(
            service.Current with
            {
                App = service.Current.App with { Theme = "light" },
            },
            WriteReason.UserAction,
            "the theme");

        // A second screen is plugged in between the change and the undo.
        service.Commit(
            service.Current with
            {
                Monitors = [.. service.Current.Monitors, new MonitorConfig { StableId = "two" }],
            },
            WriteReason.TopologyReconcile);

        service.Undo().ShouldBeTrue();

        service.Current.App.Theme.ShouldBe("system");
        service.Current.Monitors.Select(m => m.StableId).ShouldBe(["one", "two"]);
    }

    [Fact]
    public void AnImportedFileLandsOnThisMachinesScreens()
    {
        string copy = Path.Combine(_dir, "copy.json");

        using (SettingsService theirs = Open())
        {
            theirs.Commit(
                theirs.Current with
                {
                    App = theirs.Current.App with { Theme = "light" },
                    Monitors =
                    [
                        new MonitorConfig
                        {
                            StableId = "their-first",
                            Widgets = [WidgetConfig.New("mcd.cpu")],
                            Edge = AppBarEdge.Left,
                        },
                        new MonitorConfig { StableId = "their-second" },
                    ],
                },
                WriteReason.UserAction);

            theirs.Export(copy);
        }

        File.Delete(Path.Combine(_dir, "config.json"));
        File.Delete(Path.Combine(_dir, "config.bak"));

        using SettingsService mine = Open();

        mine.Commit(
            mine.Current with
            {
                Monitors = [new MonitorConfig { StableId = "my-only", FriendlyName = "DELL" }],
            },
            WriteReason.UserAction);

        MonitorConfig before = mine.Current.Monitors[0];

        // One screen here, two in the file: the first arrangement lands, the
        // second has nowhere to go and is said so rather than dropped quietly.
        mine.Import(copy, "loaded").ShouldBe(1);

        mine.Current.App.Theme.ShouldBe("light");
        mine.Current.Monitors.Length.ShouldBe(1);
        mine.Current.Monitors[0].StableId.ShouldBe("my-only");
        mine.Current.Monitors[0].FriendlyName.ShouldBe("DELL");
        mine.Current.Monitors[0].Edge.ShouldBe(AppBarEdge.Left);
        mine.Current.Monitors[0].Widgets.Single().TypeId.ShouldBe("mcd.cpu");

        // And it is one press of undo away, like everything else a person does.
        mine.Undo().ShouldBeTrue();
        mine.Current.Monitors[0].ShouldBe(before);
    }

    [Fact]
    public void AFileThatIsNotSettingsIsRefused()
    {
        string junk = Path.Combine(_dir, "junk.json");
        File.WriteAllText(junk, "{ this is not json");

        using SettingsService service = Open();

        service.Import(junk, "loaded").ShouldBeNull();
    }

    private SettingsService Open() => new(NullLogger<SettingsService>.Instance, _dir);

    private string Config() => File.ReadAllText(Path.Combine(_dir, "config.json"));

    private void Write(string content) =>
        File.WriteAllText(Path.Combine(_dir, "config.json"), content);
}

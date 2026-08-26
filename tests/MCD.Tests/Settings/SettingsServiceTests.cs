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

    private SettingsService Open() => new(NullLogger<SettingsService>.Instance, _dir);

    private string Config() => File.ReadAllText(Path.Combine(_dir, "config.json"));

    private void Write(string content) =>
        File.WriteAllText(Path.Combine(_dir, "config.json"), content);
}

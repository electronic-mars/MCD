using Mcd.Core.Settings;
using Mcd.Interop.AppBar;
using Shouldly;
using Xunit;

namespace Mcd.Tests.Settings;

public sealed class StarterLayoutTests
{
    [Fact]
    public void ANewProfileComesWithThreeNamedLayouts()
    {
        SettingsDefaults.Model.App.Presets.Select(p => p.Name)
            .ShouldBe(["Minimal", "Monitoring", "Work"]);
    }

    [Fact]
    public void EveryStarterHasWidgetsAndIsNotTiedToAScreenCount()
    {
        foreach (BarPreset preset in DockContents.Starters)
        {
            preset.Widgets.ShouldNotBeEmpty();
            preset.Screens.ShouldBe(0);
        }
    }

    [Fact]
    public void ABarStandsAtTheTopUnlessSaidOtherwise()
    {
        new MonitorConfig().Edge.ShouldBe(AppBarEdge.Top);
    }
}

using Mcd.Interop.Machine;
using Shouldly;

namespace Mcd.Tests.Machine;

/// <summary>
/// Keeping the machine awake is process-wide state, so it is one test: two
/// running side by side would each see the other's cup.
/// </summary>
public sealed class KeepAwakeTests
{
    [Fact]
    public void ItStartsForAWhileOrForEverAndStopsWhenAsked()
    {
        KeepAwake.On.ShouldBeFalse();

        KeepAwake.Start(TimeSpan.FromMinutes(30));
        KeepAwake.On.ShouldBeTrue();
        KeepAwake.Until.ShouldNotBeNull();

        (KeepAwake.Until!.Value - DateTimeOffset.Now).ShouldBeInRange(
            TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));

        // A second start replaces the first rather than stacking on it.
        KeepAwake.Start(null);
        KeepAwake.On.ShouldBeTrue();
        KeepAwake.Until.ShouldBeNull();

        KeepAwake.Stop();
        KeepAwake.On.ShouldBeFalse();
        KeepAwake.Until.ShouldBeNull();
    }

    [Fact]
    public void ItLetsGoByItselfWhenTheTimeIsUp()
    {
        // Nothing here runs in parallel with the test above: xunit keeps the
        // tests of one class in one sequence.
        KeepAwake.Start(TimeSpan.FromMilliseconds(150));

        SpinWait.SpinUntil(() => !KeepAwake.On, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        KeepAwake.Until.ShouldBeNull();
    }
}

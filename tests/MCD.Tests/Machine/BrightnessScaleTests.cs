using Mcd.Interop.Display;
using Shouldly;

namespace Mcd.Tests.Machine;

/// <summary>
/// A monitor's brightness is a step of its own range, not a percentage: the
/// ranges differ from monitor to monitor, and a slider that means a percent
/// has to be turned into a step and back without drifting.
/// </summary>
public sealed class BrightnessScaleTests
{
    [Theory]
    [InlineData(0, 0u, 100u, 0u)]
    [InlineData(55, 0u, 100u, 55u)]
    [InlineData(100, 0u, 100u, 100u)]
    [InlineData(50, 0u, 255u, 128u)]
    [InlineData(50, 10u, 90u, 50u)]
    [InlineData(-5, 0u, 100u, 0u)]
    [InlineData(140, 0u, 100u, 100u)]
    public void APercentageBecomesAStepOfTheMonitorsOwnRange(int percent, uint minimum, uint maximum, uint step) =>
        Brightness.Scale(percent, minimum, maximum).ShouldBe(step);

    [Theory]
    [InlineData(0u, 0u, 100u, 0)]
    [InlineData(55u, 0u, 100u, 55)]
    [InlineData(128u, 0u, 255u, 50)]
    [InlineData(50u, 10u, 90u, 50)]
    [InlineData(5u, 10u, 90u, 0)]
    [InlineData(200u, 10u, 90u, 100)]
    public void AStepOfTheMonitorsOwnRangeBecomesAPercentage(uint step, uint minimum, uint maximum, int percent) =>
        Brightness.Percent(step, minimum, maximum).ShouldBe(percent);

    [Theory]
    [InlineData(0u, 100u)]
    [InlineData(0u, 255u)]
    [InlineData(16u, 80u)]
    public void ThereAndBackAgainEndsWhereItStarted(uint minimum, uint maximum)
    {
        for (int percent = 0; percent <= 100; percent++)
        {
            uint step = Brightness.Scale(percent, minimum, maximum);

            // A range of fewer than a hundred steps cannot say every
            // percentage; the answer is the nearest one it can.
            double granule = 100.0 / (maximum - minimum);
            Math.Abs(Brightness.Percent(step, minimum, maximum) - percent).ShouldBeLessThanOrEqualTo((int)Math.Ceiling(granule / 2));
        }
    }

    [Fact]
    public void ARangeThatIsNotARangeSaysNothingUseful()
    {
        Brightness.Scale(70, 40u, 40u).ShouldBe(40u);
        Brightness.Percent(40u, 40u, 40u).ShouldBe(0);
    }
}

using Mcd.Sensors.Providers;
using Shouldly;

namespace Mcd.Tests.Sensors;

public sealed class IntelThermalTests
{
    [Fact]
    public void TjMaxIsBits23To16AndOnlyWhenPlausible()
    {
        IntelThermal.TjMax(0x0000_0000_0064_0000).ShouldBe(100);
        IntelThermal.TjMax(0x0000_0000_0000_0000).ShouldBeNull();
        IntelThermal.TjMax(0x0000_0000_00FF_0000).ShouldBeNull();
    }

    [Fact]
    public void ADegreeCountIsTheThrottlePointLessTheReadout()
    {
        // Valid bit set, 39 degrees below TjMax.
        IntelThermal.Celsius(0x8000_0000 | (39u << 16), 100).ShouldBe(61);
    }

    [Fact]
    public void AReadoutWithoutTheValidBitIsNothing()
    {
        IntelThermal.Celsius(39u << 16, 100).ShouldBeNull();
    }

    [Fact]
    public void EnergyUnitsComeFromBits12To8()
    {
        // ESU = 14 is the usual: 1/16384 J per tick.
        IntelThermal.JoulesPerUnit(14u << 8).ShouldBe(1.0 / 16384);
    }

    [Fact]
    public void WattsAreJoulesOverSecondsAndSurviveTheCounterWrapping()
    {
        double unit = 1.0 / 16384;

        // 16384 ticks in one second is one joule per second.
        IntelThermal.Watts(1000, 1000 + 16384, 1.0, unit)!.Value.ShouldBe(1.0, 1e-9);

        // Wrapped: from near the top of 32 bits to just past zero.
        IntelThermal.Watts(0xFFFF_FFF0, 0x10, 1.0, unit)!.Value.ShouldBe(32 * unit, 1e-9);

        IntelThermal.Watts(1, 2, 0, unit).ShouldBeNull();
    }
}

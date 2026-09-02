namespace Mcd.Sensors.Providers;

/// <summary>
/// The arithmetic of Intel's thermal and energy registers, kept apart from
/// the driver so it can be tested against numbers.
/// </summary>
/// <remarks>
/// Intel SDM vol. 4: IA32_THERM_STATUS and IA32_PACKAGE_THERM_STATUS carry
/// the distance below the throttle point in bits 22:16, valid when bit 31 is
/// set; MSR_TEMPERATURE_TARGET carries that point in bits 23:16; the energy
/// counter MSR_PKG_ENERGY_STATUS counts in units of 1/2^ESU joules, ESU
/// being bits 12:8 of MSR_RAPL_POWER_UNIT.
/// </remarks>
public static class IntelThermal
{
    public const uint ThermStatus = 0x19C;
    public const uint PackageThermStatus = 0x1B1;
    public const uint TemperatureTarget = 0x1A2;
    public const uint RaplPowerUnit = 0x606;
    public const uint PackageEnergyStatus = 0x611;

    /// <summary>The throttle point in degrees, or null for a register that says nothing sane.</summary>
    public static int? TjMax(ulong temperatureTarget)
    {
        int degrees = (int)((temperatureTarget >> 16) & 0xFF);
        return degrees is >= 50 and <= 125 ? degrees : null;
    }

    /// <summary>Degrees, from a thermal status register and the throttle point it counts down from.</summary>
    public static int? Celsius(ulong thermStatus, int tjMax)
    {
        if ((thermStatus & 0x8000_0000u) == 0)
        {
            return null;
        }

        int below = (int)((thermStatus >> 16) & 0x7F);
        return tjMax - below;
    }

    /// <summary>Joules per tick of the energy counter.</summary>
    public static double JoulesPerUnit(ulong raplPowerUnit) =>
        1.0 / (1u << (int)((raplPowerUnit >> 8) & 0x1F));

    /// <summary>
    /// Watts, from two readings of the 32-bit energy counter and the time between them.
    /// </summary>
    /// <remarks>The counter wraps; a later reading smaller than an earlier one is one wrap.</remarks>
    public static double? Watts(ulong before, ulong after, double seconds, double joulesPerUnit)
    {
        if (seconds <= 0)
        {
            return null;
        }

        ulong ticks = ((after & 0xFFFF_FFFF) - (before & 0xFFFF_FFFF)) & 0xFFFF_FFFF;
        return ticks * joulesPerUnit / seconds;
    }
}

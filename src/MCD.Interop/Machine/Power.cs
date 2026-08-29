using Windows.Win32;
using Windows.Win32.System.Power;

namespace Mcd.Interop.Machine;

/// <summary>What the machine's battery is doing, or that it has not got one.</summary>
/// <param name="Present">False on a desktop, and on a laptop with the pack removed.</param>
/// <param name="Percent">How full, 0 to 100, or -1 when the firmware will not say.</param>
/// <param name="Charging">True while it is filling up.</param>
/// <param name="Plugged">True while the machine is on the mains, charging or not.</param>
/// <param name="Minutes">How long is left, or -1 when it is not known yet.</param>
public readonly record struct BatteryState(
    bool Present, int Percent, bool Charging, bool Plugged, int Minutes)
{
    public static BatteryState None => new(false, -1, false, true, -1);
}

/// <summary>
/// The battery, as the machine reports it.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetSystemPowerStatus</c> rather than WMI or the power-setting
/// notifications: it is one call with no handles, no COM and no thread of its
/// own, and it is what the taskbar's own indicator reads. Asking once a second
/// costs nothing measurable.
/// </para>
/// <para>
/// The interesting answer is the one that says there is no battery at all. A
/// desktop should not carry a battery widget that reads a dash forever, and the
/// only honest way to know is to ask the machine.
/// </para>
/// </remarks>
public static class Power
{
    /// <summary>Bit 7 of BatteryFlag: no system battery.</summary>
    private const byte NoBattery = 128;

    /// <summary>255: the state is unknown, which on a desktop it always is.</summary>
    private const byte Unknown = 255;

    public static BatteryState Read()
    {
        if (!PInvoke.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
        {
            return BatteryState.None;
        }

        bool present = status.BatteryFlag != NoBattery && status.BatteryFlag != Unknown;

        if (!present)
        {
            return BatteryState.None;
        }

        return new BatteryState(
            Present: true,
            Percent: status.BatteryLifePercent == Unknown ? -1 : status.BatteryLifePercent,
            Charging: (status.BatteryFlag & 8) != 0,
            Plugged: status.ACLineStatus == 1,
            Minutes: status.BatteryLifeTime == uint.MaxValue
                ? -1
                : (int)(status.BatteryLifeTime / 60));
    }
}

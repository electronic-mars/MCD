using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Mcd.Interop.Machine;

/// <summary>
/// The processor settings of the active power plan, and the Windows power mode.
/// </summary>
/// <remarks>
/// <para>
/// The plan is where Windows keeps the turbo and the frequency ceiling of each
/// kind of core, and where it reads them from on Intel and AMD alike. Most of
/// these settings are hidden from the Control Panel, not locked: the plans
/// give every user the right to change them.
/// </para>
/// <para>
/// The power mode's overlays do not carry the turbo or the ceilings - checked
/// on the overlays of an Alienware m18 R2, which carry the energy preference
/// and core parking - so what is written into the plan holds whichever mode is
/// chosen.
/// </para>
/// </remarks>
public static unsafe class PowerPlan
{
    public static readonly Guid ProcessorGroup = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Processor performance boost mode: 0 off, 1 on, 2 aggressive, and efficient variants.</summary>
    public static readonly Guid Boost = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>
    /// The maximum frequency in MHz, 0 for no ceiling: of the efficient cores,
    /// or of every core on a processor with one kind.
    /// </summary>
    public static readonly Guid MaxFrequency = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");

    /// <summary>The same, for the performance cores (efficiency class 1).</summary>
    public static readonly Guid MaxFrequencyFast = new("75b0ae3f-bce0-45a7-8c89-c9611c25e101");

    /// <summary>The power mode "Best power efficiency".</summary>
    public static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>The power mode "Balanced": no overlay at all.</summary>
    public static readonly Guid Balanced = Guid.Empty;

    /// <summary>The power mode "Best performance".</summary>
    public static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>The plan in use, or null when Windows will not say.</summary>
    public static Guid? Active()
    {
        Guid* scheme = null;

        if (PInvoke.PowerGetActiveScheme(HKEY.Null, &scheme) != WIN32_ERROR.ERROR_SUCCESS || scheme == null)
        {
            return null;
        }

        Guid found = *scheme;
        PInvoke.LocalFree((HLOCAL)scheme);
        return found;
    }

    /// <summary>One setting of a plan, on the mains or on the battery.</summary>
    public static uint? Read(Guid scheme, Guid setting, bool mains)
    {
        Guid group = ProcessorGroup;
        uint value;

        uint result = mains
            ? (uint)PInvoke.PowerReadACValueIndex(HKEY.Null, &scheme, &group, &setting, &value)
            : (uint)PInvoke.PowerReadDCValueIndex(HKEY.Null, &scheme, &group, &setting, &value);

        return result == 0 ? value : null;
    }

    /// <summary>
    /// Writes one setting. Takes effect once the plan is applied again.
    /// </summary>
    /// <returns>False when Windows refused: a plan locked by an administrator or by policy.</returns>
    public static bool Write(Guid scheme, Guid setting, bool mains, uint value)
    {
        Guid group = ProcessorGroup;

        return (mains
            ? (uint)PInvoke.PowerWriteACValueIndex(HKEY.Null, &scheme, &group, &setting, value)
            : (uint)PInvoke.PowerWriteDCValueIndex(HKEY.Null, &scheme, &group, &setting, value)) == 0;
    }

    /// <summary>Makes the written values of a plan the ones in force.</summary>
    public static bool Apply(Guid scheme) =>
        PInvoke.PowerSetActiveScheme(HKEY.Null, &scheme) == WIN32_ERROR.ERROR_SUCCESS;

    /// <summary>The power mode in force: one of the three overlays, or null when Windows will not say.</summary>
    public static Guid? Mode() => PowerGetEffectiveOverlayScheme(out Guid mode) == 0 ? mode : null;

    /// <summary>
    /// Chooses the power mode, as the slider in Windows' own settings does.
    /// </summary>
    /// <returns>False where there is no such choice - a plan other than Balanced is active.</returns>
    public static bool SetMode(Guid mode) => PowerSetActiveOverlayScheme(mode) == 0;

    // Not in the Windows metadata, so not generated: the two calls behind
    // the power-mode slider. Exported by powrprof.dll since Windows 10 1709
    // and used the same way by the laptop makers' own utilities.
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}

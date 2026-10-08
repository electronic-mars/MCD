using System.Collections.Immutable;
using System.Globalization;
using Mcd.Sensors.Contracts;

namespace Mcd.App.Widgets;

/// <summary>
/// What a reading is called and what it means, in words meant for whoever is
/// reading it.
/// </summary>
/// <remarks>
/// <para>
/// A sensor's own name comes from the part's firmware or from the counter it
/// was read out of: "Composite", "GPU clock", "RAM installed", "mem". Those
/// tell an expert exactly what they are looking at and everybody else nothing
/// at all - and they arrive in whatever mixture of capitals and abbreviations
/// each source chose.
/// </para>
/// <para>
/// So a reading is named from what it measures, which part it came off, and
/// <em>which</em> of that part's readings it is. That last one is not
/// optional: a machine with a graphics card offers its overall load, its
/// chip's load and its memory's load, and calling all three "Graphics" with
/// the same sentence underneath is worse than the firmware's own names - it
/// looks like the same reading printed three times.
/// </para>
/// <para>
/// The name and the explanation are decided in one place, from one key, so
/// they cannot drift apart.
/// </para>
/// </remarks>
public sealed class SensorNames(ImmutableDictionary<string, string> chosen)
{
    /// <summary>
    /// Names a source uses for itself rather than for a part somebody owns.
    /// </summary>
    /// <remarks>
    /// "ram", "cpu", "net" are how the providers tell their own readings
    /// apart. Printed under a name they read as a technical smudge repeated
    /// down the page. A real model - "NVIDIA GeForce RTX 4090 Laptop GPU" -
    /// is the opposite: it is the one line that says which of two drives this
    /// row is about.
    /// </remarks>
    private static readonly HashSet<string> Plumbing = new(StringComparer.OrdinalIgnoreCase)
    {
        "ram", "mem", "memory", "cpu", "gpu", "disk", "net", "network", "board", "system",
    };

    public static SensorNames None { get; } = new(ImmutableDictionary<string, string>.Empty);

    /// <summary>What to call this reading.</summary>
    public string For(SensorDescriptor sensor) =>
        chosen.TryGetValue(sensor.Key.Value, out string? own) && own.Length > 0
            ? own
            : Plain(sensor);

    /// <summary>Whether this reading has been given a name by hand.</summary>
    public bool Renamed(SensorKey key) => chosen.ContainsKey(key.Value);

    /// <summary>
    /// The part this reading came off, when that is worth printing.
    /// </summary>
    /// <remarks>
    /// Empty for the providers' own shorthand. A machine with two drives is
    /// the reason this exists at all: both rows are called "Drive", and the
    /// model underneath is what tells them apart.
    /// </remarks>
    public static string Detail(SensorDescriptor sensor)
    {
        string hardware = sensor.Hardware.Trim();

        return hardware.Length == 0 || Plumbing.Contains(hardware) ? string.Empty : hardware;
    }

    /// <summary>The ordinary name for a reading.</summary>
    public static string Plain(SensorDescriptor sensor) =>
        Known(sensor)?.Name ?? Tidy(sensor.Label);

    /// <summary>What this reading means, in a sentence.</summary>
    public static string Explain(SensorDescriptor sensor) =>
        Known(sensor)?.Note ?? General(sensor.Kind);

    /// <summary>
    /// The readings this program has words of its own for.
    /// </summary>
    /// <remarks>
    /// Matched on the part, what is measured, and which of that part's
    /// readings it is. Anything not in here keeps the name its source gave
    /// it, tidied, and the general sentence for its kind - which is the right
    /// answer for a temperature per processor core, and a poor one for
    /// anything a person would put on their bar.
    /// </remarks>
    private static (string Name, string Note)? Known(SensorDescriptor sensor)
    {
        string slot = Slot(sensor.Key);

        return (sensor.Group, sensor.Kind, slot) switch
        {
            (HardwareGroup.Cpu, SensorKind.Load, _) => (
                Loc.Tr("PartCpu", "CPU"),
                Loc.Tr("SenseCpuLoad", "CPU load.")),

            (HardwareGroup.Cpu, SensorKind.Clock, _) => (
                Loc.Tr("SenseCpuClockName", "CPU clock speed"),
                Loc.Tr("SenseCpuClock", "Clock speed as a percentage of rated speed.")),

            // Named apart from the temperature below. Two rows called
            // "Graphics" one under the other, differing only in the sentence
            // beneath them, is the fault this class exists to avoid - and it
            // was committing it on its own page.
            (HardwareGroup.Gpu, SensorKind.Load, "total") => (
                Loc.Tr("SenseGpuBusyName", "GPU load"),
                Loc.Tr("SenseGpuAll", "Total GPU load, as reported by Windows.")),

            (HardwareGroup.Gpu, SensorKind.Load, "core") => (
                Loc.Tr("SenseGpuCoreName", "GPU core"),
                Loc.Tr("SenseGpuCore", "GPU core load, as reported by the driver.")),

            // Two different numbers that read as one: the controller's
            // busyness and the memory's fullness. The first wore the second's
            // name for a while, and taught people the wrong figure.
            (HardwareGroup.Gpu, SensorKind.Load, "memory") => (
                Loc.Tr("SenseGpuVramName", "GPU memory controller"),
                Loc.Tr("SenseGpuVram", "Memory controller load, not memory in use.")),

            (HardwareGroup.Gpu, SensorKind.Load, "vram") => (
                Loc.Tr("SenseGpuVramFillName", "GPU memory in use"),
                Loc.Tr("SenseGpuVramFill", "GPU memory in use, as a percentage.")),

            (HardwareGroup.Gpu, SensorKind.Clock, "memory") => (
                Loc.Tr("SenseGpuVramClockName", "GPU memory clock speed"),
                Loc.Tr("SenseGpuVramClock", "GPU memory clock speed.")),

            (HardwareGroup.Gpu, SensorKind.Clock, _) => (
                Loc.Tr("SenseGpuClockName", "GPU clock speed"),
                Loc.Tr("SenseGpuClock", "GPU core clock speed.")),

            (HardwareGroup.Gpu, SensorKind.Fan, _) => (
                Loc.Tr("SenseGpuFanName", "GPU fan"),
                Loc.Tr("SenseGpuFan", "GPU fan speed.")),

            (HardwareGroup.Gpu, SensorKind.Power, _) => (
                Loc.Tr("SenseGpuPowerName", "GPU power"),
                Loc.Tr("SenseGpuPower", "GPU power consumption.")),

            (HardwareGroup.Gpu, SensorKind.Temperature, _) => (
                Loc.Tr("SenseGpuHeatName", "GPU temperature"),
                Loc.Tr("SenseGpuTemp", "GPU temperature.")),

            (HardwareGroup.Memory, SensorKind.Load, "commit") => (
                Loc.Tr("SenseCommitName", "Committed memory"),
                Loc.Tr("SenseCommit", "Committed memory as a percentage of the commit limit (RAM plus page file).")),

            (HardwareGroup.Memory, SensorKind.Load, _) => (
                Loc.Tr("PartMemory", "Memory"),
                Loc.Tr("SenseRamLoad", "Percentage of memory in use.")),

            (HardwareGroup.Memory, SensorKind.Bytes, "free") => (
                Loc.Tr("SenseRamFreeName", "Free memory"),
                Loc.Tr("SenseRamFree", "Installed memory not in use.")),

            (HardwareGroup.Memory, SensorKind.Bytes, "commit-used") => (
                Loc.Tr("SenseCommitUsedName", "Committed memory (bytes)"),
                Loc.Tr("SenseCommit", "Committed memory as a percentage of the commit limit (RAM plus page file).")),

            (HardwareGroup.Memory, SensorKind.Bytes, "commit-total") => (
                Loc.Tr("SenseCommitTotalName", "Commit limit"),
                Loc.Tr("SenseCommitTotal", "Maximum committed memory: RAM plus the page file.")),

            (HardwareGroup.Memory, SensorKind.Bytes, "total") => (
                Loc.Tr("SenseRamTotalName", "Installed memory"),
                Loc.Tr("SenseRamTotal", "Total installed memory.")),

            (HardwareGroup.Memory, SensorKind.Bytes, _) => (
                Loc.Tr("SenseRamUsedName", "Memory in use"),
                Loc.Tr("SenseRamUsed", "Memory in use right now.")),

            (HardwareGroup.Memory, SensorKind.Temperature, _) => (
                Loc.Tr("SenseRamHeatName", "Memory temperature"),
                Loc.Tr("SenseRamTemp", "Memory temperature. Shows the hottest module.")),

            (HardwareGroup.Storage, SensorKind.Load, _) => (
                Loc.Tr("SenseDiskBusyName", "Drive activity"),
                Loc.Tr("SenseDiskBusy", "Percentage of time the drive is active.")),

            (HardwareGroup.Storage, SensorKind.Temperature, _) => (
                Loc.Tr("SenseDiskHeatName", "Drive temperature"),
                Loc.Tr("SenseDiskTemp", "Drive temperature.")),

            (HardwareGroup.Storage, SensorKind.BytesPerSecond, "read") => (
                Loc.Tr("SenseDiskReadName", "Drive read rate"),
                Loc.Tr("SenseDiskRead", "Current read rate, all drives combined.")),

            (HardwareGroup.Storage, SensorKind.BytesPerSecond, "write") => (
                Loc.Tr("SenseDiskWriteName", "Drive write rate"),
                Loc.Tr("SenseDiskWrite", "Current write rate, all drives combined.")),

            (HardwareGroup.Network, SensorKind.BytesPerSecond, "up") => (
                Loc.Tr("LabelSend", "Send"),
                Loc.Tr("SenseNetUp", "Current send rate.")),

            (HardwareGroup.Network, SensorKind.BytesPerSecond, "down") => (
                Loc.Tr("LabelReceive", "Receive"),
                Loc.Tr("SenseNetDown", "Current receive rate.")),

            (HardwareGroup.Motherboard, SensorKind.Temperature, _) when sensor.Prominent => (
                Loc.Tr("PartBoard", "Motherboard"),
                Loc.Tr("SenseBoardTemp", "Motherboard temperature.")),

            (HardwareGroup.Peripheral, _, _) => (
                string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("OfferDeviceCharge", "{0} battery"),
                    sensor.Hardware),
                Loc.Tr("SenseDeviceCharge", "Battery charge of this device.")),

            (HardwareGroup.Cpu, SensorKind.Temperature, "core-max") => (
                Loc.Tr("SenseCpuCoreMaxName", "Hottest CPU core"),
                Loc.Tr("SenseCpuCoreMax", "Temperature of the hottest CPU core.")),

            (HardwareGroup.Cpu, SensorKind.Power, _) => (
                Loc.Tr("SenseCpuPowerName", "CPU power"),
                Loc.Tr("SenseCpuPower", "CPU power consumption.")),

            (HardwareGroup.Cpu, SensorKind.Temperature, _) when sensor.Prominent => (
                Loc.Tr("SenseCpuHeatName", "CPU temperature"),
                Loc.Tr("SenseCpuTemp", "CPU temperature.")),

            _ => null,
        };
    }

    /// <summary>The best that can be said about a reading nothing is known about.</summary>
    private static string General(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => Loc.Tr("SenseTemp", "A temperature reported by this computer."),
        SensorKind.Load => Loc.Tr("SenseLoad", "Load as a percentage of capacity."),
        SensorKind.BytesPerSecond => Loc.Tr("SenseBytes", "Current data transfer rate."),
        SensorKind.Clock => Loc.Tr("SenseClock", "Current clock speed."),
        SensorKind.Fan => Loc.Tr("SenseFan", "Fan speed."),
        SensorKind.Power => Loc.Tr("SensePower", "Power consumption."),
        SensorKind.Bytes => Loc.Tr("SenseSize", "A value reported by this computer."),
        _ => string.Empty,
    };

    /// <summary>The last part of a key: which of a part's readings this is.</summary>
    private static string Slot(SensorKey key)
    {
        string value = key.Value;
        int cut = value.LastIndexOf('/');

        return cut < 0 ? value : value[(cut + 1)..];
    }

    /// <summary>
    /// A firmware name made presentable: a first capital, and the shorthands
    /// nobody says out loud spelled out.
    /// </summary>
    private static string Tidy(string label)
    {
        string text = label.Trim();

        if (text.Length == 0)
        {
            return text;
        }

        string spelled = text.ToLowerInvariant() switch
        {
            "mem" or "ram" => Loc.Tr("PartMemory", "Memory"),
            "cpu" => Loc.Tr("PartCpu", "CPU"),
            "gpu" => Loc.Tr("PartGpu", "GPU"),
            "ssd" or "hdd" or "disk" => Loc.Tr("PartDisk", "Drive"),
            "composite" => Loc.Tr("PartWhole", "Whole device"),
            _ => text,
        };

        // Left alone once it starts with a capital or a digit: "RTX 4090" and
        // "PVC10" are names, not sentences waiting to be corrected.
        return char.IsLower(spelled[0])
            ? char.ToUpper(spelled[0], CultureInfo.CurrentCulture) + spelled[1..]
            : spelled;
    }
}

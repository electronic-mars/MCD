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
                Loc.Tr("PartCpu", "Processor"),
                Loc.Tr("SenseCpuLoad", "How busy the processor is, as a share of what it can do.")),

            (HardwareGroup.Cpu, SensorKind.Clock, _) => (
                Loc.Tr("SenseCpuClockName", "Processor speed"),
                Loc.Tr("SenseCpuClock", "How fast it is running, as a share of the speed it is rated for.")),

            // Named apart from the temperature below. Two rows called
            // "Graphics" one under the other, differing only in the sentence
            // beneath them, is the fault this class exists to avoid - and it
            // was committing it on its own page.
            (HardwareGroup.Gpu, SensorKind.Load, "total") => (
                Loc.Tr("SenseGpuBusyName", "Graphics - how busy"),
                Loc.Tr("SenseGpuAll", "Everything the graphics hardware is doing, as Windows counts it.")),

            (HardwareGroup.Gpu, SensorKind.Load, "core") => (
                Loc.Tr("SenseGpuCoreName", "Graphics chip"),
                Loc.Tr("SenseGpuCore", "How busy the chip itself is, straight from the driver.")),

            (HardwareGroup.Gpu, SensorKind.Load, "memory") => (
                Loc.Tr("SenseGpuVramName", "Graphics memory"),
                Loc.Tr("SenseGpuVram", "How busy the memory on the graphics card is.")),

            (HardwareGroup.Gpu, SensorKind.Clock, "memory") => (
                Loc.Tr("SenseGpuVramClockName", "Graphics memory speed"),
                Loc.Tr("SenseGpuVramClock", "What speed the memory on the graphics card is running at.")),

            (HardwareGroup.Gpu, SensorKind.Clock, _) => (
                Loc.Tr("SenseGpuClockName", "Graphics speed"),
                Loc.Tr("SenseGpuClock", "What speed the graphics chip is running at.")),

            (HardwareGroup.Gpu, SensorKind.Fan, _) => (
                Loc.Tr("SenseGpuFanName", "Graphics fan"),
                Loc.Tr("SenseGpuFan", "How fast the graphics card's fan is turning.")),

            (HardwareGroup.Gpu, SensorKind.Power, _) => (
                Loc.Tr("SenseGpuPowerName", "Graphics power"),
                Loc.Tr("SenseGpuPower", "How much power the graphics card is drawing.")),

            (HardwareGroup.Gpu, SensorKind.Temperature, _) => (
                Loc.Tr("SenseGpuHeatName", "Graphics - temperature"),
                Loc.Tr("SenseGpuTemp", "How hot the graphics chip is.")),

            (HardwareGroup.Memory, SensorKind.Load, _) => (
                Loc.Tr("PartMemory", "Memory"),
                Loc.Tr("SenseRamLoad", "How much of the memory is taken up, as a share of all of it.")),

            (HardwareGroup.Memory, SensorKind.Bytes, "total") => (
                Loc.Tr("SenseRamTotalName", "Memory installed"),
                Loc.Tr("SenseRamTotal", "How much memory is fitted in this machine.")),

            (HardwareGroup.Memory, SensorKind.Bytes, _) => (
                Loc.Tr("SenseRamUsedName", "Memory in use"),
                Loc.Tr("SenseRamUsed", "How much memory is taken up right now.")),

            (HardwareGroup.Memory, SensorKind.Temperature, _) => (
                Loc.Tr("PartMemory", "Memory"),
                Loc.Tr("SenseRamTemp", "How hot the memory is.")),

            (HardwareGroup.Storage, SensorKind.Load, _) => (
                Loc.Tr("SenseDiskBusyName", "Drive activity"),
                Loc.Tr("SenseDiskBusy", "How much of the time this drive is busy.")),

            (HardwareGroup.Storage, SensorKind.Temperature, _) => (
                Loc.Tr("PartDisk", "Drive"),
                Loc.Tr("SenseDiskTemp", "How hot this drive is.")),

            (HardwareGroup.Network, SensorKind.BytesPerSecond, "up") => (
                Loc.Tr("LabelSend", "Send"),
                Loc.Tr("SenseNetUp", "How much data is going out right now.")),

            (HardwareGroup.Network, SensorKind.BytesPerSecond, "down") => (
                Loc.Tr("LabelReceive", "Receive"),
                Loc.Tr("SenseNetDown", "How much data is coming in right now.")),

            (HardwareGroup.Motherboard, SensorKind.Temperature, _) when sensor.Prominent => (
                Loc.Tr("PartBoard", "Motherboard"),
                Loc.Tr("SenseBoardTemp", "How hot the motherboard is.")),

            (HardwareGroup.Cpu, SensorKind.Temperature, _) when sensor.Prominent => (
                Loc.Tr("PartCpu", "Processor"),
                Loc.Tr("SenseCpuTemp", "How hot the processor is.")),

            _ => null,
        };
    }

    /// <summary>The best that can be said about a reading nothing is known about.</summary>
    private static string General(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => Loc.Tr("SenseTemp", "A temperature this machine reports."),
        SensorKind.Load => Loc.Tr("SenseLoad", "How busy it is, as a share of what it can do."),
        SensorKind.BytesPerSecond => Loc.Tr("SenseBytes", "How much data is moving right now."),
        SensorKind.Clock => Loc.Tr("SenseClock", "What speed it is running at."),
        SensorKind.Fan => Loc.Tr("SenseFan", "How fast this fan is turning."),
        SensorKind.Power => Loc.Tr("SensePower", "How much power it is drawing."),
        SensorKind.Bytes => Loc.Tr("SenseSize", "An amount this machine reports."),
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
            "cpu" => Loc.Tr("PartCpu", "Processor"),
            "gpu" => Loc.Tr("PartGpu", "Graphics"),
            "ssd" or "hdd" or "disk" => Loc.Tr("PartDisk", "Drive"),
            "composite" => Loc.Tr("PartWhole", "The whole part"),
            _ => text,
        };

        // Left alone once it starts with a capital or a digit: "RTX 4090" and
        // "PVC10" are names, not sentences waiting to be corrected.
        return char.IsLower(spelled[0])
            ? char.ToUpper(spelled[0], CultureInfo.CurrentCulture) + spelled[1..]
            : spelled;
    }
}

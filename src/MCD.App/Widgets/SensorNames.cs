using System.Collections.Immutable;
using System.Globalization;
using Mcd.Sensors.Contracts;

namespace Mcd.App.Widgets;

/// <summary>
/// What a reading is called, in words meant for whoever is reading it.
/// </summary>
/// <remarks>
/// A sensor's own name comes from the part's firmware: "Composite", "CPU
/// Package", "mem", a drive's model number. Those tell an expert exactly what
/// they are looking at and everybody else nothing at all - and they arrive in
/// whatever mixture of capitals and abbreviations each maker chose. What is
/// shown is the person's own name for it if they gave one, then a plain name
/// for the part, and only then the raw one, tidied.
/// </remarks>
public sealed class SensorNames(ImmutableDictionary<string, string> chosen)
{
    public static SensorNames None { get; } = new(ImmutableDictionary<string, string>.Empty);

    /// <summary>What to call this reading.</summary>
    public string For(SensorDescriptor sensor) =>
        chosen.TryGetValue(sensor.Key.Value, out string? own) && own.Length > 0
            ? own
            : Plain(sensor);

    /// <summary>Whether this reading has been given a name by hand.</summary>
    public bool Renamed(SensorKey key) => chosen.ContainsKey(key.Value);

    /// <summary>The ordinary name for a part's headline reading.</summary>
    /// <remarks>
    /// Only for the one reading that stands for the whole part. A processor
    /// reports a dozen of them, and calling every core "Processor" would be
    /// worse than the firmware's own names.
    /// </remarks>
    private static string Plain(SensorDescriptor sensor)
    {
        if (sensor.Prominent && sensor.Kind == SensorKind.Temperature)
        {
            string? plain = sensor.Group switch
            {
                HardwareGroup.Cpu => Loc.Tr("PartCpu", "Processor"),
                HardwareGroup.Gpu => Loc.Tr("PartGpu", "Graphics"),
                HardwareGroup.Storage => Loc.Tr("PartDisk", "Drive"),
                HardwareGroup.Memory => Loc.Tr("PartMemory", "Memory"),
                HardwareGroup.Motherboard => Loc.Tr("PartBoard", "Motherboard"),
                _ => null,
            };

            if (plain is not null)
            {
                return plain;
            }
        }

        return Tidy(sensor.Label);
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

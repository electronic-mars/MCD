namespace Mcd.Sensors.Contracts;

/// <summary>What a reading measures.</summary>
public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Fan,
    Power,
    Bytes,
    BytesPerSecond,
}

/// <summary>Which part of the machine a reading came from.</summary>
public enum HardwareGroup
{
    Cpu,
    Gpu,
    Memory,
    Storage,
    Network,
    Motherboard,
}

/// <summary>
/// The name a widget stores to say which reading it wants.
/// </summary>
/// <remarks>
/// <para>
/// Format: <c>{provider}/{hardware}/{kind}/{slot}</c>, for example
/// <c>nvml/gpu-10de1f95/temp/core</c>.
/// </para>
/// <para>
/// The hardware part must never be an index. A widget's key lives in
/// config.json and has to mean the same thing after a reboot, after a driver
/// update, and after the user moves a graphics card to another slot - so it is
/// derived from the hardware itself, not from the order things were enumerated
/// in this time.
/// </para>
/// </remarks>
public readonly record struct SensorKey(string Value)
{
    public static SensorKey Make(string provider, string hardware, SensorKind kind, string slot) =>
        new($"{provider}/{hardware}/{Abbreviate(kind)}/{slot}");

    public override string ToString() => Value;

    private static string Abbreviate(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "temp",
        SensorKind.Load => "load",
        SensorKind.Clock => "clock",
        SensorKind.Fan => "fan",
        SensorKind.Power => "power",
        SensorKind.Bytes => "bytes",
        SensorKind.BytesPerSecond => "rate",
        _ => "other",
    };
}

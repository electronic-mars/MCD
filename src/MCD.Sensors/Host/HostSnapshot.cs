using System.Text.Json.Serialization;

namespace Mcd.Sensors.Host;

/// <summary>
/// What the sensor service says, once a second.
/// </summary>
/// <remarks>
/// Shared by the service that writes it and the bar that reads it, so the two
/// cannot quietly disagree about a field. Nullable figures are figures the
/// service could not read this second; an empty module list is a machine
/// with no module thermometers, or a bus it could not scan.
/// </remarks>
public sealed record HostSnapshot(
    int Version,
    string Model,
    int TjMax,
    double? Package,
    double? CoreMax,
    double? Watts,
    DimmReading[] Dimms,
    long At = 0)
{
    public const string PipeName = "MasterControlDock.Sensors";

    public static HostSnapshot Empty { get; } = new(1, string.Empty, 0, null, null, null, []);

    /// <summary>
    /// When the service took this reading, in Unix milliseconds; nought from
    /// a service too old to say. The service sends its latest reading every
    /// second whether or not it has taken a new one, so a service stuck
    /// mid-reading looked as alive as a working one.
    /// </summary>
    [JsonIgnore]
    public bool Stale => At > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - At > 15_000;

    /// <summary>Whether the service has the processor at all.</summary>
    [JsonIgnore]
    public bool HasProcessor => TjMax > 0;
}

/// <summary>One memory module's thermometer, by the slot it sits in.</summary>
public sealed record DimmReading(int Slot, double Celsius);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostSnapshot))]
public sealed partial class HostJson : JsonSerializerContext
{
}

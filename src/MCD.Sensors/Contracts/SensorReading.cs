using System.Collections.Immutable;

namespace Mcd.Sensors.Contracts;

/// <summary>How much a reading can be trusted right now.</summary>
public enum Quality
{
    /// <summary>Read this tick.</summary>
    Fresh,

    /// <summary>The last good value, kept while its source is having trouble.</summary>
    Stale,

    /// <summary>The source has given up. Widgets show a dash, never a zero.</summary>
    Missing,
}

/// <summary>What a sensor is and how to present it.</summary>
/// <param name="Unit">Already localised at the point of display; this is the symbol, e.g. "°C".</param>
/// <param name="Warning">Where a temperature stops being comfortable, if it has such a point.</param>
/// <param name="Critical">Where it stops being safe.</param>
/// <param name="Rank">
/// Used when two providers offer the same physical sensor. Higher wins: a value
/// straight from the graphics driver beats the same value relayed by a
/// third-party monitor.
/// </param>
/// <param name="Prominent">
/// Whether this is worth showing without being asked for. False for the readings
/// a source offers by the dozen - a temperature per processor core, say - which
/// belong in a list someone goes looking for, not on a bar they glance at.
/// </param>
public sealed record SensorDescriptor(
    SensorKey Key,
    SensorKind Kind,
    HardwareGroup Group,
    string Hardware,
    string Label,
    string Unit,
    double? Warning = null,
    double? Critical = null,
    int Rank = 0,
    bool Prominent = true);

/// <summary>One value, at one moment.</summary>
public readonly record struct SensorReading(double Value, Quality Quality)
{
    public static SensorReading Missing => new(double.NaN, Quality.Missing);

    public bool HasValue => Quality != Quality.Missing && !double.IsNaN(Value);

    public SensorReading Aging() => Quality == Quality.Missing ? this : this with { Quality = Quality.Stale };
}

/// <summary>
/// Everything measured, as of one tick.
/// </summary>
/// <remarks>
/// Immutable, and replaced wholesale rather than mutated. Readers - every dock
/// window, on the UI thread - take the current reference and use it without a
/// lock, and cannot see a half-updated set of values.
/// </remarks>
public sealed record SensorSnapshot(
    ImmutableDictionary<SensorKey, SensorReading> Readings,
    ImmutableDictionary<SensorKey, ImmutableArray<double>> History,
    DateTimeOffset TakenAt)
{
    public static SensorSnapshot Empty { get; } = new(
        ImmutableDictionary<SensorKey, SensorReading>.Empty,
        ImmutableDictionary<SensorKey, ImmutableArray<double>>.Empty,
        DateTimeOffset.UnixEpoch);

    public SensorReading this[SensorKey key] =>
        Readings.TryGetValue(key, out SensorReading reading) ? reading : SensorReading.Missing;

    /// <summary>
    /// The last minute of a sensor, oldest first. Empty unless someone asked the
    /// hub to keep it.
    /// </summary>
    /// <remarks>
    /// History is kept in the hub rather than in the widget that draws it. When
    /// the display topology changes every dock window is destroyed and rebuilt,
    /// and a graph that started again from nothing on every Win+P would be
    /// useless exactly when it is most interesting.
    /// </remarks>
    public ImmutableArray<double> Trend(SensorKey key) =>
        History.TryGetValue(key, out ImmutableArray<double> trend) ? trend : [];
}

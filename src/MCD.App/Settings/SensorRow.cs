using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Sensors.Contracts;

namespace Mcd.App.Settings;

/// <summary>One reading on the sensors page: what it is, and what it says now.</summary>
/// <remarks>
/// The page exists to answer "is it reading anything, and from where" without
/// anyone having to open a log file. That question is the whole reason the
/// PowerToys dock bug was impossible for its users to report usefully.
/// </remarks>
public sealed partial class SensorRow(SensorDescriptor sensor) : ObservableObject
{
    public SensorKey Key { get; } = sensor.Key;

    public string Label { get; } = sensor.Label;

    /// <summary>The hardware, and the source it came through.</summary>
    public string Hardware { get; } = $"{sensor.Hardware} · {Source(sensor.Key)}";

    [ObservableProperty]
    public partial string Value { get; set; } = "--";

    private string Unit { get; } = sensor.Unit;

    public void Update(SensorSnapshot snapshot)
    {
        SensorReading reading = snapshot[Key];

        Value = reading.HasValue
            ? reading.Value.ToString("F0", CultureInfo.InvariantCulture) + " " + Unit
            : "--";
    }

    /// <summary>The provider's name, which is the first part of every key it makes.</summary>
    private static string Source(SensorKey key) =>
        key.Value.Split('/', 2)[0];
}

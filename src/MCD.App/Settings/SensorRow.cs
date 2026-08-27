using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Sensors;
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

    /// <summary>
    /// What this reading means, in ordinary words. The label above it is the
    /// part's own name, which is a model number as often as not.
    /// </summary>
    public string Explain { get; } = ExplainOf(sensor);

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

    private static string ExplainOf(SensorDescriptor sensor) => sensor.Kind switch
    {
        SensorKind.Temperature => sensor.Group switch
        {
            HardwareGroup.Cpu => Loc.Tr("SenseCpuTemp", "How hot the processor is."),
            HardwareGroup.Gpu => Loc.Tr("SenseGpuTemp", "How hot the graphics chip is."),
            HardwareGroup.Storage => Loc.Tr("SenseDiskTemp", "How hot this drive is."),
            HardwareGroup.Memory => Loc.Tr("SenseRamTemp", "How hot the memory is."),
            HardwareGroup.Motherboard => Loc.Tr("SenseBoardTemp", "How hot the motherboard is."),
            _ => Loc.Tr("SenseTemp", "A temperature this machine reports."),
        },
        SensorKind.Load => Loc.Tr("SenseLoad", "How busy it is, as a share of what it can do."),
        SensorKind.BytesPerSecond => Loc.Tr("SenseBytes", "How much data is moving right now."),
        _ => string.Empty,
    };
}

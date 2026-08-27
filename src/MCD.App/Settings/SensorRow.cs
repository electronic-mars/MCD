using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Widgets;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;

namespace Mcd.App.Settings;

/// <summary>One reading on the sensors page: what it is, and what it says now.</summary>
/// <remarks>
/// The page exists to answer "is it reading anything, and from where" without
/// anyone having to open a log file. That question is the whole reason the
/// PowerToys dock bug was impossible for its users to report usefully.
/// </remarks>
public sealed partial class SensorRow : ObservableObject
{
    private readonly SensorDescriptor _sensor;
    private readonly Action<SensorRow, string> _rename;

    public SensorRow(SensorDescriptor sensor, SensorNames names, Action<SensorRow, string> rename)
    {
        _sensor = sensor;
        _rename = rename;

        Key = sensor.Key;
        Label = names.For(sensor);
        Explain = ExplainOf(sensor);
        Hardware = sensor.Hardware;
        Unit = sensor.Unit;
    }

    public SensorKey Key { get; }

    /// <summary>What this reading is called - the person's own name, if they gave one.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>What the part itself is, under the name.</summary>
    public string Hardware { get; }

    /// <summary>
    /// What this reading means, in ordinary words. The name above it is the
    /// part's, which is a model number as often as not.
    /// </summary>
    public string Explain { get; }

    [ObservableProperty]
    public partial string Value { get; set; } = "--";

    /// <summary>True while the name is being typed rather than read.</summary>
    [ObservableProperty]
    public partial bool Renaming { get; set; }

    private string Unit { get; }

    /// <summary>Takes the typed name, or puts the old one back when it is empty.</summary>
    public void Rename(string typed)
    {
        Renaming = false;
        _rename(this, typed.Trim());
    }

    public void Update(SensorSnapshot snapshot)
    {
        SensorReading reading = snapshot[Key];

        Value = reading.HasValue
            ? reading.Value.ToString("F0", CultureInfo.InvariantCulture) + " " + Unit
            : "--";
    }

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

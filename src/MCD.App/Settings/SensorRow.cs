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
        Explain = SensorNames.Explain(sensor);
        Hardware = SensorNames.Detail(sensor);
        Unit = sensor.Unit;
    }

    public SensorKey Key { get; }

    /// <summary>What this reading is called - the person's own name, if they gave one.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>
    /// Which part this came off - a model number, or nothing at all.
    /// </summary>
    /// <remarks>
    /// A source's own shorthand for itself is not printed: "ram" under
    /// "Memory" says nothing and repeats down the page. A real model does the
    /// one job this line has, which is telling two drives apart.
    /// </remarks>
    public string Hardware { get; }

    /// <summary>Whether there is a part worth naming under the name.</summary>
    public Microsoft.UI.Xaml.Visibility HardwareShown => Hardware.Length > 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>What a double-click on this row does.</summary>
    public string RenameHint =>
        Loc.Tr("RenameTip", "Double-click to give this reading a name of your own");

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

        Value = reading.HasValue ? Readable.Value(reading.Value, Unit) : "--";
    }
}

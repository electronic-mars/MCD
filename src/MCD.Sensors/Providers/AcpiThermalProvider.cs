using System.Management;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The firmware's own thermal zones, when the machine exposes them.
/// </summary>
/// <remarks>
/// <para>
/// The only temperature Windows will hand an ordinary program without a kernel
/// driver, which is why it is worth having despite its faults: on machines
/// that answer, it usually tracks the processor closely enough to be worth a
/// glance. Many machines do not answer at all - the query returns "Not
/// supported" - and this provider simply is not there on those, the same as
/// hardware the machine does not have.
/// </para>
/// <para>
/// The zone is firmware territory, so it is never allowed to pretend to be
/// the processor sensor itself: a real reading from a monitoring program
/// outranks it, and its label says what it is.
/// </para>
/// </remarks>
public sealed class AcpiThermalProvider(ILogger<AcpiThermalProvider> log) : ISensorProvider
{
    public const string ProviderId = "acpi";

    private ManagementObjectSearcher? _searcher;
    private bool _probed;
    private bool _present;

    public string Id => ProviderId;

    public Tier Tier => Tier.Platform;

    /// <summary>
    /// How often the firmware's thermal zone is asked.
    /// </summary>
    /// <remarks>
    /// Rarely. Each ask is a call across into the WMI host, which calls into
    /// the ACPI driver, which runs a method in the firmware - and it keeps
    /// that host process resident for as long as this program runs. The
    /// temperature of a case does not move in two seconds.
    /// </remarks>
    public TimeSpan Interval => TimeSpan.FromSeconds(10);

    public bool IsAvailable()
    {
        if (_probed)
        {
            return _present;
        }

        _probed = true;

        try
        {
            _searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            _present = Read() is not null;
        }
        catch (Exception e)
        {
            // "Not supported" is the ordinary answer on machines whose
            // firmware does not publish a zone. Not a fault, just absent.
            log.LogInformation("sensors.acpi thermal zones not offered: {Reason}", e.Message);
            _present = false;
        }

        log.LogInformation("sensors.acpi present={Present}", _present);
        return _present;
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        new(
            Key,
            SensorKind.Temperature,
            HardwareGroup.Cpu,
            "ACPI",
            "Thermal zone",
            "°C",

            // The firmware slows the machine down around here itself.
            Warning: 85,
            Critical: 95,

            // Anything that reads the part directly outranks the firmware's
            // rounded, laggy zone.
            Rank: 1),
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (Read() is { } celsius)
        {
            into[Key] = celsius;
        }
    }

    public void Dispose()
    {
        _searcher?.Dispose();
        _searcher = null;
    }

    private static SensorKey Key =>
        SensorKey.Make(ProviderId, "zone", SensorKind.Temperature, "max");

    /// <summary>The hottest zone, in degrees. Null when nothing answers.</summary>
    /// <remarks>
    /// The value arrives in tenths of a kelvin. Some firmware publishes a zone
    /// and then reports a constant or a zero; those are filtered by the same
    /// plausibility bounds a thermometer would use.
    /// </remarks>
    private double? Read()
    {
        if (_searcher is null)
        {
            return null;
        }

        double? hottest = null;

        foreach (ManagementBaseObject zone in _searcher.Get())
        {
            using (zone)
            {
                if (zone["CurrentTemperature"] is not uint tenthsKelvin)
                {
                    continue;
                }

                double celsius = (tenthsKelvin / 10.0) - 273.15;

                if (celsius is > 5 and < 120 && (hottest is null || celsius > hottest))
                {
                    hottest = celsius;
                }
            }
        }

        return hottest;
    }
}

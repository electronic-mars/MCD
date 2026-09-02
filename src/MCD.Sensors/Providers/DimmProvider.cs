using Mcd.Interop.PawnIo;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;
using RAMSPDToolkit.SPD.Interfaces;
using RAMSPDToolkit.SPD.Interop.Shared;
using RAMSPDToolkit.Windows.Driver;
using RAMSPDToolkit.Windows.Driver.Implementations;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The temperature of each memory module, from the sensor on the module.
/// </summary>
/// <remarks>
/// <para>
/// DDR5 sticks (and some DDR4) carry a thermometer next to their SPD chip,
/// reachable over the SMBus - which is a kernel's business, so this too goes
/// through PawnIO, by way of RAMSPDToolkit, which knows the bus controllers
/// and the SPD dialects. The toolkit reads only; it says itself that writing
/// on that bus is dangerous, and nothing here writes.
/// </para>
/// <para>
/// Every fifth second. A memory module's temperature moves slowly, and each
/// read is a handful of bus transactions under a system-wide mutex.
/// </para>
/// </remarks>
public sealed class DimmProvider(ILogger<DimmProvider> log) : ISensorProvider
{
    private const string ProviderId = "dimm";

    private readonly List<Stick> _sticks = [];
    private bool _looked;
    private bool _saidNoDriver;

    public string Id => ProviderId;

    public Tier Tier => Tier.Driver;

    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public bool IsAvailable()
    {
        if (_sticks.Count != 0)
        {
            return true;
        }

        if (PawnIo.InstalledVersion() is null && !PawnIo.Present())
        {
            if (!_saidNoDriver)
            {
                _saidNoDriver = true;
                log.LogInformation("sensors.dimm the PawnIO driver is not installed; no memory temperature");
            }

            return false;
        }

        // Once. The bus scan touches every SPD address on every controller;
        // a machine that showed no thermometer the first time will not have
        // grown one, and the hub asks this every half minute.
        if (_looked)
        {
            return false;
        }

        _looked = true;

        try
        {
            if (!DriverManager.LoadDriver(DriverImplementation.PawnIO))
            {
                log.LogWarning("sensors.dimm PawnIO is installed but the SMBus module would not load");
                return false;
            }

            SMBusManager.DetectSMBuses();

            foreach (SMBusInterface bus in SMBusManager.RegisteredSMBuses)
            {
                for (byte address = SPDConstants.SPD_BEGIN; address <= SPDConstants.SPD_END; address++)
                {
                    var detector = new SPDDetector(bus, address);

                    if (!detector.IsValid || detector.Accessor is not IThermalSensor sensor
                        || !sensor.HasThermalSensor)
                    {
                        continue;
                    }

                    int slot = address - SPDConstants.SPD_BEGIN;
                    _sticks.Add(new Stick(sensor, $"dimm{slot}", $"DIMM {slot + 1}"));
                }
            }
        }
        catch (Exception e)
        {
            // The toolkit throws for a controller it half-recognises; a bus
            // that cannot be scanned is a bus with no thermometers on it.
            log.LogWarning(e, "sensors.dimm the memory bus could not be scanned");
            _sticks.Clear();
            return false;
        }

        log.LogInformation("sensors.dimm found {Count} module(s) with a thermometer", _sticks.Count);
        return _sticks.Count != 0;
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        .. _sticks.Select(s => new SensorDescriptor(
            SensorKey.Make(ProviderId, s.Key, SensorKind.Temperature, "spd"),
            SensorKind.Temperature,
            HardwareGroup.Memory,
            s.Name,
            s.Name,
            "°C",
            Warning: 70,
            Critical: 85))
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        foreach (Stick stick in _sticks)
        {
            try
            {
                if (stick.Sensor.UpdateTemperature())
                {
                    into[SensorKey.Make(ProviderId, stick.Key, SensorKind.Temperature, "spd")] =
                        stick.Sensor.Temperature;
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "sensors.dimm {Name} did not answer", stick.Name);
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed record Stick(IThermalSensor Sensor, string Key, string Name);
}

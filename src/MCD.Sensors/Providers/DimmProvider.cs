using Mcd.Sensors.Contracts;
using Mcd.Sensors.Host;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The temperature of each memory module, as the sensor service reads it
/// from the thermometer on the module.
/// </summary>
/// <remarks>
/// DDR5 sticks (and some DDR4) carry a thermometer next to their SPD chip,
/// reachable over the SMBus - a kernel's business, done by the service and
/// handed over the pipe. One sensor per module that has one, named by slot.
/// </remarks>
public sealed class DimmProvider(HostClient host, ILogger<DimmProvider> log) : ISensorProvider
{
    private const string ProviderId = "dimm";

    private int[] _slots = [];

    public string Id => ProviderId;

    public Tier Tier => Tier.Driver;

    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public bool IsAvailable()
    {
        if (!host.EnsureConnected())
        {
            return false;
        }

        int[] slots = [.. host.Latest.Dimms.Select(d => d.Slot).Order()];

        if (!slots.SequenceEqual(_slots))
        {
            _slots = slots;
            log.LogInformation("sensors.dimm {Count} module(s) with a thermometer", slots.Length);
        }

        return slots.Length != 0;
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        .. _slots.Select((slot, i) => new SensorDescriptor(
            SensorKey.Make(ProviderId, $"dimm{slot}", SensorKind.Temperature, "spd"),
            SensorKind.Temperature,
            HardwareGroup.Memory,
            $"DIMM {i + 1}",
            $"DIMM {i + 1}",
            "°C",
            Warning: 70,
            Critical: 85))
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (!host.Connected)
        {
            return;
        }

        foreach (DimmReading dimm in host.Latest.Dimms)
        {
            into[SensorKey.Make(ProviderId, $"dimm{dimm.Slot}", SensorKind.Temperature, "spd")] = dimm.Celsius;
        }
    }

    public void Dispose()
    {
    }
}

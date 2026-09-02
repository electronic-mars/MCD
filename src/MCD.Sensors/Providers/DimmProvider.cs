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

    /// <summary>
    /// One reading for the memory as a whole: the warmest module.
    /// </summary>
    /// <remarks>
    /// Two sticks a degree apart are one fact about the machine, not two
    /// rows and two chips. The warmest is the one that matters, and the
    /// service still reads them all.
    /// </remarks>
    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        new(
            SensorKey.Make(ProviderId, "all", SensorKind.Temperature, "max"),
            SensorKind.Temperature,
            HardwareGroup.Memory,
            string.Empty,
            "Memory",
            "°C",
            Warning: 70,
            Critical: 85),
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (!host.Connected || host.Latest.Dimms.Length == 0)
        {
            return;
        }

        into[SensorKey.Make(ProviderId, "all", SensorKind.Temperature, "max")] =
            host.Latest.Dimms.Max(d => d.Celsius);
    }

    public void Dispose()
    {
    }
}

using Mcd.Sensors.Contracts;
using Mcd.Sensors.Host;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The processor's own temperature and power, as the sensor service reads
/// them from its registers.
/// </summary>
/// <remarks>
/// <para>
/// The registers are a kernel's business and the PawnIO driver that reads
/// them admits administrators only, so the reading is done by a small
/// service and arrives here down a pipe. This provider only names what it
/// gets: the package temperature - the figure a person means by "how hot is
/// the processor" - the hottest single core, and the package power.
/// </para>
/// </remarks>
public sealed class CpuMsrProvider(HostClient host, ILogger<CpuMsrProvider> log) : ISensorProvider
{
    private const string ProviderId = "cpu";
    private const string Hardware = "pkg0";

    private int _tjMax;
    private string _model = string.Empty;

    public string Id => ProviderId;

    public Tier Tier => Tier.Driver;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public bool IsAvailable()
    {
        if (!host.EnsureConnected() || !host.Latest.HasProcessor)
        {
            return false;
        }

        HostSnapshot snapshot = host.Latest;

        if (_tjMax != snapshot.TjMax)
        {
            _tjMax = snapshot.TjMax;
            _model = snapshot.Model;
            log.LogInformation("sensors.cpu {Model} through the sensor service; TjMax {TjMax}", _model, _tjMax);
        }

        return true;
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        new(
            SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "package"),
            SensorKind.Temperature,
            HardwareGroup.Cpu,
            _model,
            "Package",
            "°C",
            Warning: _tjMax - 15,
            Critical: _tjMax - 5),

        new(
            SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "core-max"),
            SensorKind.Temperature,
            HardwareGroup.Cpu,
            _model,
            "Hottest core",
            "°C",
            Warning: _tjMax - 15,
            Critical: _tjMax - 5,
            Prominent: false),

        new(
            SensorKey.Make(ProviderId, Hardware, SensorKind.Power, "package"),
            SensorKind.Power,
            HardwareGroup.Cpu,
            _model,
            "Package power",
            "W",
            Prominent: false),
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (!host.Connected)
        {
            return;
        }

        HostSnapshot snapshot = host.Latest;

        if (snapshot.Package is { } package)
        {
            into[SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "package")] = package;
        }

        if (snapshot.CoreMax is { } core)
        {
            into[SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "core-max")] = core;
        }

        if (snapshot.Watts is { } watts)
        {
            into[SensorKey.Make(ProviderId, Hardware, SensorKind.Power, "package")] = watts;
        }
    }

    public void Dispose()
    {
    }
}

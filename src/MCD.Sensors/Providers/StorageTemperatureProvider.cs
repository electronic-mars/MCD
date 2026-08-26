using System.Security.Cryptography;
using System.Text;
using Mcd.Interop.Storage;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// Temperatures for NVMe and SATA drives.
/// </summary>
/// <remarks>
/// <para>
/// Asked for every thirty seconds, not every second. The query is more expensive
/// than a performance counter, a drive's temperature moves slowly, and some
/// firmware logs each command it is sent.
/// </para>
/// <para>
/// A drive that times out twice is dropped for the rest of the session. USB
/// enclosures in particular can block on this, and a blocked drive would take
/// the whole sensor thread down with it - so each drive is read on its own task
/// with a deadline.
/// </para>
/// </remarks>
public sealed class StorageTemperatureProvider(ILogger<StorageTemperatureProvider> log) : ISensorProvider
{
    private const string ProviderId = "disk";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private const int TimeoutsBeforeGivingUp = 2;

    private readonly List<Drive> _drives = [];
    private bool _looked;

    public string Id => ProviderId;

    public Tier Tier => Tier.Platform;

    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public bool IsAvailable()
    {
        if (_looked)
        {
            return _drives.Count != 0;
        }

        _looked = true;

        foreach (int index in StorageTemperature.Enumerate())
        {
            if (StorageTemperature.Identify(index) is not { } identity)
            {
                continue;
            }

            // Not every drive reports a temperature. One that stays silent is
            // left out entirely rather than shown as a permanent dash.
            if (Read(index) is not { } first)
            {
                log.LogInformation("sensors.disk {Model} reports no temperature", identity.Model);
                continue;
            }

            _drives.Add(new Drive(index, Key(identity), identity.Model, first.Warning, first.Critical));

            log.LogInformation(
                "sensors.disk found {Model} at {Celsius} C (warning {Warning}, critical {Critical})",
                identity.Model, first.Celsius, first.Warning, first.Critical);
        }

        return _drives.Count != 0;
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        .. _drives.Select(d => new SensorDescriptor(
            SensorKey.Make(ProviderId, d.Key, SensorKind.Temperature, "composite"),
            SensorKind.Temperature,
            HardwareGroup.Storage,
            d.Model,
            d.Model,
            "°C",

            // The drive's own declared limits when it has them. A hard-coded
            // number would be wrong for half the drives on the market; the
            // firmware knows what it was designed for.
            Warning: d.Warning ?? 60,
            Critical: d.Critical ?? 70))
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        foreach (Drive drive in _drives.Where(d => !d.GivenUp).ToList())
        {
            if (Read(drive.Index) is { } reading)
            {
                drive.Timeouts = 0;
                into[SensorKey.Make(ProviderId, drive.Key, SensorKind.Temperature, "composite")] = reading.Celsius;
                continue;
            }

            if (++drive.Timeouts >= TimeoutsBeforeGivingUp)
            {
                drive.GivenUp = true;
                log.LogWarning("sensors.disk giving up on {Model}", drive.Model);
            }
        }
    }

    public void Dispose() => _drives.Clear();

    /// <summary>
    /// Reads one drive with a deadline.
    /// </summary>
    /// <remarks>
    /// The call is synchronous and, on some USB bridges, does not come back. The
    /// sensor thread serves every other source too, so it cannot be the thing
    /// that waits.
    /// </remarks>
    private static DriveTemperature? Read(int index)
    {
        Task<DriveTemperature?> work = Task.Run(() => StorageTemperature.Read(index));

        return work.Wait(Deadline) ? work.Result : null;
    }

    /// <summary>
    /// A key from the serial number, so it survives the drive being moved to a
    /// different port and the enumeration order changing.
    /// </summary>
    private static string Key(DriveIdentity identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.Serial));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    private sealed class Drive(int index, string key, string model, short? warning, short? critical)
    {
        public int Index { get; } = index;

        public string Key { get; } = key;

        public string Model { get; } = model;

        public short? Warning { get; } = warning;

        public short? Critical { get; } = critical;

        public int Timeouts { get; set; }

        public bool GivenUp { get; set; }
    }
}

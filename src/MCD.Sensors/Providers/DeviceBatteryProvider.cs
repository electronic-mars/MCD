using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mcd.Interop.Machine;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The charge of wireless mice, headsets and whatever else reports one.
/// </summary>
/// <remarks>
/// <para>
/// Two ways in. Bluetooth devices are read from what Windows already keeps -
/// a property lookup, nothing sent anywhere. A HyperX Cloud Flight S on its
/// USB dongle is asked, because Windows knows nothing about it; the question
/// is sent here and the answer is picked up by the dongle's own listener, so
/// nothing on the sensor thread ever waits for a headset.
/// </para>
/// <para>
/// Every half minute. A battery does not move faster than that, and the
/// scan of present devices is a few milliseconds.
/// </para>
/// <para>
/// The kind of device travels in the key - "mouse-3f1a09c2" - because the
/// gallery sorts by it and the chip draws by it, and a key is the one thing
/// both have.
/// </para>
/// </remarks>
public sealed class DeviceBatteryProvider(ILogger<DeviceBatteryProvider> log) : ISensorProvider
{
    private const string ProviderId = "dev";
    private const string Slot = "battery";

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);

    private List<DeviceCharge> _devices = [];
    private HyperXDongle? _dongle;
    private bool _toldDongle;

    public string Id => ProviderId;

    public Tier Tier => Tier.Platform;

    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    /// <summary>"mouse", "headset", "keyboard" or "other", from a key this provider made.</summary>
    public static string FamilyOf(SensorKey key)
    {
        string[] parts = key.Value.Split('/');
        return parts.Length > 1 && parts[0] == ProviderId && parts[1].IndexOf('-') is > 0 and var dash
            ? parts[1][..dash]
            : "other";
    }

    public bool IsAvailable()
    {
        Look();
        return _devices.Count > 0 || _dongle is not null;
    }

    public IReadOnlyList<SensorDescriptor> Discover()
    {
        var found = _devices.Select(d => Describe(Key(d.Family, d.Id.ToString("N")), d.Name)).ToList();

        if (_dongle is not null)
        {
            found.Add(Describe(Key("headset", "hyperx16ea"), _dongle.Name));
        }

        return found;
    }

    public void Poll(IDictionary<SensorKey, double> into)
    {
        Look();

        foreach (DeviceCharge device in _devices)
        {
            into[Key(device.Family, device.Id.ToString("N"))] = device.Percent;
        }

        if (_dongle is null)
        {
            return;
        }

        // What the headset said since the last round; then the next question,
        // whose answer the round after will read.
        if (_dongle.Percent(Fresh) is { } percent)
        {
            into[Key("headset", "hyperx16ea")] = percent;
        }

        _dongle.Ask();
    }

    public void Dispose() => _dongle?.Dispose();

    /// <summary>Reads the Bluetooth devices, and opens the dongle if one has been plugged in.</summary>
    private void Look()
    {
        long started = Stopwatch.GetTimestamp();
        List<DeviceCharge> devices = DeviceBatteries.Read();

        if (devices.Count != _devices.Count || devices.Any(d => !_devices.Any(e => e.Id == d.Id)))
        {
            log.LogInformation(
                "sensors.dev {Count} Bluetooth device(s) with a charge: {Names} ({Ms:F1} ms)",
                devices.Count,
                string.Join(", ", devices.Select(d => $"{d.Name} ({d.Family})")),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        _devices = devices;

        if (_dongle is { Gone: true })
        {
            log.LogInformation("sensors.dev the HyperX dongle went away");
            _dongle.Dispose();
            _dongle = null;
            _toldDongle = false;
        }

        if (_dongle is null && HyperXDongle.Open() is { } dongle)
        {
            _dongle = dongle;
            _dongle.Ask();
        }

        if (_dongle is not null && !_toldDongle)
        {
            _toldDongle = true;
            log.LogInformation("sensors.dev found the {Name} dongle", _dongle.Name);
        }
    }

    private static SensorKey Key(string family, string id) =>
        SensorKey.Make(ProviderId, $"{family}-{Short(id)}", SensorKind.Load, Slot);

    private static string Short(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0, 4).ToLowerInvariant();

    private static SensorDescriptor Describe(SensorKey key, string name) => new(
        key,
        SensorKind.Load,
        HardwareGroup.Peripheral,
        name,
        name,
        "%",
        Prominent: false);
}

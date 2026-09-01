using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// Temperatures by way of LibreHardwareMonitor's web server.
/// </summary>
/// <remarks>
/// <para>
/// The general answer to the parts of a machine that Windows will not talk
/// about: the processor, the motherboard, its fans, and graphics from AMD and
/// Intel, for which there is no library on the machine to ask. It is free and
/// open source, it publishes over plain HTTP on the local machine, and unlike
/// HWiNFO's shared memory it does not stop after twelve hours.
/// </para>
/// <para>
/// Off until someone turns it on, like every source that depends on software
/// this program neither ships nor installs. The address is a setting, because
/// the port is one in LibreHardwareMonitor too.
/// </para>
/// </remarks>
/// <param name="enabled">Asked afresh each time, so the switch works without a restart.</param>
/// <param name="endpoint">Asked afresh too, so does changing the address.</param>
public sealed class LhmProvider(
    ILogger<LhmProvider> log,
    Func<bool> enabled,
    Func<string> endpoint) : ISensorProvider
{
    public const string ProviderId = "lhm";

    /// <summary>
    /// Long enough that a machine which is not running it does not pay for the
    /// asking, short enough that a person watching a temperature sees it move.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = new() { Timeout = Wait };
    private ImmutableArray<SensorDescriptor> _found = [];
    private ImmutableDictionary<string, SensorKey> _keys = ImmutableDictionary<string, SensorKey>.Empty;

    public string Id => ProviderId;

    public Tier Tier => Tier.External;

    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    /// <summary>Why the source is not being used, in words worth showing someone.</summary>
    public string? Trouble { get; private set; }

    public bool IsAvailable()
    {
        if (!enabled())
        {
            Trouble = "switched off in settings";
            _found = [];
            return false;
        }

        if (Read() is not { Length: > 0 } sensors)
        {
            return false;
        }

        ImmutableDictionary<string, SensorKey>.Builder keys =
            ImmutableDictionary.CreateBuilder<string, SensorKey>();

        ImmutableArray<SensorDescriptor>.Builder found =
            ImmutableArray.CreateBuilder<SensorDescriptor>(sensors.Length);

        foreach (LhmSensor sensor in sensors)
        {
            SensorKey key = SensorKey.Make(
                ProviderId, Hardware(sensor.Hardware), sensor.Kind, Slot(sensor.Id));

            keys[sensor.Id] = key;

            bool warm = sensor.Kind == SensorKind.Temperature;

            found.Add(new SensorDescriptor(
                key,
                sensor.Kind,
                sensor.Group,
                sensor.Hardware,
                sensor.Label,
                sensor.Kind switch
                {
                    SensorKind.Fan => "RPM",
                    SensorKind.Power => "W",
                    _ => "\u00b0C",
                },

                // LibreHardwareMonitor knows each part's limits but does not
                // publish them here, so these are the ordinary ones: sustained
                // 85 is where a processor stops being comfortable and 95 is
                // where it slows itself down; graphics chips and drives run
                // cooler, and a board sensor past 75 means the airflow failed.
                Warning: warm ? sensor.Group switch
                {
                    HardwareGroup.Motherboard => 60,
                    HardwareGroup.Gpu => 80,
                    HardwareGroup.Storage => 65,
                    _ => 85,
                } : null,
                Critical: warm ? sensor.Group switch
                {
                    HardwareGroup.Motherboard => 75,
                    HardwareGroup.Gpu => 90,
                    HardwareGroup.Storage => 75,
                    _ => 95,
                } : null,
                Rank: 60,

                // A fan speed or a power figure is looked up, not glanced at;
                // neither belongs on the bar unasked.
                Prominent: warm && Headline(sensor)));
        }

        _keys = keys.ToImmutable();
        _found = found.ToImmutable();
        Trouble = null;

        log.LogInformation("sensors.lhm opened, {Count} readings at {Where}", _found.Length, endpoint());
        return true;
    }

    public IReadOnlyList<SensorDescriptor> Discover() => _found;

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (Read() is not { } sensors)
        {
            throw new InvalidOperationException(Trouble ?? "LibreHardwareMonitor stopped answering.");
        }

        foreach (LhmSensor sensor in sensors)
        {
            // Found by the identifier it gave, not by position: the tree changes
            // shape when hardware appears, and an index captured at startup would
            // then point at a different part.
            if (_keys.TryGetValue(sensor.Id, out SensorKey key))
            {
                into[key] = sensor.Value;
            }
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The document, or null with <see cref="Trouble"/> saying why not.</summary>
    private ImmutableArray<LhmSensor>? Read()
    {
        string where = endpoint();

        if (!Uri.TryCreate(where, UriKind.Absolute, out Uri? uri))
        {
            Trouble = $"\"{where}\" is not an address this can be asked at.";
            return null;
        }

        try
        {
            string json = _http.GetStringAsync(uri).GetAwaiter().GetResult();
            ImmutableArray<LhmSensor> sensors = LhmReport.Sensors(json);

            if (sensors.IsEmpty)
            {
                Trouble = "Something answered, but it was not LibreHardwareMonitor's sensor list.";
                return null;
            }

            return sensors;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Trouble = "LibreHardwareMonitor is not answering. Start it and switch on "
                    + "Options → Remote Web Server → Run.";
            return null;
        }
    }

    /// <summary>
    /// Which of a part's temperatures stands for the part as a whole.
    /// </summary>
    /// <remarks>
    /// A processor reports one per core and a graphics chip several across the
    /// die. All of them are offered to choose from; only these go on the bar
    /// without being asked for.
    /// </remarks>
    private static bool Headline(LhmSensor sensor) =>
        sensor.Group switch
        {
            HardwareGroup.Cpu => sensor.Label.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                 || sensor.Label.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                                 || sensor.Label.Contains("Average", StringComparison.OrdinalIgnoreCase),
            HardwareGroup.Gpu => sensor.Label.Contains("Core", StringComparison.OrdinalIgnoreCase),
            HardwareGroup.Storage => sensor.Label.Contains("Temperature", StringComparison.OrdinalIgnoreCase),
            _ => sensor.Label.Contains("Temperature", StringComparison.OrdinalIgnoreCase),
        };

    /// <summary>A short, stable key for a piece of hardware, from its name.</summary>
    private static string Hardware(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>The identifier, in a shape a sensor key can hold.</summary>
    private static string Slot(string sensorId) =>
        sensorId.Trim('/').Replace('/', '-');
}

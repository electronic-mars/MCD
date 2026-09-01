using Mcd.Interop.Pdh;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// Load and throughput, from the performance counters Windows keeps anyway.
/// </summary>
/// <remarks>
/// <para>
/// Needs no administrator rights, no driver and no third-party software, which
/// is why it is the floor the rest of the sensor layer stands on: whatever else
/// is or is not available, these numbers are always there.
/// </para>
/// <para>
/// Every counter is added by its English path. The localised names are what
/// <c>PdhAddCounter</c> wants, and on a Russian Windows the English path matches
/// nothing at all - the dock would show a dash for every user outside an English
/// locale, on a machine where nothing looks broken.
/// </para>
/// </remarks>
public sealed class PdhSensorProvider(ILogger<PdhSensorProvider> log) : ISensorProvider
{
    private const string ProviderId = "pdh";

    private static readonly Counter[] Counters =
    [
        // "% Processor Utility" is what Task Manager shows. "% Processor Time"
        // stops at 100 and hides turbo, so the two disagree visibly under load
        // and the dock would look wrong sitting next to Task Manager.
        new("cpu", "total", SensorKind.Load, HardwareGroup.Cpu, "CPU", "%",
            @"\Processor Information(_Total)\% Processor Utility"),

        // Named for what it is. The counter is the current speed as a share of
        // the processor's nominal one, so it sits above a hundred under turbo
        // and below it when the machine is throttling - and "CPU clock: 100 %"
        // reads like a processor pinned at full speed, which it is not.
        new("cpu", "clock", SensorKind.Clock, HardwareGroup.Cpu, "CPU speed, of nominal", "%",
            @"\Processor Information(_Total)\% Processor Performance"),

        new("gpu", "total", SensorKind.Load, HardwareGroup.Gpu, "GPU", "%",
            @"\GPU Engine(*)\Utilization Percentage", Aggregate.Sum),

        new("disk", "total", SensorKind.Load, HardwareGroup.Storage, "Disk", "%",
            @"\PhysicalDisk(_Total)\% Disk Time"),

        new("disk", "read", SensorKind.BytesPerSecond, HardwareGroup.Storage, "Disk read", "B/s",
            @"\PhysicalDisk(_Total)\Disk Read Bytes/sec"),

        new("disk", "write", SensorKind.BytesPerSecond, HardwareGroup.Storage, "Disk write", "B/s",
            @"\PhysicalDisk(_Total)\Disk Write Bytes/sec"),

        new("net", "down", SensorKind.BytesPerSecond, HardwareGroup.Network, "Down", "B/s",
            @"\Network Interface(*)\Bytes Received/sec", Aggregate.Sum),

        new("net", "up", SensorKind.BytesPerSecond, HardwareGroup.Network, "Up", "B/s",
            @"\Network Interface(*)\Bytes Sent/sec", Aggregate.Sum),
    ];

    private PdhQuery? _query;
    private readonly List<Counter> _live = [];

    public string Id => ProviderId;

    public Tier Tier => Tier.Platform;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public bool IsAvailable() => Open();

    public IReadOnlyList<SensorDescriptor> Discover() =>
        [.. _live.Select(c => c.Describe(ProviderId))];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (_query is null)
        {
            return;
        }

        IReadOnlyDictionary<string, double> values = _query.Collect();

        foreach (Counter counter in _live)
        {
            if (values.TryGetValue(counter.Name, out double value))
            {
                into[counter.Key(ProviderId)] = counter.Clamp(value);
            }
        }
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _live.Clear();
    }

    private bool Open()
    {
        if (_query is not null)
        {
            return true;
        }

        PdhQuery? query = PdhQuery.TryOpen();
        if (query is null)
        {
            log.LogWarning("sensors.pdh could not open a query");
            return false;
        }

        foreach (Counter counter in Counters)
        {
            // A counter set can be missing - a machine with no discrete GPU has
            // no GPU Engine object - and that is not a failure of the provider.
            if (query.TryAdd(counter.Name, counter.Path, counter.Mode == Aggregate.Sum))
            {
                _live.Add(counter);
            }
            else
            {
                log.LogInformation("sensors.pdh counter not present: {Path}", counter.Path);
            }
        }

        if (_live.Count == 0)
        {
            query.Dispose();
            return false;
        }

        _query = query;
        return true;
    }

    private enum Aggregate
    {
        Single,
        Sum,
    }

    private sealed record Counter(
        string Hardware,
        string Slot,
        SensorKind Kind,
        HardwareGroup Group,
        string Label,
        string Unit,
        string Path,
        Aggregate Mode = Aggregate.Single)
    {
        public string Name => $"{Hardware}.{Slot}";

        public SensorKey Key(string provider) => SensorKey.Make(provider, Hardware, Kind, Slot);

        public SensorDescriptor Describe(string provider) =>
            new(Key(provider), Kind, Group, Hardware, Label, Unit);

        /// <summary>
        /// Percentages are held to 0-100. The summed GPU counter runs over every
        /// engine of every adapter, so a busy machine legitimately produces more
        /// than a hundred, and a bar drawn from that would overflow its track.
        /// </summary>
        public double Clamp(double value) =>
            Unit == "%" ? Math.Clamp(value, 0, 100) : Math.Max(0, value);
    }
}

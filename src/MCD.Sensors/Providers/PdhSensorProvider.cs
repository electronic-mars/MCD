using Mcd.Interop.Machine;
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

    /// <summary>
    /// The performance cores and the efficient ones, on a processor that has
    /// both: each kind's load and speed, worked out from the same counters per
    /// logical processor that Task Manager's per-core graphs come from.
    /// </summary>
    private readonly List<Kind> _kinds = [];

    public string Id => ProviderId;

    public Tier Tier => Tier.Platform;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public bool IsAvailable() => Open();

    public IReadOnlyList<SensorDescriptor> Discover() =>
        [.. _live.Select(c => c.Describe(ProviderId)), .. _kinds.SelectMany(k => k.Describe())];

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

        foreach (Kind kind in _kinds)
        {
            kind.Read(values, into);
        }
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _live.Clear();
        _kinds.Clear();
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

        if (Cores.Here.Hybrid)
        {
            AddKind(query, "p", Cores.Here.Performance);
            AddKind(query, "e", Cores.Here.Efficient);

            // Half a split says nothing: "P 34 %" with no E beside it reads
            // as the whole processor.
            if (_kinds.Count < 2)
            {
                _kinds.Clear();
            }
        }

        if (_live.Count == 0)
        {
            query.Dispose();
            _kinds.Clear();
            return false;
        }

        _query = query;
        return true;
    }

    private void AddKind(PdhQuery query, string slot, IEnumerable<LogicalProcessor> processors)
    {
        var instances = new List<string>();

        foreach (LogicalProcessor processor in processors)
        {
            string at = processor.Instance;

            // Three per logical processor: how busy, how fast against its
            // own nominal speed, and that nominal speed - which differs
            // between the kinds (2.2 GHz against 1.5 on a 14900HX), so a
            // percentage averaged across both would mean nothing.
            if (query.TryAdd($"util {at}", $@"\Processor Information({at})\% Processor Utility")
                && query.TryAdd($"perf {at}", $@"\Processor Information({at})\% Processor Performance")
                && query.TryAdd($"freq {at}", $@"\Processor Information({at})\Processor Frequency"))
            {
                instances.Add(at);
            }
        }

        if (instances.Count > 0)
        {
            _kinds.Add(new Kind(slot, [.. instances]));
        }
        else
        {
            log.LogInformation("sensors.pdh no per-processor counters for the {Slot} cores", slot);
        }
    }

    /// <summary>One kind of core, and the logical processors it is made of.</summary>
    private sealed record Kind(string Slot, string[] Instances)
    {
        private SensorKey Load => SensorKey.Make(ProviderId, "cpu", SensorKind.Load, Slot);

        private SensorKey Speed => SensorKey.Make(ProviderId, "cpu", SensorKind.Clock, Slot);

        public IEnumerable<SensorDescriptor> Describe() =>
        [
            new(Load, SensorKind.Load, HardwareGroup.Cpu, "cpu", Slot == "p" ? "P-cores" : "E-cores", "%"),
            new(Speed, SensorKind.Clock, HardwareGroup.Cpu, "cpu", Slot == "p" ? "P-core speed" : "E-core speed", "GHz"),
        ];

        /// <summary>
        /// The kind's load is the average over its logical processors, as the
        /// processor's total is; its speed is the average of what each runs at
        /// - nominal speed times the performance counter - in gigahertz.
        /// </summary>
        public void Read(IReadOnlyDictionary<string, double> values, IDictionary<SensorKey, double> into)
        {
            double busy = 0, speed = 0;
            int counted = 0, timed = 0;

            foreach (string at in Instances)
            {
                if (values.TryGetValue($"util {at}", out double util) && double.IsFinite(util))
                {
                    busy += Math.Clamp(util, 0, 100);
                    counted++;
                }

                if (values.TryGetValue($"perf {at}", out double perf) && double.IsFinite(perf)
                    && values.TryGetValue($"freq {at}", out double nominal) && nominal > 0)
                {
                    speed += nominal * perf / 100;
                    timed++;
                }
            }

            if (counted > 0)
            {
                into[Load] = busy / counted;
            }

            if (timed > 0)
            {
                into[Speed] = speed / timed / 1000;
            }
        }
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
        /// Loads are held to 0-100. The summed GPU counter runs over every
        /// engine of every adapter, so a busy machine legitimately produces more
        /// than a hundred, and a bar drawn from that would overflow its track.
        /// The processor's speed is a share of its nominal one and goes above a
        /// hundred under turbo on purpose - capped, turbo never showed.
        /// </summary>
        public double Clamp(double value) =>
            Unit == "%" && Kind != SensorKind.Clock ? Math.Clamp(value, 0, 100) : Math.Max(0, value);
    }
}

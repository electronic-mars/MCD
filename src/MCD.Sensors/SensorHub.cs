using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors;

/// <summary>
/// Owns every sensor source, on one thread, and publishes what they read.
/// </summary>
/// <remarks>
/// <para>
/// One thread for all of them, deliberately. Several of these sources are not
/// thread-safe, one holds COM objects that belong to the thread that made them,
/// and the performance-counter handles behave best when a single thread does the
/// asking. Nothing here is ever called from the UI thread.
/// </para>
/// <para>
/// Results leave through <see cref="Current"/>: an immutable snapshot, replaced
/// wholesale. Readers take the reference and use it, with no lock and no chance
/// of seeing half an update.
/// </para>
/// </remarks>
public sealed class SensorHub : IDisposable
{
    /// <summary>How long a source keeps showing its last value before it goes blank.</summary>
    private const int StaleTicksBeforeMissing = 3;

    /// <summary>How long a source that has failed is left alone before being probed again.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    /// <summary>Samples kept per watched sensor: a minute at one a second.</summary>
    private const int TrendLength = 60;

    private readonly ILogger<SensorHub> _log;
    private readonly List<Source> _sources = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread? _thread;

    private readonly Dictionary<SensorKey, Trend> _trends = [];
    private readonly Lock _watching = new();
    private readonly ConcurrentQueue<string> _toReset = new();

    private volatile SensorSnapshot _current = SensorSnapshot.Empty;
    private ImmutableArray<SensorDescriptor> _catalog = [];

    /// <param name="pumpItself">
    /// False in tests, which drive <see cref="Pump"/> by hand. The state machine
    /// here - ageing, giving up, probing again - is worth testing without waiting
    /// on a real clock.
    /// </param>
    public SensorHub(
        ILogger<SensorHub> log,
        IEnumerable<ISensorProvider> providers,
        bool pumpItself = true)
    {
        _log = log;
        _sources.AddRange(providers.Select(p => new Source(p)));

        if (!pumpItself)
        {
            _thread = null;
            return;
        }

        _thread = new Thread(Run)
        {
            Name = "mcd-sensors",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The latest readings. Safe to read from any thread.</summary>
    public SensorSnapshot Current => _current;

    /// <summary>Every sensor known to be available right now.</summary>
    public ImmutableArray<SensorDescriptor> Catalog => _catalog;

    /// <summary>Raised on the hub's thread after each round of polling.</summary>
    public event EventHandler<SensorSnapshot>? Updated;

    /// <summary>
    /// Asks for a sensor's recent values to be kept, so something can graph them.
    /// </summary>
    /// <remarks>
    /// Opt-in rather than kept for everything. Most readings are never graphed,
    /// and building an unwanted minute of history for every one of them on every
    /// tick is work nobody asked for.
    /// </remarks>
    public void Watch(SensorKey key)
    {
        lock (_watching)
        {
            _trends.TryAdd(key, new Trend(TrendLength));
        }
    }

    /// <summary>
    /// Forgets what a source has said, so that it is asked again from the start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wanted when someone switches an external source on or off in settings.
    /// The hub would notice within half a minute on its own, and half a minute
    /// of a settings page doing nothing visible reads as a settings page that
    /// does not work.
    /// </para>
    /// <para>
    /// Queued rather than applied here: this is called from the interface, and
    /// the state it affects belongs to the hub's own thread.
    /// </para>
    /// </remarks>
    public void Reset(string providerId) => _toReset.Enqueue(providerId);

    /// <summary>One round of servicing every source. Called by the hub's thread.</summary>
    public void Pump(DateTimeOffset now)
    {
        while (_toReset.TryDequeue(out string? id))
        {
            Forget(id, now);
        }

        foreach (Source source in _sources)
        {
            Service(source, now);
        }

        Publish(now);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _stopping.Dispose();

        if (_thread is null)
        {
            foreach (Source source in _sources)
            {
                Safely(source, source.Provider.Dispose, "closing");
            }
        }
    }

    private void Run()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                Pump(DateTimeOffset.UtcNow);
                _stopping.Token.WaitHandle.WaitOne(Tick);
            }
        }
        finally
        {
            foreach (Source source in _sources)
            {
                Safely(source, () => source.Provider.Dispose(), "closing");
            }
        }
    }

    private void Service(Source source, DateTimeOffset now)
    {
        if (!source.Ready)
        {
            if (now < source.NextProbe)
            {
                return;
            }

            source.NextProbe = now + RetryAfter;

            if (!Safely(source, () => source.Provider.IsAvailable(), "probing", false))
            {
                return;
            }

            IReadOnlyList<SensorDescriptor> found =
                Safely(source, source.Provider.Discover, "listing sensors", [])!;

            source.Ready = true;
            source.Descriptors = [.. found];
            RebuildCatalog();

            _log.LogInformation(
                "sensors.provider id={Id} state=available sensors={Count}",
                source.Provider.Id, found.Count);
        }

        if (now < source.NextPoll)
        {
            return;
        }

        source.NextPoll = now + source.Provider.Interval;

        var buffer = new Dictionary<SensorKey, double>();

        if (!Safely(source, () => { source.Provider.Poll(buffer); return true; }, "reading", false))
        {
            Fail(source, now);
            return;
        }

        source.Failures = 0;

        foreach ((SensorKey key, double value) in buffer)
        {
            source.Values[key] = new SensorReading(value, Quality.Fresh);
        }

        // A key the provider knows about but did not return this time is ageing,
        // not gone: a drive that was busy for one tick should not blink out.
        foreach (SensorKey key in source.Values.Keys.Where(k => !buffer.ContainsKey(k)).ToList())
        {
            source.Values[key] = source.Values[key].Aging();
        }
    }

    private void Forget(string providerId, DateTimeOffset now)
    {
        foreach (Source source in _sources.Where(s => s.Provider.Id == providerId))
        {
            // Not disposed: a source is asked whether it is available again
            // straight after this, and a disposed one has no obligation to
            // answer sensibly.
            source.Ready = false;
            source.Failures = 0;
            source.Values.Clear();
            source.Descriptors = [];
            source.NextProbe = now;
        }

        RebuildCatalog();
    }

    private void Fail(Source source, DateTimeOffset now)
    {
        source.Failures++;

        foreach (SensorKey key in source.Values.Keys.ToList())
        {
            source.Values[key] = source.Values[key].Aging();
        }

        if (source.Failures < StaleTicksBeforeMissing)
        {
            return;
        }

        // Blank, never zero. "0 °C" reads as a measurement; a dash reads as
        // "nothing is answering", which is the truth.
        foreach (SensorKey key in source.Values.Keys.ToList())
        {
            source.Values[key] = SensorReading.Missing;
        }

        source.Ready = false;
        source.NextProbe = now + RetryAfter;
        RebuildCatalog();

        _log.LogWarning("sensors.provider id={Id} state=unavailable", source.Provider.Id);
    }

    private void Publish(DateTimeOffset now)
    {
        ImmutableDictionary<SensorKey, SensorReading>.Builder builder =
            ImmutableDictionary.CreateBuilder<SensorKey, SensorReading>();

        foreach (Source source in _sources)
        {
            foreach ((SensorKey key, SensorReading reading) in source.Values)
            {
                builder[key] = reading;
            }
        }

        ImmutableDictionary<SensorKey, SensorReading> readings = builder.ToImmutable();

        var snapshot = new SensorSnapshot(readings, RecordTrends(readings), now);
        _current = snapshot;
        Updated?.Invoke(this, snapshot);
    }

    private ImmutableDictionary<SensorKey, ImmutableArray<double>> RecordTrends(
        ImmutableDictionary<SensorKey, SensorReading> readings)
    {
        lock (_watching)
        {
            if (_trends.Count == 0)
            {
                return ImmutableDictionary<SensorKey, ImmutableArray<double>>.Empty;
            }

            ImmutableDictionary<SensorKey, ImmutableArray<double>>.Builder builder =
                ImmutableDictionary.CreateBuilder<SensorKey, ImmutableArray<double>>();

            foreach ((SensorKey key, Trend trend) in _trends)
            {
                if (readings.TryGetValue(key, out SensorReading reading) && reading.HasValue)
                {
                    trend.Add(reading.Value);
                }

                builder[key] = trend.Snapshot();
            }

            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// Rebuilds the list of what can be read, with duplicates demoted.
    /// </summary>
    /// <remarks>
    /// Two sources can describe the same physical part - the graphics driver
    /// and a monitoring program both report the same chip, to the tenth of a
    /// degree, under the same hardware name. Both stay in the list, because
    /// someone may have a reason to prefer either, but only the better one is
    /// shown without being asked for; otherwise every such temperature appears
    /// on the bar twice.
    /// </remarks>
    private void RebuildCatalog()
    {
        List<SensorDescriptor> all = [.. _sources.Where(s => s.Ready).SelectMany(s => s.Descriptors)];

        Dictionary<(HardwareGroup, SensorKind, string), SensorKey> best = all
            .Where(d => d.Prominent)
            .GroupBy(d => (d.Group, d.Kind, d.Hardware))
            .Where(g => g.Count() > 1)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.Rank).ThenBy(d => d.Key.Value).First().Key);

        _catalog =
        [
            .. all.Select(d =>
                d.Prominent
                && best.TryGetValue((d.Group, d.Kind, d.Hardware), out SensorKey keep)
                && !keep.Equals(d.Key)
                    ? d with { Prominent = false }
                    : d)
        ];
    }

    private T? Safely<T>(Source source, Func<T> work, string what, T? whenItFails = default)
    {
        try
        {
            return work();
        }
        catch (Exception e)
        {
            // One misbehaving source must not take the others with it, and must
            // not take the dock with it either.
            _log.LogError(e, "sensors.provider id={Id} failed while {What}", source.Provider.Id, what);
            return whenItFails;
        }
    }

    private void Safely(Source source, Action work, string what) =>
        Safely(source, () => { work(); return true; }, what, false);

    /// <summary>A fixed-length window of recent values, oldest first once full.</summary>
    private sealed class Trend(int length)
    {
        private readonly double[] _values = new double[length];
        private int _next;
        private int _count;

        public void Add(double value)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length);
        }

        public ImmutableArray<double> Snapshot()
        {
            if (_count == 0)
            {
                return [];
            }

            var ordered = new double[_count];
            int start = _count < _values.Length ? 0 : _next;

            for (int i = 0; i < _count; i++)
            {
                ordered[i] = _values[(start + i) % _values.Length];
            }

            return [.. ordered];
        }
    }

    private sealed class Source(ISensorProvider provider)
    {
        public ISensorProvider Provider { get; } = provider;

        public Dictionary<SensorKey, SensorReading> Values { get; } = [];

        public ImmutableArray<SensorDescriptor> Descriptors { get; set; } = [];

        public bool Ready { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset NextProbe { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset NextPoll { get; set; } = DateTimeOffset.MinValue;
    }
}

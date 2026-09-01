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

    /// <summary>How often a working source is asked whether its list has changed.</summary>
    private static readonly TimeSpan Relist = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>How long the thread sleeps while nobody is looking.</summary>
    /// <remarks>
    /// Not stopped altogether: the thread owns the providers, and one of them
    /// holds a shared-memory handle and another an HTTP client. It wakes rarely
    /// enough to cost nothing and often enough to notice the session coming
    /// back even if the message about it is missed.
    /// </remarks>
    private static readonly TimeSpan Asleep = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether anybody is looking at what this produces.
    /// </summary>
    /// <remarks>
    /// Stopping the bars from drawing behind a lock screen saved the smaller
    /// half: reading the sensors is the expensive part - a WMI call into the
    /// firmware, a summed performance counter with an instance per process, an
    /// HTTP request to another program - and it went on all night in a bag.
    /// </remarks>
    public bool Watched
    {
        get => _watched;
        set => _watched = value;
    }

    private volatile bool _watched = true;

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
        RebuildRoll();

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

    /// <summary>
    /// Every source the program knows how to ask, and whether it is answering.
    /// </summary>
    /// <remarks>
    /// A count of sources answers nothing. The question people bring to that
    /// page is about one source by name - whether the program has found the
    /// monitoring program they installed for it - and a number cannot say.
    /// The ones that are not answering are the point, so they stay in the list.
    /// </remarks>
    public ImmutableArray<SourceRoll> Sources => _roll;

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
                if (_watched)
                {
                    Pump(DateTimeOffset.UtcNow);
                }

                _stopping.Token.WaitHandle.WaitOne(_watched ? Tick : Asleep);
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

            // The list was taken this very moment; asking again further down
            // this same pass would be the one wasted Discover per opening.
            source.NextRelist = now + Relist;
            RebuildCatalog();

            _log.LogInformation(
                "sensors.provider id={Id} state=available sensors={Count}",
                source.Provider.Id, found.Count);
        }

        if (now >= source.NextRelist)
        {
            source.NextRelist = now + Relist;

            IReadOnlyList<SensorDescriptor>? fresh =
                Safely(source, source.Provider.Discover, "listing sensors");

            if (fresh is not null && !source.Descriptors.SequenceEqual(fresh))
            {
                source.Descriptors = [.. fresh];
                RebuildCatalog();

                _log.LogInformation(
                    "sensors.provider id={Id} state=relisted sensors={Count}",
                    source.Provider.Id, fresh.Count);
            }
        }

        if (now < source.NextPoll)
        {
            return;
        }

        source.NextPoll = now + source.Provider.Interval;

        // The source's own buffer, cleared rather than remade: most sources
        // poll every second, and a fresh dictionary per source per tick was
        // steady garbage bought for nothing.
        Dictionary<SensorKey, double> buffer = source.Buffer;
        buffer.Clear();

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
        // Overwriting a value in place is safe to do mid-walk; only growing or
        // shrinking the dictionary is not.
        foreach ((SensorKey key, SensorReading value) in source.Values)
        {
            if (!buffer.ContainsKey(key))
            {
                source.Values[key] = value.Aging();
            }
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
        RebuildRoll();

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

    private void RebuildRoll() =>
        _roll =
        [
            .. _sources.Select(s => new SourceRoll(
                s.Provider.Id, s.Provider.Tier, s.Ready, s.Descriptors.Length))
        ];

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

    private ImmutableArray<SourceRoll> _roll = [];

    private sealed class Source(ISensorProvider provider)
    {
        public ISensorProvider Provider { get; } = provider;

        public Dictionary<SensorKey, SensorReading> Values { get; } = [];

        /// <summary>Reused across polls; see the note where it is filled.</summary>
        public Dictionary<SensorKey, double> Buffer { get; } = [];

        public ImmutableArray<SensorDescriptor> Descriptors { get; set; } = [];

        public bool Ready { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset NextProbe { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset NextPoll { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset NextRelist { get; set; } = DateTimeOffset.MinValue;
    }
}

/// <summary>One source of readings, and what it is doing.</summary>
/// <param name="Id">The short name the source calls itself, and the first part of its keys.</param>
/// <param name="Tier">What it needs before it can work at all.</param>
/// <param name="Answering">Whether it is here and being read right now.</param>
/// <param name="Readings">How many things it offers to measure.</param>
public readonly record struct SourceRoll(string Id, Tier Tier, bool Answering, int Readings);

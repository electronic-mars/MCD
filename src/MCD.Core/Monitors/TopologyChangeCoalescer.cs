using Microsoft.Extensions.Logging;

namespace Mcd.Core.Monitors;

/// <param name="Snapshot">The topology as finally read.</param>
/// <param name="Authoritative">
/// The same snapshot cast down, or null when the topology never settled. Null
/// means "reposition what exists, decide nothing".
/// </param>
public sealed record TopologySettled(
    MonitorSnapshot Snapshot,
    AuthoritativeSnapshot? Authoritative,
    TopologyTrigger Trigger);

/// <summary>
/// Turns a burst of system notifications into one decision, once the topology
/// has stopped moving.
/// </summary>
/// <remarks>
/// Switching projection mode with Win+P produces several <c>WM_DISPLAYCHANGE</c>
/// messages in a row, and the topology is inconsistent between them. PowerToys
/// acts on each one and writes settings each time, so a single bad intermediate
/// reading is enough to leave a monitor permanently configured as disabled and
/// empty (microsoft/PowerToys#49604). Here nothing is decided until the noise
/// stops and the reading passes its consistency checks.
/// </remarks>
public sealed class TopologyChangeCoalescer : IDisposable
{
    /// <summary>How long the system must stay quiet before we look at all.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// An upper bound on waiting for quiet. Without it, a machine that keeps
    /// emitting display notifications would keep the docks frozen forever.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    /// <summary>Backoff for re-reading a topology that has not settled yet.</summary>
    private static readonly TimeSpan[] Retries =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1600),
        TimeSpan.FromMilliseconds(3200),
    ];

    private readonly ILogger<TopologyChangeCoalescer> _log;
    private readonly MonitorService _monitors;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly ITimer _timer;

    private TopologyTrigger _trigger = TopologyTrigger.Startup;
    private DateTimeOffset _firstPoke;
    private int _retry = -1;
    private bool _pending;
    private bool _disposed;

    public TopologyChangeCoalescer(
        ILogger<TopologyChangeCoalescer> log,
        MonitorService monitors,
        TimeProvider? time = null)
    {
        _log = log;
        _monitors = monitors;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised on a timer thread once the topology has settled, or given up settling.</summary>
    public event EventHandler<TopologySettled>? Settled;

    /// <summary>Something happened that might have changed the monitors.</summary>
    public void Poke(TopologyTrigger trigger)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _trigger = trigger;

            if (!_pending)
            {
                _pending = true;
                _firstPoke = _time.GetUtcNow();
                _retry = -1;
            }

            TimeSpan waited = _time.GetUtcNow() - _firstPoke;
            TimeSpan delay = waited + Quiet > MaxWait ? TimeSpan.Zero : Quiet;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the topology right now, bypassing the debounce. For startup.</summary>
    public void ReadNow(TopologyTrigger trigger)
    {
        lock (_gate)
        {
            _pending = true;
            _trigger = trigger;
            _firstPoke = _time.GetUtcNow();
            _retry = -1;
        }

        Tick();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void Tick()
    {
        TopologyTrigger trigger;

        lock (_gate)
        {
            if (_disposed || !_pending)
            {
                return;
            }

            trigger = _trigger;
        }

        MonitorSnapshot snapshot = _monitors.Enumerate();

        if (snapshot is AuthoritativeSnapshot authoritative)
        {
            lock (_gate)
            {
                _pending = false;
                _retry = -1;
            }

            Settled?.Invoke(this, new TopologySettled(snapshot, authoritative, trigger));
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _retry++;

            if (_retry < Retries.Length)
            {
                _timer.Change(Retries[_retry], Timeout.InfiniteTimeSpan);
                return;
            }

            _pending = false;
            _retry = -1;
        }

        // Out of patience. The docks are repositioned from what we can see, but
        // nothing is decided and nothing is written: a monitor that could not be
        // identified must not be allowed to rewrite the registry.
        _log.LogWarning(
            "monitors.snapshot provisional-timeout trigger={Trigger} reason={Reason}",
            trigger,
            (snapshot as ProvisionalSnapshot)?.Reason);

        Settled?.Invoke(this, new TopologySettled(snapshot, null, trigger));
    }
}

using System.Collections.Immutable;
using Mcd.Core.Monitors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Mcd.Tests.Monitors;

/// <summary>
/// Win+P produces a burst of display notifications with an inconsistent topology
/// between them. These tests pin down that the burst costs one decision, and that
/// an unsettled reading produces none at all.
/// </summary>
public sealed class TopologyChangeCoalescerTests
{
    [Fact]
    public void ABurstOfNotificationsProducesOneDecision()
    {
        var (coalescer, monitors, time, settled) = Build();
        using (coalescer)
        {
            monitors.Next = Authoritative();

            for (int i = 0; i < 8; i++)
            {
                coalescer.Poke(TopologyTrigger.DisplayChange);
                time.Advance(TimeSpan.FromMilliseconds(50));
            }

            settled.Count.ShouldBe(0, "nothing may be decided while the notifications are still arriving");

            time.Advance(TimeSpan.FromMilliseconds(400));

            settled.Count.ShouldBe(1);
            settled[0].Authoritative.ShouldNotBeNull();
        }
    }

    [Fact]
    public void ANeverEndingBurstStillGetsDecidedAtTheCeiling()
    {
        var (coalescer, monitors, time, settled) = Build();
        using (coalescer)
        {
            monitors.Next = Authoritative();

            // A machine that keeps emitting notifications must not keep the docks
            // frozen; the quiet window is bounded.
            for (int i = 0; i < 40; i++)
            {
                coalescer.Poke(TopologyTrigger.DisplayChange);
                time.Advance(TimeSpan.FromMilliseconds(200));
            }

            settled.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void AnUnsettledTopologyIsRetriedAndThenGivenUpOnWithoutDeciding()
    {
        var (coalescer, monitors, time, settled) = Build();
        using (coalescer)
        {
            monitors.Next = Provisional("no active display path for \\\\.\\DISPLAY2");

            coalescer.Poke(TopologyTrigger.DisplayChange);
            Advance(time, TimeSpan.FromSeconds(12));

            monitors.Reads.ShouldBeGreaterThan(3, "an unsettled reading must be retried, not accepted");

            // It gives up eventually, but with nothing to decide from: the docks
            // get repositioned and the registry is left alone.
            settled.Count.ShouldBe(1);
            settled[0].Authoritative.ShouldBeNull();
        }
    }

    [Fact]
    public void ATopologyThatSettlesDuringTheRetriesIsAccepted()
    {
        var (coalescer, monitors, time, settled) = Build();
        using (coalescer)
        {
            monitors.Next = Provisional("mid-change");
            coalescer.Poke(TopologyTrigger.DisplayChange);

            Advance(time, TimeSpan.FromMilliseconds(500));
            monitors.Next = Authoritative();
            Advance(time, TimeSpan.FromSeconds(2));

            settled.Count.ShouldBe(1);
            settled[0].Authoritative.ShouldNotBeNull();
            settled[0].Authoritative!.Count.ShouldBe(2);
        }
    }

    /// <summary>
    /// Moves the clock in small steps. One big jump would skip timers that the
    /// callback re-arms while the jump is being processed, which is exactly the
    /// retry ladder under test.
    /// </summary>
    private static void Advance(FakeTimeProvider time, TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(25);
        for (TimeSpan spent = TimeSpan.Zero; spent < total; spent += step)
        {
            time.Advance(step);
        }
    }

    private static (TopologyChangeCoalescer, FakeMonitors, FakeTimeProvider, List<TopologySettled>) Build()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var monitors = new FakeMonitors();
        var coalescer = new TopologyChangeCoalescer(
            NullLogger<TopologyChangeCoalescer>.Instance, monitors, time);

        var settled = new List<TopologySettled>();
        coalescer.Settled += (_, e) => settled.Add(e);

        return (coalescer, monitors, time, settled);
    }

    private static AuthoritativeSnapshot Authoritative() =>
        Topology.Snapshot(Topology.Laptop(), Topology.Left());

    private static ProvisionalSnapshot Provisional(string reason) =>
        new(ImmutableArray<MonitorInfo>.Empty, DateTimeOffset.UnixEpoch, reason);

    private sealed class FakeMonitors()
        : MonitorService(NullLogger<MonitorService>.Instance)
    {
        public MonitorSnapshot Next { get; set; } =
            new ProvisionalSnapshot(ImmutableArray<MonitorInfo>.Empty, DateTimeOffset.UnixEpoch, "unset");

        public int Reads { get; private set; }

        public override MonitorSnapshot Enumerate()
        {
            Reads++;
            return Next;
        }
    }
}

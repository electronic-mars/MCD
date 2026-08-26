using System.Collections.Immutable;
using Mcd.Core.Monitors;
using Mcd.Core.Settings;
using Mcd.Interop.AppBar;
using Shouldly;

namespace Mcd.Tests.Monitors;

/// <summary>
/// The regression suite for microsoft/PowerToys#49604 and its relatives.
/// </summary>
/// <remarks>
/// Their bug is that a monitor which briefly fails to identify is written out as
/// a disabled entry with no widgets, and that state is then permanent. Several
/// tests here assert the negative of that directly, and
/// <see cref="NoInputEverProducesAnEmptyDock"/> asserts it over every topology
/// this suite knows how to build.
/// </remarks>
public sealed class MonitorReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static ImmutableArray<MonitorConfig> Empty => [];

    [Fact]
    public void FirstRunGivesEveryMonitorAWorkingDock()
    {
        AuthoritativeSnapshot snapshot = Topology.Snapshot(
            Topology.Laptop(), Topology.Left(), Topology.Right());

        ReconcileResult result = MonitorReconciler.Reconcile(snapshot, Empty, Now);

        result.Plans.Length.ShouldBe(3);
        result.Registry.Length.ShouldBe(3);
        result.Plans.ShouldAllBe(p => p.Config.Enabled);
        result.Registry.ShouldAllBe(c => !c.Bands.IsEmpty);
    }

    [Fact]
    public void ASecondaryMonitorInheritsThePrimaryLayoutWithItsOwnWidgetInstances()
    {
        MonitorConfig primary = Saved(Topology.Laptop()) with
        {
            Edge = AppBarEdge.Top,
            Density = DockDensity.Compact,
            Bands = new DockBands { End = [WidgetConfig.New("mcd.load"), WidgetConfig.New("mcd.temperature")] },
        };

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop(), Topology.Left()),
            [primary],
            Now);

        MonitorConfig added = result.Plans.Single(p => p.Monitor.Identity.GdiName == @"\\.\DISPLAY2").Config;

        added.Edge.ShouldBe(AppBarEdge.Top);
        added.Density.ShouldBe(DockDensity.Compact);
        added.Bands.End.Select(w => w.TypeId).ShouldBe(["mcd.load", "mcd.temperature"]);

        // Same widgets, different instances: two docks must not share per-instance state.
        added.Bands.End.Select(w => w.InstanceId)
            .ShouldNotBe(primary.Bands.End.Select(w => w.InstanceId));
    }

    [Fact]
    public void AKnownMonitorKeepsItsSettings()
    {
        MonitorConfig saved = Saved(Topology.Left()) with { Edge = AppBarEdge.Left, Enabled = false };

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop(true), Topology.Left()),
            [Saved(Topology.Laptop()), saved],
            Now);

        DockPlan plan = result.Plans.Single(p => p.Monitor.Identity.DevicePath == Topology.LeftPath);

        plan.Config.Edge.ShouldBe(AppBarEdge.Left);

        // Disabled stays disabled: that flag only ever comes from a person.
        plan.Config.Enabled.ShouldBeFalse();
        plan.ShouldShow.ShouldBeFalse();
    }

    [Fact]
    public void AnUnpluggedMonitorIsRememberedAndItsLastSeenIsNotTouched()
    {
        DateTimeOffset seenBefore = Now.AddDays(-200);
        MonitorConfig away = Saved(Topology.Right()) with { LastSeenUtc = seenBefore };

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop()),
            [Saved(Topology.Laptop()), away],
            Now);

        MonitorConfig kept = result.Registry.Single(c => c.DevicePath == Topology.RightPath);

        // PowerToys prunes at 180 days. A laptop away from its dock for a summer
        // would come back to a blank second screen.
        kept.LastSeenUtc.ShouldBe(seenBefore);
        kept.Bands.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void TwoMonitorsWithTheSameEdidAreNeverMatchedByIt()
    {
        // Both external screens report RTK:2555. If EDID were trusted here, the
        // left and right docks could swap every time a device path changed.
        MonitorConfig left = Saved(Topology.Left()) with { Edge = AppBarEdge.Left };
        MonitorConfig right = Saved(Topology.Right()) with { Edge = AppBarEdge.Right };

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop(), Topology.Left(), Topology.Right()),
            [Saved(Topology.Laptop()), left, right],
            Now);

        result.Plans.Single(p => p.Monitor.Identity.DevicePath == Topology.LeftPath)
            .Config.Edge.ShouldBe(AppBarEdge.Left);
        result.Plans.Single(p => p.Monitor.Identity.DevicePath == Topology.RightPath)
            .Config.Edge.ShouldBe(AppBarEdge.Right);
    }

    [Fact]
    public void AMonitorWhoseDevicePathChangedIsReAssociatedRatherThanTreatedAsNew()
    {
        // Win+P hands the same screen a new UID. One monitor and one entry are
        // left over, so they are the same thing.
        MonitorConfig saved = Saved(Topology.Left()) with
        {
            Edge = AppBarEdge.Left,
            LastSeenUtc = Now.AddMinutes(-2),
        };

        MonitorInfo renamed = Topology.Make(
            @"\\?\DISPLAY#RTK2555#5&11131be0&4&UID9999#{e6f07b5f}",
            "RTK 2555", "RTK:2555", @"\\.\DISPLAY2", 2560, -261, 4480, 819, 96, primary: false);

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop(), renamed),
            [Saved(Topology.Laptop()), saved],
            Now);

        result.Registry.Length.ShouldBe(2);

        MonitorConfig moved = result.Registry.Single(c => c.StableId == renamed.StableId.Value);
        moved.Edge.ShouldBe(AppBarEdge.Left);
        moved.PreviousStableIds.ShouldContain(saved.StableId);
    }

    [Fact]
    public void AStaleLeftoverEntryIsNotReAssociated()
    {
        // A screen last seen a year ago is not evidence about the monitor plugged
        // in today, even when it is the only candidate.
        MonitorConfig ancient = Saved(Topology.Left()) with
        {
            Edge = AppBarEdge.Left,
            LastSeenUtc = Now.AddDays(-365),
        };

        MonitorInfo stranger = Topology.Make(
            @"\\?\DISPLAY#DEL4321#5&aaaa&0&UID1#{e6f07b5f}",
            "DELL U2723QE", "DEL:4321", @"\\.\DISPLAY2", 2560, 0, 6400, 2160, 96, primary: false);

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop(), stranger),
            [Saved(Topology.Laptop()), ancient],
            Now);

        result.Registry.Length.ShouldBe(3);
        result.Registry.ShouldContain(c => c.StableId == ancient.StableId);
        result.Plans.Single(p => p.Monitor.Identity.DevicePath == stranger.Identity.DevicePath)
            .Config.Edge.ShouldBe(Saved(Topology.Laptop()).Edge);
    }

    [Fact]
    public void AnUnchangedTopologyAsksForNoSettingsWrite()
    {
        AuthoritativeSnapshot same = Topology.Snapshot(Topology.Laptop(), Topology.Left());

        ImmutableArray<MonitorConfig> saved = MonitorReconciler.Reconcile(same, Empty, Now).Registry;

        // Later, but not by much - the clock moving on is not a change.
        // Registering an AppBar makes Windows broadcast a work-area change, which
        // brings us straight back here; counting LastSeenUtc as a difference made
        // the program write its settings four times in the first second and a
        // half of an ordinary start.
        ReconcileResult again = MonitorReconciler.Reconcile(same, saved, Now.AddMinutes(3));

        again.RegistryChanged.ShouldBeFalse();
    }

    [Fact]
    public void ALongRunningSessionStillRefreshesLastSeenEventually()
    {
        AuthoritativeSnapshot same = Topology.Snapshot(Topology.Laptop());
        ImmutableArray<MonitorConfig> saved = MonitorReconciler.Reconcile(same, Empty, Now).Registry;

        // LastSeen drives the "forget this monitor" button, so it has to reach
        // the disk sometimes - just not on every reading.
        ReconcileResult later = MonitorReconciler.Reconcile(same, saved, Now.AddHours(7));

        later.RegistryChanged.ShouldBeTrue();
        later.Registry.Single().LastSeenUtc.ShouldBe(Now.AddHours(7));
    }

    [Fact]
    public void AnEntryWithNoWidgetsIsRepairedRatherThanShown()
    {
        // Config files get edited by hand, and older versions of this program may
        // yet write something we would now reject.
        MonitorConfig broken = Saved(Topology.Laptop()) with { Bands = new DockBands() };

        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(Topology.Laptop()), [broken], Now);

        result.Plans.Single().Config.Bands.IsEmpty.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(EveryTopology))]
    public void NoInputEverProducesAnEmptyDock(MonitorInfo[] monitors, MonitorConfig[] saved)
    {
        ReconcileResult result = MonitorReconciler.Reconcile(
            Topology.Snapshot(monitors), [.. saved], Now);

        result.Plans.Length.ShouldBe(monitors.Length);

        foreach (DockPlan plan in result.Plans)
        {
            plan.Config.Bands.IsEmpty.ShouldBeFalse(
                $"{plan.Monitor.Identity.FriendlyName} came out with an empty dock");
        }

        // Every saved entry survives, under its own id or under a new one it was
        // re-associated with.
        foreach (MonitorConfig before in saved)
        {
            bool survived = result.Registry.Any(c =>
                c.StableId == before.StableId || c.PreviousStableIds.Contains(before.StableId));

            survived.ShouldBeTrue($"the entry for {before.FriendlyName} was dropped");
        }
    }

    public static TheoryData<MonitorInfo[], MonitorConfig[]> EveryTopology()
    {
        MonitorInfo laptop = Topology.Laptop();
        MonitorInfo left = Topology.Left();
        MonitorInfo right = Topology.Right();

        MonitorConfig[] all = [Saved(laptop), Saved(left), Saved(right)];

        return new TheoryData<MonitorInfo[], MonitorConfig[]>
        {
            { [laptop], [] },
            { [laptop, left], [] },
            { [laptop, left, right], [] },
            { [laptop], all },
            { [laptop, left], all },
            { [laptop, left, right], all },
            { [Topology.Left(primary: true)], all },
            { [laptop, right], [Saved(laptop)] },
            { [laptop, left, right], [Saved(laptop) with { Bands = new DockBands() }] },
        };
    }

    private static MonitorConfig Saved(MonitorInfo monitor) => new()
    {
        StableId = monitor.StableId.Value,
        DevicePath = monitor.Identity.DevicePath,
        EdidKey = monitor.Identity.EdidKey,
        FriendlyName = monitor.Identity.FriendlyName,
        ShapeHint = monitor.ShapeKey,
        LastSeenUtc = Now.AddMinutes(-1),
        Bands = DockBands.Default,
    };
}

using System.Collections.Immutable;
using Mcd.Core.Settings;
using Shouldly;

namespace Mcd.Tests.Settings;

/// <summary>
/// The arithmetic of a bar made of slots: what fits where, and what does not.
/// </summary>
/// <remarks>
/// This is the whole promise the interface makes - a widget goes wherever there
/// are enough free slots and nowhere else - so it is tested here rather than
/// left to be discovered by dragging things about.
/// </remarks>
public sealed class DockGridTests
{
    private static WidgetConfig Entry(string id, int cell = -1) => new()
    {
        InstanceId = id,
        TypeId = "mcd.gauge",
        Cell = cell,
    };

    private static WidgetConfig Sized(string id, int cell, int span) =>
        Entry(id, cell) with { Span = span };

    [Fact]
    public void UnplacedWidgetsTakeTheFirstFreeSlots()
    {
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a"), 2), (Entry("b"), 1), (Entry("c"), 3)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe([("a", 0), ("b", 2), ("c", 3)]);
    }

    [Fact]
    public void TheAnchorLeavesRoomOnBothSidesOfACentredRow()
    {
        // Two widgets holding six of twenty slots, gathered in the middle:
        // seven slots free before them and seven after. Both halves are real
        // slots, which is the whole point - the empty part of a centred bar
        // has to be somewhere a widget can be dropped.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 2), 4)], capacity: 20);

        int offset = DockGrid.Offset(placed, 20, DockAnchor.Centre);
        offset.ShouldBe(7);

        List<Placement> drawn = [.. placed.Select(p => p with { Cell = p.Cell + offset })];

        DockGrid.Free(drawn, 20).Count.ShouldBe(14);
        DockGrid.Free(drawn, 20).ShouldContain(0);
        DockGrid.Free(drawn, 20).ShouldContain(19);
        DockGrid.Fits(drawn, 20, cell: 0, span: 2).ShouldBeTrue();
    }

    [Fact]
    public void TheAnchorMovesTheDrawingAndNotTheArrangement()
    {
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 5), 1)], capacity: 20);

        // Eighteen of twenty slots are spare: at the start nothing moves, at
        // the end everything is drawn eighteen along, in the middle nine.
        DockGrid.Offset(placed, 20, DockAnchor.Start).ShouldBe(0);
        DockGrid.Offset(placed, 20, DockAnchor.End).ShouldBe(14);
        DockGrid.Offset(placed, 20, DockAnchor.Centre).ShouldBe(7);

        // And the arrangement itself is untouched, whatever the anchor says.
        placed.Single(p => p.InstanceId == "a").Cell.ShouldBe(0);
        placed.Single(p => p.InstanceId == "b").Cell.ShouldBe(5);
    }

    [Fact]
    public void AFullBarIsDrawnWhereItStands()
    {
        List<Placement> placed = DockGrid.Settle([(Entry("a", 0), 4)], capacity: 4);

        DockGrid.Offset(placed, 4, DockAnchor.End).ShouldBe(0);
        DockGrid.Offset(placed, 4, DockAnchor.Centre).ShouldBe(0);
    }

    [Fact]
    public void AWidgetAboutNothingTakesNoSlotAndTheBarClosesOver()
    {
        // A battery on a machine running from the mains, or Wi-Fi with the
        // cable in: no span at all. What follows it moves up rather than
        // leaving a hole nothing can be dropped into.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a"), 2), (Entry("battery"), 0), (Entry("c"), 1)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe([("a", 0), ("c", 2)]);
    }

    [Fact]
    public void AWidgetAboutNothingKeepsItsPlaceInTheOrder()
    {
        // It is not shown, but it has not been given up either: the moment it
        // is about something again it goes back where it was, not to the end.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("battery", 2), 0), (Entry("c", 3), 1)], capacity: 20);

        placed.ShouldNotContain(p => p.InstanceId == "battery");
        placed.Single(p => p.InstanceId == "c").Cell.ShouldBe(3);

        // The cable comes out.
        List<Placement> back = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("battery", 2), 1), (Entry("c", 3), 1)], capacity: 20);

        back.Single(p => p.InstanceId == "battery").Cell.ShouldBe(2);
        back.Single(p => p.InstanceId == "c").Cell.ShouldBe(3);
    }

    [Fact]
    public void AWidgetKeepsTheSlotItWasPutOn()
    {
        // A bar arranged by hand comes back exactly as it was left, gaps and
        // all - that is the difference between placing a thing and listing it.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 10), 2), (Entry("b", 4), 1)], capacity: 20);

        placed.Single(p => p.InstanceId == "a").Cell.ShouldBe(10);
        placed.Single(p => p.InstanceId == "b").Cell.ShouldBe(4);
    }

    [Fact]
    public void AWidgetWhoseSlotIsTakenShufflesAlong()
    {
        // Two claims on slot 5 - a screen narrower than the one the arrangement
        // was made on, or a reading that grew when its figure did. The second
        // moves up against the first rather than being scattered elsewhere:
        // left-to-right order is the one thing a bar must never lose.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 5), 2), (Entry("b", 5), 2)], capacity: 20);

        placed.Single(p => p.InstanceId == "a").Cell.ShouldBe(5);
        placed.Single(p => p.InstanceId == "b").Cell.ShouldBe(7);
    }

    [Fact]
    public void OrderFollowsTheSlotsWhateverOrderTheySettleIn()
    {
        // The settings list is not the bar's order - where a widget is on the
        // bar is its slot, and a bar rearranged by hand says so out of order.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 9), 1), (Entry("b", 2), 1), (Entry("c", 5), 1)], capacity: 20);

        placed.Select(p => p.InstanceId).ShouldBe(["b", "c", "a"]);
        placed.Select(p => p.Cell).ShouldBe([2, 5, 9]);
    }

    [Fact]
    public void SomethingNewFollowsWhateverIsAlreadyPlaced()
    {
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("new"), 1)], capacity: 20);

        placed.Single(p => p.InstanceId == "new").Cell.ShouldBe(2);
    }

    [Fact]
    public void SomethingNewTakesTheFirstGapThatHoldsItAndMovesNobody()
    {
        // A bar arranged against its far end: one icon at the start, and a
        // cluster that ends exactly at the edge. Pressing "add" used to put
        // the new widget after the cluster, where there is no room, and
        // squeeze the whole cluster leftward to make some.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("home", 0), 3), (Entry("a", 150), 10), (Entry("b", 160), 25), (Entry("new"), 3)],
            capacity: 185);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe(
            [("home", 0), ("new", 3), ("a", 150), ("b", 160)]);
    }

    [Fact]
    public void SomethingNewSkipsAGapTooSmallForItAndFollowsTheLast()
    {
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 4), 2), (Entry("new"), 3)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe([("a", 0), ("b", 4), ("new", 6)]);
    }

    [Fact]
    public void SeveralNewOnesEachTakeTheNextGapInTheOrderTheyCame()
    {
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 10), 2), (Entry("x"), 3), (Entry("y"), 3)],
            capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe(
            [("a", 0), ("x", 2), ("y", 5), ("b", 10)]);
    }

    [Fact]
    public void AWidgetWithNowhereToGoIsNotDrawn()
    {
        // It keeps its place in the settings and comes back when there is room.
        // Dropping it would lose somebody's widget to a moment of narrowness.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a"), 3), (Entry("b"), 3)], capacity: 4);

        placed.Select(p => p.InstanceId).ShouldBe(["a"]);
    }

    [Fact]
    public void AnArrangementFromAWiderScreenSqueezesLeftwardInsteadOfFallingOff()
    {
        // Slots chosen on a wide bar, arriving on one with twenty. The row is
        // squeezed from the far end: what sat at the end still sits at the
        // end, what sat at the start stays put, and only the gaps give way.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 30), 2), (Entry("c", 55), 2)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe([("a", 0), ("b", 16), ("c", 18)]);
    }

    [Fact]
    public void GapsThatFitAreLeftAlone()
    {
        // Only what cannot be honoured is repaired. A bar arranged in groups
        // on a screen wide enough for them comes back in groups.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 10), 2)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe([("a", 0), ("b", 10)]);
    }

    [Fact]
    public void NothingVanishesWhileTheFrontOfTheBarStandsEmpty()
    {
        // The third screen, reconstructed from the log of 30.08.2026: a bar
        // of 63 slots handed cells chosen on a bar of 78. The literal slots
        // eat the far end, and before this rule six widgets simply vanished
        // while the whole front of the bar stood empty.
        List<Placement> placed = DockGrid.Settle(
            [
                (Entry("icon", 0), 2),
                (Entry("media", 53), 6),
                (Entry("sound", 58), 2),
                (Entry("gpu", 60), 3),
                (Entry("temp", 63), 3),
                (Entry("ram", 66), 3),
                (Entry("cpu", 69), 3),
                (Entry("battery", 72), 3),
                (Entry("clock", 75), 2),
                (Entry("settings", 77), 1),
            ],
            capacity: 63);

        // Every single one is on the bar, in the order it was arranged.
        placed.Count.ShouldBe(10);
        placed.Select(p => p.Cell).ShouldBe([.. placed.Select(p => p.Cell).OrderBy(c => c)]);

        // And nothing overlaps anything else.
        var taken = new HashSet<int>();
        foreach (Placement p in placed)
        {
            for (int c = p.Cell; c < p.End; c++)
            {
                taken.Add(c).ShouldBeTrue($"{p.InstanceId} overlaps at {c}");
            }
        }
    }

    [Fact]
    public void TheSqueezeTakesOnlyTheRoomItNeeds()
    {
        // "b" runs off the end by two, so "a" gives two slots of its gap and
        // "c", further along the bar than the squeeze reaches, does not move.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("c", 10), 2), (Entry("a", 16), 3), (Entry("b", 18), 3)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell))
            .ShouldBe([("c", 10), ("a", 14), ("b", 17)]);
    }

    [Fact]
    public void NeighboursStayNeighboursWhenEveryWidgetGetsNarrower()
    {
        // The smaller reading size: same bar, every widget one slot thinner.
        // Scaled by position alone this puts a one-slot hole after each
        // widget; preserving the gaps, the cluster stays a cluster.
        ImmutableArray<WidgetConfig> refit = DockGrid.Refitted(
            [Sized("a", 10, 3), Sized("b", 13, 3), Sized("c", 16, 3)],
            new Dictionary<string, int> { ["a"] = 2, ["b"] = 2, ["c"] = 2 },
            from: 60,
            to: 60);

        // The three slots the shrink freed are shared between the row's two
        // outer gaps in proportion - the leading ten grows to eleven, the
        // tail takes the rest - and the neighbours stay glued.
        refit.Select(w => (w.InstanceId, w.Cell)).ShouldBe([("a", 11), ("b", 13), ("c", 15)]);
    }

    [Fact]
    public void ADeliberateGapScalesWithTheBar()
    {
        // A folder at the start, a cluster at the end of 78 slots, arriving
        // on 63: the widgets touch as they touched, and the emptiness between
        // scales with the bar.
        ImmutableArray<WidgetConfig> refit = DockGrid.Refitted(
            [Sized("icon", 0, 2), Sized("media", 53, 6), Sized("gear", 59, 1), Entry("new")],
            new Dictionary<string, int> { ["icon"] = 2, ["media"] = 6, ["gear"] = 1 },
            from: 78,
            to: 63);

        refit.Single(w => w.InstanceId == "icon").Cell.ShouldBe(0);

        // The middle gap and the tail share the shrink in proportion.
        refit.Single(w => w.InstanceId == "media").Cell.ShouldBe(42);
        refit.Single(w => w.InstanceId == "gear").Cell.ShouldBe(48);

        // A widget that never had a slot still has none.
        refit.Single(w => w.InstanceId == "new").Cell.ShouldBe(-1);
    }

    [Fact]
    public void ARefitRoundTripComesHome()
    {
        // Thickness there and back: adjacency in, adjacency out, at the
        // original cells.
        var spansAt68 = new Dictionary<string, int> { ["a"] = 4, ["b"] = 4 };
        var spansAt78 = new Dictionary<string, int> { ["a"] = 3, ["b"] = 3 };

        ImmutableArray<WidgetConfig> there = DockGrid.Refitted(
            [Sized("a", 40, 3), Sized("b", 43, 3)], spansAt68, from: 78, to: 68);

        // What the write-back would record after the journey out.
        ImmutableArray<WidgetConfig> recorded =
            [.. there.Select(w => w with { Span = spansAt68[w.InstanceId] })];

        ImmutableArray<WidgetConfig> home = DockGrid.Refitted(recorded, spansAt78, from: 68, to: 78);

        home.Select(w => (w.InstanceId, w.Cell)).ShouldBe([("a", 40), ("b", 43)]);
    }

    [Fact]
    public void ASliverBetweenNeighboursSnapsShut()
    {
        // Two slots of air between "a" and "b" is the coarse grid's rounding
        // written into the file, not an arrangement; ten slots is somebody's
        // deliberate spacing and survives.
        ImmutableArray<WidgetConfig> refit = DockGrid.Refitted(
            [Sized("a", 10, 3), Sized("b", 15, 3), Sized("c", 28, 3)],
            new Dictionary<string, int> { ["a"] = 3, ["b"] = 3, ["c"] = 3 },
            from: 60,
            to: 60);

        refit.Single(w => w.InstanceId == "b").Cell
            .ShouldBe(refit.Single(w => w.InstanceId == "a").Cell + 3);

        (refit.Single(w => w.InstanceId == "c").Cell
            - refit.Single(w => w.InstanceId == "b").Cell - 3).ShouldBeGreaterThan(2);
    }

    [Fact]
    public void ARowAgainstTheFarEndStaysAgainstTheFarEnd()
    {
        // The user's own bar: a folder at the start and a cluster ending
        // flush at slot 78. Every thickness round trip was walking the
        // cluster a few slots further from the edge; ten toggles later a
        // quarter of the bar stood empty past the gear.
        (string Id, int Cell, int Span)[] compact =
        [
            ("icon", 0, 2), ("media", 50, 5), ("sound", 55, 2), ("gpu", 57, 3),
            ("temp", 60, 3), ("ram", 63, 3), ("cpu", 66, 3), ("battery", 69, 3),
            ("clock", 72, 2), ("gear", 74, 1), ("extra", 75, 3),
        ];

        var wide = compact.ToDictionary(w => w.Id, w => w.Id is "icon" or "gear" ? w.Span : w.Span + 1);
        var thin = compact.ToDictionary(w => w.Id, w => w.Span);

        ImmutableArray<WidgetConfig> arrangement = [.. compact.Select(w => Sized(w.Id, w.Cell, w.Span))];

        for (int trip = 0; trip < 5; trip++)
        {
            ImmutableArray<WidgetConfig> at68 = DockGrid.Refitted(arrangement, wide, from: 78, to: 68);
            at68 = [.. at68.Select(w => w with { Span = wide[w.InstanceId] })];

            ImmutableArray<WidgetConfig> at78 = DockGrid.Refitted(at68, thin, from: 68, to: 78);
            arrangement = [.. at78.Select(w => w with { Span = thin[w.InstanceId] })];
        }

        // Five round trips later the cluster still ends at the far edge and
        // the folder still stands at the start.
        WidgetConfig last = arrangement.Single(w => w.InstanceId == "extra");
        (last.Cell + last.Span).ShouldBe(78);
        arrangement.Single(w => w.InstanceId == "icon").Cell.ShouldBe(0);

        // And nothing overlaps anything else.
        foreach (WidgetConfig w in arrangement)
        {
            foreach (WidgetConfig other in arrangement)
            {
                if (w.InstanceId != other.InstanceId && w.Cell >= 0 && other.Cell >= 0)
                {
                    (w.Cell + w.Span <= other.Cell || other.Cell + other.Span <= w.Cell)
                        .ShouldBeTrue($"{w.InstanceId} overlaps {other.InstanceId}");
                }
            }
        }
    }

    [Fact]
    public void SomethingFitsOnlyWhereEveryOneOfItsSlotsIsFree()
    {
        List<Placement> placed = [new("a", 0, 2), new("b", 5, 2)];

        DockGrid.Fits(placed, capacity: 10, cell: 2, span: 3).ShouldBeTrue();
        DockGrid.Fits(placed, capacity: 10, cell: 2, span: 4).ShouldBeFalse();
        DockGrid.Fits(placed, capacity: 10, cell: 9, span: 2).ShouldBeFalse();
        DockGrid.Fits(placed, capacity: 10, cell: -1, span: 1).ShouldBeFalse();
    }

    [Fact]
    public void AWidgetDoesNotBlockItself()
    {
        // Nudging something one slot along asks whether it fits where it partly
        // already is; counting itself as in the way would forbid every move.
        List<Placement> placed = [new("a", 4, 2)];

        DockGrid.Fits(placed, capacity: 10, cell: 5, span: 2, ignore: "a").ShouldBeTrue();
        DockGrid.Fits(placed, capacity: 10, cell: 5, span: 2).ShouldBeFalse();
    }

    [Fact]
    public void TheNearestLandingGivesGroundBothWays()
    {
        // Aimed at a slot inside its neighbour, a widget lands beside it rather
        // than refusing: a hand aiming at a gap means the gap.
        List<Placement> placed = [new("a", 0, 2), new("b", 6, 2)];

        DockGrid.Nearest(placed, capacity: 10, wanted: 1, span: 2, ignore: null).ShouldBe(2);
        DockGrid.Nearest(placed, capacity: 10, wanted: 6, span: 1, ignore: null).ShouldBe(5);
    }

    [Fact]
    public void ThereIsNoLandingOnAFullBar()
    {
        List<Placement> placed = [new("a", 0, 4)];

        DockGrid.Nearest(placed, capacity: 4, wanted: 0, span: 1, ignore: null).ShouldBeNull();
    }

    [Fact]
    public void TheSlotUnderAPointNamesWhateverCoversIt()
    {
        List<Placement> placed = [new("a", 2, 3)];

        DockGrid.At(placed, 2)!.Value.InstanceId.ShouldBe("a");
        DockGrid.At(placed, 4)!.Value.InstanceId.ShouldBe("a");
        DockGrid.At(placed, 5).ShouldBeNull();
        DockGrid.At(placed, 1).ShouldBeNull();
    }

    [Fact]
    public void OnlyTheEmptySlotsAreOfferedDuringADrag()
    {
        // What the drag draws. A grid ruled through the widgets already
        // standing on the bar reads as graph paper, and worse, it offers
        // places nothing can be dropped on.
        List<Placement> placed = [new("a", 0, 2), new("b", 5, 1)];

        DockGrid.Free(placed, capacity: 8).ShouldBe([2, 3, 4, 6, 7]);
    }

    [Fact]
    public void TheSlotsAWidgetIsLeavingCountAsFree()
    {
        // Otherwise a widget picked up cannot be put back where it came from,
        // and the gap it is about to leave is drawn as though it were solid.
        List<Placement> placed = [new("a", 0, 2), new("b", 5, 1)];

        DockGrid.Free(placed, capacity: 8, ignore: "a").ShouldBe([0, 1, 2, 3, 4, 6, 7]);
    }

    [Fact]
    public void AFullBarOffersNothingAtAll()
    {
        List<Placement> placed = [new("a", 0, 3)];

        DockGrid.Free(placed, capacity: 3).ShouldBeEmpty();
    }
}

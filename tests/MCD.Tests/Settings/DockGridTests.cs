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
    public void AWidgetWithNowhereToGoIsNotDrawn()
    {
        // It keeps its place in the settings and comes back when there is room.
        // Dropping it would lose somebody's widget to a moment of narrowness.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a"), 3), (Entry("b"), 3)], capacity: 4);

        placed.Select(p => p.InstanceId).ShouldBe(["a"]);
    }

    [Fact]
    public void AnArrangementFromAWiderScreenClosesUpInsteadOfFallingOff()
    {
        // What "make the others the same" produces: slots chosen on a bar with
        // sixty-eight of them, arriving on one with twenty. The spacing cannot
        // survive that and the order can, so the order is what is kept.
        List<Placement> placed = DockGrid.Settle(
            [(Entry("a", 0), 2), (Entry("b", 30), 2), (Entry("c", 55), 2)], capacity: 20);

        placed.Select(p => (p.InstanceId, p.Cell)).ShouldBe([("a", 0), ("b", 2), ("c", 4)]);
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

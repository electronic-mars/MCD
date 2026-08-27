namespace Mcd.Core.Settings;

/// <summary>Where one widget sits: its first slot, and how many it takes.</summary>
public readonly record struct Placement(string InstanceId, int Cell, int Span)
{
    public int End => Cell + Span;

    public bool Covers(int cell) => cell >= Cell && cell < End;
}

/// <summary>
/// The arithmetic of a bar made of slots.
/// </summary>
/// <remarks>
/// Kept apart from the window so it can be reasoned about - and tested -
/// without a screen. Every question the bar asks while something is being
/// dragged is answered here: where would this land, does it fit, what is
/// already there.
/// </remarks>
public static class DockGrid
{
    /// <summary>
    /// Settles where everything sits: each widget on the slot it asked for, or
    /// on the first free one after whatever comes before it.
    /// </summary>
    /// <remarks>
    /// Left to right, in the order the slots say - so a bar arranged by hand
    /// comes back exactly as it was left, gaps and all, and one that cannot be
    /// honoured is repaired by shuffling along rather than by scattering. A
    /// widget that has never been placed follows the one before it, which is
    /// how a newly added thing lands on the first free slot.
    /// </remarks>
    public static List<Placement> Settle(
        IReadOnlyList<(WidgetConfig Entry, int Span)> items, int capacity)
    {
        var placed = new List<Placement>();
        int cursor = 0;

        // Ordered by slot; anything not yet placed keeps the order it was
        // given and goes after everything that has a slot of its own.
        foreach ((WidgetConfig entry, int span) in items
            .Select((item, i) => (item, i))
            .OrderBy(x => x.item.Entry.Cell < 0 ? int.MaxValue : x.item.Entry.Cell)
            .ThenBy(x => x.i)
            .Select(x => x.item))
        {
            int cell = Math.Max(entry.Cell < 0 ? 0 : entry.Cell, cursor);

            if (cell + span > capacity)
            {
                // The bar is full. The widget keeps its place in the settings
                // and is simply not drawn - a slot that does not exist cannot
                // be shown, and dropping it would lose somebody's widget to a
                // moment of narrowness.
                continue;
            }

            placed.Add(new Placement(entry.InstanceId, cell, span));
            cursor = cell + span;
        }

        return placed;
    }

    /// <summary>The first run of free slots long enough, or null when there is none.</summary>
    public static int? FirstFree(IReadOnlyList<Placement> placed, int capacity, int span) =>
        FirstFree(Occupancy(placed, capacity), span);

    /// <summary>Whether a widget of this length fits here, ignoring one of its own.</summary>
    public static bool Fits(
        IReadOnlyList<Placement> placed, int capacity, int cell, int span, string? ignore = null)
    {
        if (cell < 0 || cell + span > capacity)
        {
            return false;
        }

        foreach (Placement other in placed)
        {
            if (other.InstanceId == ignore)
            {
                continue;
            }

            if (cell < other.End && other.Cell < cell + span)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Which widget covers this slot, if any.</summary>
    public static Placement? At(IReadOnlyList<Placement> placed, int cell)
    {
        foreach (Placement p in placed)
        {
            if (p.Covers(cell))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// The nearest slot a widget of this length can be dropped on, aiming at
    /// the one under the pointer and giving ground either way.
    /// </summary>
    /// <remarks>
    /// Without the search a wide widget could only be dropped where its own
    /// first slot happened to land, so aiming at the middle of a gap did
    /// nothing - which is exactly what a person aiming at an empty slot cannot
    /// be expected to work out.
    /// </remarks>
    public static int? Nearest(
        IReadOnlyList<Placement> placed, int capacity, int wanted, int span, string? ignore)
    {
        for (int away = 0; away <= capacity; away++)
        {
            if (Fits(placed, capacity, wanted - away, span, ignore))
            {
                return wanted - away;
            }

            if (away > 0 && Fits(placed, capacity, wanted + away, span, ignore))
            {
                return wanted + away;
            }
        }

        return null;
    }

    private static bool[] Occupancy(IReadOnlyList<Placement> placed, int capacity)
    {
        var taken = new bool[Math.Max(0, capacity)];

        foreach (Placement p in placed)
        {
            Fill(taken, p.Cell, p.Span);
        }

        return taken;
    }

    private static int? FirstFree(bool[] taken, int span)
    {
        for (int cell = 0; cell + span <= taken.Length; cell++)
        {
            if (Free(taken, cell, span))
            {
                return cell;
            }
        }

        return null;
    }

    private static bool Free(bool[] taken, int cell, int span)
    {
        if (cell < 0 || cell + span > taken.Length)
        {
            return false;
        }

        for (int i = cell; i < cell + span; i++)
        {
            if (taken[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void Fill(bool[] taken, int cell, int span)
    {
        for (int i = Math.Max(0, cell); i < Math.Min(taken.Length, cell + span); i++)
        {
            taken[i] = true;
        }
    }
}

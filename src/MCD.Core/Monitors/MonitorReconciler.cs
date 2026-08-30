using System.Collections.Immutable;
using Mcd.Core.Settings;

namespace Mcd.Core.Monitors;

/// <summary>
/// Decides which saved configuration belongs to which attached monitor.
/// </summary>
/// <remarks>
/// <para>
/// A pure function: no files, no Win32, no clock beyond the value handed in. It
/// is the one piece of this program that is covered by tests exhaustively,
/// because it is the piece PowerToys got wrong.
/// </para>
/// <para>
/// Two rules here are load-bearing and must survive any future edit:
/// </para>
/// <list type="number">
/// <item>
/// A monitor that matches nothing inherits the primary monitor's dock and is
/// enabled. PowerToys gives it an entry that is disabled with no widgets, so a
/// momentary lookup failure becomes indistinguishable from "the user switched
/// this dock off" - and the user is left with an empty bar and no explanation
/// (microsoft/PowerToys#49604). Here the worst case is a duplicated layout.
/// </item>
/// <item>
/// Entries are never removed. PowerToys prunes anything unseen for 180 days, so
/// a laptop that spends half a year away from its docking station loses its
/// setup. Forgetting a monitor is a button on the settings page, not a side
/// effect of looking at the topology.
/// </item>
/// </list>
/// </remarks>
public static class MonitorReconciler
{
    /// <summary>
    /// How stale an unmatched entry may be and still count as the same monitor
    /// whose id churned. Past this it is likelier to be different hardware that
    /// merely happens to be the only other candidate.
    /// </summary>
    private static readonly TimeSpan ReassociationWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How out of date a stored <c>LastSeenUtc</c> may get before refreshing it
    /// is worth a settings write of its own.
    /// </summary>
    private static readonly TimeSpan LastSeenRefresh = TimeSpan.FromHours(6);

    private enum MatchKind
    {
        StableId,
        PreviousStableId,
        Edid,
        Shape,
    }

    /// <param name="snapshot">
    /// Authoritative by type, and that is the point: a half-settled topology
    /// cannot be passed in at all, so the identity churn behind #49604 has no
    /// route into the registry.
    /// </param>
    /// <param name="saved">The monitor registry as it stands.</param>
    /// <param name="now">Injected so the tests do not depend on a clock.</param>
    public static ReconcileResult Reconcile(
        AuthoritativeSnapshot snapshot,
        ImmutableArray<MonitorConfig> saved,
        DateTimeOffset now)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        var matches = new Dictionary<MonitorStableId, MonitorConfig>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        List<MonitorInfo> pending = [.. snapshot.Monitors];

        MatchBy(pending, saved, claimed, matches, notes, MatchKind.StableId);
        MatchBy(pending, saved, claimed, matches, notes, MatchKind.PreviousStableId);
        MatchBy(pending, saved, claimed, matches, notes, MatchKind.Edid);
        MatchBy(pending, saved, claimed, matches, notes, MatchKind.Shape);
        MatchLoneSurvivor(pending, saved, claimed, matches, notes, now);

        // Whatever is still pending is new to us. It copies what the primary is
        // showing, so a dock never appears with nothing on it.
        MonitorInfo primary = snapshot.Monitors.First(m => m.IsPrimary);
        MonitorConfig template = matches.TryGetValue(primary.StableId, out MonitorConfig? existing)
            ? existing
            : new MonitorConfig();

        foreach (MonitorInfo monitor in pending)
        {
            matches[monitor.StableId] = InheritFrom(template);
            notes.Add($"{monitor.Identity.FriendlyName}: new monitor, inherited the primary dock");
        }

        return Assemble(snapshot, saved, matches, now, notes);
    }

    private static void MatchBy(
        List<MonitorInfo> pending,
        ImmutableArray<MonitorConfig> saved,
        HashSet<string> claimed,
        Dictionary<MonitorStableId, MonitorConfig> matches,
        ImmutableArray<string>.Builder notes,
        MatchKind kind)
    {
        foreach (MonitorInfo monitor in pending.ToList())
        {
            string key = MonitorKey(monitor, kind);
            if (key.Length == 0)
            {
                continue;
            }

            List<MonitorConfig> hits =
            [
                .. saved.Where(c => !claimed.Contains(c.StableId) && ConfigKey(c, kind) == key)
            ];

            if (hits.Count != 1)
            {
                continue;
            }

            // Ambiguity is resolved by refusing to guess. The author's own desk
            // has two monitors reporting the same EDID; letting them swap
            // configurations behind the user's back would be worse than leaving
            // one of them to be treated as new.
            if (kind != MatchKind.StableId && pending.Count(m => MonitorKey(m, kind) == key) != 1)
            {
                notes.Add(
                    $"{monitor.Identity.FriendlyName}: {kind} match skipped, another monitor shares that key");
                continue;
            }

            MonitorConfig hit = hits[0];
            claimed.Add(hit.StableId);
            matches[monitor.StableId] = hit;
            pending.Remove(monitor);

            if (kind != MatchKind.StableId)
            {
                notes.Add($"{monitor.Identity.FriendlyName}: matched by {kind}");
            }
        }
    }

    /// <summary>
    /// The Win+P case. Changing projection mode can hand the same physical
    /// monitor a new device path; when exactly one monitor and exactly one
    /// recently-seen entry are left over, they are the same screen.
    /// </summary>
    private static void MatchLoneSurvivor(
        List<MonitorInfo> pending,
        ImmutableArray<MonitorConfig> saved,
        HashSet<string> claimed,
        Dictionary<MonitorStableId, MonitorConfig> matches,
        ImmutableArray<string>.Builder notes,
        DateTimeOffset now)
    {
        if (pending.Count != 1)
        {
            return;
        }

        List<MonitorConfig> leftover = [.. saved.Where(c => !claimed.Contains(c.StableId))];
        if (leftover.Count != 1)
        {
            return;
        }

        MonitorInfo monitor = pending[0];
        MonitorConfig candidate = leftover[0];

        if (now - candidate.LastSeenUtc > ReassociationWindow)
        {
            notes.Add(
                $"{monitor.Identity.FriendlyName}: the only unmatched entry was last seen "
                + $"{candidate.LastSeenUtc:u}, too long ago to re-associate");
            return;
        }

        claimed.Add(candidate.StableId);
        matches[monitor.StableId] = candidate;
        pending.Clear();
        notes.Add(
            $"{monitor.Identity.FriendlyName}: re-associated with the entry for "
            + $"{candidate.FriendlyName}, whose id changed");
    }

    private static string MonitorKey(MonitorInfo monitor, MatchKind kind) => kind switch
    {
        MatchKind.StableId or MatchKind.PreviousStableId => monitor.StableId.Value,
        MatchKind.Edid => monitor.Identity.EdidKey,
        MatchKind.Shape => monitor.ShapeKey,
        _ => string.Empty,
    };

    private static string ConfigKey(MonitorConfig config, MatchKind kind) => kind switch
    {
        MatchKind.StableId => config.StableId,

        // Only an unambiguous history counts: an entry that has answered to
        // several ids tells us nothing about which monitor it is now.
        MatchKind.PreviousStableId => config.PreviousStableIds.Length == 1
            ? config.PreviousStableIds[0]
            : string.Empty,

        MatchKind.Edid => config.EdidKey,
        MatchKind.Shape => config.ShapeHint,
        _ => string.Empty,
    };

    private static MonitorConfig InheritFrom(MonitorConfig template) => new()
    {
        Enabled = true,
        Edge = template.Edge,
        Mode = template.Mode,
        Density = template.Density,

        // Everything about how the bar sits, not only most of it. A screen
        // plugged in after the others were arranged used to arrive with the
        // widgets copied and the anchor and the topmost flag left at their
        // defaults - so it looked like the others until you noticed that its
        // contents were gathered at the wrong end.
        Anchor = template.Anchor,
        Topmost = template.Topmost,
        Widgets = [.. template.Widgets.Select(w => w.AsNewInstance())],
    };

    private static ReconcileResult Assemble(
        AuthoritativeSnapshot snapshot,
        ImmutableArray<MonitorConfig> saved,
        Dictionary<MonitorStableId, MonitorConfig> matches,
        DateTimeOffset now,
        ImmutableArray<string>.Builder notes)
    {
        var plans = ImmutableArray.CreateBuilder<DockPlan>(snapshot.Count);
        var registry = ImmutableArray.CreateBuilder<MonitorConfig>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        bool staleTimestamp = false;

        foreach (MonitorInfo monitor in snapshot.Monitors)
        {
            MonitorConfig before = matches[monitor.StableId];
            MonitorConfig after = Refresh(before, monitor, now);

            staleTimestamp |= now - before.LastSeenUtc > LastSeenRefresh;

            plans.Add(new DockPlan(monitor, after));
            registry.Add(after);
            consumed.Add(before.StableId);
        }

        // Entries for monitors that are not attached are carried over untouched,
        // LastSeen included. They describe a screen we cannot see, and rewriting
        // their timestamps would erase the record of when we last could.
        foreach (MonitorConfig config in saved.Where(c => !consumed.Contains(c.StableId)))
        {
            registry.Add(config);
        }

        ImmutableArray<MonitorConfig> result = registry.ToImmutable();

        return new ReconcileResult(
            plans.ToImmutable(),
            result,
            RegistryChanged: WorthWriting(result, saved, staleTimestamp),
            notes.ToImmutable());
    }

    /// <summary>
    /// Whether the new registry differs from the stored one in a way worth
    /// putting on disk.
    /// </summary>
    /// <remarks>
    /// <c>LastSeenUtc</c> moves on every single reading and is deliberately
    /// excluded. Registering an AppBar changes the desktop work area, Windows
    /// broadcasts that, the broadcast triggers another reading - so counting the
    /// timestamp as a change makes the program write settings in a loop it
    /// started itself. Measured before this check existed: four writes in the
    /// first one and a half seconds of an ordinary start.
    ///
    /// The timestamp still reaches disk, just not on every tick: an entry whose
    /// stored value has fallen more than six hours behind is refreshed, which is
    /// far finer than the resolution anything reads it at.
    /// </remarks>
    private static bool WorthWriting(
        ImmutableArray<MonitorConfig> now,
        ImmutableArray<MonitorConfig> saved,
        bool staleTimestamp)
    {
        if (staleTimestamp || now.Length != saved.Length)
        {
            return true;
        }

        for (int i = 0; i < now.Length; i++)
        {
            if (now[i] with { LastSeenUtc = default } != (saved[i] with { LastSeenUtc = default }))
            {
                return true;
            }
        }

        return false;
    }

    private static MonitorConfig Refresh(MonitorConfig config, MonitorInfo monitor, DateTimeOffset now)
    {
        ImmutableArray<string> history = config.PreviousStableIds;
        if (config.StableId.Length > 0 && config.StableId != monitor.StableId.Value)
        {
            history =
            [
                .. history.Where(id => id != config.StableId).Prepend(config.StableId).Take(4)
            ];
        }

        return config with
        {
            StableId = monitor.StableId.Value,
            DevicePath = monitor.Identity.DevicePath,
            EdidKey = monitor.Identity.EdidKey,
            FriendlyName = monitor.Identity.FriendlyName,
            ShapeHint = monitor.ShapeKey,
            PreviousStableIds = history,
            LastSeenUtc = now,

            // The invariant this whole class exists to hold: a dock that is on
            // always has something to show.
            Widgets = config.Widgets.IsEmpty ? DockContents.Default : config.Widgets,
        };
    }
}

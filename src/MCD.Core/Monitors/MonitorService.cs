using System.Collections.Immutable;
using Mcd.Interop.Display;
using Microsoft.Extensions.Logging;

namespace Mcd.Core.Monitors;

/// <summary>Turns the two Windows display APIs into one snapshot, and judges it.</summary>
public class MonitorService(ILogger<MonitorService> log)
{
    /// <summary>
    /// Reads the current topology. Returns an <see cref="AuthoritativeSnapshot"/>
    /// only when GDI and the Display Configuration API agree with each other and
    /// with themselves.
    /// </summary>
    public virtual MonitorSnapshot Enumerate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<RawMonitor> raw = MonitorEnumerator.Enumerate();
        IReadOnlyDictionary<string, DisplayTarget> targets = DisplayConfigApi.QueryActiveTargets();

        var monitors = ImmutableArray.CreateBuilder<MonitorInfo>(raw.Count);
        string? defect = null;

        foreach (RawMonitor m in raw)
        {
            if (!targets.TryGetValue(m.GdiName, out DisplayTarget target))
            {
                // GDI sees a monitor the Display Configuration API has not caught
                // up with. Naming it now would mean inventing an identity.
                defect ??= $"no active display path for {m.GdiName}";
                continue;
            }

            monitors.Add(new MonitorInfo(
                new MonitorIdentity(
                    MonitorStableId.FromDevicePath(target.DevicePath),
                    target.DevicePath,
                    target.EdidKey,
                    Describe(target),
                    m.GdiName),
                m.Bounds,
                m.WorkArea,
                m.Dpi,
                m.IsPrimary));
        }

        ImmutableArray<MonitorInfo> result = monitors.ToImmutable();
        defect ??= FindDefect(raw.Count, result);

        if (defect is null)
        {
            log.LogDebug("monitors.snapshot authoritative count={Count}", result.Length);
            return new AuthoritativeSnapshot(result, now);
        }

        log.LogInformation("monitors.snapshot provisional count={Count} reason={Reason}", result.Length, defect);
        return new ProvisionalSnapshot(result, now, defect);
    }

    private static string? FindDefect(int gdiCount, ImmutableArray<MonitorInfo> monitors)
    {
        if (monitors.Length == 0)
        {
            return "no monitors";
        }

        if (monitors.Length != gdiCount)
        {
            return $"resolved {monitors.Length} of {gdiCount} monitors";
        }

        int primaries = monitors.Count(m => m.IsPrimary);
        if (primaries != 1)
        {
            return $"{primaries} primary monitors";
        }

        if (monitors.Any(m => m.Width <= 0 || m.Height <= 0))
        {
            return "a monitor reported an empty rectangle";
        }

        // Two monitors resolving to one id means the device paths collided, which
        // in practice means one of them is stale.
        if (monitors.Select(m => m.StableId).Distinct().Count() != monitors.Length)
        {
            return "duplicate stable ids";
        }

        return null;
    }

    private static string Describe(DisplayTarget target) =>
        target.FriendlyName.Length > 0 ? target.FriendlyName
        : target.EdidVendor.Length > 0 ? $"{target.EdidVendor} {target.EdidProduct}"
        : "Display";
}

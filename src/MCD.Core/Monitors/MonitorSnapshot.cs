using System.Collections.Immutable;

namespace Mcd.Core.Monitors;

/// <summary>What the display topology looked like at one instant.</summary>
public abstract record MonitorSnapshot(ImmutableArray<MonitorInfo> Monitors, DateTimeOffset TakenAt)
{
    public int Count => Monitors.Length;
}

/// <summary>
/// A snapshot that passed every consistency check and may be acted on.
/// </summary>
/// <remarks>
/// This type is the whole defence against PowerToys #49604. Reconciliation and
/// settings writes accept nothing else, so a half-settled topology cannot reach
/// them - not by discipline, but because the code would not compile.
/// </remarks>
public sealed record AuthoritativeSnapshot(ImmutableArray<MonitorInfo> Monitors, DateTimeOffset TakenAt)
    : MonitorSnapshot(Monitors, TakenAt);

/// <summary>
/// The topology is still moving. Good enough to reposition windows that already
/// exist; never good enough to decide what a monitor is.
/// </summary>
public sealed record ProvisionalSnapshot(
    ImmutableArray<MonitorInfo> Monitors,
    DateTimeOffset TakenAt,
    string Reason)
    : MonitorSnapshot(Monitors, TakenAt);

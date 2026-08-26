using System.Collections.Immutable;
using Mcd.Core.Settings;

namespace Mcd.Core.Monitors;

/// <param name="Plans">What to put on screen right now, one entry per live monitor.</param>
/// <param name="Registry">
/// The full monitor registry afterwards, including entries for monitors that are
/// not currently attached. Entries are never dropped here.
/// </param>
/// <param name="RegistryChanged">
/// False when the registry came out identical. The caller uses this to decide
/// whether a settings write is warranted at all.
/// </param>
/// <param name="Diagnostics">Human-readable notes for the log and the settings page.</param>
public sealed record ReconcileResult(
    ImmutableArray<DockPlan> Plans,
    ImmutableArray<MonitorConfig> Registry,
    bool RegistryChanged,
    ImmutableArray<string> Diagnostics);

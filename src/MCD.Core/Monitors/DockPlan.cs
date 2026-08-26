using Mcd.Core.Settings;

namespace Mcd.Core.Monitors;

/// <summary>One live monitor paired with the configuration it is to be shown with.</summary>
public sealed record DockPlan(MonitorInfo Monitor, MonitorConfig Config)
{
    public bool ShouldShow => Config.Enabled;
}

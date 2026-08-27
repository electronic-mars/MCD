using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Core.Monitors;
using Mcd.Core.Settings;
using Mcd.Interop.AppBar;

namespace Mcd.App.Settings;

/// <summary>One monitor as the settings window shows it, attached or not.</summary>
public sealed partial class MonitorRow : ObservableObject
{
    private readonly Action<MonitorRow> _onEdited;
    private bool _loading = true;

    public MonitorRow(MonitorConfig config, MonitorInfo? attached, Action<MonitorRow> onEdited)
    {
        _onEdited = onEdited;

        Config = config;
        Attached = attached is not null;
        Name = config.FriendlyName.Length > 0
            ? config.FriendlyName
            : Loc.Tr("MonitorUnknown", "Unknown display");

        Detail = attached is not null
            ? $"{attached.Width} × {attached.Height} · {attached.Dpi * 100 / 96}% · {attached.Identity.GdiName}"
            : string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("MonitorNotAttached", "not attached · last seen {0}"),
                Describe(config.LastSeenUtc));

        StableId = config.StableId;
        EdidKey = config.EdidKey.Length > 0 ? config.EdidKey : "unreadable";
        DevicePath = config.DevicePath;

        // Forgetting a monitor throws away its whole layout, so it is only
        // offered for entries old enough that they are unlikely to come back.
        CanForget = !Attached && DateTimeOffset.UtcNow - config.LastSeenUtc > TimeSpan.FromDays(30);

        Enabled = config.Enabled;
        EdgeIndex = (int)config.Edge;
        DensityIndex = (int)config.Density;
        ModeIndex = (int)config.Mode;
        _loading = false;
    }

    public MonitorConfig Config { get; private set; }

    public bool Attached { get; }

    public string Name { get; }

    public string Detail { get; }

    public string StableId { get; }

    public string EdidKey { get; }

    public string DevicePath { get; }

    public bool CanForget { get; }

    /// <summary>Edge and density can only be changed for a screen that is here to see it change.</summary>
    public bool CanEdit => Attached;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    /// <summary>Index into Left, Top, Right, Bottom - the order of <see cref="AppBarEdge"/>.</summary>
    [ObservableProperty]
    public partial int EdgeIndex { get; set; }

    [ObservableProperty]
    public partial int DensityIndex { get; set; }

    /// <summary>Index into Pinned, Auto-hide - the order of <see cref="AppBarMode"/>.</summary>
    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    /// <summary>
    /// Set when auto-hide has been asked for on an edge the taskbar already
    /// hides on. The shell allows one hiding bar per edge, so the dock stays
    /// pinned - and says so, rather than appearing not to have taken the setting.
    /// </summary>
    /// <summary>Whether there is anything to say about the chosen behaviour.</summary>
    public bool HasModeNote => ModeNote is not null;

    public string? ModeNote =>
        (AppBarMode)ModeIndex == AppBarMode.AutoHide
        && AppBarHost.TaskbarAutoHidesOn((AppBarEdge)EdgeIndex)
            ? "The taskbar already hides on this edge, so the dock stays pinned."
            : null;

    public MonitorConfig Apply(MonitorConfig config) => config with
    {
        Enabled = Enabled,
        Edge = (AppBarEdge)EdgeIndex,
        Density = (DockDensity)DensityIndex,
        Mode = (AppBarMode)ModeIndex,
    };

    partial void OnEnabledChanged(bool value) => Edited();

    partial void OnEdgeIndexChanged(int value)
    {
        // Whether auto-hide is possible depends on the edge, so moving the dock
        // can make the note appear or go away.
        OnPropertyChanged(nameof(ModeNote));
        OnPropertyChanged(nameof(HasModeNote));
        Edited();
    }

    partial void OnDensityIndexChanged(int value) => Edited();

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ModeNote));
        OnPropertyChanged(nameof(HasModeNote));
        Edited();
    }

    private void Edited()
    {
        // The setters run once while the row is being filled in from settings.
        // Reporting those as edits would write the file on every window opening.
        if (_loading)
        {
            return;
        }

        _onEdited(this);
    }

    private static string Describe(DateTimeOffset when)
    {
        if (when == default)
        {
            return Loc.Tr("SeenNever", "never");
        }

        TimeSpan ago = DateTimeOffset.UtcNow - when;

        return ago switch
        {
            { TotalMinutes: < 2 } => Loc.Tr("SeenMoment", "a moment ago"),
            { TotalHours: < 1 } => string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("SeenMinutes", "{0} minutes ago"), (int)ago.TotalMinutes),
            { TotalDays: < 1 } => string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("SeenHours", "{0} hours ago"), (int)ago.TotalHours),
            { TotalDays: < 30 } => string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("SeenDays", "{0} days ago"), (int)ago.TotalDays),
            _ => when.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
        };
    }
}

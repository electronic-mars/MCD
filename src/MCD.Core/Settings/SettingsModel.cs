using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcd.Interop.AppBar;

namespace Mcd.Core.Settings;

/// <summary>How thick the dock is.</summary>
public enum DockDensity
{
    Default,
    Compact,
}

/// <summary>Everything the program remembers between runs.</summary>
public sealed record SettingsModel
{
    public int SchemaVersion { get; init; } = SettingsDefaults.SchemaVersion;

    public AppSettings App { get; init; } = new();

    public ImmutableArray<MonitorConfig> Monitors { get; init; } = [];

    public SensorSettings Sensors { get; init; } = new();
}

public sealed record AppSettings
{
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; init; } = "system";

    /// <summary>
    /// What the bar is made of: "acrylic" for the translucent finish, "solid"
    /// for the theme's plain colour, "colour" for one of the person's own,
    /// "image" for a picture.
    /// </summary>
    /// <remarks>
    /// Solid is not only a taste: acrylic costs a little power to draw, and over
    /// a busy wallpaper some people find numbers on it harder to read.
    /// </remarks>
    public string Backdrop { get; init; } = "acrylic";

    /// <summary>The bar's colour while Backdrop is "colour", as #AARRGGBB.</summary>
    public string BackdropColour { get; init; } = "#FF202020";

    /// <summary>The picture behind the bar while Backdrop is "image".</summary>
    public string BackdropImage { get; init; } = string.Empty;

    /// <summary>
    /// Where the readings take their colour: "neutral" for the theme's text
    /// colour, "windows" for the accent colour chosen in Windows.
    /// </summary>
    public string Accent { get; init; } = "neutral";

    /// <summary>
    /// The interface language: "system" to follow Windows, or a tag such as
    /// "ru-RU". Takes effect the next time the program starts.
    /// </summary>
    public string Language { get; init; } = "system";

    public bool Autostart { get; init; }

    /// <summary>
    /// Which icon each reading is drawn with, by the reading's stable name.
    /// Anything missing falls back to the widget's own default.
    /// </summary>
    public ImmutableDictionary<string, string> Icons { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// What the launcher widget starts, in the order it shows them.
    /// </summary>
    /// <remarks>
    /// One list for every dock rather than one per monitor. Someone who pins a
    /// program to the bar means the bar, not the bar on this screen, and having
    /// to pin it again on each monitor would be a chore with nothing to show
    /// for it.
    /// </remarks>
    public ImmutableArray<LaunchItem> Launcher { get; init; } = [];
}

/// <summary>One thing the launcher can start.</summary>
public sealed record LaunchItem
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// A file, a folder, or an address. Handed to the shell as it stands, so
    /// whatever works typed into the Run box works here.
    /// </summary>
    public string Target { get; init; } = string.Empty;

    public static LaunchItem For(string target, string name) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Name = name,
        Target = target,
    };
}

/// <summary>One monitor's dock, remembered by the monitor's stable id.</summary>
public sealed record MonitorConfig
{
    public string StableId { get; init; } = string.Empty;

    /// <summary>
    /// The raw device path the id was hashed from. Not used for lookup - it is
    /// here so a person reading config.json or the diagnostics page can tell
    /// which entry is which screen.
    /// </summary>
    public string DevicePath { get; init; } = string.Empty;

    public string EdidKey { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Name, resolution and DPI as last seen. The last-resort match key.</summary>
    public string ShapeHint { get; init; } = string.Empty;

    /// <summary>Ids this entry has answered to before, kept for auditing a re-association.</summary>
    public ImmutableArray<string> PreviousStableIds { get; init; } = [];

    public DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>
    /// False only when a person switched this dock off. A monitor that merely
    /// failed to match is never written out as disabled - that is the shape of
    /// PowerToys #49604.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public AppBarEdge Edge { get; init; } = AppBarEdge.Bottom;

    public AppBarMode Mode { get; init; } = AppBarMode.Pinned;

    public DockDensity Density { get; init; } = DockDensity.Default;

    /// <summary>
    /// Whether this bar stays above other windows. On by default; a full-screen
    /// program steps in front either way.
    /// </summary>
    public bool Topmost { get; init; } = true;

    /// <summary>
    /// Everything on the bar, in display order. Spacers are widgets too.
    /// </summary>
    /// <remarks>
    /// One flat run rather than three regions. Where things sit is decided by
    /// spacer widgets that stretch to take the free length, so any arrangement
    /// the regions could express is a particular ordering of this list - and a
    /// person dragging a widget along the bar is never told "not there".
    /// </remarks>
    public ImmutableArray<WidgetConfig> Widgets { get; init; } = DockContents.Default;

    /// <summary>The three regions files before schema 4 were arranged in. Read
    /// by the migration and never written back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DockBands? Bands { get; init; }
}

/// <summary>The regions of a dock as they were before schema 4.</summary>
public sealed record DockBands
{
    public ImmutableArray<WidgetConfig> Start { get; init; } = [];

    public ImmutableArray<WidgetConfig> Center { get; init; } = [];

    public ImmutableArray<WidgetConfig> End { get; init; } = [];
}

/// <summary>What a dock holds before anyone has arranged it.</summary>
public static class DockContents
{
    /// <summary>
    /// Launcher at the start, media transport in the middle, readings at the
    /// end. The media widget keeps itself off the bar while nothing is playing,
    /// and the two spacers simply meet where it was.
    /// </summary>
    public static ImmutableArray<WidgetConfig> Default =>
    [
        WidgetConfig.New("mcd.launcher"),
        WidgetConfig.Spacer(),
        WidgetConfig.New("mcd.media"),
        WidgetConfig.Spacer(),
        WidgetConfig.New("mcd.load"),
        WidgetConfig.New("mcd.temperature"),
    ];
}

public sealed record WidgetConfig
{
    public string InstanceId { get; init; } = string.Empty;

    public string TypeId { get; init; } = string.Empty;

    /// <summary>Opaque to everything but the widget that owns it.</summary>
    public JsonElement? Config { get; init; }

    public static WidgetConfig New(string typeId) => new()
    {
        InstanceId = Guid.NewGuid().ToString("n"),
        TypeId = typeId,
    };

    /// <summary>A stretch of empty bar that takes up the free length.</summary>
    public static WidgetConfig Spacer() => New("mcd.spacer");

    /// <summary>The same widget, configured the same way, as a separate instance.</summary>
    public WidgetConfig AsNewInstance() => this with { InstanceId = Guid.NewGuid().ToString("n") };
}

public sealed record SensorSettings
{
    /// <summary>
    /// Tier 2 sources are off until someone turns them on: they depend on
    /// software this program neither ships nor installs.
    /// </summary>
    public ImmutableDictionary<string, bool> EnabledProviders { get; init; } =
        ImmutableDictionary<string, bool>.Empty;

    public ImmutableDictionary<string, string> SourceOverrides { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    public string LhmHttpEndpoint { get; init; } = "http://localhost:8085/data.json";

    /// <summary>Drives whose temperature IOCTL timed out twice; not asked again.</summary>
    public ImmutableArray<string> StorageBlacklist { get; init; } = [];
}

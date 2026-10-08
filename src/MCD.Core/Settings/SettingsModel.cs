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
    /// <summary>
    /// Whether the program also puts an icon in the notification area. Off
    /// unless asked for: the bars are the program's own controls.
    /// </summary>
    public bool TrayIcon { get; init; }

    /// <summary>
    /// Whether the program looks for a new version once a day by itself and
    /// puts it in. Off unless asked for: until then it contacts nobody.
    /// </summary>
    public bool AutoUpdate { get; init; }

    /// <summary>
    /// Whether a Windows notification says so when a part goes past the
    /// temperature its maker calls critical. Off unless asked for: the bar is
    /// a quiet thing, and its colours already say it.
    /// </summary>
    public bool HeatAlert { get; init; }

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

    /// <summary>
    /// How large the readings are drawn: "large", "medium" or "small".
    /// </summary>
    /// <remarks>
    /// Large is the size the bar has always used; small is the sixteen-pixel
    /// icon and twelve-point text of the PowerToys dock, for whoever wants a
    /// quieter bar; medium sits between them. Separate from a bar's density,
    /// which is about how much screen the bar itself takes.
    /// </remarks>
    public string Size { get; init; } = "large";

    /// <summary>
    /// The combinations the whole machine listens for, by what they do.
    /// </summary>
    /// <remarks>
    /// Written as text - "Ctrl+Alt+B" - rather than as codes, because that is
    /// what the settings file is for: somebody opening it should be able to
    /// read what they chose and change it. Anything missing is not bound.
    /// </remarks>
    public ImmutableDictionary<string, string> Keys { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Which icon each reading is drawn with, by the reading's stable name.
    /// Anything missing falls back to the widget's own default.
    /// </summary>
    public ImmutableDictionary<string, string> Icons { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// What the launcher widget started, before schema 6. Read by the migration
    /// that turns each pinned item into an icon widget on every dock, and never
    /// written to again.
    /// </summary>
    public ImmutableArray<LaunchItem> Launcher { get; init; } = [];

    /// <summary>
    /// Arrangements kept by name, to be put onto any bar.
    /// </summary>
    /// <remarks>
    /// A preset remembers how many slots the bar it was saved from had, so
    /// that loading it onto a narrower or wider bar can keep the shape by
    /// scaling the slot numbers rather than carrying them literally - the
    /// mistake the copy-to-other-screens button made first.
    /// </remarks>
    public ImmutableArray<BarPreset> Presets { get; init; } = DockContents.Starters;
}

/// <summary>One saved arrangement: what was on a bar and where it stood.</summary>
public sealed record BarPreset
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>How many slots the bar this was saved from had.</summary>
    public int Slots { get; init; }

    public ImmutableArray<WidgetConfig> Widgets { get; init; } = [];

    /// <summary>
    /// When this many screens are connected, the main screen's bar is given
    /// this layout by itself. Zero: never. The desk at work and the desk at
    /// home are told apart by how many screens are on them.
    /// </summary>
    public int Screens { get; init; }
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

    /// <summary>
    /// A drawn icon chosen from the library, by name. Empty means the file's
    /// own extracted icon, or its first letter when it has none.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

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

    /// <summary>
    /// Top, not bottom: a bar at the bottom of the main screen stands directly
    /// on Windows' own taskbar, two strips in a row.
    /// </summary>
    public AppBarEdge Edge { get; init; } = AppBarEdge.Top;

    public AppBarMode Mode { get; init; } = AppBarMode.Pinned;

    public DockDensity Density { get; init; } = DockDensity.Default;

    /// <summary>
    /// How large this bar draws its icons and figures - "small", "medium",
    /// "large" - or null for the program-wide size. Set together with the
    /// density by the one "size of the bar" choice on the Bars page.
    /// </summary>
    public string? Size { get; init; }

    /// <summary>Which end of the bar its contents are gathered at.</summary>
    public DockAnchor Anchor { get; init; } = DockAnchor.Start;

    /// <summary>
    /// Whether this bar stays above other windows. On by default; a full-screen
    /// program steps in front either way.
    /// </summary>
    public bool Topmost { get; init; } = true;

    /// <summary>
    /// Everything on the bar, each at the slot it was put in.
    /// </summary>
    /// <remarks>
    /// The bar is a row of slots, and a widget sits at one of them and takes as
    /// many as it needs. Where a thing is on the bar is a number, not a place in
    /// a list - so a gap is simply slots nobody has filled, and there is nothing
    /// invisible on the bar holding empty space open.
    /// </remarks>
    public ImmutableArray<WidgetConfig> Widgets { get; init; } = DockContents.Default;

    /// <summary>
    /// How many slots the bar had when these cells were written. Zero until
    /// the bar has said.
    /// </summary>
    /// <remarks>
    /// A slot number means nothing without the bar it was counted on. When
    /// the bar comes back with a different count - the density changed, the
    /// screen changed mode - the arrangement is refitted by the ratio of the
    /// two, instead of being settled literally and scattering: cells past the
    /// new end were being rescued one by one at the front of the bar, and the
    /// write-back then recorded the wreckage.
    /// </remarks>
    public int Slots { get; init; }

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
    /// The player, then a reading each for the processor, the memory, both
    /// directions of the network and the graphics chip, a temperature, and the
    /// two that are only about some machines. Every one is placed on the first
    /// free slots, in this order.
    /// </summary>
    /// <remarks>
    /// The battery and the Wi-Fi are in the standard bar on every machine and
    /// draw on none that they are not about: a desktop never shows a battery,
    /// and a laptop on a cable shows no aerial until the cable comes out. That
    /// is the point of putting them here rather than leaving them to be found -
    /// the thing a laptop most wants on its bar should not have to be looked
    /// for, and the same list has to be right on a tower as well.
    /// </remarks>
    public static ImmutableArray<WidgetConfig> Default =>
    [
        .. Documents(),
        .. new[]
        {
            WidgetConfig.New("mcd.media"),
            WidgetConfig.New("mcd.sound"),
            Gauge("cpu"),
            WidgetConfig.New("mcd.temp"),
            Gauge("ram"),
            Gauge("gpu"),
            WidgetConfig.New("mcd.battery"),
            WidgetConfig.New("mcd.wifi"),
            WidgetConfig.New("mcd.clock"),
            WidgetConfig.New("mcd.settings"),
        }.Select(w => w with { Cell = DockGrid.FromEnd }),
    ];

    /// <summary>
    /// A pinned link to the Documents folder, on a machine that has one.
    /// </summary>
    /// <remarks>
    /// On the default bar to show, not to be essential: a folder on the bar is
    /// the feature nobody would guess is there - a link to any folder or
    /// program, wearing any icon from the set - and the way to say so is to
    /// have one standing there when the program is first met. Taken off like
    /// any other widget.
    /// </remarks>
    private static IEnumerable<WidgetConfig> Documents()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (path.Length == 0 || !Directory.Exists(path))
        {
            yield break;
        }

        yield return WidgetConfig.New("mcd.icon") with
        {
            Config = WidgetJson.Object(
                ("target", path),
                ("name", Path.GetFileName(path)),
                ("icon", "Folder")),
        };
    }

    /// <summary>
    /// Three arrangements a person can put on a bar with one press before they
    /// have made one of their own. Kept as ordinary presets - they can be
    /// applied, renamed and forgotten like any other.
    /// </summary>
    public static ImmutableArray<BarPreset> Starters { get; } =
    [
        Starter("Minimal", [WidgetConfig.New("mcd.sound"), WidgetConfig.New("mcd.clock"), WidgetConfig.New("mcd.settings")]),
        Starter(
            "Monitoring",
            [
                Gauge("cpu"),
                WidgetConfig.New("mcd.temp"),
                Gauge("ram"),
                Gauge("gpu"),
                Gauge("down"),
                WidgetConfig.New("mcd.clock"),
                WidgetConfig.New("mcd.settings"),
            ]),
        Starter(
            "Work",
            [
                .. Documents(),
                WidgetConfig.New("mcd.media"),
                WidgetConfig.New("mcd.sound"),
                WidgetConfig.New("mcd.mic"),
                WidgetConfig.New("mcd.awake"),
                WidgetConfig.New("mcd.layout"),
                WidgetConfig.New("mcd.clock"),
                WidgetConfig.New("mcd.settings"),
            ]),
    ];

    private static BarPreset Starter(string name, IEnumerable<WidgetConfig> widgets) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Name = name,
        Widgets = [.. widgets],
    };

    /// <summary>One reading, as a widget of its own.</summary>
    public static WidgetConfig Gauge(string reading) =>
        WidgetConfig.New("mcd.gauge") with { Config = WidgetJson.Object(("reading", reading)) };
}

public sealed record WidgetConfig
{
    public string InstanceId { get; init; } = string.Empty;

    public string TypeId { get; init; } = string.Empty;

    /// <summary>
    /// How many slots this widget held when its cell was written. Zero until
    /// the bar has said.
    /// </summary>
    /// <remarks>
    /// Without it, two adjacent widgets and two widgets a gap apart look the
    /// same in the settings - just two numbers - and a change that shrinks
    /// every widget (a smaller reading size, a denser bar) turns adjacency
    /// into a row of little gaps nobody asked for. With the old span written
    /// down, the gap between neighbours is knowable, and it is the gap that
    /// is preserved.
    /// </remarks>
    public int Span { get; init; }

    /// <summary>
    /// Which slot of the bar this sits at, counted from the near end.
    /// </summary>
    /// <remarks>
    /// -1 means "wherever it lands": the bar puts it on the first free slots
    /// when it is built and writes the number back. Only the bar knows how many
    /// slots a screen has, so nothing upstream of it can decide this.
    /// </remarks>
    public int Cell { get; init; } = -1;

    /// <summary>Opaque to everything but the widget that owns it.</summary>
    public JsonElement? Config { get; init; }

    public static WidgetConfig New(string typeId) => new()
    {
        InstanceId = Guid.NewGuid().ToString("n"),
        TypeId = typeId,
    };

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

    /// <summary>
    /// What a person has decided to call a reading, by its sensor key.
    /// </summary>
    /// <remarks>
    /// A sensor's own name is whatever its maker wrote in the firmware -
    /// "Composite", "CPU Package", a drive's model number - and those are for
    /// people who already know what they are looking at. Anything named here
    /// is used everywhere the reading appears, the bar included.
    /// </remarks>
    public ImmutableDictionary<string, string> Names { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    public string LhmHttpEndpoint { get; init; } = "http://localhost:8085/data.json";

    /// <summary>Drives whose temperature IOCTL timed out twice; not asked again.</summary>
    public ImmutableArray<string> StorageBlacklist { get; init; } = [];
}

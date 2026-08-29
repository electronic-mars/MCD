using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Mcd.App.Dock;
using Mcd.App.Widgets;
using Mcd.Core.Infrastructure;
using Mcd.Core.Monitors;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Mcd.Sensors.Providers;
using Microsoft.Extensions.Logging;
using Mcd.Interop.AppBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Mcd.Interop.Display;
using Mcd.Interop.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.Win32.Foundation;

namespace Mcd.App.Settings;

/// <summary>
/// The program's only ordinary window.
/// </summary>
/// <remarks>
/// It opens from a right-click on an empty part of a dock, and from starting the
/// program again while it is already running. There is deliberately no icon in
/// the notification area: a program whose whole purpose is a bar of visible
/// controls should not hide its own controls behind a chevron in someone else's
/// bar.
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private readonly ILogger _log;
    private readonly SettingsService _settings;
    private readonly DockWindowManager _docks;
    private readonly SensorHub _sensors;
    private readonly Action _onExit;
    /// <summary>The widget the bar sent here to be set up.</summary>
    private string? _selectedId;
    private WidgetViewModel? _inspected;

    /// <summary>The dock being edited, by its monitor's stable id.</summary>
    private string? _editing;

    /// <summary>True while controls are being filled in, so their events mean nothing.</summary>
    private bool _filling;

    /// <summary>
    /// Layouts as they were before each change, most recent first.
    /// </summary>
    /// <remarks>
    /// There is no Save button and nothing to discard: what the settings say and
    /// what the bar does are never allowed to differ. This is what makes that
    /// safe - a mistake is one keystroke back rather than a form to abandon.
    /// </remarks>
    private readonly Stack<(string StableId, MonitorConfig Before, string Label)> _undo = new();
    private readonly ObservableCollection<IconRow> _icons = [];
    private readonly ObservableCollection<SensorRow> _readings = [];
    private readonly DispatcherQueueTimer _refresh;

    public SettingsWindow(
        ILogger log,
        SettingsService settings,
        DockWindowManager docks,
        SensorHub sensors,
        Action onExit)
    {

        _log = log;
        _settings = settings;
        _docks = docks;
        _sensors = sensors;
        _onExit = onExit;

        InitializeComponent();

        SystemBackdrop = null;

        // The Braun body, painted from code: a ThemeResource on the root
        // element cannot see the root's own dictionary during the parse.
        PaintBody();
        Root.ActualThemeChanged += (_, _) => PaintBody();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        SizeAndCentre(screen: null);

        IconList.ItemsSource = _icons;
        SensorList.ItemsSource = _readings;
        _version = string.Format(
            CultureInfo.CurrentCulture, Loc.Tr("VersionFormat", "Version {0}"), AppInfo.Version);


        // Only while the sensors page is on screen. A window sitting behind
        // everything else has no business waking the machine once a second.
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(1);
        _refresh.Tick += (_, _) => Tick();
        Closed += (_, _) => _refresh.Stop();

        Reload();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>
    /// Puts the window in the middle of a given screen, sized for that screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AppWindow works in physical pixels. A fixed number gives a window that is
    /// right at 100% and cramped at 125%, which is what the laptop this was
    /// built on runs at.
    /// </para>
    /// <para>
    /// The screen comes from whichever dock was right-clicked. Opening on the
    /// primary monitor when the click happened on the third one means the window
    /// appears somewhere the user is not looking.
    /// </para>
    /// </remarks>
    /// <summary>The narrowest this window is allowed to be, in effective pixels.</summary>
    /// <remarks>
    /// The navigation pane folds to icons below 980 and to a hamburger below
    /// 820; this is under both, so the pane is a single column of icons and
    /// the content still has room for a settings row and its explanation.
    /// </remarks>
    /// <remarks>
    /// The pane at its open width, the page's own padding either side, and
    /// enough left for a settings row to keep its words beside its control.
    /// Measured rather than guessed: below this the explanations start
    /// wrapping every other word, which the unattended check now looks for.
    /// </remarks>
    private const double MinimumWide = 760;

    private const double MinimumTall = 520;

    public void SizeAndCentre(MonitorInfo? screen)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        double scale = screen?.Scale ?? WindowFrame.GetScale(hwnd);
        RECT bounds = screen?.Bounds ?? Displays.BoundsFor(hwnd);

        var size = new Windows.Graphics.SizeInt32(
            (int)Math.Round(1000 * scale),
            (int)Math.Round(760 * scale));

        // A floor under the window, in the same physical pixels AppWindow
        // works in. Without one the pane keeps its width while the content
        // column is squeezed to nothing, and an explanation ends up set one
        // word to a line - which is not a thing to notice in a screenshot, it
        // is a thing the window should refuse to do.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWide * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumTall * scale);
        }

        AppWindow.Resize(size);

        AppWindow.Move(new Windows.Graphics.PointInt32(
            bounds.left + ((bounds.right - bounds.left) - size.Width) / 2,
            bounds.top + ((bounds.bottom - bounds.top) - size.Height) / 2));
    }

    /// <summary>
    /// Turns to the dock the request came from, and to the widget it named.
    /// </summary>
    /// <remarks>
    /// A widget is pointed at on the bar and set up here; without this the
    /// person is left to find it again in a window that opened on whatever it
    /// was last showing.
    /// </remarks>
    public void Show(MonitorInfo screen, string? widgetId)
    {
        _editing = _docks.Plans
            .FirstOrDefault(p => p.Monitor.Identity.Equals(screen.Identity))?.Config.StableId
            ?? _editing;

        _selectedId = widgetId;
        Nav.SelectedItem = Nav.MenuItems[0];
        ReloadDocks();
    }

    /// <summary>
    /// Opens one page by its tag, for the unattended visual check. A tag
    /// ending in "!" also scrolls to the foot of the page, so the parts below
    /// the window can be photographed too.
    /// </summary>
    public void GoTo(string tag)
    {
        bool foot = tag.EndsWith('!');
        string wanted = foot ? tag[..^1] : tag;

        foreach (object item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (item is not NavigationViewItem entry || (entry.Tag as string) != wanted)
            {
                continue;
            }

            Nav.SelectedItem = entry;

            // After the pages have been laid out, not before: the page just
            // switched to has no extent yet.
            DispatcherQueue.TryEnqueue(() => Pages.ChangeView(
                null, foot ? Pages.ScrollableHeight : 0, null, disableAnimation: true));

            return;
        }
    }

    /// <summary>
    /// Squeezes the window to the narrowest it allows and reports the
    /// narrowest wrapping text on each page.
    /// </summary>
    /// <remarks>
    /// For the unattended check. A settings page does not fail at a narrow
    /// width, it degrades: the pane keeps its column, the content gets what
    /// is left, and an explanation ends up set one word to a line. Nothing
    /// throws, no test notices, and it is obvious the moment anybody looks.
    /// So the window is made as small as it is allowed to be and the text is
    /// measured - a wrapping line under a hundred points wide is a page that
    /// has collapsed.
    /// </remarks>
    /// <summary>
    /// Makes the window as small as it is allowed to be.
    /// </summary>
    /// <remarks>
    /// Resizing is a request, not a change: the window is laid out again on a
    /// later turn of the message loop. Measuring in the same breath measures
    /// the size it used to be, which is how this check first came back with
    /// comfortable numbers from a window that had not moved.
    /// </remarks>
    public void SqueezeBegin()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = WindowFrame.GetScale(hwnd);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(MinimumWide * scale), (int)Math.Round(MinimumTall * scale)));
    }

    /// <summary>Reports the narrowest sentence on each page, as it stands now.</summary>
    public double Squeeze()
    {
        double narrowest = double.MaxValue;

        foreach (object item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (item is not NavigationViewItem entry || entry.Tag is not string tag)
            {
                continue;
            }

            GoTo(tag);
            Pages.UpdateLayout();

            (double width, string text) = Narrowest(Pages);

            // The offending line is named. A number alone says a page is
            // wrong and leaves whoever reads it to go looking; the first
            // words of the text that got squeezed say where.
            _log.LogInformation(
                "selftest.squeezed page={Page} narrowest={Narrowest} window={Window} text={Text}",
                tag,
                Math.Round(width),
                Math.Round(Root.ActualWidth),
                text.Length > 40 ? text[..40] : text);

            narrowest = Math.Min(narrowest, width);
        }

        return narrowest;
    }

    /// <summary>
    /// The narrowest wrapping line of text anywhere under this element.
    /// </summary>
    /// <remarks>
    /// Only wrapping text, and only a sentence of it. A name like "The bar
    /// itself" is legitimately as wide as its own words and no wider, and
    /// counting it finds a narrow thing that is not a fault. What is being
    /// looked for is a paragraph squeezed into a column of single words, and
    /// a paragraph is long.
    /// </remarks>
    private static (double Width, string Text) Narrowest(DependencyObject root)
    {
        (double Width, string Text) narrowest = (double.MaxValue, string.Empty);

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            // A page that is not showing keeps whatever width it was last
            // arranged at, which is nobody's business: all five pages live
            // in the same panel, so measuring the whole subtree measures the
            // four that are hidden as well.
            if (child is UIElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }

            if (child is TextBlock { TextWrapping: not TextWrapping.NoWrap } text
                && text.Text.Length > 40
                && text.Visibility == Visibility.Visible
                && text.ActualWidth > 0
                && text.ActualWidth < narrowest.Width)
            {
                narrowest = (text.ActualWidth, text.Text);
            }

            (double Width, string Text) below = Narrowest(child);

            if (below.Width < narrowest.Width)
            {
                narrowest = below;
            }
        }

        return narrowest;
    }

    /// <summary>Rebuilds both lists from the settings and the live topology.</summary>
    public void Reload()
    {
        _icons.Clear();

        AppSettings look = _settings.Current.App;

        _filling = true;
        _filling = false;

        Root.RequestedTheme = Appearance.Of(look.Theme);
        RefreshLook();

        foreach ((string id, string label, string fallback) in IconChoices.Known)
        {
            _icons.Add(new IconRow(id, label, Chosen(id, fallback), OnIconChosen));
        }

        ReloadDocks();
    }

    /// <summary>
    /// The appearance page, built the way Master Audio Switcher builds its
    /// settings tab: a heading, then one panel whose rows are divided by
    /// hairlines, each row a name, an explanation and the keys that act.
    /// </summary>
    /// <remarks>
    /// Built in code rather than declared. Three of these choices are three,
    /// five and two keys wide, and a row that puts a control of unknown width
    /// in an <c>Auto</c> column beside a starred one starves the explanation:
    /// the text ends up a column of single letters, which is exactly what
    /// this page did.
    /// </remarks>
    private void RefreshLook()
    {
        AppSettings look = _settings.Current.App;
        Braun.Theme = Root.ActualTheme;

        LookBody.Children.Clear();

        LookBody.Children.Add(Braun.Heading("Brush", Loc.Tr("LookGroupBar", "The bar")));

        string[] backdrops = ["acrylic", "solid", "colour", "image", "braun"];

        LookBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("ThemeLabel", "Theme"),
                Loc.Tr("ThemeHint", "Light or dark, or the same as Windows."),
                Braun.Segs(
                    [
                        Loc.Tr("SegThemeSystem", "Match Windows"),
                        Loc.Tr("SegThemeLight", "Light"),
                        Loc.Tr("SegThemeDark", "Dark"),
                    ],
                    Appearance.Index(look.Theme),
                    i => ApplyLook(theme: Appearance.FromIndex(i)),
                    wide: true),
                stack: true),

            Braun.Row(
                Loc.Tr("BackgroundLabel", "Background"),
                Loc.Tr(
                    "BackgroundHint",
                    "Translucent lets the desktop through; solid is easier to read over a busy wallpaper and costs a little less to draw."),
                Braun.Segs(
                    [
                        Loc.Tr("SegBackdropTranslucent", "Translucent"),
                        Loc.Tr("SegBackdropSolid", "Solid"),
                        Loc.Tr("SegBackdropColour", "A colour"),
                        Loc.Tr("SegBackdropImage", "A picture"),
                        Loc.Tr("SegBackdropBraun", "Braun"),
                    ],
                    Math.Max(0, Array.IndexOf(backdrops, look.Backdrop)),
                    i => ApplyLook(backdrop: backdrops[i]),
                    wide: true),
                stack: true),

            BackdropExtraRow(look),

            Braun.Row(
                Loc.Tr("AccentLabel", "Colour of the readings"),
                Loc.Tr(
                    "AccentHint",
                    "The accent colour comes from your Windows settings. Readings past their warning level keep their warning colour either way."),
                Braun.Segs(
                    [
                        Loc.Tr("SegAccentNeutral", "Plain text"),
                        Loc.Tr("SegAccentWindows", "Windows accent"),
                    ],
                    look.Accent == "windows" ? 1 : 0,
                    i => ApplyLook(accent: i == 1 ? "windows" : "neutral"),
                    wide: true),
                stack: true)));

        LookBody.Children.Add(Braun.Heading("Globe", Loc.Tr("LookGroupLanguage", "Language")));

        string[] languages = ["system", "en-US", "ru-RU"];

        LookBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("LanguageLabel", "Language"),
                Loc.Tr("LanguageHint", "Takes effect the next time the program starts."),
                Braun.Segs(
                    [Loc.Tr("LanguageSystemItem", "Same as Windows"), "English", "Русский"],
                    Math.Max(0, Array.IndexOf(languages, look.Language)),
                    i => PickLanguage(languages[i]),
                    wide: true),
                stack: true),

            // Offered only once a language has actually been chosen: people
            // reasonably think closing this window is a restart.
            _restartOffered
                ? Braun.Row(
                    Loc.Tr("RestartRow", "The language has changed"),
                    Loc.Tr("RestartRowHint", "The bars will read in the new language once the program starts again."),
                    Braun.Action(Loc.Tr("RestartNowText", "Restart now"), Restart, "Undo"))
                : null));
    }

    /// <summary>
    /// The row under Background that says which colour or which picture -
    /// and nothing at all for the backgrounds that need neither.
    /// </summary>
    private FrameworkElement? BackdropExtraRow(AppSettings app)
    {
        if (app.Backdrop is not ("colour" or "image"))
        {
            return null;
        }

        bool colour = app.Backdrop == "colour";

        var shown = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (colour)
        {
            shown.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = Braun.LineHi,
                Background = new SolidColorBrush(Swatch(app.BackdropColour)),
            });
        }

        shown.Children.Add(new TextBlock
        {
            Text = colour
                ? app.BackdropColour
                : app.BackdropImage.Length > 0
                    ? Path.GetFileName(app.BackdropImage)
                    : Loc.Tr("NoPictureYet", "No picture chosen yet"),
            FontSize = 12,
            Foreground = Braun.Tx2,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        shown.Children.Add(Braun.Action(
            colour ? Loc.Tr("PickColour", "Choose a colour") : Loc.Tr("PickPicture", "Choose a picture"),
            () => _ = PickBackdrop(),
            "Pen"));

        return Braun.Row(
            colour ? Loc.Tr("ColourRow", "The colour") : Loc.Tr("PictureRow", "The picture"),
            null,
            shown);
    }

    /// <summary>Writes the chosen language down and offers the restart it needs.</summary>
    private void PickLanguage(string language)
    {
        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with { App = current.App with { Language = language } },
            WriteReason.UserAction);

        _log.LogInformation("settings.language {Language}", language);
        _restartOffered = true;
        RefreshLook();
    }

    /// <summary>True once the language has been changed in this sitting.</summary>
    private bool _restartOffered;

    /// <summary>The one writer of the appearance settings.</summary>
    private void ApplyLook(string? theme = null, string? backdrop = null, string? accent = null)
    {
        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                App = current.App with
                {
                    Theme = theme ?? current.App.Theme,
                    Backdrop = backdrop ?? current.App.Backdrop,
                    Accent = accent ?? current.App.Accent,
                },
            },
            WriteReason.UserAction);

        // The window this is being set from follows it too, or the person is
        // choosing a theme while looking at the old one.
        Root.RequestedTheme = Appearance.Of(_settings.Current.App.Theme);
        RefreshLook();

        _log.LogInformation(
            "settings.appearance theme={Theme} backdrop={Backdrop} accent={Accent}",
            _settings.Current.App.Theme,
            _settings.Current.App.Backdrop,
            _settings.Current.App.Accent);
    }

    /// <summary>Rebuilds the display switcher and shows whichever dock was chosen.</summary>
    private void ReloadDocks()
    {
        ImmutableArray<MonitorConfig> monitors = _settings.Current.Monitors;
        Dictionary<string, MonitorInfo> live = _docks.Plans
            .ToDictionary(p => p.Config.StableId, p => p.Monitor);

        int chosen = Math.Max(0, monitors.ToList().FindIndex(m => m.StableId == _editing));
        _editing = monitors.Length > 0 ? monitors[chosen].StableId : null;

        var tabs = new List<(string Glyph, string Label, bool On)>();

        for (int i = 0; i < monitors.Length; i++)
        {
            // A lamp says whether that screen has a bar of its own, so the
            // tile stays the width of the screen's name.
            tabs.Add(("Computer", Label(monitors[i], monitors, live, i), monitors[i].Enabled));
        }

        DisplayTabs.Content = Braun.Tabs(tabs, chosen, i =>
        {
            _editing = monitors[i].StableId;
            ReloadDocks();
        });

        ShowDock();
    }

    /// <summary>
    /// The number Windows itself shows for the screen: "Display 2", the same
    /// digit as in its own display settings.
    /// </summary>
    /// <remarks>
    /// Not the monitor's EDID name. "RTK 2555" is the model number of the
    /// panel's controller - it tells two identical screens apart and nothing
    /// else, and nobody thinks of their monitor by it.
    /// </remarks>
    private static string Label(
        MonitorConfig config,
        ImmutableArray<MonitorConfig> all,
        Dictionary<string, MonitorInfo> live,
        int index)
    {
        if (live.TryGetValue(config.StableId, out MonitorInfo? screen)
            && new string([.. screen.Identity.GdiName.Where(char.IsDigit)]) is { Length: > 0 } digits)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("DisplayNumber", "Display {0}"),
                int.Parse(digits, CultureInfo.InvariantCulture));
        }

        // An unplugged screen has no Windows number; its model name is all
        // that is left to tell it by.
        string name = config.FriendlyName.Length > 0
            ? config.FriendlyName
            : Loc.Tr("ScreenFallback", "Screen");

        if (all.Count(m => m.FriendlyName == config.FriendlyName) < 2)
        {
            return name;
        }

        string tail = live.TryGetValue(config.StableId, out MonitorInfo? monitor)
            ? new string([.. monitor.Identity.GdiName.Where(char.IsDigit)])
            : (index + 1).ToString(CultureInfo.InvariantCulture);

        return $"{name} #{tail}";
    }

    /// <summary>Builds the whole Docks page from the chosen dock.</summary>
    /// <remarks>
    /// In the order somebody asks the questions in: which screen, where the
    /// bar sits on it, what is on the bar, how it behaves. Each of those is
    /// one panel of hairline-divided rows, so the controls line themselves up
    /// down the right-hand edge with no fixed width anywhere.
    /// </remarks>
    private void ShowDock()
    {
        Braun.Theme = Root.ActualTheme;
        DockBody.Children.Clear();

        MonitorConfig? dock = _settings.Current.Monitors
            .FirstOrDefault(m => m.StableId == _editing);

        DocksSection.Opacity = dock is null ? 0.5 : 1;

        if (dock is null)
        {
            DockBody.Children.Add(Braun.Group(Braun.Row(
                Loc.Tr("DockNoScreen", "No screen has been set up yet."), null, null)));

            return;
        }

        MonitorInfo? live = _docks.Plans
            .FirstOrDefault(p => p.Config.StableId == dock.StableId)?.Monitor;

        _filling = true;

        // ---------------------------------------------------------- the screen
        DockBody.Children.Add(Braun.Heading("Computer", Loc.Tr("DockGroupScreen", "This screen")));

        DockBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("ShowDockLabel", "Show a dock on this display"),
            live is not null
                ? $"{live.Width} x {live.Height} · {live.Dpi * 100 / 96}%"
                : Loc.Tr("DockNotAttached", "not attached · its layout is kept and comes back with the screen"),
            Braun.Switch(
                dock.Enabled,
                on => SetDock(d => d with { Enabled = on }, Loc.Tr("UndoShown", "shown"))))));

        // Everything the master switch governs dims with it.
        var gated = new StackPanel
        {
            Opacity = dock.Enabled ? 1 : 0.35,
            IsHitTestVisible = dock.Enabled,
        };

        DockBody.Children.Add(gated);

        // ------------------------------------------------------------- placing
        bool horizontal = DockMetrics.IsHorizontal(dock.Edge);

        gated.Children.Add(Braun.Heading("Layout", Loc.Tr("PlacementTitle", "Placement")));

        gated.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("EdgeLabel", "Edge"),
                Loc.Tr("EdgeHint", "Which side of the screen the bar sits on."),
                Braun.Segs(
                    [
                        Loc.Tr("SegEdgeLeft", "Left"),
                        Loc.Tr("SegEdgeTop", "Top"),
                        Loc.Tr("SegEdgeRight", "Right"),
                        Loc.Tr("SegEdgeBottom", "Bottom"),
                    ],
                    (int)dock.Edge,
                    i => SetDock(d => d with { Edge = (AppBarEdge)i }, Loc.Tr("UndoEdge", "edge")),
                    wide: true),
                stack: true),

            // A bar down the side of the screen has one thickness. The row is
            // replaced by its explanation rather than offered greyed and mute.
            horizontal
                ? Braun.Row(
                    Loc.Tr("SizeLabel", "Thickness"),
                    Loc.Tr("SizeHint", "How much room the bar takes up."),
                    Braun.Segs(
                        [Loc.Tr("SegThickDefault", "Default"), Loc.Tr("SegThickCompact", "Compact")],
                        dock.Density == DockDensity.Compact ? 1 : 0,
                        i => SetDock(
                            d => d with { Density = i == 1 ? DockDensity.Compact : DockDensity.Default },
                            Loc.Tr("UndoThickness", "thickness")),
                        wide: true),
                    stack: true)
                : Braun.Row(
                    Loc.Tr("SizeLabel", "Thickness"),
                    Loc.Tr("ThicknessNote", "A bar down the side of the screen has one thickness."),
                    null)));

        // ------------------------------------------------------------ contents
        gated.Children.Add(Braun.Heading("List", Loc.Tr("GalleryTitle", "Widgets")));

        gated.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("BarLiveTitle", "The bar itself"),
                Loc.Tr(
                    "BarHelp",
                    "The bar is a row of slots. Drag a widget along it to move it between free slots, or off it to take it away. Right-click a slot to add a widget there, or a widget for its own settings."),
                Undo()),

            Braun.Row(
                Loc.Tr("GalleryRow", "Put one on the bar"),
                Loc.Tr(
                    "GalleryRowHint",
                    "A press adds it to the first free slot. There is no limit: two of the same reading in different places is an ordinary thing to want."),
                Gallery(dock),
                stack: true),

            Braun.Row(
                Loc.Tr("PinRow", "A program of your own"),
                Loc.Tr(
                    "PinRowHint",
                    "Pinned programs sit on the bar as icons. A file dropped straight onto the bar is pinned to the slot it lands on."),
                Braun.Action(
                    Loc.Tr("PinProgramButton", "Pin a program..."), () => _ = PinDialog(), "Plus")),

            Braun.Row(
                Loc.Tr("ResetRow", "The standard set"),
                Loc.Tr(
                    "ResetRowHint",
                    "Puts this bar back to what it holds on a new installation: the player, the processor, the memory, both directions of the network, the graphics chip and a temperature. Undo brings your own arrangement back."),
                Braun.Action(Loc.Tr("ResetButton", "Restore the standard bar"), ResetDock, "Undo"))));

        // ------------------------------------------------------- chosen widget
        if (Inspector() is { } inspector)
        {
            gated.Children.Add(Braun.Heading("Sliders", _inspectorName));
            gated.Children.Add(inspector);
        }

        // ----------------------------------------------------------- behaviour
        gated.Children.Add(Braun.Heading("Gear", Loc.Tr("BehaviourTitle", "Behaviour")));

        bool clash = dock.Mode == AppBarMode.AutoHide && AppBarHost.TaskbarAutoHidesOn(dock.Edge);

        AppBarMode[] modes = [AppBarMode.Pinned, AppBarMode.AutoHide, AppBarMode.Desktop];

        gated.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("ModeLabel", "How the bar holds its edge"),
                dock.Mode switch
                {
                    AppBarMode.AutoHide => clash
                        ? Loc.Tr("HideNote", "The taskbar already hides on this edge, so this dock stays visible.")
                        : Loc.Tr("ModeHideHint", "The bar steps off the screen and comes back when the pointer reaches that edge."),
                    AppBarMode.Desktop => Loc.Tr(
                        "ModeDesktopHint",
                        "The bar lies on the desktop: it takes no room from other windows, and any window opened over it covers it."),
                    _ => Loc.Tr(
                        "ModePinnedHint",
                        "The bar keeps its strip of screen. A maximised window stops at it rather than covering it."),
                },
                Braun.Segs(
                    [
                        Loc.Tr("ModePinned", "Keeps its place"),
                        Loc.Tr("ModeHide", "Hides"),
                        Loc.Tr("ModeDesktop", "On the desktop"),
                    ],
                    Math.Max(0, Array.IndexOf(modes, dock.Mode)),
                    i => SetDock(d => d with { Mode = modes[i] }, Loc.Tr("UndoMode", "how it holds its edge")),
                    wide: true),
                stack: true),

            // Not offered on the desktop, where it would contradict the mode
            // rather than qualify it. A setting that cannot act is not shown.
            dock.Mode == AppBarMode.Desktop
                ? null
                : Braun.Row(
                    Loc.Tr("TopmostLabel", "Keep above other windows"),
                    Loc.Tr("TopmostHint", "Off lets a maximised window cover the bar."),
                    Braun.Switch(
                        dock.Topmost,
                        on => SetDock(d => d with { Topmost = on }, Loc.Tr("UndoTopmost", "topmost"))))));

        _filling = false;
    }

    /// <summary>Changes one thing about the dock being edited, and redraws.</summary>
    private void SetDock(Func<MonitorConfig, MonitorConfig> change, string what)
    {
        if (_filling)
        {
            return;
        }

        EditDock(change, what);
        ShowDock();
    }

    /// <summary>
    /// Puts the bar back to what a new installation gives it.
    /// </summary>
    /// <remarks>
    /// Every widget is made afresh, so nothing carries over from the
    /// arrangement being replaced. It goes on the undo stack like any other
    /// change - this is the largest edit the page offers, and a large edit is
    /// exactly the one people want back.
    /// </remarks>
    private void ResetDock()
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        _selectedId = null;

        Rearrange(
            dock.StableId,
            _ => DockContents.Default,
            Loc.Tr("UndoReset", "the standard bar"));
    }

    /// <summary>
    /// The undo button now on the page, so its label can be changed without
    /// building the page again.
    /// </summary>
    /// <remarks>
    /// A reference to something already in the tree, never something to put
    /// into a new one. Everything this page draws is built fresh with the
    /// page: an element that outlives a rebuild has to be taken off its old
    /// parent before it can be given a new one, and that is a rule easy to
    /// keep and easy to forget - forgetting it once ended the program on
    /// every right-click.
    /// </remarks>
    private Button? _undoButton;

    /// <summary>What the chosen widget is called, for the heading above it.</summary>
    private string _inspectorName = string.Empty;

    /// <summary>
    /// One chip per thing that can go on the bar: its icon, its name, and how
    /// many of it the chosen dock already has.
    /// </summary>
    /// <remarks>
    /// A press puts one on the first free slot of that dock's bar - the bar
    /// itself decides which, because only it knows how many slots the screen
    /// has. Nothing is ever refused here: a second temperature, a third copy
    /// of a reading somewhere else along the bar, are all ordinary things to
    /// want. Taking one off is done on the bar, by dragging it off or by its
    /// own right-click menu.
    /// </remarks>
    private VariableSizedWrapGrid Gallery(MonitorConfig dock)
    {
        var gallery = new VariableSizedWrapGrid
        {
            ItemHeight = 40,
            ItemWidth = 204,
            Orientation = Orientation.Horizontal,
        };

        foreach (WidgetOffer offer in WidgetCatalog.Offers(_sensors))
        {
            int already = dock.Widgets.Count(offer.Matches);

            var shape = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = IconRow.Draw(offer.Icon),
                Stroke = Braun.Tx,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(shape);

            var row = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = offer.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Grid.SetColumn(name, 1);

            // How many are on this bar already, rather than a tick that only
            // says "yes": two of a thing is allowed, and the number is the
            // only honest way to show it.
            var count = new TextBlock
            {
                Text = already > 0 ? already.ToString(CultureInfo.CurrentCulture) : string.Empty,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Braun.Acc,
            };

            Grid.SetColumn(count, 2);
            row.Children.Add(new Viewbox { Width = 16, Height = 16, Child = canvas });
            row.Children.Add(name);
            row.Children.Add(count);

            var chip = new Button
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 6, 10, 6),
                MinWidth = 196,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Braun.Line,
                Background = Braun.Card,
                Content = row,
            };

            ToolTipService.SetToolTip(chip, offer.Description);

            WidgetOffer chosen = offer;

            chip.Click += (_, _) => Rearrange(
                dock.StableId,
                widgets => [.. widgets, chosen.Make()],
                string.Format(
                    CultureInfo.CurrentCulture, Loc.Tr("UndoAdded", "added {0}"), chosen.Name));

            gallery.Children.Add(chip);
        }

        return gallery;
    }

    /// <summary>
    /// The chosen widget's own options, or nothing at all.
    /// </summary>
    /// <remarks>
    /// Nothing at all is the point: an empty panel headed "WIDGET" beside a
    /// line explaining that no widget is chosen is a room with nothing in it.
    /// A widget is chosen by right-clicking it on the bar itself, and until
    /// somebody does that this part of the page does not exist.
    /// </remarks>
    private FrameworkElement? Inspector()
    {
        WidgetConfig? entry = Dock()?.Widgets.FirstOrDefault(w => w.InstanceId == _selectedId);

        _inspected?.Dispose();
        _inspected = null;

        if (entry is null)
        {
            _selectedId = null;
            _inspectorName = string.Empty;
            return null;
        }

        WidgetType? type = WidgetCatalog.Find(entry.TypeId);

        _inspectorName = type?.Name ?? entry.TypeId;
        _inspected = Build(entry);

        string id = entry.InstanceId;

        FrameworkElement? editor = _inspected?.CreateEditor(
            options => OnInspectorConfigured(id, options));

        return Braun.Group(
            editor is null
                ? Braun.Row(
                    Loc.Tr("NothingToSetUp", "This widget has nothing to set up."), null, null)
                : Braun.Row(
                    Loc.Tr("WidgetOptions", "Its own settings"), null, editor, stack: true),

            Braun.Row(
                Loc.Tr("RemoveRow", "Take it off the bar"),
                Loc.Tr("RemoveRowHint", "The same as dragging it off the bar onto the desktop."),
                Braun.Action(
                    Loc.Tr("RemoveFromBar", "Remove from bar"),
                    RemoveSelected,
                    "Delete",
                    danger: true)));
    }

    private void OnInspectorConfigured(string id, System.Text.Json.JsonElement? options)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        Rearrange(
            dock.StableId,
            widgets => [.. widgets.Select(w => w.InstanceId == id ? w with { Config = options } : w)],
            Loc.Tr("UndoOptions", "widget options"),
            rebuild: false);
    }

    /// <summary>Takes the chosen widget off the bar.</summary>
    private void RemoveSelected()
    {
        if (_selectedId is not { } id || Dock() is not { } dock)
        {
            return;
        }

        Rearrange(
            dock.StableId,
            widgets => [.. widgets.Where(w => w.InstanceId != id)],
            Loc.Tr("UndoRemovedOne", "removed"));
    }

    /// <summary>One round of updating whatever this window is showing.</summary>
    private void Tick()
    {
        if (SensorsSection.Visibility == Visibility.Visible)
        {
            ShowReadings();
        }
    }

    /// <summary>
    /// The undo button, always there and only sometimes able to act.
    /// </summary>
    /// <remarks>
    /// Enabled rather than shown: a control that appears after the first
    /// mistake is invisible exactly while a person is working out what is
    /// safe to try.
    /// </remarks>
    private Button Undo()
    {
        _undoButton = Braun.Action(UndoLabel(), DoUndo, "Undo");
        _undoButton.IsEnabled = _undo.Count > 0;

        return _undoButton;
    }

    /// <summary>
    /// Says on the button what undoing would put back, without building the
    /// page again.
    /// </summary>
    /// <remarks>
    /// The one thing on this page that changes while the rest of it has to
    /// stand still: a widget's own options are saved as they are typed, and
    /// rebuilding the page under somebody typing takes the field away
    /// mid-word.
    /// </remarks>
    private void ShowUndo()
    {
        if (_undoButton is not { } button)
        {
            return;
        }

        button.Content = Braun.Legend(UndoLabel(), "Undo", Braun.Tx2);
        button.IsEnabled = _undo.Count > 0;
    }

    private string UndoLabel() =>
        _undo.Count > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("UndoWithLabel", "Undo - {0}"),
                _undo.Peek().Label)
            : Loc.Tr("Undo", "Undo");

    /// <summary>Puts the last layout back.</summary>
    private void DoUndo()
    {
        if (!_undo.TryPop(out (string StableId, MonitorConfig Before, string Label) step))
        {
            return;
        }

        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(
                        c => c.StableId == step.StableId ? step.Before : c)
                ],
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.dock undone {What} monitor={Monitor}", step.Label, step.StableId);

        // The dock that was changed is not always the one on screen: undo after
        // switching screens has to take you back to what you changed.
        _editing = step.StableId;
        ReloadDocks();
    }

    private WidgetViewModel? Build(WidgetConfig entry) => WidgetCatalog.Create(
        new WidgetContext(
            _sensors,
            new IconChoices(_settings.Current.App.Icons),
            new SensorNames(_settings.Current.Sensors.Names),
            _log),
        entry);

    /// <summary>
    /// Starts the program again, for the change that only takes at a start.
    /// </summary>
    /// <remarks>
    /// Through a shell one-liner that waits a beat: a copy started while this
    /// one still holds the single-instance mutex would only signal it and
    /// leave. Not pretty, but honest about what a restart is.
    /// </remarks>
    /// <summary>Graphite body, or cream on the light theme - the Braun ground.</summary>
    private void PaintBody()
    {
        // The tiles are built in code and cannot reach this window's own
        // dictionary, so they are told which way round the palette goes.
        Braun.Theme = Root.ActualTheme;
        PaintGround();
    }

    private void PaintGround() =>
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 0xED, 0xEA, 0xE3)
            : Windows.UI.Color.FromArgb(255, 0x1C, 0x1F, 0x24));

    private void Restart()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        _log.LogInformation("settings.restart requested");
        _onExit();
    }

    private static Windows.UI.Color Swatch(string text)
    {
        string hex = text.TrimStart('#');

        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint argb)
            ? Windows.UI.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : Windows.UI.Color.FromArgb(255, 32, 32, 32);
    }

    /// <summary>
    /// A hand of swatches and one slider, or the file picker - whichever the
    /// chosen background needs.
    /// </summary>
    private async Task PickBackdrop()
    {
        AppSettings app = _settings.Current.App;

        if (app.Backdrop == "colour")
        {
            // A hand of swatches and one slider, the way Master Audio Switcher
            // offers colours - not a colour laboratory. The slider is how
            // see-through the bar is; the swatch is its hue.
            Windows.UI.Color current0 = Swatch(app.BackdropColour);

            string[] swatches =
            [
                "#1C1F24", "#23272D", "#101014", "#2D3748", "#1E3A5F", "#14484F",
                "#1F3D2B", "#4A1E2A", "#322450", "#F26A21", "#EDEAE3", "#F7F5F1",
            ];

            var grid = new Microsoft.UI.Xaml.Controls.VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 6,
                ItemWidth = 36,
                ItemHeight = 36,
            };

            var slider = new Slider
            {
                Header = Loc.Tr("SeeThroughHeader", "How see-through"),
                Minimum = 5,
                Maximum = 100,
                StepFrequency = 5,
                Value = Math.Round(current0.A / 255.0 * 100),
            };

            Windows.UI.Color hue = current0;

            void Save()
            {
                byte a = (byte)Math.Round(slider.Value / 100 * 255);
                string chosen = $"#{a:X2}{hue.R:X2}{hue.G:X2}{hue.B:X2}";

                SettingsModel now = _settings.Current;
                _settings.Commit(
                    now with { App = now.App with { BackdropColour = chosen } },
                    WriteReason.UserAction);

                _log.LogInformation("settings.backdrop colour={Colour}", chosen);
                RefreshLook();
            }

            foreach (string hex in swatches)
            {
                Windows.UI.Color c = Swatch(hex);

                var chip = new Button
                {
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Braun.Line,
                    Background = new SolidColorBrush(c),
                };

                chip.Click += (_, _) => { hue = c; Save(); };
                grid.Children.Add(chip);
            }

            slider.ValueChanged += (_, _) => Save();

            var flyout = new Flyout
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 220,
                    Children = { grid, slider },
                },
            };

            // Anchored on the page rather than on the button: the row that
            // holds it is rebuilt whenever the colour changes, so the button
            // this was opened from does not outlive the first swatch clicked.
            flyout.ShowAt(LookBody);
            return;
        }

        try
        {
            var open = new Windows.Storage.Pickers.FileOpenPicker();

            WinRT.Interop.InitializeWithWindow.Initialize(
                open, WinRT.Interop.WindowNative.GetWindowHandle(this));

            open.FileTypeFilter.Add(".png");
            open.FileTypeFilter.Add(".jpg");
            open.FileTypeFilter.Add(".jpeg");
            open.FileTypeFilter.Add(".bmp");
            open.FileTypeFilter.Add(".webp");

            if (await open.PickSingleFileAsync() is not { } file)
            {
                return;
            }

            SettingsModel current = _settings.Current;
            _settings.Commit(
                current with { App = current.App with { BackdropImage = file.Path } },
                WriteReason.UserAction);

            _log.LogInformation("settings.backdrop image={Image}", file.Path);
            RefreshLook();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings.backdrop could not open the file picker");
        }
    }

    /// <summary>The one place a dock's layout is written.</summary>
    private void Rearrange(
        string stableId,
        Func<ImmutableArray<WidgetConfig>, ImmutableArray<WidgetConfig>> change,
        string what,
        bool rebuild = true)
    {
        SettingsModel current = _settings.Current;

        if (current.Monitors.FirstOrDefault(m => m.StableId == stableId) is { } before)
        {
            _undo.Push((stableId, before, what));

            while (_undo.Count > 25)
            {
                // A stack that grows without limit is a stack holding every
                // layout of the session in memory for a button nobody will press
                // twenty-six times.
                _undo.TrimExcess();
                break;
            }
        }

        _settings.Commit(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(
                        c => c.StableId == stableId ? c with { Widgets = change(c.Widgets) } : c)
                ],
            },
            WriteReason.WidgetConfig);

        _log.LogInformation("settings.dock {What} monitor={Monitor}", what, stableId);

        if (rebuild)
        {
            ShowDock();
        }
        else
        {
            // A widget's own options changed: the bar redraws itself, and the
            // page must not rebuild the editor the person is typing into.
            ShowUndo();
        }
    }

    private void EditDock(Func<MonitorConfig, MonitorConfig> change, string what)
    {
        if (_editing is not { } stableId)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        // Instant means undoable - every change this page applies at once is
        // reachable by the same Undo, not only the widget layout.
        if (current.Monitors.FirstOrDefault(m => m.StableId == stableId) is { } before)
        {
            _undo.Push((stableId, before, what));

            while (_undo.Count > 25)
            {
                _undo.TrimExcess();
                break;
            }
        }

        _settings.Commit(
            current with
            {
                Monitors = [.. current.Monitors.Select(c => c.StableId == stableId ? change(c) : c)],
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.dock {What} monitor={Monitor}", what, stableId);
        ShowUndo();
    }

    private MonitorConfig? Dock() =>
        _settings.Current.Monitors.FirstOrDefault(m => m.StableId == _editing);

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "docks";

        DocksSection.Visibility = Show(tag == "docks");
        AppearanceSection.Visibility = Show(tag == "appearance");
        IconsSection.Visibility = Show(tag == "icons");
        SensorsSection.Visibility = Show(tag == "sensors");
        AboutSection.Visibility = Show(tag == "about");

        if (tag == "about")
        {
            ShowAbout();
        }

        // Only while the page that shows live figures is on screen. A window
        // sitting behind everything else has no business waking the machine
        // once a second.
        if (tag is "sensors")
        {
            Tick();
            _refresh.Start();
        }
        else
        {
            _refresh.Stop();
        }
    }

    /// <summary>Fills the sensors page from whatever the hub has right now.</summary>
    private void ShowReadings()
    {
        SensorDescriptor[] found =
        [
            .. _sensors.Catalog
                .OrderBy(d => d.Group)
                .ThenBy(d => d.Kind)
                .ThenBy(d => d.Label, StringComparer.CurrentCulture)
        ];

        // Rebuilt only when the set itself changes. Sources appear and vanish
        // while this page is open - that is rather the point of it - but
        // replacing every row once a second would fight with the scroll bar.
        if (!found.Select(d => d.Key).SequenceEqual(_readings.Select(r => r.Key)))
        {
            _readings.Clear();

            var names = new SensorNames(_settings.Current.Sensors.Names);

            foreach (SensorDescriptor sensor in found)
            {
                _readings.Add(new SensorRow(sensor, names, OnSensorRenamed));
            }
        }

        SensorSnapshot snapshot = _sensors.Current;

        foreach (SensorRow row in _readings)
        {
            row.Update(snapshot);
        }

        SensorSummary.Text = found.Length == 0
            ? Loc.Tr("SensorsNoneYet", "Nothing is answering yet.")
            : string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("SensorsSummary", "{0} readings from {1} sources."),
                found.Length,
                found.Select(d => d.Key.Value.Split('/', 2)[0]).Distinct().Count());

    }

    /// <summary>
    /// Asks what to call a reading, on a double-click of its row.
    /// </summary>
    /// <remarks>
    /// The name given here is used everywhere the reading appears, the bar
    /// included: a drive called "Games" on this page is called "Games" on the
    /// dock, because there is no version of this where two names for one thing
    /// is the friendlier answer. An empty box gives the part's own name back.
    /// </remarks>
    private async void OnRenameSensor(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key
            || _readings.FirstOrDefault(r => r.Key.Value == key) is not { } row)
        {
            return;
        }

        var box = new TextBox
        {
            Header = Loc.Tr("RenameHeader", "What to call this reading"),
            Text = row.Label,
            SelectionStart = 0,
            SelectionLength = row.Label.Length,
        };

        var dialog = new ContentDialog
        {
            Title = row.Hardware,
            PrimaryButtonText = Loc.Tr("RenameSave", "Rename"),
            SecondaryButtonText = Loc.Tr("RenameReset", "Its own name"),
            CloseButtonText = Loc.Tr("PinCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            Content = new StackPanel { MinWidth = 320, Children = { box } },
        };

        ContentDialogResult answer = await dialog.ShowAsync();

        if (answer == ContentDialogResult.Primary)
        {
            row.Rename(box.Text);
        }
        else if (answer == ContentDialogResult.Secondary)
        {
            row.Rename(string.Empty);
        }
    }

    private void OnSensorRenamed(SensorRow row, string name)
    {
        SettingsModel current = _settings.Current;

        ImmutableDictionary<string, string> names = name.Length > 0
            ? current.Sensors.Names.SetItem(row.Key.Value, name)
            : current.Sensors.Names.Remove(row.Key.Value);

        _settings.Commit(
            current with { Sensors = current.Sensors with { Names = names } },
            WriteReason.UserAction);

        _log.LogInformation("settings.sensor named {Key} -> {Name}", row.Key.Value, name);

        // Rebuilt rather than patched: the row's own name comes from the same
        // rules the bar uses, and "the part's own name" is one of them.
        _readings.Clear();
        ShowReadings();
    }

    private string Chosen(string id, string fallback) =>
        _settings.Current.App.Icons.TryGetValue(id, out string? name)
        && IconLibrary.Paths.ContainsKey(name)
            ? name
            : fallback;

    private void OnIconChosen(IconRow row)
    {
        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with { App = current.App with { Icons = current.App.Icons.SetItem(row.Id, row.Icon) } },
            WriteReason.UserAction);

        _log.LogInformation("settings.icon reading={Reading} icon={Icon}", row.Id, row.Icon);
    }

    /// <summary>Opens the grid of icons over the button that was pressed.</summary>
    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        IconRow? row = _icons.FirstOrDefault(r => r.Id == id);

        if (row is null)
        {
            return;
        }

        var grid = new GridView
        {
            ItemsSource = IconRow.Choices(),
            SelectionMode = ListViewSelectionMode.Single,
            MaxWidth = 320,
            MaxHeight = 300,
            ItemTemplate = (DataTemplate)Root.Resources["IconChoiceTemplate"],
        };

        var flyout = new Flyout { Content = grid, XamlRoot = Content.XamlRoot };

        grid.SelectionChanged += (_, args) =>
        {
            if (args.AddedItems.FirstOrDefault() is IconChoice picked)
            {
                row.Choose(picked.Name);
                flyout.Hide();
            }
        };

        flyout.ShowAt(button);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The pin dialog's fields, alive only while it is open.</summary>
    private TextBox? _newTarget;
    private TextBox? _newName;

    /// <summary>Asks what to pin, then pins it to the chosen dock.</summary>
    /// <remarks>
    /// This is the way in for an address, which has no file to drag onto the
    /// bar. Built here rather than declared in the window's tree: a
    /// ContentDialog sitting unopened inside a window has been seen to hang
    /// that window's close, and a dialog needs no place in a tree it only
    /// ever covers.
    /// </remarks>
    /// <summary>Asks for a program to pin, and pins it.</summary>
    private async Task PinDialog()
    {
        if (Dock() is null)
        {
            return;
        }

        _newTarget = new TextBox
        {
            Header = Loc.Tr("PinTargetHeader", "Program, folder or address"),
            PlaceholderText = @"C:\Windows\notepad.exe  or  https://example.com",
        };

        _newName = new TextBox
        {
            Header = Loc.Tr("PinNameHeader", "Name"),
            PlaceholderText = Loc.Tr("PinNameOptional", "optional"),
        };

        var browse = new Button { Content = Loc.Tr("PinBrowse", "Browse...") };
        browse.Click += OnBrowse;

        var dialog = new ContentDialog
        {
            Title = Loc.Tr("PinTitle", "Pin to the bar"),
            PrimaryButtonText = Loc.Tr("PinAdd", "Add"),
            CloseButtonText = Loc.Tr("PinCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            Content = new StackPanel
            {
                MinWidth = 360,
                Spacing = 12,
                Children = { _newTarget, browse, _newName },
            },
        };

        bool add = await dialog.ShowAsync() == ContentDialogResult.Primary;

        string target = _newTarget.Text.Trim();
        string name = _newName.Text.Trim();
        _newTarget = null;
        _newName = null;

        if (!add || target.Length == 0 || Dock() is not { } dock)
        {
            return;
        }

        WidgetConfig pin = IconWidget.Pin(target);

        if (name.Length > 0)
        {
            pin = pin with
            {
                Config = WidgetOptions.Merge(
                    pin.Config, ("name", System.Text.Json.Nodes.JsonValue.Create(name))),
            };
        }

        Rearrange(
            dock.StableId,
            widgets => [.. widgets, pin],
            string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("UndoAdded", "added {0}"),
                name.Length > 0 ? name : IconWidget.NameFor(target)));

        _log.LogInformation("settings.pin added {Target}", target);
    }

    /// <summary>Fills the address box from a file the user picks.</summary>
    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();

            // A picker in a desktop app has no window of its own to belong to,
            // and without being told which one it is ours it throws.
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            picker.FileTypeFilter.Add("*");

            if (await picker.PickSingleFileAsync() is not { } file)
            {
                return;
            }

            if (_newTarget is not null)
            {
                _newTarget.Text = file.Path;
            }

            if (_newName is not null && _newName.Text.Trim().Length == 0)
            {
                _newName.Text = IconWidget.NameFor(file.Path);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings.launcher could not open the file picker");
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Open(AppPaths.LogDirectory);

    private void OnOpenConfig(object sender, RoutedEventArgs e) => Open(AppPaths.Root);

    /// <summary>
    /// The About page: what this program is, and the few switches that
    /// belong to the program rather than to any one bar.
    /// </summary>
    private void ShowAbout()
    {
        Braun.Theme = Root.ActualTheme;
        AboutBody.Children.Clear();

        var mark = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 4),
        };

        mark.Children.Add(new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 14),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/icon.ico")),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            },
        });

        mark.Children.Add(new TextBlock
        {
            Text = "MASTER CONTROL DOCK",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            CharacterSpacing = 200,
            Foreground = Braun.Tx,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        mark.Children.Add(new TextBlock
        {
            Text = _version,
            FontSize = 11,
            Foreground = Braun.Tx3,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        mark.Children.Add(new TextBlock
        {
            Text = Loc.Tr(
                "AboutIntro",
                "A dock for the edge of your screen. Free software under GPL-3.0-or-later; parts adapted from Microsoft PowerToys under the MIT licence."),
            FontSize = 12,
            LineHeight = 20,
            MaxWidth = 460,
            Foreground = Braun.Tx2,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        AboutBody.Children.Add(mark);

        AboutBody.Children.Add(Braun.Heading("Power", Loc.Tr("StartupTitle", "Startup")));

        AboutBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("StartWithWindowsLabel", "Start with Windows"),
            Loc.Tr("StartWithWindowsHint", "The docks come back when you sign in."),
            Braun.Switch(AutoStart.Enabled, on =>
            {
                AutoStart.Enabled = on;
                _log.LogInformation("settings.autostart enabled={Enabled}", on);
                ShowAbout();
            }))));

        AboutBody.Children.Add(Braun.Heading("Document", Loc.Tr("FilesTitle", "Its own files")));

        AboutBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("LogsRow", "The log"),
                Loc.Tr("LogsRowHint", "What the program wrote down about its own run."),
                Braun.Action(
                    Loc.Tr("OpenFolder", "Open the folder"),
                    () => Open(AppPaths.LogDirectory),
                    "Folder")),

            Braun.Row(
                Loc.Tr("ConfigRow", "The settings file"),
                Loc.Tr("ConfigRowHint", "Everything on these pages, as it is stored on disk."),
                Braun.Action(
                    Loc.Tr("OpenFolder", "Open the folder"),
                    () => Open(AppPaths.Root),
                    "Folder"))));

        AboutBody.Children.Add(Braun.Heading("Power", Loc.Tr("QuitTitle", "Quitting")));

        AboutBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("ExitRow", "Stop the program"),
            Loc.Tr(
                "ExitHint",
                "Closing this window leaves the docks running. To stop them, use the button below - ending the task from Task Manager leaves the reserved screen space behind."),
            Braun.Action(
                Loc.Tr("ExitButton", "Exit Master Control Dock"),
                () => _onExit(),
                "Power",
                danger: true))));
    }

    /// <summary>This build's version, for the About page.</summary>
    private string _version = string.Empty;


    private void Open(string path)
    {
        try
        {
            // explorer.exe rather than opening the folder through the shell API:
            // inside an MSIX package a child process inherits the package
            // context, and Explorer is the one thing that reliably steps out of it.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings could not open {Path}", path);
        }
    }
}

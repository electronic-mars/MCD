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
    /// <remarks>
    /// A list used as a stack, because a stack cannot drop its oldest entry
    /// and this one has to. What was here before capped nothing: TrimExcess
    /// reduces capacity, not count, so the "no more than twenty-five" the
    /// comment promised was twenty-five hundred if somebody worked that long.
    /// </remarks>
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

        SensorList.ItemsSource = _readings;
        _version = string.Format(
            CultureInfo.CurrentCulture, Loc.Tr("VersionFormat", "Version {0}"), AppInfo.Version);


        // Only while the sensors page is on screen. A window sitting behind
        // everything else has no business waking the machine once a second.
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(1);
        _refresh.Tick += (_, _) => Tick();
        Closed += (_, _) => _refresh.Stop();

        // What happens on a bar is a change to the same settings this window
        // is showing, and until now the window never heard about it: the chip
        // counts, the undo label and the chosen widget all went stale the
        // moment somebody dragged something. Worse, a widget dragged off a
        // bar - the way the bar's own help text tells people to remove one -
        // could not be undone from anywhere.
        _seen = _settings.Current;
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;

        // A line left round a widget because the window shut while the pointer
        // was over its row would stay there until something else redrew the
        // bar - a mark nobody made and nobody can clear.
        Closed += (_, _) => _docks.Point(string.Empty, null);

        Reload();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>The settings as this window last acted on them.</summary>
    private SettingsModel _seen;

    /// <summary>
    /// The last settings this window committed, by identity.
    /// </summary>
    /// <remarks>
    /// A flag raised around the call did not work: the change comes back
    /// through the watcher on the same turn, but the work of looking at it is
    /// put off to the next one, by which time the flag is down again. So every
    /// change made here was written to the undo stack twice - once with its
    /// real name and once as "on the bar" - and the button never emptied,
    /// because undoing also wrote. The model itself is the mark: it is the
    /// same object coming back, whenever it comes back.
    /// </remarks>
    private SettingsModel? _mine;

    /// <summary>
    /// Takes note of a change this window did not make.
    /// </summary>
    /// <remarks>
    /// Which in practice means a bar: a widget moved, dropped or dragged off
    /// on the screen itself. Each of those is a change to one monitor's
    /// layout, so each goes on the undo stack the same way a change made
    /// here would - and then the page is rebuilt, because everything on it
    /// describing that bar is now a sentence about the past.
    /// </remarks>
    private void OnSettingsChanged(object? sender, SettingsModel now)
    {
        if (!DispatcherQueue.TryEnqueue(() => Noticed(now)))
        {
            _seen = now;
        }
    }

    private void Noticed(SettingsModel now)
    {
        _seen = now;

        if (ReferenceEquals(now, _mine))
        {
            return;
        }

        ReloadDocks();
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
    /// <para>
    /// The floor: the pane at its open width, the page's own padding either
    /// side, and enough left for a settings row to keep its words beside its
    /// control. Measured rather than guessed - below this the explanations
    /// start wrapping every other word, which the unattended check looks for.
    /// </para>
    /// <para>
    /// And a ceiling, which matters as much. A settings page is a column of
    /// rows; dragged to two thousand points wide it becomes a column of rows
    /// with a field of empty panel beside it, and dragged short at the same
    /// time it becomes a letterbox. Neither is a window anybody wanted, and
    /// the way to not have them is to not offer them.
    /// </para>
    /// </remarks>
    private const double MinimumWide = 820;

    private const double MinimumTall = 660;

    private const double MaximumWide = 1500;

    private const double MaximumTall = 1200;

    /// <summary>
    /// Room left round the window on a screen that has none to spare.
    /// </summary>
    /// <remarks>
    /// Enough for the taskbar and for the window's own shadow, so that the
    /// bar this window is meant to be dragged onto is still reachable behind
    /// it. The screen rectangle is the whole display rather than the working
    /// area, because that is what the monitor list hands over.
    /// </remarks>
    private const double Spare = 80;

    public void SizeAndCentre(MonitorInfo? screen)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        double scale = screen?.Scale ?? WindowFrame.GetScale(hwnd);
        RECT bounds = screen?.Bounds ?? Displays.BoundsFor(hwnd);

        // What the screen has, in the same units the sizes are written in.
        // Without this the window asked for 780 points of height whatever it
        // was opening on: on a 1920 by 1080 screen at 150 per cent that is a
        // window taller than the display it is centred on, with its foot off
        // the bottom - and the bar it is meant to be dragged onto underneath
        // it. The commonest laptop screen there is.
        double roomWide = (bounds.right - bounds.left) / scale;
        double roomTall = (bounds.bottom - bounds.top) / scale;

        double widest = Math.Max(MinimumWide, Math.Min(MaximumWide, roomWide - Spare));
        double tallest = Math.Max(MinimumTall, Math.Min(MaximumTall, roomTall - Spare));

        var size = new Windows.Graphics.SizeInt32(
            (int)Math.Round(Math.Clamp(1000, MinimumWide, widest) * scale),
            (int)Math.Round(Math.Clamp(780, MinimumTall, tallest) * scale));

        // A floor under the window, in the same physical pixels AppWindow
        // works in. Without one the pane keeps its width while the content
        // column is squeezed to nothing, and an explanation ends up set one
        // word to a line - which is not a thing to notice in a screenshot, it
        // is a thing the window should refuse to do.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth =
                (int)Math.Round(Math.Min(MinimumWide, widest) * scale);

            presenter.PreferredMinimumHeight =
                (int)Math.Round(Math.Min(MinimumTall, tallest) * scale);

            presenter.PreferredMaximumWidth = (int)Math.Round(widest * scale);
            presenter.PreferredMaximumHeight = (int)Math.Round(tallest * scale);
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

        // A widget was pointed at, so the page that has its settings is the
        // page to open. Pointing at bare bar opens the page about the bar.
        Nav.SelectedItem = widgetId is null ? Nav.MenuItems[0] : Nav.MenuItems[1];

        ReloadDocks();

        // And brought into view. A widget pointed at on the bar used to open
        // this window at the top of a page a screen and a half long, with its
        // own settings below the fold - which reads as the wrong window
        // opening, and the second try is another right click on the same
        // widget.
        if (widgetId is not null)
        {
            DispatcherQueue.TryEnqueue(() => _inspectorAt?.StartBringIntoView(
                new BringIntoViewOptions { VerticalAlignmentRatio = 0.15 }));
        }
    }

    /// <summary>The heading of the chosen widget's own settings, to scroll to.</summary>
    private FrameworkElement? _inspectorAt;

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
        _clipped = 0;

        // Put back afterwards. Something else is photographing these pages by
        // name, from the log; a measuring pass that leaves the window on a
        // different page than the one announced files the photograph under
        // the wrong name, and a picture of the wrong page looks right.
        object? was = Nav.SelectedItem;

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

            // A word with its ends cut off is a different fault from a
            // paragraph squeezed thin, and the measure that catches one is
            // blind to the other. "Полупрозрачный" sat in a key too narrow to
            // hold it for a whole release, through every check there was,
            // because nothing here ever asked whether a word fitted its box.
            (double over, string cut) = Cut(Pages);

            if (over > Tolerance)
            {
                _log.LogWarning(
                    "selftest.clipped page={Page} over={Over} text={Text}",
                    tag,
                    Math.Round(over),
                    cut);

                _clipped = Math.Max(_clipped, over);
            }
        }

        _log.LogInformation("selftest.clipped worst={Worst}", Math.Round(_clipped));

        Nav.SelectedItem = was;
        Pages.UpdateLayout();

        return narrowest;
    }

    /// <summary>
    /// A point and a half, which is rounding rather than clipping.
    /// </summary>
    /// <remarks>
    /// Text is laid out in whole pixels at the screen's scale and measured in
    /// points; the difference is under a point, and a word losing a letter is
    /// over ten. There is no useful fault between the two.
    /// </remarks>
    private const double Tolerance = 1.5;

    private double _clipped;

    /// <summary>
    /// The worst-cut line of text anywhere under this element, and by how much.
    /// </summary>
    /// <remarks>
    /// Only text that neither wraps nor trims: those two have somewhere to put
    /// what will not fit, and are doing what they were told. Anything else with
    /// more text than box is losing letters off both ends, which reads as a
    /// shorter word rather than as a fault - nobody widens a window over a word
    /// that looks like a word.
    /// </remarks>
    private static (double Over, string Text) Cut(DependencyObject root)
    {
        (double Over, string Text) worst = (0, string.Empty);

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is UIElement { Visibility: Visibility.Collapsed })
            {
                continue;
            }

            if (child is TextBlock
                {
                    TextWrapping: TextWrapping.NoWrap,
                    TextTrimming: TextTrimming.None,
                    Visibility: Visibility.Visible,
                } text
                && text.Text.Length > 0
                && text.ActualWidth > 0)
            {
                // Against the box, not against the text's own width. A line
                // too long for the place it was given is arranged at the width
                // it wanted and then clipped by whatever holds it, so its own
                // width says everything fitted right up to the moment the
                // letters disappear.
                double over = Natural(text) - Math.Min(text.ActualWidth, Room(text));

                if (over > worst.Over)
                {
                    worst = (over, text.Text);
                }
            }

            (double Over, string Text) below = Cut(child);

            if (below.Over > worst.Over)
            {
                worst = below;
            }
        }

        return worst;
    }

    /// <summary>How wide this line of text would be if nothing stopped it.</summary>
    private static double Natural(TextBlock text)
    {
        var loose = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            FontStretch = text.FontStretch,
            CharacterSpacing = text.CharacterSpacing,
            TextWrapping = TextWrapping.NoWrap,
        };

        loose.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));

        // Letter spacing is added after the last letter as well as between,
        // and that trailing gap is not part of the word: counting it makes
        // every spaced caption in the program look a point too big for its
        // box, and a check that cries wolf on every line catches nothing.
        double trailing = text.CharacterSpacing / 1000.0 * text.FontSize;

        return loose.DesiredSize.Width - trailing;
    }

    /// <summary>
    /// The narrowest column any paragraph was given, anywhere under this
    /// element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only wrapping text, and only a sentence of it. A name like "The bar
    /// itself" is legitimately as wide as its own words and no wider, and
    /// counting it finds a narrow thing that is not a fault. What is being
    /// looked for is a paragraph squeezed into a column of single words, and
    /// a paragraph is long.
    /// </para>
    /// <para>
    /// The column, not the text. A wrapping TextBlock ends up as wide as its
    /// longest laid-out line, which is shorter than the room it was given by
    /// however much the last word on that line did not fit - and in a language
    /// of long words that is a lot. Reading the text's own width called a
    /// perfectly wide Russian page collapsed, twice, and each time the fault
    /// was in the ruler.
    /// </para>
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
                && Room(text) < narrowest.Width)
            {
                narrowest = (Room(text), text.Text);
            }

            (double Width, string Text) below = Narrowest(child);

            if (below.Width < narrowest.Width)
            {
                narrowest = below;
            }
        }

        return narrowest;
    }

    /// <summary>How much width this paragraph was given to wrap inside.</summary>
    /// <remarks>
    /// Its parent's, less its own margins. The paragraph is the only thing in
    /// that column, so what the column has is what it was offered, whether the
    /// words happened to fill the last line of it or not.
    /// </remarks>
    private static double Room(TextBlock text) =>
        VisualTreeHelper.GetParent(text) is FrameworkElement parent && parent.ActualWidth > 0
            ? parent.ActualWidth - text.Margin.Left - text.Margin.Right
            : text.ActualWidth;

    /// <summary>Rebuilds both lists from the settings and the live topology.</summary>
    public void Reload()
    {
        AppSettings look = _settings.Current.App;

        _filling = true;
        _filling = false;

        Root.RequestedTheme = Appearance.Of(look.Theme);
        RefreshLook();

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
                Loc.Tr("ThemeHint", "The bars and this window follow it together."),
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
                        Loc.Tr("SegBackdropBraun", "Instrument"),
                    ],
                    Math.Max(0, Array.IndexOf(backdrops, look.Backdrop)),
                    i => ApplyLook(backdrop: backdrops[i]),
                    wide: true),
                stack: true),

            BackdropExtraRow(look),

            Braun.Row(
                Loc.Tr("SizeLabel", "Size of the readings"),
                Loc.Tr(
                    "SizeHint",
                    "How large the icons and figures are drawn. The bar itself stays the thickness chosen on its own page."),
                Braun.Segs(
                    [
                        Loc.Tr("SegSizeLarge", "Large"),
                        Loc.Tr("SegSizeMedium", "Medium"),
                        Loc.Tr("SegSizeSmall", "Small"),
                    ],
                    Math.Max(0, Array.IndexOf(Sizes, look.Size)),
                    i => ApplyLook(size: Sizes[i]),
                    wide: true),
                stack: true),

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

    }

    /// <summary>
    /// One shortcut: what it does, and the keys it answers to.
    /// </summary>
    /// <remarks>
    /// A combination another program already holds is refused by the system,
    /// and the refusal is shown here rather than swallowed. A key somebody
    /// chose that quietly does nothing is the worst of the three outcomes.
    /// </remarks>
    private Grid KeyRow(string what, string name, string hint)
    {
        AppSettings app = _settings.Current.App;

        Chord chord = Chord.Parse(app.Keys.TryGetValue(what, out string? text) ? text : null);
        bool taken = chord.Set && _docks.KeyRefused(chord.ToString());

        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        keys.Children.Add(Braun.Action(
            chord.Set ? chord.ToString() : Loc.Tr("KeyNone", "Not set"),
            () => _ = CatchKey(what, name),
            "Sliders"));

        if (chord.Set)
        {
            keys.Children.Add(Braun.Action(
                Loc.Tr("KeyClear", "Clear"),
                () => BindKey(what, Chord.None),
                "Delete"));
        }

        return Braun.Row(
            name,
            taken
                ? Loc.Tr("KeyTaken", "Another program already holds this combination, so it does nothing here. Choose a different one.")
                : hint,
            keys);
    }

    /// <summary>
    /// Waits for a combination to be typed.
    /// </summary>
    /// <remarks>
    /// A dialog rather than a field that listens while the window has focus:
    /// a control that swallows every key it sees is a control somebody gets
    /// stuck in. Escape leaves it, and leaving it changes nothing.
    /// </remarks>
    private async Task CatchKey(string what, string name)
    {
        var shown = new TextBlock
        {
            Text = Loc.Tr("KeyWaiting", "Hold Ctrl, Alt or Win and press a key."),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        };

        var caught = Chord.None;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = name,
            Content = shown,
            PrimaryButtonText = Loc.Tr("KeyUse", "Use it"),
            CloseButtonText = Loc.Tr("Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        void Typed(object sender, KeyRoutedEventArgs e)
        {
            var pressed = new Chord(
                Down(Windows.System.VirtualKey.Control),
                Down(Windows.System.VirtualKey.Menu),
                Down(Windows.System.VirtualKey.Shift),
                Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows),
                (uint)e.Key);

            // The modifiers on their own are not a combination; they are the
            // first half of one, and showing "Ctrl" as though it were finished
            // invites somebody to press the button.
            if (e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Menu
                or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows)
            {
                return;
            }

            e.Handled = true;
            caught = pressed;

            shown.Text = pressed.Sane
                ? pressed.ToString()
                : Loc.Tr("KeyBare", "A key on its own would take it away from every other program. Hold Ctrl, Alt or Win as well.");

            dialog.IsPrimaryButtonEnabled = pressed.Sane;
        }

        dialog.KeyDown += Typed;

        ContentDialogResult answer = await dialog.ShowAsync();
        dialog.KeyDown -= Typed;

        if (answer == ContentDialogResult.Primary && caught.Sane)
        {
            BindKey(what, caught);
        }
    }

    private static bool Down(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Writes a shortcut down, or takes it away.</summary>
    private void BindKey(string what, Chord chord)
    {
        SettingsModel current = _settings.Current;

        ImmutableDictionary<string, string> keys = chord.Sane
            ? current.App.Keys.SetItem(what, chord.ToString())
            : current.App.Keys.Remove(what);

        Write(
            current with { App = current.App with { Keys = keys } },
            WriteReason.UserAction,
            Loc.Tr("UndoKey", "a key"));

        _log.LogInformation("settings.key {What} {Chord}", what, chord);

        // The page these rows live on, which is no longer the one they were
        // written for. Refreshing the other page left the button reading "not
        // set" after a key had been set, and the warning about a combination
        // another program holds never appeared at all.
        ShowAbout();
    }

    /// <summary>
    /// The row under Background that says which colour or which picture, or
    /// what a background named after something rather than described is - and
    /// nothing at all for the ones that need neither.
    /// </summary>
    private FrameworkElement? BackdropExtraRow(AppSettings app)
    {
        // A button with a name on it that only its author knows is a button
        // nobody presses. Every other background says what it does; this one
        // says whose it is, so what it does has to be written underneath.
        if (app.Backdrop == "braun")
        {
            return Braun.Row(
                Loc.Tr("BraunRow", "What this is"),
                Loc.Tr(
                    "BraunHint",
                    "The bar as a piece of equipment: a graphite body on the dark theme, a cream one on the light, readings in orange. Plain, strict and quiet - the same look these settings are drawn in, after Braun's instruments."),
                null);
        }

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

        Write(
            current with { App = current.App with { Language = language } },
            WriteReason.UserAction,
            Loc.Tr("UndoLanguage", "the language"));

        _log.LogInformation("settings.language {Language}", language);
        _restartOffered = true;
        ShowAbout();
    }

    /// <summary>True once the language has been changed in this sitting.</summary>
    private bool _restartOffered;

    /// <summary>The one writer of the appearance settings.</summary>
    private static readonly string[] Sizes = ["large", "medium", "small"];

    private void ApplyLook(
        string? theme = null, string? backdrop = null, string? accent = null, string? size = null)
    {
        SettingsModel current = _settings.Current;

        Write(
            current with
            {
                App = current.App with
                {
                    Theme = theme ?? current.App.Theme,
                    Backdrop = backdrop ?? current.App.Backdrop,
                    Accent = accent ?? current.App.Accent,
                    Size = size ?? current.App.Size,
                },
            },
            WriteReason.UserAction,
            Loc.Tr("UndoLook", "how the bars are painted"));

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
        ShowWidgets();
    }

    /// <summary>The name one screen goes by in the switcher, by its id.</summary>
    private string ScreenName(string stableId)
    {
        ImmutableArray<MonitorConfig> monitors = _settings.Current.Monitors;
        int at = monitors.ToList().FindIndex(m => m.StableId == stableId);

        if (at < 0)
        {
            return Loc.Tr("ScreenFallback", "Screen");
        }

        return Label(
            monitors[at],
            monitors,
            _docks.Plans.ToDictionary(p => p.Config.StableId, p => p.Monitor),
            at);
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
        _undoOnDocks = null;

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
        DockBody.Children.Add(Braun.Heading("Computer", Loc.Tr("DockGroupScreen", "This screen's bar")));

        DockBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("ShowDockLabel", "Show a bar on this display"),
                live is not null
                    ? $"{live.Width} x {live.Height} · {live.Dpi * 100 / 96}%"
                    : Loc.Tr("DockNotAttached", "not attached · its layout is kept and comes back with the screen"),
                Braun.Switch(
                    dock.Enabled,
                    on => SetDock(d => d with { Enabled = on }, Loc.Tr("UndoShown", "showing the bar")))),

            // Only for a screen that is not there. A list of screens that only
            // ever grows is a list somebody stops reading: every projector,
            // every television, every Win+P arrangement the program has ever
            // seen stays in it. The comment in the reconciler has promised
            // this button since the registry was written.
            live is null
                ? Braun.Row(
                    Loc.Tr("ForgetRow", "Forget this screen"),
                    Loc.Tr(
                        "ForgetRowHint",
                        "Takes its bar and its arrangement out of the settings. If the screen comes back it arrives as a new one."),
                    Braun.Action(
                        Loc.Tr("ForgetButton", "Forget it"), ForgetScreen, "Delete", danger: true))
                : null,

            // Outside everything the master switch dims. It was inside, which
            // meant that switching a dock off left an enabled Undo button in
            // a panel that could not be clicked - including the undo of
            // switching it off.
            Braun.Row(
                Loc.Tr("UndoRow", "Take the last change back"),
                Loc.Tr("UndoRowHint", "Anything changed here or on a bar itself, on any screen."),
                _undoOnDocks = Undo())));

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

            Braun.Row(
                Loc.Tr("AnchorLabel", "Where the widgets sit"),
                Loc.Tr(
                    "AnchorHint",
                    "A bar is as long as the screen and what is on it usually is not. This is which end the empty part goes."),
                Braun.Segs(
                    [
                        Loc.Tr("SegAnchorStart", "At the start"),
                        Loc.Tr("SegAnchorCentre", "In the middle"),
                        Loc.Tr("SegAnchorEnd", "At the end"),
                    ],
                    (int)dock.Anchor,
                    i => SetDock(
                        d => d with { Anchor = (DockAnchor)i },
                        Loc.Tr("UndoAnchor", "where the widgets sit")),
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
                        ? Loc.Tr("HideNote", "The taskbar already hides on this edge, so this bar stays visible.")
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


    /// <summary>
    /// Fills the page about what is on the bar.
    /// </summary>
    /// <remarks>
    /// A page of its own because it is the part that grows. Every new kind of
    /// widget lands in this list, and the settings of whichever one is chosen
    /// land under it; both were sitting in the middle of the page about where
    /// the bar sits, which made that page jump in height whenever somebody
    /// pointed at a widget on the screen.
    /// </remarks>
    private void ShowWidgets()
    {
        Braun.Theme = Root.ActualTheme;
        WidgetsBody.Children.Clear();
        _undoOnWidgets = null;

        MonitorConfig? dock = _settings.Current.Monitors
            .FirstOrDefault(m => m.StableId == _editing);

        WidgetsSection.Opacity = dock is null ? 0.5 : 1;

        if (dock is null)
        {
            WidgetsBody.Children.Add(Braun.Group(Braun.Row(
                Loc.Tr("DockNoScreen", "No screen has been set up yet."), null, null)));

            return;
        }

        // ------------------------------------------------------------ contents
        WidgetsBody.Children.Add(Braun.Heading("List", Loc.Tr("GalleryTitle", "On the bar")));

        // The page that makes most of the changes worth taking back, and it
        // had no way to take one back: the button was on the page next door,
        // and the destructive things - the crosses, the reset - are all here.
        WidgetsBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("UndoRow", "Take the last change back"),
            Loc.Tr("UndoRowHint", "Anything changed here or on a bar itself, on any screen."),
            _undoOnWidgets = Undo())));

        WidgetsBody.Children.Add(BarList(dock));

        WidgetsBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("BarLiveTitle", "The bar itself"),
                Loc.Tr(
                    "BarHelp",
                    "Drag a widget along the bar to move it, off the bar to remove it. A right click anywhere on the bar opens this window - over a widget, with that widget already chosen."),
                null),

            Braun.Row(
                Loc.Tr("GalleryRow", "Put one on the bar"),
                Loc.Tr(
                    "GalleryRowHint",
                    "Drag one onto a bar and it lands where you drop it. Two of the same is fine."),
                Gallery(dock),
                stack: true),

            Braun.Row(
                Loc.Tr("PinRow", "A program of your own"),
                Loc.Tr(
                    "PinRowHint",
                    "Pinned programs sit on the bar as icons. A file dropped straight onto the bar is pinned to the slot it lands on."),
                Braun.Action(
                    Loc.Tr("PinProgramButton", "Pin a program..."), () => _ = PinDialog(), "Plus")),

            // Only when there are any. A row that says "nothing is missing"
            // on every ordinary day is a row nobody reads on the day
            // something is.
            Crowded(dock) is { Count: > 0 } stranded
                ? Braun.Row(
                    stranded.Count == 1
                        ? Loc.Tr("CrowdedRowOne", "One of these does not fit")
                        : string.Format(
                            CultureInfo.CurrentCulture,
                            Loc.Tr("CrowdedRow", "{0} of these do not fit"),
                            stranded.Count),
                    Loc.Tr(
                        "CrowdedRowHint",
                        "The bar has run out of slots. These are kept and come back when there is room - on another edge, or at compact thickness."),
                    Braun.Action(
                        stranded.Count == 1
                            ? Loc.Tr("CrowdedButtonOne", "Take it off")
                            : Loc.Tr("CrowdedButton", "Take them off"),
                        () => Shed(dock, stranded, Loc.Tr("UndoCrowded", "the ones that did not fit")),
                        "Delete",
                        danger: true))
                : null,

            // Also only when there are any, and worded as waiting rather than
            // as missing. Somebody who drags a Wi-Fi widget onto the bar of a
            // machine holding a cable sees nothing happen, and nothing
            // happening is indistinguishable from broken.
            Quiet(dock) is { Count: > 0 } quiet
                ? Braun.Row(
                    // Counted rows are read as sentences, and a sentence that
                    // says "1 are waiting" is a sentence written by a machine.
                    quiet.Count == 1
                        ? Loc.Tr("QuietRowOne", "One is waiting for its moment")
                        : string.Format(
                            CultureInfo.CurrentCulture,
                            Loc.Tr("QuietRow", "{0} are waiting for their moment"),
                            quiet.Count),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Tr(
                            "QuietRowHint",
                            "{0} - on the bar, taking no slot until it has something to say. It comes back where you left it."),
                        Named(quiet)),
                    Braun.Action(
                        quiet.Count == 1
                            ? Loc.Tr("QuietButtonOne", "Take it off")
                            : Loc.Tr("QuietButton", "Take them off"),
                        () => Shed(dock, quiet, Loc.Tr("UndoQuiet", "the ones that were waiting")),
                        "Delete",
                        danger: true))
                : null,

            // Only where there is somewhere to copy to.
            _settings.Current.Monitors.Length > 1
                ? Braun.Row(
                    Loc.Tr("CopyRow", "The other screens"),
                    Loc.Tr(
                        "CopyRowHint",
                        "Gives every other screen the same widgets in the same places. Each bar keeps its own copies, and undo puts the others back."),
                    Braun.Action(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Loc.Tr("CopyButtonCount", "Make {0} the same"),
                            _settings.Current.Monitors.Length - 1),
                        CopyToOthers,
                        "Layout"))
                : null,

            Braun.Row(
                Loc.Tr("ResetRow", "The standard set"),
                Loc.Tr(
                    "ResetRowHint",
                    "Puts back the set the bar came with. Undo brings your own arrangement back."),
                // Dressed as what it is. It throws away the whole
                // arrangement of this screen - every pinned program, every
                // widget's own settings - and it was wearing a quieter coat
                // than the button that takes off a single widget.
                Braun.Action(
                    Loc.Tr("ResetButton", "Restore the standard bar"),
                    ResetDock,
                    "Undo",
                    danger: true))));

        // ------------------------------------------------------------ presets
        WidgetsBody.Children.Add(Braun.Heading("Star", Loc.Tr("PresetsGroup", "Presets")));

        var presetRows = new List<FrameworkElement?>
        {
            Braun.Row(
                Loc.Tr("PresetSaveRow", "Keep this arrangement"),
                Loc.Tr(
                    "PresetSaveRowHint",
                    "Saves what is on this bar, by name, to be put onto any bar later - here or on another screen."),
                Braun.Action(Loc.Tr("PresetSaveButton", "Save as a preset"), SavePreset, "Plus")),
        };

        foreach (BarPreset preset in _settings.Current.App.Presets)
        {
            string id = preset.Id;

            var apply = Braun.Action(
                Loc.Tr("PresetApply", "Put it on this bar"), () => ApplyPreset(id), "ArrowDown");

            var drop = Braun.Action(
                Loc.Tr("PresetDelete", "Forget it"), () => DeletePreset(id), "Delete", danger: true);

            presetRows.Add(Braun.Row(
                preset.Name,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("PresetHint", "{0} widgets"),
                    preset.Widgets.Length),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { apply, drop },
                }));
        }

        WidgetsBody.Children.Add(Braun.Group([.. presetRows]));

        // ------------------------------------------------------- chosen widget
        if (Inspector() is { } inspector)
        {
            _inspectorAt = Braun.Heading("Sliders", _inspectorName);

            WidgetsBody.Children.Add(_inspectorAt);
            WidgetsBody.Children.Add(inspector);
        }
        else
        {
            _inspectorAt = null;
        }

    }

    /// <summary>Asks for a name and keeps this bar's arrangement under it.</summary>
    private async void SavePreset()
    {
        if (Dock() is not { } dock || _docks.Slots(dock.StableId) is not { } slots)
        {
            return;
        }

        var box = new TextBox
        {
            Header = Loc.Tr("PresetNameHeader", "What to call it"),
            Text = ScreenName(dock.StableId),
        };

        box.SelectionLength = box.Text.Length;

        var dialog = new ContentDialog
        {
            Title = Loc.Tr("PresetSaveTitle", "Save this arrangement"),
            PrimaryButtonText = Loc.Tr("PresetSaveGo", "Save"),
            CloseButtonText = Loc.Tr("Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            Content = new StackPanel { MinWidth = 320, Children = { box } },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        var preset = new BarPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = box.Text.Trim(),
            Slots = slots,
            Widgets = dock.Widgets,
        };

        Write(
            current with
            {
                App = current.App with { Presets = [.. current.App.Presets, preset] },
            },
            WriteReason.UserAction,
            Loc.Tr("UndoPresetSaved", "a preset saved"));

        _log.LogInformation("settings.preset saved name={Name} widgets={Count}", preset.Name, preset.Widgets.Length);
        ShowWidgets();
    }

    /// <summary>
    /// Puts a saved arrangement onto the bar being edited.
    /// </summary>
    /// <remarks>
    /// Fresh instances, scaled slots: the preset remembers how many slots its
    /// bar had, and the numbers are refitted to this one so the shape arrives
    /// rather than the arithmetic. Undo puts back what was there.
    /// </remarks>
    private void ApplyPreset(string id)
    {
        if (Dock() is not { } dock
            || _settings.Current.App.Presets.FirstOrDefault(p => p.Id == id) is not { } preset)
        {
            return;
        }

        ImmutableArray<WidgetConfig> copies = [.. preset.Widgets.Select(w => w.AsNewInstance())];

        _selectedId = null;

        Rearrange(
            dock.StableId,
            _ => preset.Slots > 0 && _docks.Slots(dock.StableId) is { } here
                ? DockGrid.Scaled(copies, preset.Slots, here)
                : copies,
            Loc.Tr("UndoPresetApplied", "a preset put on"));

        _log.LogInformation("settings.preset applied name={Name} monitor={Monitor}", preset.Name, dock.StableId);
    }

    /// <summary>Forgets one saved arrangement. Undoable like everything here.</summary>
    private void DeletePreset(string id)
    {
        SettingsModel current = _settings.Current;

        Write(
            current with
            {
                App = current.App with
                {
                    Presets = [.. current.App.Presets.Where(p => p.Id != id)],
                },
            },
            WriteReason.UserAction,
            Loc.Tr("UndoPresetDropped", "a preset forgotten"));

        ShowWidgets();
    }

    /// <summary>
    /// Takes a widget off the bar, asking first if it is the way back in.
    /// </summary>
    /// <remarks>
    /// One widget on the whole machine is not about a reading: the wheel is
    /// the door. Taking off the last one, with no key bound, leaves the right
    /// click on a bar as the only way into this window - which is fine for
    /// somebody who knows it and a locked door for somebody who does not.
    /// Asked once, in that one case, and never for anything else.
    /// </remarks>
    private async void TakeOff(WidgetConfig entry, string what)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        if (LastWayIn(entry) && !await Sure())
        {
            return;
        }

        Rearrange(
            dock.StableId,
            widgets => [.. widgets.Where(w => w.InstanceId != entry.InstanceId)],
            what);
    }

    /// <summary>Whether this widget is the last door into the settings.</summary>
    private bool LastWayIn(WidgetConfig entry) =>
        entry.TypeId == SettingsWidget.Type
        && !_settings.Current.App.Keys.ContainsKey(Shortcut.Settings)
        && _settings.Current.Monitors
            .SelectMany(m => m.Widgets)
            .Count(w => w.TypeId == SettingsWidget.Type) == 1;

    /// <summary>Asks before the door is taken away, and says what is left.</summary>
    private async Task<bool> Sure()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.Tr("LastDoorTitle", "This is the last way in"),
            Content = Loc.Tr(
                "LastDoorBody",
                "No key opens this window, and this is the only wheel left on any bar. "
                    + "After this, the way back in is a right click on a bar."),
            PrimaryButtonText = Loc.Tr("LastDoorGo", "Take it off anyway"),
            CloseButtonText = Loc.Tr("Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Takes a screen that is not here out of the settings.
    /// </summary>
    /// <remarks>
    /// Only offered for a screen that is not attached, because for one that
    /// is the answer would be undone by the next reconcile. Undoable like
    /// anything else, so a wrong press costs one click rather than a layout.
    /// </remarks>
    private void ForgetScreen()
    {
        if (_editing is not { } stableId)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        Write(
            current with
            {
                Monitors = [.. current.Monitors.Where(m => m.StableId != stableId)],
            },
            WriteReason.UserAction,
            Loc.Tr("UndoForgot", "a screen forgotten"));

        _log.LogInformation("settings.forgot monitor={Monitor}", stableId);

        _editing = null;
        ReloadDocks();
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

    /// <summary>What this screen's bar is holding but has no room to show.</summary>
    private IReadOnlyList<WidgetConfig> Crowded(MonitorConfig dock) =>
        _docks.Unplaced(dock.StableId);

    /// <summary>What it is holding that is about nothing on this machine today.</summary>
    private IReadOnlyList<WidgetConfig> Quiet(MonitorConfig dock) =>
        _docks.Quiet(dock.StableId);

    /// <summary>
    /// What they are called, for a line that has to name them.
    /// </summary>
    /// <remarks>
    /// "2 are waiting" is a count; "Battery, Wi-Fi" is an answer. The same
    /// kind twice is listed once - two clocks are still "Clock".
    /// </remarks>
    private static string Named(IReadOnlyList<WidgetConfig> widgets) =>
        string.Join(
            ", ",
            widgets
                .Select(w => WidgetCatalog.Find(w.TypeId)?.Name ?? w.TypeId)
                .Distinct(StringComparer.CurrentCulture));

    /// <summary>Takes off the ones named.</summary>
    private void Shed(MonitorConfig dock, IReadOnlyList<WidgetConfig> stranded, string what)
    {
        var gone = stranded.Select(w => w.InstanceId).ToHashSet(StringComparer.Ordinal);

        Rearrange(
            dock.StableId,
            widgets => [.. widgets.Where(w => !gone.Contains(w.InstanceId))],
            what);
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

    /// <summary>
    /// What is on this bar, in the order it stands there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bar itself is the better place to arrange things, and it stays the
    /// better place. But it is not always a reachable place: a bar that hides
    /// has to be held open with the pointer that is doing the dragging, a bar
    /// on the third screen is a long way from this window, a bar 86 points
    /// wide is a small target, and a widget that did not fit is not drawn at
    /// all - so the one gesture that reaches its settings cannot reach it.
    /// </para>
    /// <para>
    /// So the same widgets are also a list. Choosing one here is the same as
    /// right-clicking it there, and the two arrows and the cross do what a
    /// drag and a drag off the bar do.
    /// </para>
    /// </remarks>
    private FrameworkElement BarList(MonitorConfig dock)
    {
        List<WidgetConfig> order =
        [
            .. dock.Widgets
                .Select((w, i) => (Widget: w, Index: i))
                .OrderBy(x => x.Widget.Cell < 0 ? int.MaxValue : x.Widget.Cell)
                .ThenBy(x => x.Index)
                .Select(x => x.Widget)
        ];

        var rows = new List<FrameworkElement?>();

        for (int i = 0; i < order.Count; i++)
        {
            WidgetConfig entry = order[i];
            int at = i;

            var line = new Grid { ColumnSpacing = 6, Padding = new Thickness(13, 7, 13, 7) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = NameOf(entry),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = entry.InstanceId == _selectedId ? Braun.Acc : Braun.Tx,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            line.Children.Add(name);

            // Where it stands, or that it is not standing. Two widgets of the
            // same kind are allowed and common - the list said "CPU" twice
            // with nothing to tell them apart, so neither arrow nor cross
            // could be aimed.
            var place = new TextBlock
            {
                Text = entry.Cell >= 0
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Tr("ListAtSlot", "slot {0}"),
                        entry.Cell)
                    : Loc.Tr("ListNoSlot", "waiting"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Braun.Tx3,
            };

            Grid.SetColumn(place, 1);
            line.Children.Add(place);

            Button Small(string glyph, string tip, Action click, bool danger = false)
            {
                var button = new Button
                {
                    Width = 30,
                    Height = 26,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Background = Braun.PanelHi,
                    BorderBrush = Braun.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Content = Braun.Glyph(glyph, 13, danger ? Braun.Danger : Braun.Tx2),
                };

                ToolTipService.SetToolTip(button, tip);
                button.Click += (_, _) => click();

                return button;
            }

            string where = dock.StableId;

            Button up = Small(
                "ArrowUp", Loc.Tr("ListEarlier", "Move it earlier along the bar"), () => Shift(where, at, -1));

            Button down = Small(
                "ArrowDown", Loc.Tr("ListLater", "Move it later along the bar"), () => Shift(where, at, 1));

            // Not offered where it would do nothing: a widget with no slot of
            // its own cannot trade places with one that has.
            up.IsEnabled = at > 0 && entry.Cell >= 0 && order[at - 1].Cell >= 0;
            down.IsEnabled = at < order.Count - 1 && entry.Cell >= 0 && order[at + 1].Cell >= 0;

            Grid.SetColumn(up, 2);
            Grid.SetColumn(down, 3);
            line.Children.Add(up);
            line.Children.Add(down);

            Button off = Small(
                "Delete",
                Loc.Tr("ListRemove", "Take it off the bar"),
                () => TakeOff(entry, Loc.Tr("UndoRemoved", "a widget taken off")),
                danger: true);

            Grid.SetColumn(off, 4);
            line.Children.Add(off);

            // The whole line chooses it, so its own settings appear below
            // without anybody having to find it on the screen.
            line.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            line.PointerReleased += (_, _) =>
            {
                _selectedId = entry.InstanceId;
                ShowWidgets();
            };

            // And the bar itself says which one this line is about. Two
            // widgets of the same kind read as the same row otherwise, and
            // the arrow next to one of them is then a guess.
            line.PointerEntered += (_, _) => _docks.Point(dock.StableId, entry.InstanceId);
            line.PointerExited += (_, _) => _docks.Point(dock.StableId, null);

            rows.Add(line);
        }

        return rows.Count > 0
            ? Braun.Group([.. rows])
            : Braun.Group(Braun.Row(
                Loc.Tr("ListEmpty", "Nothing on this bar yet."), null, null));
    }

    /// <summary>What to call a widget in the list, without drawing it.</summary>
    private string NameOf(WidgetConfig entry)
    {
        WidgetViewModel? widget = Build(entry);

        try
        {
            string said = widget?.Called ?? string.Empty;

            return said.Length > 0
                ? said
                : WidgetCatalog.Find(entry.TypeId)?.Name ?? entry.TypeId;
        }
        finally
        {
            widget?.Dispose();
        }
    }

    /// <summary>
    /// Moves one widget past its neighbour, by trading places.
    /// </summary>
    /// <remarks>
    /// The slots are swapped rather than the order rewritten, so a bar with
    /// deliberate gaps in it keeps them: the two widgets change places and
    /// everything else stays where it was put.
    /// </remarks>
    private void Shift(string stableId, int at, int by)
    {
        // Read now, not from the list the button was built with: the button
        // outlives its page, and a stale copy made the second press compute
        // the same swap again and write it as a fresh change.
        if (_settings.Current.Monitors.FirstOrDefault(m => m.StableId == stableId) is not { } dock)
        {
            return;
        }

        List<WidgetConfig> order =
        [
            .. dock.Widgets
                .Select((w, i) => (Widget: w, Index: i))
                .OrderBy(x => x.Widget.Cell < 0 ? int.MaxValue : x.Widget.Cell)
                .ThenBy(x => x.Index)
                .Select(x => x.Widget)
        ];

        int to = at + by;

        if (at < 0 || to < 0 || at >= order.Count || to >= order.Count)
        {
            return;
        }

        string moved = order[at].InstanceId;
        string other = order[to].InstanceId;
        int here = order[at].Cell;
        int there = order[to].Cell;

        // One of them has never been placed - a widget added while it had
        // nothing to say, and still waiting for its moment. Trading slots with
        // it would hand a real slot to something invisible and send its
        // neighbour to the end of the bar.
        if (here < 0 || there < 0)
        {
            return;
        }

        Rearrange(
            dock.StableId,
            widgets =>
            [
                .. widgets.Select(w =>
                    w.InstanceId == moved ? w with { Cell = there }
                    : w.InstanceId == other ? w with { Cell = here }
                    : w)
            ],
            Loc.Tr("UndoMoved", "a widget moved"));
    }

    /// <summary>
    /// Gives every other screen the same widgets as this one.
    /// </summary>
    /// <remarks>
    /// The program's first sentence is that each screen has its own bar, and
    /// the price of that was setting each of them up by hand - thirty drags
    /// for three screens, because a widget is put on a bar by dragging it
    /// onto that bar. Copies rather than the same widgets: each bar owns its
    /// own, so taking one off one screen does not take it off the others.
    /// The slots are kept, so a bar arranged in groups arrives in groups; a
    /// narrower screen shuffles them along as it always does.
    /// </remarks>
    private void CopyToOthers()
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        var others = current.Monitors
            .Where(m => m.StableId != dock.StableId)
            .ToList();

        if (others.Count == 0)
        {
            return;
        }

        // Refitted to each destination, not carried by number. A slot number
        // chosen on this screen means a place along this screen; on a screen
        // with fewer slots the literal numbers eat the far end and everything
        // after them has nowhere to go - which is how the copy arrived on the
        // third screen missing six of its ten widgets.
        int? from = _docks.Slots(dock.StableId);

        Write(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(m =>
                    {
                        if (m.StableId == dock.StableId)
                        {
                            return m;
                        }

                        ImmutableArray<WidgetConfig> copies =
                            [.. dock.Widgets.Select(w => w.AsNewInstance())];

                        return m with
                        {
                            Widgets = from is { } f && _docks.Slots(m.StableId) is { } t
                                ? DockGrid.Scaled(copies, f, t)
                                : copies,
                        };
                    })
                ],
            },
            WriteReason.WidgetConfig,
            Loc.Tr("UndoCopied", "the same widgets"));

        _log.LogInformation(
            "settings.copied from={Monitor} to={Count}", dock.StableId, others.Count);

        ReloadDocks();
    }

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
                StrokeThickness = 1.7,
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

            var picture = new Viewbox { Width = 16, Height = 16, Child = canvas };

            List<(Button Pencil, WidgetOffer Offer)> pencils = [];

            // A pencil on the ones whose picture can be changed, and nothing
            // on the clock and the player, which draw their own faces.
            //
            // It is the pencil that changes the picture, not the chip. The
            // chip is what a widget is carried by, and a press on the thing
            // you carry should not quietly repaint every copy of it on every
            // bar - which is what pressing the chip did, and it is how a
            // processor came to be drawn as a cogwheel.
            if (offer.Chooses is not null)
            {
                var pencil = new Button
                {
                    Width = 22,
                    Height = 22,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Braun.PanelHi,
                    BorderBrush = Braun.LineHi,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Content = Braun.Glyph("Pen", 11, Braun.Tx2),
                };

                ToolTipService.SetToolTip(
                    pencil, Loc.Tr("ChipPencil", "Change the picture, on every bar."));

                Grid.SetColumn(pencil, 3);
                row.Children.Add(pencil);
                pencils.Add((pencil, offer));
            }

            row.Children.Add(picture);
            row.Children.Add(name);
            row.Children.Add(count);

            // A chip is dragged onto a bar to put one there, and pressed to
            // change the picture it wears. It is not pressed to add one: a
            // press that silently puts a thing on a screen somewhere else is
            // an action with no visible result, and the bar is the place the
            // thing is going, so the bar is where the gesture should end.
            var chip = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 6, 10, 6),
                MinWidth = 196,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Braun.Line,
                Background = Braun.Card,
                Child = row,
                CanDrag = true,
                AllowDrop = false,
            };

            ToolTipService.SetToolTip(
                chip,
                offer.Chooses is null
                    ? Loc.Tr("ChipDragOnly", "Drag it onto a bar to put one there.")
                    : Loc.Tr("ChipDragOrPick", "Drag it onto a bar to put one there. The pencil changes its picture."));

            WidgetOffer chosen = offer;

            chip.DragStarting += (_, args) =>
            {
                args.Data.SetText(WidgetDrag.Wrap(chosen.Make()));
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            };

            foreach ((Button pencil, WidgetOffer whose) in pencils)
            {
                WidgetOffer picked = whose;
                pencil.Click += (_, _) => PickPicture(picked, pencil);
            }

            gallery.Children.Add(chip);
        }

        return gallery;
    }

    /// <summary>
    /// Offers the pictures this reading can be drawn with.
    /// </summary>
    /// <remarks>
    /// The picture belongs to the reading rather than to one widget: every
    /// processor gauge on every bar wears the same one. That is why this is
    /// reached from the thing being drawn rather than from a page of its own.
    /// </remarks>
    private void PickPicture(WidgetOffer offer, FrameworkElement at)
    {
        if (offer.Chooses is not { } id)
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

        // And a way back. A picture chosen by accident - and it is chosen by
        // accident, because until today the whole chip was the button - could
        // not be un-chosen: the list offers sixty-nine drawings and none of
        // them is "the one it came with".
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };
        panel.Children.Add(grid);

        var flyout = new Flyout { Content = panel, XamlRoot = Content.XamlRoot };

        if (_settings.Current.App.Icons.ContainsKey(id))
        {
            panel.Children.Add(Braun.Action(
                Loc.Tr("IconDefault", "The one it came with"),
                () =>
                {
                    ForgetPicture(id);
                    flyout.Hide();
                },
                "Undo"));
        }

        grid.SelectionChanged += (_, args) =>
        {
            if (args.AddedItems.FirstOrDefault() is IconChoice picked)
            {
                ChoosePicture(id, picked.Name);
                flyout.Hide();
            }
        };

        flyout.ShowAt(at);
    }

    /// <summary>Forgets a chosen picture, so the reading wears its own again.</summary>
    private void ForgetPicture(string id)
    {
        SettingsModel current = _settings.Current;

        Write(
            current with
            {
                App = current.App with { Icons = current.App.Icons.Remove(id) },
            },
            WriteReason.UserAction,
            Loc.Tr("UndoIcon", "a picture"));

        _log.LogInformation("settings.icon {Id} default", id);
        ReloadDocks();
    }

    /// <summary>Writes down which picture a reading is drawn with.</summary>
    private void ChoosePicture(string id, string icon)
    {
        SettingsModel current = _settings.Current;

        Write(
            current with
            {
                App = current.App with { Icons = current.App.Icons.SetItem(id, icon) },
            },
            WriteReason.UserAction,
            Loc.Tr("UndoIcon", "a picture"));

        _log.LogInformation("settings.icon {Id} {Icon}", id, icon);
        ReloadDocks();
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
        if (_selectedId is not { } id || Dock() is not { } dock
            || dock.Widgets.FirstOrDefault(w => w.InstanceId == id) is not { } entry)
        {
            return;
        }

        TakeOff(entry, Loc.Tr("UndoRemovedOne", "removed"));
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
        var button = Braun.Action(UndoLabel(), DoUndo, "Undo");
        button.IsEnabled = _settings.CanUndo;

        return button;
    }

    /// <summary>
    /// The undo buttons now in the tree, one per page that has one.
    /// </summary>
    /// <remarks>
    /// Two, because there are two pages that make changes worth taking back -
    /// and the one that makes most of them was the one without a button.
    /// Held as references to what a page has already built, never as
    /// something to put into the next one: an element that outlives a rebuild
    /// has two parents, and that ended the program once.
    /// </remarks>
    private Button? _undoOnDocks;
    private Button? _undoOnWidgets;

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
        foreach (Button? button in new[] { _undoOnDocks, _undoOnWidgets })
        {
            if (button is null)
            {
                continue;
            }

            button.Content = Braun.Legend(UndoLabel(), "Undo", Braun.Tx2);
            button.IsEnabled = _settings.CanUndo;
        }
    }

    private string UndoLabel()
    {
        if (_settings.UndoWhat is not { } what)
        {
            return Loc.Tr("Undo", "Undo");
        }

        // Named only when it is somebody else's screen. Saying "on this
        // screen" on every button teaches people to stop reading it, and the
        // whole point of the words is the one case where pressing undo moves
        // something they cannot see.
        if (_settings.UndoWhere is { } where && where != _editing)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("UndoElsewhereLabel", "Undo - {0}, {1}"),
                what,
                ScreenName(where));
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            Loc.Tr("UndoWithLabel", "Undo - {0}"),
            what);
    }

    /// <summary>
    /// Writes the settings and remembers that this window did it.
    /// </summary>
    /// <remarks>
    /// Without the mark, every change made here comes back through the
    /// watcher as a change made elsewhere, and lands on the undo stack twice.
    /// </remarks>
    private void Write(
        SettingsModel model, WriteReason reason, string? what = null, string? where = null)
    {
        _mine = model;
        _settings.Commit(model, reason, what, where);
    }

    /// <summary>Puts the settings back to before the last change.</summary>
    private void DoUndo()
    {
        // Read before the undo, which pops it off the stack.
        string? where = _settings.UndoWhere;

        if (!_settings.Undo())
        {
            return;
        }

        // Shown, not only done. A change taken back on a screen the window is
        // not looking at moves nothing anybody can see, and a button that
        // appears to do nothing is a button people press again.
        if (where is not null && where != _editing
            && _settings.Current.Monitors.Any(m => m.StableId == where))
        {
            _editing = where;
        }

        ReloadDocks();
        ShowUndo();
    }

    /// <summary>Builds a widget's view model, to ask it about itself.</summary>
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
                Write(
                    now with { App = now.App with { BackdropColour = chosen } },
                    WriteReason.UserAction,
                    Loc.Tr("UndoLook", "how the bars are painted"));

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
            Write(
                current with { App = current.App with { BackdropImage = file.Path } },
                WriteReason.UserAction,
                Loc.Tr("UndoLook", "how the bars are painted"));

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

        Write(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(
                        c => c.StableId == stableId ? c with { Widgets = change(c.Widgets) } : c)
                ],
            },
            WriteReason.WidgetConfig,
            what,
            stableId);

        _log.LogInformation("settings.dock {What} monitor={Monitor}", what, stableId);

        if (rebuild)
        {
            ShowDock();
            ShowWidgets();
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
        Write(
            current with
            {
                Monitors = [.. current.Monitors.Select(c => c.StableId == stableId ? change(c) : c)],
            },
            WriteReason.UserAction,
            what,
            stableId);

        _log.LogInformation("settings.dock {What} monitor={Monitor}", what, stableId);
        ShowUndo();
    }

    private MonitorConfig? Dock() =>
        _settings.Current.Monitors.FirstOrDefault(m => m.StableId == _editing);

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "docks";

        DocksSection.Visibility = Show(tag == "docks");
        WidgetsSection.Visibility = Show(tag == "widgets");
        AppearanceSection.Visibility = Show(tag == "appearance");
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
                Loc.Tr("SensorsSummary", "{0} readings. Sources: {1}."),
                found.Length,
                Roll());
    }

    /// <summary>
    /// Names every source and says what it is doing.
    /// </summary>
    /// <remarks>
    /// A count answered nothing. The question people bring to this page is
    /// about one source by name - whether the program has found the monitoring
    /// program they installed for it - so the ones that are silent are the
    /// point and stay in the list.
    /// </remarks>
    private string Roll() =>
        string.Join(
            "; ",
            _sensors.Sources
                .OrderBy(s => s.Tier)
                .ThenBy(s => SourceName(s.Id), StringComparer.CurrentCulture)
                .Select(s => $"{SourceName(s.Id)} — {SourceState(s)}"));

    /// <summary>What a source is called, rather than the tag in its keys.</summary>
    private static string SourceName(string id) => id switch
    {
        "pdh" => Loc.Tr("SourcePdh", "Windows counters"),
        "mem" => Loc.Tr("SourceMem", "Windows memory"),
        "acpi" => Loc.Tr("SourceAcpi", "ACPI thermal zones"),
        "disk" => Loc.Tr("SourceDisk", "drive temperatures"),
        "nvml" => Loc.Tr("SourceNvml", "NVIDIA driver"),
        "lhm" => "LibreHardwareMonitor",
        "hwinfo" => "HWiNFO",
        _ => id,
    };

    /// <summary>Why a source has nothing to say, when it has nothing to say.</summary>
    private static string SourceState(SourceRoll source) => source switch
    {
        { Answering: true } => string.Format(
            CultureInfo.CurrentCulture,
            Loc.Tr("SourceAnswering", "answering ({0})"),
            source.Readings),
        { Tier: Tier.External } => Loc.Tr("SourceNotRunning", "not running"),
        _ => Loc.Tr("SourceNotHere", "not on this machine"),
    };

    /// <summary>
    /// Asks what to call a reading, on a double-click of its row.
    /// </summary>
    /// <remarks>
    /// The name given here is used everywhere the reading appears, the bar
    /// included: a drive called "Games" on this page is called "Games" on the
    /// dock, because there is no version of this where two names for one thing
    /// is the friendlier answer. An empty box gives the part's own name back.
    /// </remarks>
    /// <summary>
    /// Asks what to call a reading.
    /// </summary>
    /// <remarks>
    /// Reached two ways on purpose: the pencil, which can be seen, and the
    /// double click, which was the only way and was announced by a tooltip.
    /// The handler takes the plain event args so both can call it.
    /// </remarks>
    private async void OnRenameSensor(object sender, RoutedEventArgs e)
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

        Write(
            current with { Sensors = current.Sensors with { Names = names } },
            WriteReason.UserAction,
            Loc.Tr("UndoRenamed", "a name"));

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
                "A bar for the edge of your screen. Free software under GPL-3.0-or-later; parts adapted from Microsoft PowerToys under the MIT licence."),
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

        // --------------------------------------------------------- shortcuts
        // Keys and language live with the program, not with how the bars are
        // painted: neither is about a bar, and both are about the whole
        // machine. They sat under Appearance because that was the page that
        // was not about one screen.
        AboutBody.Children.Add(Braun.Heading("Sliders", Loc.Tr("KeysGroup", "Keys")));

        AboutBody.Children.Add(Braun.Group(
            KeyRow(
                Shortcut.Bars,
                Loc.Tr("KeyBars", "Show or hide every bar"),
                Loc.Tr("KeyBarsHint", "The bars go away and come back. Their layout is kept.")),
            KeyRow(
                Shortcut.Settings,
                Loc.Tr("KeySettings", "Open this window"),
                Loc.Tr("KeySettingsHint", "From anywhere, including over a full-screen program.")),
            KeyRow(
                Shortcut.Mute,
                Loc.Tr("KeyMute", "Silence the machine"),
                Loc.Tr("KeyMuteHint", "The same as the mute key, for keyboards that have not got one."))));

        AboutBody.Children.Add(Braun.Heading("Globe", Loc.Tr("LookGroupLanguage", "Language")));

        string[] languages = ["system", "en-US", "ru-RU"];

        AboutBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("LanguageLabel", "Language"),
                Loc.Tr("LanguageHint", "Takes effect the next time the program starts."),
                Braun.Segs(
                    [Loc.Tr("LanguageSystemItem", "Same as Windows"), "English", "Русский"],
                    Math.Max(0, Array.IndexOf(languages, _settings.Current.App.Language)),
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

        AboutBody.Children.Add(Braun.Heading("Power", Loc.Tr("StartupTitle", "Startup")));

        AboutBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("StartWithWindowsLabel", "Start with Windows"),
            Loc.Tr("StartWithWindowsHint", "The bars come back when you sign in."),
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
                    "Folder")),

            Braun.Row(
                Loc.Tr("ExportRow", "Save a copy"),
                Loc.Tr(
                    "ExportRowHint",
                    "Everything: the bars, the theme, the keys, what the readings are called."),
                Braun.Action(Loc.Tr("ExportButton", "Save to a file"), ExportSettings, "ArrowDown")),

            Braun.Row(
                Loc.Tr("ImportRow", "Load a copy"),
                _importSaid.Length > 0
                    ? _importSaid
                    : Loc.Tr(
                        "ImportRowHint",
                        "Replaces everything on these pages. The bars go to this machine's screens in the order they were saved. Undo brings it all back."),
                Braun.Action(Loc.Tr("ImportButton", "Load from a file"), ImportSettings, "ArrowUp"))));

        AboutBody.Children.Add(Braun.Heading("Power", Loc.Tr("QuitTitle", "Quitting")));

        AboutBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("ExitRow", "Stop the program"),
            Loc.Tr(
                "ExitHint",
                "Closing this window leaves the bars running. Ending the task in Task Manager leaves the reserved screen space behind."),
            Braun.Action(
                Loc.Tr("ExitButton", "Exit Master Control Dock"),
                () => _onExit(),
                "Power",
                danger: true))));
    }

    /// <summary>This build's version, for the About page.</summary>
    private string _version = string.Empty;

    /// <summary>What the last load did, said on the row that did it.</summary>
    private string _importSaid = string.Empty;

    /// <summary>
    /// Writes everything the program knows to a file somebody chooses.
    /// </summary>
    /// <remarks>
    /// The same shape as the file it keeps for itself, so it can be read and
    /// edited by whoever wants to.
    /// </remarks>
    private async void ExportSettings()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "master-control-dock",
        };

        picker.FileTypeChoices.Add(Loc.Tr("SettingsFileKind", "Settings"), [".json"]);

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }

        try
        {
            _settings.Export(file.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "settings.export failed");
        }
    }

    /// <summary>
    /// Takes everything from a file, and says what became of the bars.
    /// </summary>
    /// <remarks>
    /// The count is the honest part. This machine may have fewer screens than
    /// the one the file came from, and a bar with nowhere to go is simply not
    /// brought over - which is worth saying on the row rather than leaving
    /// somebody to count their bars.
    /// </remarks>
    private async void ImportSettings()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };

        picker.FileTypeFilter.Add(".json");

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        int? filled = _settings.Import(file.Path, Loc.Tr("UndoImported", "settings loaded from a file"));

        _importSaid = filled is { } bars
            ? string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("ImportDone", "Loaded. {0} of this machine's bars were filled in."),
                bars)
            : Loc.Tr("ImportBad", "That file is not a settings file this program can read.");

        _mine = _settings.Current;

        ReloadDocks();
        ShowAbout();
    }


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

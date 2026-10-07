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
    private readonly Mcd.Sensors.Host.HostClient _host;
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
        Mcd.Sensors.Host.HostClient host,
        Action onExit)
    {

        _log = log;
        _settings = settings;
        _docks = docks;
        _sensors = sensors;
        _host = host;
        _onExit = onExit;

        InitializeComponent();

        SystemBackdrop = null;

        // The Braun body, painted from code: a ThemeResource on the root
        // element cannot see the root's own dictionary during the parse.
        PaintBody();
        Root.ActualThemeChanged += (_, _) => PaintBody();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        // The taskbar's own theme picks which of the two drawings the window wears.
        AppWindow.SetIcon(Path.Combine(
            AppContext.BaseDirectory, "Assets", TaskbarIsLight() ? "icon-light.ico" : "icon.ico"));

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

        _toast = DispatcherQueue.CreateTimer();
        _toast.Interval = TimeSpan.FromSeconds(5);
        _toast.IsRepeating = false;
        _toast.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;

        _flash = DispatcherQueue.CreateTimer();
        _flash.Interval = TimeSpan.FromMilliseconds(1600);
        _flash.IsRepeating = false;
        _flash.Tick += (_, _) => _docks.Point(string.Empty, null);

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

        // The switch at the foot of the pane is worded for what it will do.
        BarsShown(_docks.Visible);
        _docks.VisibleChanged += OnBarsVisible;
        Closed += (_, _) => _docks.VisibleChanged -= OnBarsVisible;

        // The undo rows are gone from the pages - they were furniture - but
        // the promise in the hints ("Ctrl+Z brings it back") has to be kept
        // by an actual key.
        var undoKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Z,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };

        undoKey.Invoked += (_, e) =>
        {
            e.Handled = true;
            DoUndo();
        };

        Root.KeyboardAccelerators.Add(undoKey);

        // A widget carried over this window says where it is going instead of
        // showing the system's "forbidden" sign, which read as "this does not
        // work" rather than "not here, on a bar".
        Root.AllowDrop = true;
        Root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                && e.DragUIOverride is { } hint)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                hint.IsGlyphVisible = false;
                hint.IsCaptionVisible = true;
                hint.Caption = Loc.Tr("DragToBar", "Drop it on a bar");
            }
        };

        Root.Drop += (_, e) => e.Handled = true;

        Reload();
        // Their own titles and screen switchers are the widgets page's now.
        foreach (StackPanel part in new[] { PinsSection, PresetsSection })
        {
            part.Children[0].Visibility = Visibility.Collapsed;
            part.Children[1].Visibility = Visibility.Collapsed;
        }

        PinsTabs.Visibility = Visibility.Collapsed;
        PresetsTabs.Visibility = Visibility.Collapsed;

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnBarsVisible(object? sender, bool visible) =>
        DispatcherQueue.TryEnqueue(() => BarsShown(visible));

    private void BarsShown(bool visible) =>
        HideText.Text = visible
            ? Loc.Tr("BarMenuHideAll", "Hide the bars")
            : Loc.Tr("BarsShow", "Show the bars");

    /// <summary>The footer switch: hides every bar, or brings them back.</summary>
    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string == "hide")
        {
            _docks.Visible = !_docks.Visible;
        }
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

        // A change made on the bar itself is announced here too - the bar
        // has no room for a sentence, and this window is open.
        if (_settings.UndoWhat is { } what && !ReferenceEquals(what, _said))
        {
            Say(what);
        }
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

            // No maximize - not on the caption button and not on a double
            // click of the title bar. The window sizes itself to its content;
            // stretched over a whole display it is mostly margin, and the
            // gesture was being made by accident.
            presenter.IsMaximizable = false;
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
        // page to open: a pinned icon lives on the pins page, everything else
        // on the widgets page. Pointing at bare bar opens the page about the
        // bar.
        bool pin = widgetId is not null
            && _settings.Current.Monitors.FirstOrDefault(m => m.StableId == _editing)
                ?.Widgets.FirstOrDefault(w => w.InstanceId == widgetId)?.TypeId == IconWidget.Type;

        Nav.SelectedItem = widgetId is null
            ? Nav.MenuItems[0]
            : Nav.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => (i.Tag as string) == "widgets")
            ?? Nav.MenuItems[1];

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
        // "widgets@0.5" scrolls halfway: the middle of a long page is where
        // things go wrong unseen, and the top and the foot miss it.
        double? part = tag.Split('@') is [_, var fraction]
            && double.TryParse(fraction, CultureInfo.InvariantCulture, out double f) ? f : null;
        tag = tag.Split('@')[0];

        bool foot = tag.EndsWith('!');
        string wanted = foot ? tag[..^1] : tag;

        foreach (object item in Nav.MenuItems)
        {
            if (item is not NavigationViewItem entry || (entry.Tag as string) != wanted)
            {
                continue;
            }

            Nav.SelectedItem = entry;

            // After the pages have been laid out, not before: the page just
            // switched to has no extent yet.
            DispatcherQueue.TryEnqueue(() =>
            {
                // Laid out first: a page just switched to reports no extent,
                // and a scroll to "half of nothing" stays at the top.
                Pages.UpdateLayout();
                Pages.ChangeView(
                    null,
                    part is { } share ? Pages.ScrollableHeight * share : foot ? Pages.ScrollableHeight : 0,
                    null,
                    disableAnimation: true);
            });

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

        foreach (object item in Nav.MenuItems)
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

        // One decision per row, the current answer readable at a glance, the
        // alternatives behind it. This page was a checkerboard of thirteen
        // shouting segment keys for four quiet questions, and finding the one
        // to change meant reading all of them.
        string[] backdrops = ["acrylic", "solid", "colour", "image", "braun"];

        ComboBox Pick(IReadOnlyList<string> labels, int index, Action<int> chosen)
        {
            var box = new ComboBox
            {
                MinWidth = 200,
                ItemsSource = labels.ToList(),
                SelectedIndex = Math.Clamp(index, 0, labels.Count - 1),
            };

            box.SelectionChanged += (_, _) =>
            {
                if (!_filling && box.SelectedIndex >= 0 && box.SelectedIndex != index)
                {
                    chosen(box.SelectedIndex);
                }
            };

            return box;
        }

        // Two or three short answers are shown side by side: a list that has
        // to be opened to learn that "Compact" exists hides the choice.
        Panel Keys(IReadOnlyList<string> labels, int index, Action<int> chosen) =>
            Braun.Segs(
                labels,
                index,
                i =>
                {
                    if (!_filling && i != index)
                    {
                        chosen(i);
                    }
                },
                wide: true);

        LookBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("ThemeLabel", "Theme"),
                Loc.Tr("ThemeHint", "The bars and this window follow it together."),
                Keys(
                    [
                        Loc.Tr("SegThemeSystem", "Match Windows"),
                        Loc.Tr("SegThemeLight", "Light"),
                        Loc.Tr("SegThemeDark", "Dark"),
                    ],
                    Appearance.Index(look.Theme),
                    i => ApplyLook(theme: Appearance.FromIndex(i))),
                stack: true),

            Braun.Row(
                Loc.Tr("BackgroundLabel", "Background"),
                Loc.Tr(
                    "BackgroundHint",
                    "Translucent lets the desktop through; solid is easier to read over a busy wallpaper and costs a little less to draw."),
                Pick(
                    [
                        Loc.Tr("SegBackdropTranslucent", "Translucent"),
                        Loc.Tr("SegBackdropSolid", "Solid"),
                        Loc.Tr("SegBackdropColour", "A colour"),
                        Loc.Tr("SegBackdropImage", "A picture"),
                        Loc.Tr("SegBackdropBraun", "Instrument"),
                    ],
                    Math.Max(0, Array.IndexOf(backdrops, look.Backdrop)),
                    i => ApplyLook(backdrop: backdrops[i]))),

            BackdropExtraRow(look),

            Braun.Row(
                Loc.Tr("AccentLabel", "Colour of the readings"),
                Loc.Tr(
                    "AccentHint",
                    "The accent colour comes from your Windows settings. Readings past their warning level keep their warning colour either way."),
                Keys(
                    [
                        Loc.Tr("SegAccentNeutral", "Plain text"),
                        Loc.Tr("SegAccentWindows", "Windows accent"),
                    ],
                    look.Accent == "windows" ? 1 : 0,
                    i => ApplyLook(accent: i == 1 ? "windows" : "neutral")),
                stack: true)));

        // ------------------------------------------------------------- sample
        // The bar lives on another screen; what these rows do to it is shown
        // here, on a strip that follows them.
        var chosen = new IconChoices(look.Icons);

        LookBody.Children.Add(Braun.Heading("Layout", Loc.Tr("LookSampleGroup", "Sample")));
        LookBody.Children.Add(Sample(look, chosen));

        // -------------------------------------------------------------- icons
        // Which picture a reading wears, on every bar. This used to be a
        // pencil on the gallery chip - a local-looking key with a global
        // effect, which is how a processor came to be a cogwheel everywhere.
        LookBody.Children.Add(Braun.Heading("Star", Loc.Tr("LookIconsGroup", "Icons of the readings")));

        var iconRows = new List<FrameworkElement?>();

        foreach ((string id, string label, string fallback) in IconChoices.Known)
        {
            string wearing = chosen.For(id, fallback);
            string pick = id;

            Button change = Braun.Action(Loc.Tr("IconChange", "Change..."), () => { }, "Pen");
            change.Click += (_, _) => PickPicture(pick, change);

            var control = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };

            control.Children.Add(Braun.Tile(30, Braun.Glyph(wearing, 18, Braun.Tx)));
            control.Children.Add(change);

            iconRows.Add(Braun.Row(label, null, control));
        }

        LookBody.Children.Add(Braun.Group([.. iconRows]));
    }

    /// <summary>A strip of bar as the appearance settings would draw it.</summary>
    private FrameworkElement Sample(AppSettings look, IconChoices chosen)
    {
        double tall = look.Size switch { "small" => 36, "medium" => 42, _ => 48 };
        double font = look.Size switch { "small" => 12, "medium" => 13, _ => 15 };
        double icon = look.Size switch { "small" => 16, "medium" => 18, _ => 20 };

        Windows.UI.Color ground = look.Backdrop switch
        {
            "colour" => Swatch(look.BackdropColour),
            "braun" => Windows.UI.Color.FromArgb(255, 0x1C, 0x1F, 0x24),
            "acrylic" or "image" => Windows.UI.Color.FromArgb(255, 0x24, 0x27, 0x2E),
            _ => Root.ActualTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3)
                : Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20),
        };

        bool dark = (ground.R * 299 + ground.G * 587 + ground.B * 114) / 1000 < 128;

        Brush ink = look.Accent == "windows"
            ? Braun.Acc
            : new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 0xE9, 0xEC, 0xF1)
                : Windows.UI.Color.FromArgb(255, 0x23, 0x26, 0x2B));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        (string Glyph, string Figure, string Label)[] readings =
        [
            (chosen.For("cpu", "Cpu"), "37 %", Loc.Tr("LabelCpu", "CPU")),
            (chosen.For("temp", "Temperature"), "51 \u00b0C", Loc.Tr("LabelGpu", "GPU")),
            ("Clock", "12:54", string.Empty),
        ];

        foreach ((string glyph, string figure, string label) in readings)
        {
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            cell.Children.Add(Braun.Glyph(glyph, icon, ink));

            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = figure, FontSize = font, Foreground = ink });

            if (label.Length > 0)
            {
                words.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = Math.Max(9, font - 4),
                    Foreground = ink,
                    Opacity = 0.7,
                });
            }

            cell.Children.Add(words);
            row.Children.Add(cell);
        }

        return new Border
        {
            Height = tall,
            Background = new SolidColorBrush(ground),
            BorderBrush = Braun.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = row,
        };
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
            "Keyboard"));

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
        ShowGeneral();
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
        ShowGeneral();
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

        // The same switcher on every page that acts on one screen. Which
        // screen a page was about used to be a fact kept on another page,
        // and "I pinned it and it is not in the list" was that fact missing.
        foreach (ContentControl slot in new[] { DisplayTabs, WidgetsTabs, PinsTabs, PresetsTabs })
        {
            slot.Content = Braun.Tabs(tabs, chosen, i =>
            {
                _editing = monitors[i].StableId;
                ReloadDocks();
            });
        }

        ShowDock();
        ShowWidgets();
        ShowPins();
        ShowPresets();
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
            // With its size: two screens of one model are otherwise told apart
            // by a number nobody can see on the glass.
            return string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("DisplayNumber", "Display {0}"),
                int.Parse(digits, CultureInfo.InvariantCulture))
                + $" · {screen.Width}×{screen.Height}";
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

            // Which glass is which: each screen shows its number for a moment.
            _docks.Plans.Length > 1
                ? Braun.Row(
                    Loc.Tr("IdentifyRow", "Which screen is which"),
                    Loc.Tr("IdentifyRowHint", "Shows each screen's number on it for a moment."),
                    Braun.Action(
                        Loc.Tr("IdentifyButton", "Identify"),
                        () => Mcd.App.Dock.IdentifyScreens.Flash(_docks.Plans.Select(p => p.Monitor)),
                        "Computer"))
                : null,

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

            null));

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
                EdgeBoard(
                    dock.Edge,
                    edge => SetDock(d => d with { Edge = edge }, Loc.Tr("UndoEdge", "edge")))),

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
                    null),

            Braun.Row(
            Loc.Tr("ReadingSizeLabel", "Size of the readings"),
            Loc.Tr(
                "ReadingSizeHint",
                "How large the icons and figures are drawn. The bar itself stays the thickness chosen on its own page."),
            Braun.Segs(
                [
                    Loc.Tr("SegSizeLarge", "Large"),
                    Loc.Tr("SegSizeMedium", "Medium"),
                    Loc.Tr("SegSizeSmall", "Small"),
                ],
                Math.Max(0, Array.IndexOf(Sizes, _settings.Current.App.Size)),
                i => ApplyLook(size: Sizes[i]),
                wide: true),
            stack: true)));

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

        // The rows that point at widgets are about to be thrown away, and a
        // row that goes without a farewell leaves its line drawn on the bar.
        _docks.Point(string.Empty, null);
        WidgetsBody.Children.Clear();
        OnBarBody.Children.Clear();

        MonitorConfig? dock = _settings.Current.Monitors
            .FirstOrDefault(m => m.StableId == _editing);

        WidgetsSection.Opacity = dock is null ? 0.5 : 1;
        OnBarSection.Opacity = dock is null ? 0.5 : 1;

        if (dock is null)
        {
            WidgetsBody.Children.Add(Braun.Group(Braun.Row(
                Loc.Tr("DockNoScreen", "No screen has been set up yet."), null, null)));

            return;
        }

        // Said where it is wanted: the temperature of the processor and the
        // memory is not on offer below until it can be read, and nothing else
        // on this page would ever say why.
        if (!_sensors.Catalog.Any(d => d.Kind == SensorKind.Temperature && d.Group == HardwareGroup.Cpu))
        {
            WidgetsBody.Children.Add(Braun.Group(Braun.Row(
                Loc.Tr("TempMissingRow", "Processor and memory temperature"),
                Loc.Tr("TempMissingHint", "Not readable yet. It needs a driver and a small service: two steps on the Readings page, and the widgets appear here by themselves."),
                Braun.Action(Loc.Tr("TempMissingButton", "Set it up..."), () => GoTo("sensors"), "Gear"))));
        }

        // ---------------------------------------------- what else could be on it
        WidgetsBody.Children.Add(Braun.Heading("Layout", Loc.Tr("GalleryHeading", "Widgets")));

        WidgetsBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("GalleryRow", "Every widget"),
                Loc.Tr(
                    "GalleryRowHint",
                    "Drag one onto a bar and it lands where you drop it. Two of the same is fine."),
                Gallery(dock),
                stack: true),

            // The ones on the bar with nothing to say: a reading whose part
            // has gone, a battery pulled out. They hold no slot, so without
            // this row the only sign of them is a name in the list above and
            // no chip on the bar - which reads as a widget that broke.
            Quiet(dock) is { Count: > 0 } quiet
                ? Braun.Row(
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
                        "Delete"))
                : null,

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
                        danger: false))
                : null,


            // Only where there is somewhere to copy to.
            _settings.Current.Monitors.Length > 1
                ? Braun.Row(
                    Loc.Tr("CopyRow", "The other screens"),
                    Loc.Tr(
                        "CopyRowHint",
                        "Gives every other screen the same widgets, refitted to the shape of this bar. Each bar keeps its own copies; Ctrl+Z puts the others back."),
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
                    "Puts back the set the bar came with. Ctrl+Z brings your own arrangement back."),
                // Dressed as what it is. It throws away the whole
                // arrangement of this screen - every pinned program, every
                // widget's own settings - and it was wearing a quieter coat
                // than the button that takes off a single widget.
                Braun.Action(
                    Loc.Tr("ResetButton", "Restore the standard bar"),
                    ResetDock,
                    "Undo",
                    danger: false))));

        // ------------------------------------------------------------- the bar
        // The gestures, as a line rather than as a settings row with nothing
        // to set: a row in a card promises a control.
        OnBarBody.Children.Add(new TextBlock
        {
            Text = Loc.Tr(
                "BarGestures",
                "Drag to move \u00b7 off the edge to remove \u00b7 right-click for the menu"),
            FontSize = 12,
            Foreground = Braun.Tx3,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // ------------------------------------------------------------ contents
        OnBarBody.Children.Add(Braun.Heading("List", Loc.Tr("GalleryTitle", "On the bar")));

        OnBarBody.Children.Add(BarList(dock));

        // ------------------------------------------------------- chosen widget
        if (Inspector() is { } inspector)
        {
            _inspectorAt = Braun.Heading("Sliders", _inspectorName);

            OnBarBody.Children.Add(_inspectorAt);
            OnBarBody.Children.Add(inspector);
        }
        else
        {
            _inspectorAt = null;
        }
    }


    /// <summary>
    /// Fills the page about the programs pinned to the chosen bar.
    /// </summary>
    /// <remarks>
    /// A page of its own so a pinned program has somewhere to be seen the
    /// moment it is pinned. On the widgets page a fresh pin landed as the last
    /// row of a long list, below the fold - present, and invisible.
    /// </remarks>
    private void ShowPins()
    {
        Braun.Theme = Root.ActualTheme;
        _docks.Point(string.Empty, null);
        PinsBody.Children.Clear();

        MonitorConfig? dock = _settings.Current.Monitors
            .FirstOrDefault(m => m.StableId == _editing);

        PinsSection.Opacity = dock is null ? 0.5 : 1;

        if (dock is null)
        {
            PinsBody.Children.Add(Braun.Group(Braun.Row(
                Loc.Tr("DockNoScreen", "No screen has been set up yet."), null, null)));

            return;
        }

        PinsBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("PinRow", "A program of your own"),
            Loc.Tr(
                "PinRowHint",
                "Pinned programs sit on the bar as icons. A file dropped straight onto the bar is pinned to the slot it lands on."),
            Braun.Action(
                Loc.Tr("PinProgramButton", "Pin a program..."), () => _ = PinDialog(), "Plus"))));

        PinsBody.Children.Add(Braun.Heading(
            "Rocket", Loc.Tr("PinnedListTitle", "Pinned to this bar")));

        List<WidgetConfig> pins =
        [
            .. dock.Widgets
                .Where(w => w.TypeId == IconWidget.Type)
                .OrderBy(w => w.Cell < 0 ? int.MaxValue : w.Cell)
        ];

        if (pins.Count == 0)
        {
            PinsBody.Children.Add(Braun.Group(Braun.Row(
                Loc.Tr(
                    "PinsEmpty",
                    "Nothing is pinned yet. Drop a file onto the bar, or pin one above."),
                null,
                null)));

            return;
        }

        var rows = new List<FrameworkElement?>();

        foreach (WidgetConfig entry in pins)
        {
            string name = _docks.Called(dock.StableId, entry.InstanceId) ?? NameOf(entry);
            string target = WidgetOptions.Text(entry.Config, "target") ?? string.Empty;

            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            // The folder, for a file - what the taskbar offers on the same
            // gesture. An address has no folder.
            if (Path.IsPathRooted(target))
            {
                keys.Children.Add(Braun.Action(
                    Loc.Tr("PinOpenFolder", "Open folder"), () => Reveal(target), "Folder"));
            }

            keys.Children.Add(Braun.Action(
                Loc.Tr("ListRemove", "Take it off the bar"),
                () => TakeOff(entry, Loc.Tr("UndoRemoved", "a widget taken off")),
                "Delete"));

            Grid row = Braun.Row(name, Middle(target, 56), keys);
            ToolTipService.SetToolTip(row, target);

            // The same gestures the widget list answers: the line chooses it,
            // and the bar itself says which icon the line is about.
            row.Background = entry.InstanceId == _selectedId
                ? Braun.PanelHi
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            row.PointerReleased += (_, _) =>
            {
                _selectedId = entry.InstanceId;
                ShowPins();
            };

            row.PointerEntered += (_, _) => _docks.Point(dock.StableId, entry.InstanceId);
            row.PointerExited += (_, _) => _docks.Point(dock.StableId, null);

            rows.Add(row);
        }

        PinsBody.Children.Add(Braun.Group([.. rows]));

        // The chosen pin's name and icon, edited right here - the pin lives on
        // this page, so its settings do too.
        if (pins.FirstOrDefault(p => p.InstanceId == _selectedId) is { } chosen)
        {
            _pinShown?.Dispose();
            _pinShown = Build(chosen);

            string id = chosen.InstanceId;

            if (_pinShown?.CreateEditor(options => OnInspectorConfigured(id, options)) is { } editor)
            {
                PinsBody.Children.Add(Braun.Heading(
                    "Sliders", Loc.Tr("PinsEditorTitle", "Name and icon")));

                PinsBody.Children.Add(Braun.Group(Braun.Row(
                    _docks.Called(dock.StableId, id) ?? NameOf(chosen), null, editor, stack: true)));
            }
        }
    }

    /// <summary>The pin whose editor is on the pins page, kept to be disposed.</summary>
    private WidgetViewModel? _pinShown;

    /// <summary>Shows a file in its folder, the way the shell does.</summary>
    private void Reveal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "settings.pin could not show {Target}", target);
        }
    }

    /// <summary>
    /// A path shortened in the middle, where paths carry the least.
    /// </summary>
    /// <remarks>
    /// Cut at the end, a path loses the file; cut at the start, the drive.
    /// The framework only cuts at the end.
    /// </remarks>
    private static string Middle(string text, int most)
    {
        if (text.Length <= most)
        {
            return text;
        }

        int head = (most - 1) * 2 / 5;
        int tail = most - 1 - head;

        return text[..head] + "\u2026" + text[^tail..];
    }

    /// <summary>Fills the page of saved arrangements.</summary>
    private void ShowPresets()
    {
        Braun.Theme = Root.ActualTheme;
        PresetsBody.Children.Clear();

        var rows = new List<FrameworkElement?>
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
                Loc.Tr("PresetDelete", "Forget it"), () => DeletePreset(id), "Delete", danger: false);

            // Automatically when this many screens are connected.
            var desk = new ComboBox
            {
                MinWidth = 190,
                ItemsSource = new[]
                {
                    Loc.Tr("DeskOff", "Auto: never"),
                    Loc.Tr("DeskOne", "Auto: with 1 screen"),
                    string.Format(CultureInfo.CurrentCulture, Loc.Tr("DeskMany", "Auto: with {0} screens"), 2),
                    string.Format(CultureInfo.CurrentCulture, Loc.Tr("DeskMany", "Auto: with {0} screens"), 3),
                    string.Format(CultureInfo.CurrentCulture, Loc.Tr("DeskMany", "Auto: with {0} screens"), 4),
                },
                SelectedIndex = Math.Clamp(preset.Screens, 0, 4),
            };

            ToolTipService.SetToolTip(desk, Loc.Tr("DeskTip", "Put it on the main screen's bar by itself when this many screens are connected"));

            desk.SelectionChanged += (_, _) =>
            {
                if (desk.SelectedIndex == Math.Clamp(preset.Screens, 0, 4))
                {
                    return;
                }

                int screens = desk.SelectedIndex;

                SettingsModel current = _settings.Current;
                Write(
                    current with
                    {
                        App = current.App with
                        {
                            // One layout per screen count: choosing a count another
                            // layout holds takes it from that one.
                            Presets =
                            [
                                .. current.App.Presets.Select(p => p.Id == id
                                    ? p with { Screens = screens }
                                    : screens > 0 && p.Screens == screens ? p with { Screens = 0 } : p)
                            ],
                        },
                    },
                    WriteReason.UserAction,
                    Loc.Tr("UndoPresetDesk", "when a layout is used"));
                ShowPresets();
            };

            rows.Add(Braun.Row(
                preset.Name,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("PresetHint", "{0} widgets"),
                    preset.Widgets.Length),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { desk, apply, drop },
                }));
        }

        if (!_settings.Current.App.Presets.Any())
        {
            rows.Add(Braun.Row(
                Loc.Tr(
                    "PresetsEmpty",
                    "None yet. A saved arrangement goes onto any screen with one press."),
                null,
                null));
        }

        PresetsBody.Children.Add(Braun.Group([.. rows]));
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
        ShowPresets();
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

        SettingsModel current = _settings.Current;

        // Carried whole - cells, spans and the slot count they were written
        // for. The bar itself refits an arrangement from a different bar,
        // preserving the gaps, because only it knows its own slot count and
        // its widgets' widths at today's sizes.
        Write(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(m => m.StableId == dock.StableId
                        ? m with { Widgets = copies, Slots = preset.Slots }
                        : m)
                ],
            },
            WriteReason.WidgetConfig,
            Loc.Tr("UndoPresetApplied", "a preset put on"),
            dock.StableId);

        _log.LogInformation("settings.preset applied name={Name} monitor={Monitor}", preset.Name, dock.StableId);
        ShowDock();
        ShowWidgets();
        ShowPins();
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

        ShowPresets();
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

    /// <summary>
    /// The screen as a little drawing, with a key on each of its four edges.
    /// </summary>
    /// <remarks>
    /// The question is "which side of the screen", so the control is a screen
    /// with sides: press the side the bar should stand on. The chosen edge
    /// wears the accent, exactly where the bar itself will be. Four words in a
    /// row said the same thing more slowly, and said nothing about geometry.
    /// </remarks>
    private FrameworkElement EdgeBoard(AppBarEdge chosen, Action<AppBarEdge> pick)
    {
        const double Wide = 176;
        const double Tall = 110;
        const double Bar = 15;
        const double In = 8;

        var face = new Grid { Width = Wide, Height = Tall };

        face.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            RadiusX = 10,
            RadiusY = 10,
            Fill = Braun.CardHi,
            Stroke = Braun.LineHi,
            StrokeThickness = 1,
        });

        (AppBarEdge Edge, string Name)[] sides =
        [
            (AppBarEdge.Left, Loc.Tr("SegEdgeLeft", "Left")),
            (AppBarEdge.Top, Loc.Tr("SegEdgeTop", "Top")),
            (AppBarEdge.Right, Loc.Tr("SegEdgeRight", "Right")),
            (AppBarEdge.Bottom, Loc.Tr("SegEdgeBottom", "Bottom")),
        ];

        foreach ((AppBarEdge edge, string name) in sides)
        {
            bool on = edge == chosen;

            var strip = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                RadiusX = 4,
                RadiusY = 4,
                Fill = on ? Braun.Acc : Braun.Sunk,
                Stroke = on ? null : Braun.LineHi,
                StrokeThickness = on ? 0 : 1,
            };

            var key = new Button
            {
                Content = strip,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };

            if (DockMetrics.IsHorizontal(edge))
            {
                key.Height = Bar;
                key.HorizontalAlignment = HorizontalAlignment.Stretch;
                key.VerticalAlignment = edge == AppBarEdge.Top
                    ? VerticalAlignment.Top
                    : VerticalAlignment.Bottom;
                key.Margin = edge == AppBarEdge.Top
                    ? new Thickness(In + Bar + 3, In, In + Bar + 3, 0)
                    : new Thickness(In + Bar + 3, 0, In + Bar + 3, In);
            }
            else
            {
                key.Width = Bar;
                key.VerticalAlignment = VerticalAlignment.Stretch;
                key.HorizontalAlignment = edge == AppBarEdge.Left
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Right;
                key.Margin = edge == AppBarEdge.Left
                    ? new Thickness(In, In + Bar + 3, 0, In + Bar + 3)
                    : new Thickness(0, In + Bar + 3, In, In + Bar + 3);
            }

            ToolTipService.SetToolTip(key, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(key, name);

            // A key that answers the pointer is a key; a strip that does not
            // is a picture of the state.
            key.PointerEntered += (_, _) =>
            {
                if (!on)
                {
                    strip.Fill = Braun.LineHi;
                }
            };

            key.PointerExited += (_, _) =>
            {
                if (!on)
                {
                    strip.Fill = Braun.Sunk;
                }
            };

            AppBarEdge picked = edge;
            key.Click += (_, _) =>
            {
                if (picked != chosen)
                {
                    pick(picked);
                }
            };

            face.Children.Add(key);
        }

        var board = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        board.Children.Add(face);
        board.Children.Add(new TextBlock
        {
            Text = Loc.Tr("EdgeBoardHint", "Press a side"),
            FontSize = 12,
            Foreground = Braun.Tx3,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return board;
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

        // Twins get numbers. Two widgets of one kind are allowed and common,
        // and two rows saying "CPU" leave the arrows aimed by guesswork.
        var seen = new Dictionary<string, int>(StringComparer.CurrentCulture);
        var counts = new Dictionary<string, int>(StringComparer.CurrentCulture);

        List<string> names =
        [
            .. order.Select(w => _docks.Called(dock.StableId, w.InstanceId) ?? NameOf(w))
        ];

        foreach (string n in names)
        {
            counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
        }

        for (int i = 0; i < order.Count; i++)
        {
            WidgetConfig entry = order[i];
            int at = i;

            string called = names[i];

            if (counts[called] > 1)
            {
                seen[called] = seen.TryGetValue(called, out int nth) ? nth + 1 : 1;
                called = $"{called} \u00b7 {seen[called]}";
            }

            var line = new Grid { ColumnSpacing = 6, Padding = new Thickness(13, 7, 13, 7) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                // The widget standing on the bar, asked by name: the copy
                // built here has never met a sensor, so it would call the
                // roving thermometer "the hottest" while the bar's own says
                // "GPU" - and a person comparing the two counts a widget
                // missing.
                Text = called,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Braun.Tx,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // A handle at the head of the line: the mark of a row that can be
            // taken hold of, which these can.
            var lead = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            lead.Children.Add(Braun.Glyph("List", 12, Braun.Tx3));
            lead.Children.Add(name);
            line.Children.Add(lead);

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
                danger: false);

            Grid.SetColumn(off, 4);
            line.Children.Add(off);

            // The three buttons appear when the row is pointed at or when the
            // keyboard reaches them - twelve rows of three always-lit keys,
            // a third of them red, was the noisiest thing in the window. They
            // stay in the tree and tabbable, so nothing is lost to a keyboard.
            foreach (Button key in new[] { up, down, off })
            {
                key.Opacity = 0;
                key.GotFocus += (_, _) => key.Opacity = 1;
                key.LostFocus += (_, _) => key.Opacity = 0;
            }

            // The whole line chooses it, so its own settings appear below
            // without anybody having to find it on the screen. Chosen is a
            // filled container, not a text colour: accent-blue text read as a
            // hyperlink, and colour alone is invisible to half the eyes.
            line.Background = entry.InstanceId == _selectedId
                ? Braun.PanelHi
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            line.PointerReleased += (_, _) =>
            {
                _selectedId = entry.InstanceId;
                ShowWidgets();
            };

            // And the bar itself says which one this line is about, while
            // the row's own keys light up.
            line.PointerEntered += (_, _) =>
            {
                if (entry.InstanceId != _selectedId)
                {
                    line.Background = Braun.CardHi;
                }

                _docks.Point(dock.StableId, entry.InstanceId);
                up.Opacity = 1;
                down.Opacity = 1;
                off.Opacity = 1;
            };

            line.PointerExited += (_, _) =>
            {
                if (entry.InstanceId != _selectedId)
                {
                    line.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }

                _docks.Point(dock.StableId, null);
                up.Opacity = 0;
                down.Opacity = 0;
                off.Opacity = 0;
            };

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

        // Carried whole - cells, spans, and the slot count they were written
        // for - and refitted by each receiving bar itself, which is the only
        // party that knows its own slot count and its widgets' widths at its
        // own density. Scaled here by position alone, the copy arrived with
        // gaps where the widths differed.
        Write(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(m => m.StableId == dock.StableId
                        ? m
                        : m with
                        {
                            Widgets = [.. dock.Widgets.Select(w => w.AsNewInstance())],
                            Slots = dock.Slots,
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
    private FrameworkElement Gallery(MonitorConfig dock)
    {
        // One wrap of chips under each part of the machine. Twenty chips in
        // one heap, three of them called "Memory", was the disorder this
        // heading answers; under a heading a chip needs only its own word.
        var shelves = new Dictionary<string, VariableSizedWrapGrid>(StringComparer.Ordinal);

        VariableSizedWrapGrid Shelf(string category)
        {
            if (!shelves.TryGetValue(category, out VariableSizedWrapGrid? shelf))
            {
                shelf = new VariableSizedWrapGrid
                {
                    ItemHeight = 104,
                    ItemWidth = 200,
                    Orientation = Orientation.Horizontal,
                };

                shelves[category] = shelf;
            }

            return shelf;
        }

        var chips = new List<(Border Chip, string Category, string Words)>();

        foreach (WidgetOffer offer in WidgetCatalog.Offers(_sensors))
        {
            VariableSizedWrapGrid gallery = Shelf(offer.Category);
            int already = dock.Widgets.Count(offer.Matches);

            // What it will look like on the bar, in a small dark plate: the
            // same icon, small name and figure, with an ordinary figure in
            // place of the live one. The name is a caption beside it.
            Border picture = BarSample(offer.Sample ?? WidgetSample.Of(offer.Icon));

            // Two lines rather than an ellipsis: "Temperature - the h..."
            // was cut exactly where the meaning began.
            var name = new TextBlock
            {
                Text = offer.Short ?? offer.Name,
                FontSize = 11,
                Foreground = Braun.Tx2,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            };

            // Two rows: the widget as it looks, centred, and under it the
            // name on the left with the count and the plus on the right.
            var row = new Grid { ColumnSpacing = 8, RowSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            picture.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumnSpan(picture, 3);
            Grid.SetRow(name, 1);

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

            Grid.SetRow(count, 1);
            Grid.SetColumn(count, 1);

            // A plus puts one on the chosen bar and has the bar point at it.
            // A press used to do nothing, on purpose - its result landed on
            // another screen with nothing to watch - and the drag it left as
            // the only way in is a drag between windows, across monitors,
            // sometimes holding a hiding bar open with the same hand. The
            // pointing is what makes the press honest.
            var plus = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Braun.PanelHi,
                BorderBrush = Braun.LineHi,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Content = Braun.Glyph("Plus", 12, Braun.Tx2),
            };

            ToolTipService.SetToolTip(plus, Loc.Tr("GalleryRow", "Put one on the bar"));

            Grid.SetRow(plus, 1);
            Grid.SetColumn(plus, 2);
            row.Children.Add(plus);

            WidgetOffer adding = offer;
            plus.Click += (_, _) => AddOffer(adding);

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
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(8, 8, 8, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Braun.Line,
                Background = Braun.Card,
                Child = row,
                AllowDrop = false,
            };

            // What is picked up is the widget's own picture, not the whole
            // card with its caption, count and plus.
            picture.CanDrag = true;

            ToolTipService.SetToolTip(
                chip,
                offer.Description + "\n" + Loc.Tr(
                    "ChipHint", "Drag it onto a bar, or press the plus to put one on the chosen bar."));

            WidgetOffer chosen = offer;

            picture.DragStarting += (_, args) =>
            {
                args.Data.SetText(WidgetDrag.Wrap(chosen.Make()));
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            };

            gallery.Children.Add(chip);
            chips.Add((chip, offer.Category, (offer.Name + " " + (offer.Short ?? string.Empty) + " " + offer.Description).ToLowerInvariant()));
        }

        var stack = new StackPanel { Spacing = 0 };
        var headings = new Dictionary<string, TextBlock>(StringComparer.Ordinal);

        // Never fewer than two columns, however narrow the window: the cells
        // share the width out evenly instead of keeping one size and dropping
        // to one column. Measured on the stack, which is as wide as the page
        // lets it be; a shelf is only as wide as its own cells.
        stack.SizeChanged += (_, size) =>
        {
            double width = size.NewSize.Width - 2;
            int columns = Math.Max(2, (int)(width / 205));

            foreach (VariableSizedWrapGrid shelf in shelves.Values)
            {
                shelf.ItemWidth = Math.Floor(width / columns);
            }
        };

        foreach ((string id, string heading) in WidgetCatalog.Categories)
        {
            if (!shelves.TryGetValue(id, out VariableSizedWrapGrid? shelf))
            {
                continue;
            }

            TextBlock caption = Braun.Micro(heading + "  " + shelf.Children.Count.ToString(CultureInfo.CurrentCulture));
            caption.Margin = new Thickness(2, 6, 0, 4);
            stack.Children.Add(caption);
            stack.Children.Add(shelf);
            headings[id] = caption;
        }

        // A search through the whole catalogue: what matches stays, every shelf
        // with nothing left under it goes with its heading. It looks at the
        // name, the short caption and the description, in any case.
        var search = new TextBox
        {
            PlaceholderText = Loc.Tr("GallerySearch", "Search widgets"),
            Margin = new Thickness(0, 0, 8, 4),
        };

        search.TextChanged += (_, _) =>
        {
            string wanted = search.Text.Trim().ToLowerInvariant();

            foreach ((Border chip, string category, string words) in chips)
            {
                chip.Visibility = wanted.Length == 0 || words.Contains(wanted, StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            foreach ((string category, VariableSizedWrapGrid shelf) in shelves)
            {
                bool any = chips.Any(c => c.Category == category && c.Chip.Visibility == Visibility.Visible);
                shelf.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

                if (headings.TryGetValue(category, out TextBlock? caption))
                {
                    caption.Visibility = shelf.Visibility;
                }
            }
        };

        var whole = new StackPanel { Spacing = 2 };
        whole.Children.Add(search);
        whole.Children.Add(stack);

        return whole;
    }

    /// <summary>
    /// A widget as it looks on the bar: a small dark plate with its icons, and
    /// the small name over the figure the way a full-size bar writes them.
    /// </summary>
    private static Border BarSample(WidgetSample sample)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (string icon in sample.Icons)
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = IconRow.Draw(icon),
                Stroke = Braun.Tx,
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            line.Children.Add(new Viewbox { Width = 18, Height = 18, Child = canvas });
        }

        if (sample.Figure.Length > 0)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            if (sample.Label.Length > 0)
            {
                text.Children.Add(new TextBlock { Text = sample.Label, FontSize = 9, Foreground = Braun.Tx3, LineHeight = 10 });
            }

            text.Children.Add(new TextBlock { Text = sample.Figure, FontSize = 13, Foreground = Braun.Tx, LineHeight = 15 });
            line.Children.Add(text);
        }

        return new Border
        {
            MinWidth = 120,
            Height = 42,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            // Darker than the chip it sits on, the way the bar is darker than
            // the desktop round it.
            Background = new SolidColorBrush(Braun.Theme != ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0x70, 0x00, 0x00, 0x00)
                : Windows.UI.Color.FromArgb(0x1A, 0x00, 0x00, 0x00)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = line,
        };
    }

    /// <summary>Puts one of these on the chosen bar, and has the bar point at it.</summary>
    private void AddOffer(WidgetOffer offer)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        WidgetConfig made = offer.Make();

        Rearrange(
            dock.StableId,
            widgets => [.. widgets, made],
            string.Format(CultureInfo.CurrentCulture, Loc.Tr("UndoAdded", "added {0}"), offer.Name));

        Flash(dock.StableId, made.InstanceId);
    }

    /// <summary>
    /// Offers the pictures this reading can be drawn with.
    /// </summary>
    /// <remarks>
    /// The picture belongs to the reading rather than to one widget: every
    /// processor gauge on every bar wears the same one. That is why this is
    /// reached from the thing being drawn rather than from a page of its own.
    /// </remarks>
    private void PickPicture(string id, FrameworkElement at)
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };

        var flyout = new Flyout { Content = panel, XamlRoot = Content.XamlRoot };

        string wearing = _settings.Current.App.Icons.TryGetValue(id, out string? worn)
            ? worn
            : Mcd.App.Widgets.IconChoices.Known.FirstOrDefault(k => k.Id == id).Fallback ?? string.Empty;

        panel.Children.Insert(0, Mcd.App.Widgets.IconPicker.Build(wearing, picked =>
        {
            ChoosePicture(id, picked);
            flyout.Hide();
        }));

        // And a way back. A picture chosen by accident could not be
        // un-chosen: the list offers two thousand drawings and none of them
        // is "the one it came with".
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
        RefreshLook();
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
        RefreshLook();
    }

    /// <summary>The controls of the chosen widget's own settings, and whose they are.</summary>
    private FrameworkElement? _inspectorEditor;

    private string _inspectorFor = string.Empty;

    /// <summary>
    /// Chooses a widget on the widgets page and presses a switch in its own
    /// settings, the way a person does. For the unattended check: a switch
    /// that wrote to the widget chosen before this one changed a chip nobody
    /// was looking at.
    /// </summary>
    /// <summary>
    /// Chooses the first pinned program on the widgets page and opens the
    /// list of icons to change its picture to, as pressing "Change" does.
    /// For the unattended check.
    /// </summary>
    /// <param name="search">Typed into the search box once it is open, or empty.</param>
    private Microsoft.UI.Xaml.Controls.VariableSizedWrapGrid? _pickerGrid;

    private TextBox? _pickerBox;

    /// <summary>Types into the open picker's search box, as a person does. For the unattended check.</summary>
    public void TypeInPicker(string text)
    {
        if (_pickerBox is not null)
        {
            _pickerBox.Text = text;
        }
    }

    /// <summary>How many icons the open picker is showing. For the unattended check.</summary>
    public int PickerShows() => _pickerGrid?.Children.Count ?? -1;

    public string OpenIconPicker(string search)
    {
        if (Dock() is not { } dock
            || dock.Widgets.FirstOrDefault(w => w.TypeId == Mcd.App.Widgets.IconWidget.Type) is not { } pinned)
        {
            return "no pinned program";
        }

        // The button on the page in front of the person, not in an editor
        // that was built and never shown: a flyout opened from the second
        // has nowhere to appear.
        static Button? FindChange(DependencyObject at)
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(at); i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(at, i);

                if (child is Button { Flyout: not null, ActualWidth: > 0 } found)
                {
                    return found;
                }

                if (FindChange(child) is { } deeper)
                {
                    return deeper;
                }
            }

            return null;
        }

        Button? change = FindChange(Content);

        if (change is null)
        {
            return "no Change button";
        }

        change.Flyout.ShowAt(change);

        if (search.Length > 0
            && change.Flyout is Flyout { Content: StackPanel content }
            && content.Children.Count > 0
            && content.Children[0] is StackPanel picker
            && picker.Children[0] is Grid header
            && header.Children[1] is TextBox box)
        {
            _pickerGrid = (picker.Children[1] as ScrollViewer)?.Content as Microsoft.UI.Xaml.Controls.VariableSizedWrapGrid;
            _pickerBox = box;
        }

        return $"opened for {pinned.InstanceId[..8]}, open={change.Flyout.IsOpen}, at {change.ActualWidth}x{change.ActualHeight}, root={change.XamlRoot is not null}";
    }

    public string RehearseSwitch(string reading, int pill)
    {
        if (Dock() is not { } dock)
        {
            return "no dock";
        }

        WidgetConfig[] gauges =
        [
            .. dock.Widgets.Where(w => w.TypeId == Mcd.App.Widgets.GaugeWidget.Type
                && (Mcd.App.Widgets.WidgetOptions.Text(w.Config, "reading") ?? "cpu") == reading)
        ];

        if (gauges.Length == 0)
        {
            return $"no {reading} gauge";
        }

        _selectedId = gauges[0].InstanceId;
        ShowWidgets();

        var pills = new List<Button>();

        void Find(DependencyObject at)
        {
            if (at is not Panel panel)
            {
                return;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is Button found)
                {
                    pills.Add(found);
                }

                Find(child);
            }
        }

        Find(_inspectorEditor!);

        if (pill >= pills.Count)
        {
            return $"editor for {Short(_inspectorFor)} has {pills.Count} switches";
        }

        if (pill >= 0)
        {
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(pills[pill]);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer).Invoke();
        }

        return $"chose {Short(_selectedId!)}, editor for {Short(_inspectorFor)}, pressed {pill} of {pills.Count}: "
            + string.Join(
                " ",
                Dock()!.Widgets
                    .Where(w => w.TypeId == Mcd.App.Widgets.GaugeWidget.Type)
                    .Select(w => $"{Short(w.InstanceId)}="
                        + (w.Config is { } json ? Compact(json.GetRawText()) : "null")));
    }

    /// <summary>
    /// One line of JSON, for a log line: written out again without
    /// indentation, so the spaces inside values - a program's name, a path
    /// under Program Files - survive.
    /// </summary>
    private static string Compact(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(Unescaped) ?? "null";

    /// <summary>Names as written - "Документы" - not as a line of escapes.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Unescaped = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The first eight characters of a widget's id, or the whole of a shorter one.</summary>
    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

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

        _inspectorEditor = editor;
        _inspectorFor = id;

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
                    danger: false)));
    }

    private void OnInspectorConfigured(string id, System.Text.Json.JsonElement? options)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        // Which widget, and what it now says. Without this line a report of
        // "I changed one widget and another one moved" cannot be told from
        // "I changed the one I had chosen before".
        _log.LogInformation(
            "settings.widget {Id} {Was} -> {Now}",
            Short(id),
            Compact(dock.Widgets.FirstOrDefault(w => w.InstanceId == id)?.Config?.GetRawText() ?? "null"),
            Compact(options?.GetRawText() ?? "null"));

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

        // The widgets page, while it is open, follows what can be read: the
        // moment a temperature appears its widgets are on offer, without
        // leaving the page and coming back.
        if (WidgetsSection.Visibility == Visibility.Visible)
        {
            int temperatures = _sensors.Catalog.Count(d => d.Kind == SensorKind.Temperature);

            if (_temperaturesOffered != temperatures)
            {
                _temperaturesOffered = temperatures;
                ShowWidgets();
            }
        }
    }

    private int _temperaturesOffered = -1;

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

        if (what is not null)
        {
            Say(what);
        }
    }

    /// <summary>
    /// Says what just happened, with the way back beside it.
    /// </summary>
    /// <remarks>
    /// Five seconds, then gone. The undo itself never went anywhere; what
    /// was missing was the sentence that says a thing happened and can be
    /// taken back - without it a button that did its job looked like a
    /// button that did nothing.
    /// </remarks>
    private void Say(string what)
    {
        _said = what;

        ToastText.Text = string.Format(
            CultureInfo.CurrentCulture, Loc.Tr("ToastDone", "Done: {0}"), what);

        ToastAction.Content = Braun.Action(
            Loc.Tr("ToastUndo", "Undo"),
            () =>
            {
                Toast.Visibility = Visibility.Collapsed;
                DoUndo();
            },
            "Undo");

        Toast.Visibility = Visibility.Visible;
        _toast.Stop();
        _toast.Start();
    }

    /// <summary>The last change announced, by identity, so a change is announced once.</summary>
    private string? _said;

    private readonly DispatcherQueueTimer _toast;

    /// <summary>Has the bar point at one widget for a moment, then let go.</summary>
    private void Flash(string stableId, string instanceId)
    {
        _docks.Point(stableId, instanceId);
        _flash.Stop();
        _flash.Start();
    }

    private readonly DispatcherQueueTimer _flash;

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
            ShowPins();
        }
        else
        {
            // A widget's own options changed: the bar redraws itself, and the
            // page must not rebuild the editor the person is typing into.
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
    }

    private MonitorConfig? Dock() =>
        _settings.Current.Monitors.FirstOrDefault(m => m.StableId == _editing);

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "docks";

        DocksSection.Visibility = Show(tag == "docks");
        WidgetsSection.Visibility = Show(tag == "widgets");
        // Programs and saved layouts are parts of the same page: one answer
        // to "what is on the bar", where there used to be three pages to
        // look through for it.
        PinsSection.Visibility = Show(tag == "widgets");
        PresetsSection.Visibility = Show(tag == "widgets");
        OnBarSection.Visibility = Show(tag == "widgets");
        AppearanceSection.Visibility = Show(tag == "appearance");
        SensorsSection.Visibility = Show(tag == "sensors");
        GeneralSection.Visibility = Show(tag == "general");
        AboutSection.Visibility = Show(tag == "about");

        if (tag == "widgets")
        {
            ShowWidgets();
        }

        if (tag == "about")
        {
            ShowAbout();
        }

        if (tag == "general")
        {
            ShowGeneral();
        }

        // Only while the page that shows live figures is on screen. A window
        // sitting behind everything else has no business waking the machine
        // once a second.
        if (tag is "sensors" or "widgets")
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
                .ThenBy(d => d.Hardware, StringComparer.CurrentCulture)
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
            string was = string.Empty;

            foreach (SensorDescriptor sensor in found)
            {
                // Seventeen rows in one undivided list made every reading
                // carry its device's name; grouped, the device is said once,
                // as a heading.
                string device = DeviceName(sensor);

                _readings.Add(new SensorRow(
                    sensor, names, OnSensorRenamed, device == was ? "" : device));

                was = device;
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
                Loc.Tr("SensorsCount", "{0} readings."),
                found.Length);

        ShowSources();
    }

    /// <summary>The device a reading belongs to, for the group headings.</summary>
    private static string DeviceName(SensorDescriptor sensor) => sensor.Group switch
    {
        HardwareGroup.Cpu => Loc.Tr("DeviceCpu", "Processor"),
        HardwareGroup.Gpu => Loc.Tr("DeviceGpu", "Graphics"),
        HardwareGroup.Memory => Loc.Tr("DeviceMemory", "Memory"),
        HardwareGroup.Storage => Loc.Tr("DeviceStorage", "Drives"),
        HardwareGroup.Network => Loc.Tr("DeviceNetwork", "Network"),
        HardwareGroup.Peripheral => Loc.Tr("DevicePeripherals", "Wireless devices"),
        _ => Loc.Tr("DeviceBoard", "Board"),
    };

    /// <summary>What the last drawn source list looked like, to rebuild it only on change.</summary>
    private string _sourcesShown = string.Empty;

    /// <summary>
    /// One line per source, in place of a paragraph strung with semicolons.
    /// </summary>
    /// <remarks>
    /// The question people bring here is about one source by name, and a
    /// sentence made them read all of it. A list is scanned; and the silent
    /// sources stay in it, because they are the point.
    /// </remarks>
    private void ShowSources()
    {
        var roll = _sensors.Sources
            .Where(s => s.Tier != Tier.External)
            .OrderBy(s => s.Tier)
            .ThenBy(s => SourceName(s.Id), StringComparer.CurrentCulture)
            .ToList();

        // The driver and the service are in the signature: putting either in
        // changes the next step this page offers, and the page has to notice
        // without the program being closed and opened.
        string signature = string.Join(
            ";", roll.Select(s => $"{s.Id}:{s.Answering}:{s.Readings}"))
            + $"|{Mcd.Interop.PawnIo.PawnIo.InstalledVersion()}|{_host.Connected}";

        if (signature == _sourcesShown)
        {
            return;
        }

        _sourcesShown = signature;
        SourcesList.Children.Clear();

        // The verdict first, the roll call after it for whoever wants it.
        bool silent = roll.Any(x => !x.Answering && x.Tier == Tier.Driver);

        SourcesList.Children.Add(new TextBlock
        {
            Text = silent
                ? Loc.Tr("SourcesSomeSilent", "Some temperatures cannot be read yet. The row below says what is missing.")
                : Loc.Tr("SourcesAllWell", "Everything this machine can report is being read."),
            FontSize = 13,
            Foreground = Braun.Tx2,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (SourceRoll source in roll)
        {
            var line = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = SourceName(source.Id),
                FontSize = 12,
                Foreground = source.Answering ? Braun.Tx2 : Braun.Tx3,
            };

            var state = new TextBlock
            {
                Text = SourceState(source),
                FontSize = 12,
                Foreground = Braun.Tx3,
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(state, 1);
            line.Children.Add(name);
            line.Children.Add(state);
            SourcesList.Children.Add(line);
        }

        // The one thing on this page a person can do about a silent source.
        // The processor's and the memory's own thermometers sit behind a
        // kernel driver this program does not carry; PawnIO is signed, open,
        // installed once, and the button goes to its author's page - the
        // program neither downloads nor runs anything itself.
        if (roll.Any(s => s.Tier == Tier.Driver && !s.Answering))
        {
            SourcesList.Children.Add(DriverRow());
        }
    }

    /// <summary>
    /// The two steps between a bare machine and the processor's temperature,
    /// whichever is next: the driver, then the service that reads through it.
    /// </summary>
    /// <remarks>
    /// The driver admits administrators only, so the reading is done by a
    /// small service of this program's own, put in once with a UAC prompt by
    /// the button here. The bar itself never asks for rights.
    /// </remarks>
    private FrameworkElement DriverRow()
    {
        string version = Mcd.Interop.PawnIo.PawnIo.InstalledVersion() ?? string.Empty;
        Grid row;

        if (version.Length == 0)
        {
            row = Braun.Row(
                Loc.Tr("DriverRow", "Processor and memory temperatures"),
                Loc.Tr(
                    "DriverRowHint",
                    "They live behind a kernel driver. PawnIO is a signed, open one, installed once with administrator rights; a small service of this program then reads through it."),
                Braun.Action(
                    Loc.Tr("DriverGet", "Get the driver..."),
                    () => Open("https://pawnio.eu/"),
                    "Download"));
        }
        else if (!_host.Connected)
        {
            string service = Path.Combine(AppContext.BaseDirectory, "SensorHost", "MasterControlDock.Sensors.exe");

            row = Braun.Row(
                Loc.Tr("ServiceRow", "The sensor service"),
                File.Exists(service)
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Tr("ServiceRowHint", "PawnIO {0} is in. The service that reads through it runs as the system and is put in once - this asks for administrator rights."),
                        version)
                    : Loc.Tr("ServiceRowMissing", "The service program is not in this build."),
                Braun.Action(
                    Loc.Tr("ServiceInstall", "Install the service..."),
                    () => InstallService(service),
                    "Gear"));
        }
        else
        {
            row = Braun.Row(
                Loc.Tr("ServiceRow", "The sensor service"),
                Loc.Tr("ServiceRowWaiting", "The service is running and the readings are on their way."),
                null);
        }

        row.Margin = new Thickness(0, 8, 0, 0);
        return Braun.Group(row);
    }

    /// <summary>Runs the service's own installer, elevated. The UAC prompt is the person's to answer.</summary>
    private void InstallService(string service)
    {
        try
        {
            Process.Start(new ProcessStartInfo(service, "--install")
            {
                UseShellExecute = true,
                Verb = "runas",
            });

            _log.LogInformation("settings.service install requested");

            // The service takes a moment to start; the sources are asked to
            // look again as it does, so the readings arrive by themselves.
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(2500);
                    _sensors.ProbeNow();
                }
            });
        }
        catch (Exception e)
        {
            // Declined at the prompt, most likely. Nothing to do but say so
            // in the log; the row stays as it is.
            _log.LogWarning(e, "settings.service could not start the installer");
        }
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
        "dev" => Loc.Tr("SourceDev", "wireless devices"),
        "cpu" => Loc.Tr("SourceCpu", "processor registers"),
        "dimm" => Loc.Tr("SourceDimm", "memory modules"),
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
        { Tier: Tier.Driver } => Mcd.Interop.PawnIo.PawnIo.InstalledVersion() is null
            ? Loc.Tr("SourceNeedsDriver", "needs the PawnIO driver")
            : Loc.Tr("SourceNeedsService", "needs the sensor service"),
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
                Children =
                {
                    _newTarget,
                    browse,
                    _newName,
                    new TextBlock
                    {
                        Text = Loc.Tr(
                            "PinDialogHint",
                            "Or drop a file straight onto the bar: it is pinned where it lands."),
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
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

        Flash(dock.StableId, pin.InstanceId);
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
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(22),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 14),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/" + (Root.ActualTheme == ElementTheme.Light ? "icon-light-128.png" : "icon-128.png") + "")),
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

        // Nothing here runs by itself: the program contacts nobody until this
        // is pressed.
        AboutBody.Children.Add(Braun.Heading("Download", Loc.Tr("UpdateTitle", "Updates")));

        AboutBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("UpdateRow", "This version"),
            _updateNote.Length > 0
                ? _updateNote
                : Loc.Tr("UpdateRowHint", "The program contacts nobody until you press the button."),
            _updating
                ? null
                : _offer is { } offer
                    ? Braun.Action(
                        string.Format(CultureInfo.CurrentCulture, Loc.Tr("UpdateInstall", "Install {0} and restart"), offer.Version),
                        InstallUpdate,
                        "Download")
                    : Braun.Action(Loc.Tr("UpdateCheck", "Check for updates"), CheckUpdate, "Download"))));

        // Only the program's own papers. The machine's settings - keys,
        // language, startup, copies - have a page of their own; a page called
        // "the program" that was mostly settings was a page nobody could
        // guess the contents of.
        AboutBody.Children.Add(Braun.Heading("Document", Loc.Tr("FilesTitle", "Its own files")));

        AboutBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("LogsRow", "The log"),
                Loc.Tr("LogsRowHint", "What the program wrote down about its own run."),
                Named(
                    Braun.Action(
                        Loc.Tr("OpenFolder", "Open the folder"),
                        () => Open(AppPaths.LogDirectory),
                        "Folder"),
                    Loc.Tr("LogsRow", "The log"))),

            Braun.Row(
                Loc.Tr("ConfigRow", "The settings file"),
                Loc.Tr("ConfigRowHint", "Everything on these pages, as it is stored on disk."),
                Named(
                    Braun.Action(
                        Loc.Tr("OpenFolder", "Open the folder"),
                        () => Open(AppPaths.Root),
                        "Folder"),
                    Loc.Tr("ConfigRow", "The settings file")))));

        AboutBody.Children.Add(Braun.Heading("Power", Loc.Tr("QuitTitle", "Quitting")));

        AboutBody.Children.Add(Braun.Group(Braun.Row(
            Loc.Tr("ExitRow", "Stop the program"),
            Loc.Tr(
                "ExitHint",
                "Closing this window leaves the bars running. Ending the task in Task Manager leaves the reserved screen space behind."),
            Braun.Action(Loc.Tr("ExitButton", "Quit"), () => _onExit(), "Power"))));
    }

    /// <summary>Whether Windows draws its taskbar light.</summary>
    private static bool TaskbarIsLight()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

        return key?.GetValue("SystemUsesLightTheme") is 1;
    }

    private string _updateNote = string.Empty;
    private Mcd.Core.Update.UpdateOffer? _offer;
    private bool _updating;

    private async void CheckUpdate()
    {
        _updating = true;
        _offer = null;
        _updateNote = Loc.Tr("UpdateChecking", "Looking for a newer version...");
        ShowAbout();

        try
        {
            Mcd.Core.Update.UpdateOffer found = await Mcd.Core.Update.Updater.LatestAsync();

            if (Mcd.Core.Update.Updater.IsNewer(found.Version, _version))
            {
                _offer = found;
                _updateNote = string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("UpdateFound", "Version {0} is available. {1}"),
                    found.Version,
                    found.Notes);
            }
            else
            {
                _updateNote = Loc.Tr("UpdateLatest", "This is the newest version.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "update.check failed");
            _updateNote = Loc.Tr("UpdateFailed", "Could not reach the release page. Nothing was changed.");
        }

        _updating = false;
        ShowAbout();
    }

    private async void InstallUpdate()
    {
        if (_offer is not { } offer)
        {
            return;
        }

        _updating = true;
        _updateNote = Loc.Tr("UpdateDownloading", "Downloading and checking the installer...");
        ShowAbout();

        try
        {
            string installer = await Mcd.Core.Update.Updater.DownloadAsync(offer, null);

            _log.LogInformation("update.install version={Version}", offer.Version);
            Mcd.Core.Update.Updater.Install(installer);

            // The installer is already starting; the bars are given back
            // properly, not killed (a killed bar leaves its strip reserved).
            _onExit();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "update.install failed");
            _updateNote = Loc.Tr("UpdateRefused", "The update was not installed: it could not be downloaded or did not pass the signature check.");
            _updating = false;
            ShowAbout();
        }
    }

    /// <summary>
    /// The whole machine's settings: keys, language, startup, copies.
    /// </summary>
    /// <remarks>
    /// Everything here is about the program as installed on this computer -
    /// nothing is about one bar or one screen. It lived on the About page,
    /// which grew until the information about the program was the thing
    /// hardest to find on it.
    /// </remarks>
    private void ShowGeneral()
    {
        Braun.Theme = Root.ActualTheme;
        GeneralBody.Children.Clear();

        GeneralBody.Children.Add(Braun.Heading("Sliders", Loc.Tr("KeysGroup", "Keys")));

        GeneralBody.Children.Add(Braun.Group(
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
                Loc.Tr("KeyMuteHint", "The same as the mute key, for keyboards that have not got one.")),
            KeyRow(
                Shortcut.Mic,
                Loc.Tr("KeyMic", "Switch the microphone off"),
                Loc.Tr("KeyMicHint", "Off and on again, for the whole machine, from anywhere.")),
            KeyRow(
                Shortcut.Focus,
                Loc.Tr("KeyFocus", "Go to the bar"),
                Loc.Tr("KeyFocusHint", "Arrow keys choose a widget, Enter presses it, Esc leaves."))));

        GeneralBody.Children.Add(Braun.Heading("Globe", Loc.Tr("LookGroupLanguage", "Language")));

        // A drop-down rather than a row of segments: the segments were laid
        // out for exactly three choices, and this list is meant to grow.
        string[] languages = ["system", "en-US", "ru-RU"];

        var combo = new ComboBox
        {
            MinWidth = 220,
            ItemsSource = new List<string>
            {
                Loc.Tr("LanguageSystemItem", "Same as Windows"),
                "English",
                "Русский",
            },
            SelectedIndex = Math.Max(0, Array.IndexOf(languages, _settings.Current.App.Language)),
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0
                && languages[combo.SelectedIndex] != _settings.Current.App.Language)
            {
                PickLanguage(languages[combo.SelectedIndex]);
            }
        };

        GeneralBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("LanguageLabel", "Language"),
                Loc.Tr("LanguageHint", "Takes effect the next time the program starts."),
                combo),

            // Offered only once a language has actually been chosen: people
            // reasonably think closing this window is a restart.
            _restartOffered
                ? Braun.Row(
                    Loc.Tr("RestartRow", "The language has changed"),
                    Loc.Tr("RestartRowHint", "The bars will read in the new language once the program starts again."),
                    Braun.Action(Loc.Tr("RestartNowText", "Restart now"), Restart, "Undo"))
                : null));

        GeneralBody.Children.Add(Braun.Heading("Power", Loc.Tr("StartupTitle", "Startup")));

        GeneralBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("StartWithWindowsLabel", "Start with Windows"),
                Loc.Tr("StartWithWindowsHint", "The bars come back when you sign in."),
                Braun.Switch(AutoStart.Enabled, on =>
                {
                    AutoStart.Enabled = on;
                    _log.LogInformation("settings.autostart enabled={Enabled}", on);
                    ShowGeneral();
                })),
            Braun.Row(
                Loc.Tr("TrayLabel", "Show in the notification area"),
                Loc.Tr("TrayHint", "An icon beside the clock: a click opens the settings, a right click has the menu."),
                Braun.Switch(_settings.Current.App.TrayIcon, on =>
                {
                    SettingsModel current = _settings.Current;
                    Write(
                        current with { App = current.App with { TrayIcon = on } },
                        WriteReason.UserAction,
                        Loc.Tr("UndoTray", "the notification-area icon"));
                    ShowGeneral();
                }))));

        GeneralBody.Children.Add(Braun.Heading("Document", Loc.Tr("CopiesTitle", "Copies")));

        GeneralBody.Children.Add(Braun.Group(
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
                        "Replaces everything on these pages. The bars go to this machine's screens in the order they were saved. Ctrl+Z brings it all back."),
                Braun.Action(Loc.Tr("ImportButton", "Load from a file"), ImportSettings, "ArrowUp"))));
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
        ShowGeneral();
    }


    /// <summary>Two buttons with one label need two names for the narrator.</summary>
    private static Button Named(Button button, string context)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            button,
            string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("OpenFolderOf", "Open the folder: {0}"),
                context));

        return button;
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

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
    private readonly HwInfoProvider _hwinfo;
    private readonly LhmProvider _lhm;
    private readonly Action _onExit;
    private readonly ObservableCollection<MonitorRow> _known = [];
    private readonly ObservableCollection<WidgetCard> _cards = [];

    /// <summary>The dock being edited, by its monitor's stable id.</summary>
    private string? _editing;

    /// <summary>True while controls are being filled in, so their events mean nothing.</summary>
    private bool _filling;

    /// <summary>The widgets drawn in the picture of the bar, so they can be ticked.</summary>
    private readonly List<WidgetPreview> _preview = [];

    /// <summary>
    /// Layouts as they were before each change, most recent first.
    /// </summary>
    /// <remarks>
    /// There is no Save button and nothing to discard: what the settings say and
    /// what the bar does are never allowed to differ. This is what makes that
    /// safe - a mistake is one keystroke back rather than a form to abandon.
    /// </remarks>
    private readonly Stack<(string StableId, ImmutableArray<WidgetConfig> Before, string Label)> _undo = new();
    private readonly ObservableCollection<IconRow> _icons = [];
    private readonly ObservableCollection<LauncherRow> _launcher = [];
    private readonly ObservableCollection<SensorRow> _readings = [];
    private readonly DispatcherQueueTimer _refresh;

    public SettingsWindow(
        ILogger log,
        SettingsService settings,
        DockWindowManager docks,
        SensorHub sensors,
        HwInfoProvider hwinfo,
        LhmProvider lhm,
        Action onExit)
    {

        _log = log;
        _settings = settings;
        _docks = docks;
        _sensors = sensors;
        _hwinfo = hwinfo;
        _lhm = lhm;
        _onExit = onExit;

        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        SizeAndCentre(screen: null);

        WidgetList.ItemsSource = _cards;
        MonitorList.ItemsSource = _known;
        IconList.ItemsSource = _icons;
        LauncherList.ItemsSource = _launcher;
        SensorList.ItemsSource = _readings;
        VersionText.Text = $"Version {AppInfo.Version}";

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
    public void SizeAndCentre(MonitorInfo? screen)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        double scale = screen?.Scale ?? WindowFrame.GetScale(hwnd);
        RECT bounds = screen?.Bounds ?? Displays.BoundsFor(hwnd);

        var size = new Windows.Graphics.SizeInt32(
            (int)Math.Round(1000 * scale),
            (int)Math.Round(760 * scale));

        AppWindow.Resize(size);

        AppWindow.Move(new Windows.Graphics.PointInt32(
            bounds.left + ((bounds.right - bounds.left) - size.Width) / 2,
            bounds.top + ((bounds.bottom - bounds.top) - size.Height) / 2));
    }

    /// <summary>Rebuilds both lists from the settings and the live topology.</summary>
    public void Reload()
    {
        Dictionary<string, MonitorInfo> live = _docks.Plans
            .ToDictionary(p => p.Config.StableId, p => p.Monitor);

        _known.Clear();
        _icons.Clear();
        _launcher.Clear();

        foreach (LaunchItem item in _settings.Current.App.Launcher)
        {
            _launcher.Add(new LauncherRow(item));
        }

        AppSettings look = _settings.Current.App;

        _filling = true;
        ThemeChoice.SelectedIndex = Appearance.Index(look.Theme);
        BackdropChoice.SelectedIndex = look.Backdrop == "solid" ? 1 : 0;
        AccentChoice.SelectedIndex = look.Accent == "windows" ? 1 : 0;
        _filling = false;

        Root.RequestedTheme = Appearance.Of(look.Theme);

        HwInfoSwitch.IsOn = _settings.Current.Sensors.EnabledProviders
            .GetValueOrDefault(HwInfoProvider.ProviderId);

        LhmSwitch.IsOn = _settings.Current.Sensors.EnabledProviders
            .GetValueOrDefault(LhmProvider.ProviderId);

        LhmAddress.Text = _settings.Current.Sensors.LhmHttpEndpoint;

        foreach ((string id, string label, string fallback) in IconChoices.Known)
        {
            _icons.Add(new IconRow(id, label, Chosen(id, fallback), OnIconChosen));
        }

        foreach (MonitorConfig config in _settings.Current.Monitors)
        {
            live.TryGetValue(config.StableId, out MonitorInfo? monitor);
            _known.Add(new MonitorRow(config, monitor, OnRowEdited));
        }

        ReloadDocks();
    }

    /// <summary>Rebuilds the dock picker and shows whichever dock was chosen.</summary>
    private void ReloadDocks()
    {
        ImmutableArray<MonitorConfig> monitors = _settings.Current.Monitors;
        Dictionary<string, MonitorInfo> live = _docks.Plans
            .ToDictionary(p => p.Config.StableId, p => p.Monitor);

        _filling = true;
        DockPicker.Items.Clear();

        for (int i = 0; i < monitors.Length; i++)
        {
            DockPicker.Items.Add(new SelectorBarItem
            {
                Text = Label(monitors[i], monitors, live, i),
                Tag = monitors[i].StableId,
            });
        }

        SelectorBarItem? chosen =
            DockPicker.Items.FirstOrDefault(item => (string?)item.Tag == _editing)
            ?? DockPicker.Items.FirstOrDefault();

        DockPicker.SelectedItem = chosen;
        _editing = chosen?.Tag as string;
        _filling = false;

        ShowDock();
    }

    /// <summary>
    /// A name for the picker that tells two identical screens apart.
    /// </summary>
    /// <remarks>
    /// Two monitors of the same model report the same friendly name, and a
    /// picker offering "RTK 2555" twice is a picker nobody can use.
    /// </remarks>
    private static string Label(
        MonitorConfig config,
        ImmutableArray<MonitorConfig> all,
        Dictionary<string, MonitorInfo> live,
        int index)
    {
        string name = config.FriendlyName.Length > 0 ? config.FriendlyName : "Screen";

        if (all.Count(m => m.FriendlyName == config.FriendlyName) < 2)
        {
            return name;
        }

        string tail = live.TryGetValue(config.StableId, out MonitorInfo? monitor)
            ? new string([.. monitor.Identity.GdiName.Where(char.IsDigit)])
            : (index + 1).ToString(CultureInfo.InvariantCulture);

        return $"{name} #{tail}";
    }

    /// <summary>Fills every control on the Docks page from the chosen dock.</summary>
    private void ShowDock()
    {
        MonitorConfig? dock = _settings.Current.Monitors
            .FirstOrDefault(m => m.StableId == _editing);

        DocksSection.Opacity = dock is null ? 0.5 : 1;

        if (dock is null)
        {
            DockDetail.Text = "No screen has been set up yet.";
            return;
        }

        MonitorInfo? live = _docks.Plans
            .FirstOrDefault(p => p.Config.StableId == dock.StableId)?.Monitor;

        _filling = true;

        DockDetail.Text = live is not null
            ? $"{live.Width} × {live.Height} · {live.Dpi * 100 / 96}%"
            : "not attached · its layout is kept and comes back with the screen";

        DockEnabled.IsOn = dock.Enabled;
        DockEdge.SelectedIndex = (int)dock.Edge;
        DockThickness.SelectedIndex = (int)dock.Density;
        DockMode.SelectedIndex = (int)dock.Mode;

        // Vertical bars have one width and no compact form, so offering the
        // choice would be offering a setting that does nothing.
        DockThickness.IsEnabled = DockMetrics.IsHorizontal(dock.Edge);

        string note = Note(dock);
        DockNote.Text = note;
        DockNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Fill(dock.Widgets);

        ShowPreview(dock);
        ShowUndo();

        _filling = false;
    }

    /// <summary>Draws the bar as it is, at the thickness it really is.</summary>
    private void ShowPreview(MonitorConfig dock)
    {
        foreach (WidgetPreview old in _preview)
        {
            old.Dispose();
        }

        _preview.Clear();

        bool horizontal = DockMetrics.IsHorizontal(dock.Edge);
        double thickness = DockMetrics.ThicknessDips(dock.Edge, dock.Density);

        // Thickness is true to the dock; length is whatever the panel has. A
        // bar's thickness is the thing worth judging, and no settings window is
        // as wide as a screen.
        Preview.Height = horizontal ? thickness : 220;
        Preview.Width = horizontal ? double.NaN : thickness;
        Preview.HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        Draw(dock);

        PreviewEmpty.Visibility = _preview.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Tick();
    }

    private void Draw(MonitorConfig dock)
    {
        var items = new List<(FrameworkElement Element, GridLength Length)>();

        foreach (WidgetConfig entry in dock.Widgets)
        {
            if (Build(entry) is not { } widget)
            {
                // A kind this build does not know. It holds its place in the
                // settings list; on the bar there is nothing to draw, and the
                // picture has to agree with the bar.
                continue;
            }

            if (Application.Current.Resources[entry.TypeId] is not DataTemplate template)
            {
                widget.Dispose();
                continue;
            }

            widget.Orientation = DockMetrics.IsHorizontal(dock.Edge)
                ? Orientation.Horizontal
                : Orientation.Vertical;

            widget.Density = dock.Density;
            widget.Attach();

            if (widget is SpacerWidget spacer)
            {
                // In the picture the spacers are always shown: this is where a
                // person finds out they exist and can be dragged about.
                spacer.HintVisible = Visibility.Visible;
            }

            var preview = new WidgetPreview(widget, template);
            DockLayout.Dress(preview, dock.Edge, widget is SpacerWidget);
            _preview.Add(preview);
            items.Add((preview, DockLayout.LengthOf(entry, widget)));
        }

        DockLayout.Arrange(dock.Edge, PreviewStrip, items);
    }

    /// <summary>One round of updating whatever this window is showing.</summary>
    private void Tick()
    {
        if (SensorsSection.Visibility == Visibility.Visible)
        {
            ShowReadings();
        }

        if (DocksSection.Visibility != Visibility.Visible)
        {
            return;
        }

        SensorSnapshot snapshot = _sensors.Current;

        foreach (WidgetPreview preview in _preview)
        {
            preview.Tick(snapshot);
        }
    }

    private void ShowUndo()
    {
        // Enabled rather than shown: a control that appears only after the
        // first mistake is invisible exactly while a person is working out
        // what is safe to try.
        UndoButton.IsEnabled = _undo.Count > 0;

        UndoButton.Content = _undo.Count > 0 ? $"Undo - {_undo.Peek().Label}" : "Undo";
    }

    /// <summary>Puts the last layout back.</summary>
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (!_undo.TryPop(out (string StableId, ImmutableArray<WidgetConfig> Before, string Label) step))
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
                        c => c.StableId == step.StableId ? c with { Widgets = step.Before } : c)
                ],
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.dock undone {What} monitor={Monitor}", step.Label, step.StableId);

        // The dock that was changed is not always the one on screen: undo after
        // switching screens has to take you back to what you changed.
        _editing = step.StableId;
        ReloadDocks();
    }

    private string Note(MonitorConfig dock)
    {
        if (!DockMetrics.IsHorizontal(dock.Edge) && dock.Density == DockDensity.Compact)
        {
            return "A bar down the side of a screen has only one width, so Compact does nothing here.";
        }

        if (dock.Mode == AppBarMode.AutoHide && AppBarHost.TaskbarAutoHidesOn(dock.Edge))
        {
            return "The taskbar already hides on this edge, so this dock stays visible.";
        }

        return string.Empty;
    }

    private void Fill(ImmutableArray<WidgetConfig> entries)
    {
        foreach (WidgetCard old in _cards)
        {
            old.Dispose();
        }

        _cards.Clear();

        foreach (WidgetConfig entry in entries)
        {
            _cards.Add(new WidgetCard(entry, Build, OnWidgetConfigured));
        }

        WidgetsEmpty.Visibility = entries.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private WidgetViewModel? Build(WidgetConfig entry) => WidgetCatalog.Create(
        new WidgetContext(
            _sensors,
            new IconChoices(_settings.Current.App.Icons),
            _settings.Current.App.Launcher,
            _log),
        entry);

    private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                App = current.App with
                {
                    Theme = Appearance.FromIndex(ThemeChoice.SelectedIndex),
                    Backdrop = BackdropChoice.SelectedIndex == 1 ? "solid" : "acrylic",
                    Accent = AccentChoice.SelectedIndex == 1 ? "windows" : "neutral",
                },
            },
            WriteReason.UserAction);

        // The window this is being set from follows it too, or the person is
        // choosing a theme while looking at the old one.
        Root.RequestedTheme = Appearance.Of(Appearance.FromIndex(ThemeChoice.SelectedIndex));

        _log.LogInformation(
            "settings.appearance theme={Theme} backdrop={Backdrop} accent={Accent}",
            ThemeChoice.SelectedIndex, BackdropChoice.SelectedIndex, AccentChoice.SelectedIndex);
    }

    private void OnDockPicked(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_filling)
        {
            return;
        }

        _editing = (sender.SelectedItem?.Tag) as string;
        ShowDock();
    }

    private void OnDockEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        EditDock(dock => dock with { Enabled = DockEnabled.IsOn }, "enabled");
    }

    private void OnDockShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        EditDock(
            dock => dock with
            {
                Edge = (AppBarEdge)Math.Max(0, DockEdge.SelectedIndex),
                Density = (DockDensity)Math.Max(0, DockThickness.SelectedIndex),
                Mode = (AppBarMode)Math.Max(0, DockMode.SelectedIndex),
            },
            "shape");

        ShowDock();
    }

    /// <summary>Offers the widgets this build knows, minus any that would duplicate.</summary>
    private void OnAddWidget(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Dock() is not { } dock)
        {
            return;
        }

        // A list with the picture and a line about each, rather than three words
        // in a dropdown. Someone choosing between "Load" and "Temperature" for
        // the first time cannot tell from the names alone what either one puts
        // on their bar.
        var list = new StackPanel { Spacing = 4, MinWidth = 320 };
        var flyout = new Flyout { Content = list, XamlRoot = Content.XamlRoot };

        string[] already = [.. dock.Widgets.Select(w => w.TypeId)];

        foreach (WidgetType type in WidgetCatalog.All)
        {
            bool duplicate = !type.AllowsMultiple && already.Contains(type.TypeId);

            var choice = new Button
            {
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsEnabled = !duplicate,
                Content = Offer(type, duplicate),
            };

            choice.Click += (_, _) =>
            {
                flyout.Hide();
                Rearrange(
                    dock.StableId,
                    widgets => [.. widgets, WidgetConfig.New(type.TypeId)],
                    $"added {type.TypeId}");
            };

            list.Children.Add(choice);
        }

        flyout.ShowAt(button);
    }

    /// <summary>One row of the add-a-widget list: its picture, its name, what it does.</summary>
    private static FrameworkElement Offer(WidgetType type, bool duplicate)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var shape = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = IconRow.Draw(type.Icon),
            Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(shape);

        var box = new Viewbox
        {
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top,
            Child = canvas,
        };

        var words = new StackPanel { Spacing = 2 };
        words.Children.Add(new TextBlock { Text = type.Name });
        words.Children.Add(new TextBlock
        {
            Text = duplicate
                ? "Already on this bar. A second one would show the same thing."
                : type.Description,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        Grid.SetColumn(words, 1);
        row.Children.Add(box);
        row.Children.Add(words);

        return row;
    }

    /// <summary>Moving and removing one widget.</summary>
    private void OnWidgetMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || Dock() is not { } dock)
        {
            return;
        }

        if (Card(id) is not { } card)
        {
            return;
        }

        var menu = new MenuFlyout();

        Add("Move up", () => Shift(card, -1), Index(card) > 0);
        Add("Move down", () => Shift(card, +1), Index(card) < dock.Widgets.Length - 1);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove", () => Remove(card), enabled: true);

        menu.ShowAt(button);

        void Add(string text, Action act, bool enabled)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
    }

    private void OnWidgetConfigured(WidgetCard card, System.Text.Json.JsonElement? options)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        WidgetConfig updated = card.Entry with { Config = options };

        Rearrange(
            dock.StableId,
            widgets => [.. widgets.Select(w => w.InstanceId == card.Id ? updated : w)],
            $"configured {card.Entry.TypeId}",
            rebuild: false);

        // Only the one line changes. Rebuilding the list would close the very
        // panel the person is still working in.
        card.Restate(updated);
    }

    private void Shift(WidgetCard card, int by)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        List<WidgetConfig> run = [.. dock.Widgets];
        int at = run.FindIndex(w => w.InstanceId == card.Id);
        int to = at + by;

        if (at < 0 || to < 0 || to >= run.Count)
        {
            return;
        }

        (run[at], run[to]) = (run[to], run[at]);
        Rearrange(dock.StableId, _ => [.. run], "moved");
    }

    private void Remove(WidgetCard card)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        Rearrange(
            dock.StableId,
            widgets => [.. widgets.Where(w => w.InstanceId != card.Id)],
            $"removed {card.Entry.TypeId}");
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
            _undo.Push((stableId, before.Widgets, what));

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
            ShowPreview(Dock() ?? throw new InvalidOperationException("the dock vanished mid-edit"));
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

        _settings.Commit(
            current with
            {
                Monitors = [.. current.Monitors.Select(c => c.StableId == stableId ? change(c) : c)],
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.dock {What} monitor={Monitor}", what, stableId);
    }

    private MonitorConfig? Dock() =>
        _settings.Current.Monitors.FirstOrDefault(m => m.StableId == _editing);

    private WidgetCard? Card(string id) =>
        _cards.FirstOrDefault(c => c.Id == id);

    private int Index(WidgetCard card) =>
        (Dock()?.Widgets ?? []).ToList().FindIndex(w => w.InstanceId == card.Id);

    private void OnRowEdited(MonitorRow row)
    {
        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(c => c.StableId == row.StableId ? row.Apply(c) : c)
                ],
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.edited monitor={Monitor}", row.Name);
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "docks";

        DocksSection.Visibility = Show(tag == "docks");
        AppearanceSection.Visibility = Show(tag == "appearance");
        LauncherSection.Visibility = Show(tag == "launcher");
        IconsSection.Visibility = Show(tag == "icons");
        SensorsSection.Visibility = Show(tag == "sensors");
        MonitorsSection.Visibility = Show(tag == "monitors");
        AboutSection.Visibility = Show(tag == "about");

        // Only while a page that shows live figures is on screen. A window
        // sitting behind everything else has no business waking the machine
        // once a second.
        if (tag is "sensors" or "docks")
        {
            Tick();
            _refresh.Start();
        }
        else
        {
            _refresh.Stop();
        }
    }

    private void OnHwInfoToggled(object sender, RoutedEventArgs e) =>
        Source(HwInfoProvider.ProviderId, HwInfoSwitch.IsOn);

    private void OnLhmToggled(object sender, RoutedEventArgs e) =>
        Source(LhmProvider.ProviderId, LhmSwitch.IsOn);

    /// <summary>Takes a new address for the source, if it really is a new one.</summary>
    private void OnLhmAddressChanged(object sender, RoutedEventArgs e)
    {
        string address = LhmAddress.Text.Trim();
        SettingsModel current = _settings.Current;

        if (address.Length == 0 || address == current.Sensors.LhmHttpEndpoint)
        {
            return;
        }

        _settings.Commit(
            current with { Sensors = current.Sensors with { LhmHttpEndpoint = address } },
            WriteReason.UserAction);

        _sensors.Reset(LhmProvider.ProviderId);
        _log.LogInformation("settings.source id=lhm address={Address}", address);
    }

    /// <summary>Switches an external source on or off, and says so at once.</summary>
    private void Source(string providerId, bool on)
    {
        SettingsModel current = _settings.Current;

        if (current.Sensors.EnabledProviders.GetValueOrDefault(providerId) == on)
        {
            return;
        }

        _settings.Commit(
            current with
            {
                Sensors = current.Sensors with
                {
                    EnabledProviders = current.Sensors.EnabledProviders.SetItem(providerId, on),
                },
            },
            WriteReason.UserAction);

        // The hub is told to forget the source rather than left to notice by
        // itself. It checks every thirty seconds, and a switch that appears to
        // do nothing for half a minute is a switch people press again.
        _sensors.Reset(providerId);
        _log.LogInformation("settings.source id={Id} enabled={Enabled}", providerId, on);

        ShowReadings();
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

            foreach (SensorDescriptor sensor in found)
            {
                _readings.Add(new SensorRow(sensor));
            }
        }

        SensorSnapshot snapshot = _sensors.Current;

        foreach (SensorRow row in _readings)
        {
            row.Update(snapshot);
        }

        SensorSummary.Text = found.Length == 0
            ? "Nothing is answering yet."
            : $"{found.Length} readings from {found.Select(d => d.Key.Value.Split('/', 2)[0]).Distinct().Count()} sources.";

        HwInfoState.Text = State(HwInfoSwitch.IsOn, _hwinfo.Trouble, found, HwInfoProvider.ProviderId);
        LhmState.Text = State(LhmSwitch.IsOn, _lhm.Trouble, found, LhmProvider.ProviderId);
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

    private static string State(bool on, string? trouble, SensorDescriptor[] found, string providerId)
    {
        if (!on)
        {
            return "Off.";
        }

        int mine = found.Count(
            d => d.Key.Value.StartsWith(providerId + "/", StringComparison.Ordinal));

        return mine > 0 ? $"Reading {mine} temperatures." : trouble ?? "Looking...";
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Pins whatever is in the boxes.</summary>
    private void OnAddLaunchItem(object sender, RoutedEventArgs e)
    {
        string target = NewTarget.Text.Trim();

        if (target.Length == 0)
        {
            return;
        }

        string name = NewName.Text.Trim();

        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                App = current.App with
                {
                    Launcher =
                    [
                        .. current.App.Launcher,
                        LaunchItem.For(target, name.Length > 0 ? name : LauncherRow.NameFor(target)),
                    ],
                },
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.launcher added {Target}", target);

        NewName.Text = string.Empty;
        NewTarget.Text = string.Empty;
        Reload();
    }

    private void OnRemoveLaunchItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                App = current.App with { Launcher = [.. current.App.Launcher.Where(i => i.Id != id)] },
            },
            WriteReason.UserAction);

        _log.LogInformation("settings.launcher removed {Id}", id);
        Reload();
    }

    /// <summary>Moves one item one place earlier in the list.</summary>
    /// <remarks>
    /// A button rather than dragging. The order matters enough to be adjustable
    /// and not enough to be worth a drag-and-drop that has to work on a list, on
    /// a bar, and between the two.
    /// </remarks>
    private void OnMoveLaunchItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        SettingsModel current = _settings.Current;
        List<LaunchItem> items = [.. current.App.Launcher];
        int at = items.FindIndex(i => i.Id == id);

        if (at <= 0)
        {
            return;
        }

        (items[at - 1], items[at]) = (items[at], items[at - 1]);

        _settings.Commit(
            current with { App = current.App with { Launcher = [.. items] } },
            WriteReason.UserAction);

        Reload();
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

            NewTarget.Text = file.Path;

            if (NewName.Text.Trim().Length == 0)
            {
                NewName.Text = LauncherRow.NameFor(file.Path);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings.launcher could not open the file picker");
        }
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string stableId)
        {
            return;
        }

        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with { Monitors = [.. current.Monitors.Where(c => c.StableId != stableId)] },
            WriteReason.UserAction);

        _log.LogInformation("settings.forgot monitor={Monitor}", stableId);
        Reload();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Open(AppPaths.LogDirectory);

    private void OnOpenConfig(object sender, RoutedEventArgs e) => Open(AppPaths.Root);

    private void OnExit(object sender, RoutedEventArgs e) => _onExit();

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

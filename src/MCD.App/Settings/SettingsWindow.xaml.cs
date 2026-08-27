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
    private readonly HwInfoProvider _hwinfo;
    private readonly LhmProvider _lhm;
    private readonly Action _onExit;
    private readonly ObservableCollection<MonitorRow> _known = [];
    /// <summary>The widget selected on the bar's picture, for the inspector.</summary>
    private string? _selectedId;
    private WidgetViewModel? _inspected;

    /// <summary>Dragging inside the bar's picture.</summary>
    private WidgetPreview? _pGrab;
    private Point _pAt;
    private bool _pMoving;
    private int _pCaret = -1;
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _pMark = new();

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
    private readonly Stack<(string StableId, MonitorConfig Before, string Label)> _undo = new();
    private readonly ObservableCollection<IconRow> _icons = [];
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

        SystemBackdrop = null;

        // The Braun body, painted from code: a ThemeResource on the root
        // element cannot see the root's own dictionary during the parse.
        PaintBody();
        Root.ActualThemeChanged += (_, _) => PaintBody();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        SizeAndCentre(screen: null);

        MonitorList.ItemsSource = _known;
        IconList.ItemsSource = _icons;
        SensorList.ItemsSource = _readings;
        VersionText.Text = string.Format(
            CultureInfo.CurrentCulture, Loc.Tr("VersionFormat", "Version {0}"), AppInfo.Version);

        _filling = true;
        AutoStartToggle.IsOn = AutoStart.Enabled;
        _filling = false;

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

        AppSettings look = _settings.Current.App;

        _filling = true;
        ThemeChoice.SelectedIndex = Appearance.Index(look.Theme);
        BackdropChoice.SelectedIndex = look.Backdrop switch
        {
            "solid" => 1,
            "colour" => 2,
            "image" => 3,
            "braun" => 4,
            _ => 0,
        };
        AccentChoice.SelectedIndex = look.Accent == "windows" ? 1 : 0;
        LanguageChoice.SelectedIndex = look.Language switch
        {
            "en-US" => 1,
            "ru-RU" => 2,
            _ => 0,
        };
        _filling = false;

        Root.RequestedTheme = Appearance.Of(look.Theme);

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
                Text = Label(monitors[i], monitors, live, i)
                    + (monitors[i].Enabled ? string.Empty : Loc.Tr("PickerOff", " — off")),
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

    /// <summary>Fills every control on the Docks page from the chosen dock.</summary>
    private void ShowDock()
    {
        MonitorConfig? dock = _settings.Current.Monitors
            .FirstOrDefault(m => m.StableId == _editing);

        DocksSection.Opacity = dock is null ? 0.5 : 1;

        if (dock is null)
        {
            DockDetail.Text = Loc.Tr("DockNoScreen", "No screen has been set up yet.");
            return;
        }

        MonitorInfo? live = _docks.Plans
            .FirstOrDefault(p => p.Config.StableId == dock.StableId)?.Monitor;

        _filling = true;

        DockDetail.Text = live is not null
            ? $"{live.Width} × {live.Height} · {live.Dpi * 100 / 96}%"
            : Loc.Tr("DockNotAttached", "not attached · its layout is kept and comes back with the screen");

        DockEnabled.IsOn = dock.Enabled;
        DockTopmost.IsOn = dock.Topmost;
        DockAutoHide.IsOn = dock.Mode == AppBarMode.AutoHide;
        EdgeChoice.SelectedItem = EdgeChoice.Items[(int)dock.Edge];
        ThicknessChoice.SelectedItem =
            ThicknessChoice.Items[dock.Density == DockDensity.Compact ? 1 : 0];

        // A vertical bar has one thickness; the row is replaced by its
        // explanation rather than offered greyed and mute.
        bool horizontal = DockMetrics.IsHorizontal(dock.Edge);
        ThicknessLabel.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
        ThicknessChoice.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
        ThicknessNote.Visibility = horizontal ? Visibility.Collapsed : Visibility.Visible;

        HideNote.Visibility =
            dock.Mode == AppBarMode.AutoHide && AppBarHost.TaskbarAutoHidesOn(dock.Edge)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // The master switch gates visibly: everything it governs dims with it.
        DockBody.Opacity = dock.Enabled ? 1 : 0.35;
        DockBody.IsHitTestVisible = dock.Enabled;

        ShowPreview(dock);
        RefreshGallery(dock);
        RefreshInspector();
        ShowUndo();

        _filling = false;
    }

    // ---------------- the gallery: everything that can go on the bar ----------------

    /// <summary>The offer in hand while a gallery chip is being dragged.</summary>
    private WidgetOffer? _gOffer;
    private FrameworkElement? _gChip;
    private Point _gAt;
    private bool _gMoving;

    /// <summary>
    /// One chip per offer: its icon, its name, and a tick when the chosen dock
    /// already shows it. A click adds or removes; a drag onto the picture above
    /// puts it in that exact slot.
    /// </summary>
    private void RefreshGallery(MonitorConfig dock)
    {
        Gallery.Children.Clear();

        foreach (WidgetOffer offer in WidgetCatalog.Offers(_sensors))
        {
            bool on = dock.Widgets.Any(offer.Matches);

            var shape = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = IconRow.Draw(offer.Icon),
                Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
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

            var box = new Viewbox { Width = 16, Height = 16, Child = canvas };

            var name = new TextBlock
            {
                Text = offer.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Grid.SetColumn(name, 1);

            var tick = new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = "",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                Visibility = on ? Visibility.Visible : Visibility.Collapsed,
            };

            Grid.SetColumn(tick, 2);
            row.Children.Add(box);
            row.Children.Add(name);
            row.Children.Add(tick);

            var chip = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 0, 10, 0),
                Background = (Brush)Application.Current.Resources[
                    on ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = row,
            };

            ToolTipService.SetToolTip(chip, offer.Description);

            WidgetOffer chosen = offer;
            chip.PointerPressed += (s, e) => OnChipPressed(chip, chosen, e);
            chip.PointerMoved += OnChipMoved;
            chip.PointerReleased += (s, e) => OnChipReleased(chosen, e);
            chip.PointerCanceled += (_, _) => EndChipDrag();

            Gallery.Children.Add(chip);
        }
    }

    private void OnChipPressed(FrameworkElement chip, WidgetOffer offer, PointerRoutedEventArgs e)
    {
        _gOffer = offer;
        _gChip = chip;
        _gAt = e.GetCurrentPoint(Preview).Position;
        _gMoving = false;
        chip.CapturePointer(e.Pointer);
    }

    private void OnChipMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gOffer is null)
        {
            return;
        }

        Point at = e.GetCurrentPoint(Preview).Position;

        if (!_gMoving)
        {
            if (Math.Abs(at.X - _gAt.X) + Math.Abs(at.Y - _gAt.Y) < 6)
            {
                return;
            }

            _gMoving = true;

            if (_gChip is not null)
            {
                _gChip.Opacity = 0.5;
            }
        }

        // The marker lights in the picture while the chip is over it - the
        // same marker a widget already on the bar drags against.
        if (OverPreview(at))
        {
            ShowPreviewMark(at);
        }
        else
        {
            PreviewOverlay.Children.Remove(_pMark);
            _pCaret = -1;
        }
    }

    private void OnChipReleased(WidgetOffer offer, PointerRoutedEventArgs e)
    {
        if (_gOffer is null)
        {
            return;
        }

        Point at = e.GetCurrentPoint(Preview).Position;

        if (!_gMoving)
        {
            ToggleOffer(offer);
        }
        else if (OverPreview(at) && _pCaret >= 0 && Dock() is { } dock)
        {
            InsertAtCaret(dock, offer.Make(), offer.Name);
        }

        EndChipDrag();
    }

    private void EndChipDrag()
    {
        if (_gChip is not null)
        {
            _gChip.Opacity = 1;
            _gChip.ReleasePointerCaptures();
        }

        PreviewOverlay.Children.Remove(_pMark);
        _gOffer = null;
        _gChip = null;
        _gMoving = false;
        _pCaret = -1;
    }

    private bool OverPreview(Point at) =>
        at.X >= 0 && at.Y >= 0 && at.X < Preview.ActualWidth && at.Y < Preview.ActualHeight;

    /// <summary>A plain click on a chip: put it on the bar, or take it off.</summary>
    private void ToggleOffer(WidgetOffer offer)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        if (dock.Widgets.FirstOrDefault(offer.Matches) is { } present)
        {
            Rearrange(
                dock.StableId,
                widgets => [.. widgets.Where(w => w.InstanceId != present.InstanceId)],
                Loc.Tr("UndoRemovedOne", "removed"));
            return;
        }

        Rearrange(
            dock.StableId,
            widgets => [.. widgets, offer.Make()],
            string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("UndoAdded", "added {0}"), offer.Name));
    }

    /// <summary>Puts a new entry where the caret in the picture points.</summary>
    private void InsertAtCaret(MonitorConfig dock, WidgetConfig entry, string name)
    {
        List<WidgetPreview> drawn = [.. PreviewStrip.Children.OfType<WidgetPreview>()];

        // Placed after the last drawn widget ahead of the marker, so an entry
        // this build cannot draw keeps its place in the run.
        List<WidgetConfig> list = [.. dock.Widgets];

        int where = _pCaret == 0 || drawn.Count == 0
            ? 0
            : list.FindIndex(w => w.InstanceId
                == drawn[Math.Min(_pCaret, drawn.Count) - 1].Widget.Entry.InstanceId) + 1;

        list.Insert(Math.Clamp(where, 0, list.Count), entry);

        Rearrange(
            dock.StableId,
            _ => [.. list],
            string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("UndoAdded", "added {0}"), name));
    }

    /// <summary>The options of whatever is selected on the picture above.</summary>
    private void RefreshInspector()
    {
        WidgetConfig? entry = Dock()?.Widgets.FirstOrDefault(w => w.InstanceId == _selectedId);

        _inspected?.Dispose();
        _inspected = null;

        if (entry is null)
        {
            _selectedId = null;
            InspectorTitle.Text = Loc.Tr("InspectorNone", "WIDGET");
            InspectorHint.Visibility = Visibility.Visible;
            InspectorEditor.Visibility = Visibility.Collapsed;
            InspectorEditor.Content = null;
            InspectorRemove.Visibility = Visibility.Collapsed;
            return;
        }

        WidgetType? type = WidgetCatalog.Find(entry.TypeId);

        InspectorTitle.Text = (type?.Name ?? entry.TypeId).ToUpperInvariant();
        InspectorHint.Visibility = Visibility.Collapsed;
        InspectorRemove.Visibility = Visibility.Visible;

        _inspected = Build(entry);
        string id = entry.InstanceId;

        InspectorEditor.Content =
            _inspected?.CreateEditor(options => OnInspectorConfigured(id, options))
            ?? new TextBlock
            {
                Text = Loc.Tr("NothingToSetUp", "This widget has nothing to set up."),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            };

        InspectorEditor.Visibility = Visibility.Visible;
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

    private void OnInspectorRemove(object sender, RoutedEventArgs e)
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

    // ---------------- the picture is the editing surface ----------------

    private void OnPreviewPressed(object sender, PointerRoutedEventArgs e)
    {
        _pAt = e.GetCurrentPoint(Preview).Position;
        _pGrab = PreviewUnder(_pAt);
        _pMoving = false;
    }

    private void OnPreviewMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pGrab is null)
        {
            return;
        }

        Point at = e.GetCurrentPoint(Preview).Position;

        if (!_pMoving)
        {
            if (Math.Abs(at.X - _pAt.X) + Math.Abs(at.Y - _pAt.Y) < 6)
            {
                return;
            }

            _pMoving = true;
            _pGrab.Opacity = 0.4;
            Preview.CapturePointer(e.Pointer);
        }

        ShowPreviewMark(at);
    }

    private void OnPreviewReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pGrab is { } grabbed)
        {
            if (_pMoving)
            {
                LandPreview(grabbed);
            }
            else
            {
                // A plain click selects: the inspector below fills in.
                _selectedId = grabbed.Widget.Entry.InstanceId;
                RefreshInspector();
            }
        }

        EndPreviewDrag();
    }

    private void OnPreviewCancelled(object sender, PointerRoutedEventArgs e) => EndPreviewDrag();

    private void OnPreviewRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (PreviewUnder(e.GetPosition(Preview)) is not { } target || Dock() is not { } dock)
        {
            return;
        }

        e.Handled = true;

        var menu = new MenuFlyout { XamlRoot = Content.XamlRoot };
        var remove = new MenuFlyoutItem { Text = Loc.Tr("WidgetMenuRemove", "Remove from bar") };
        string id = target.Widget.Entry.InstanceId;

        remove.Click += (_, _) => Rearrange(
            dock.StableId,
            widgets => [.. widgets.Where(w => w.InstanceId != id)],
            Loc.Tr("UndoRemovedOne", "removed"));

        menu.Items.Add(remove);
        menu.ShowAt(Preview, e.GetPosition(Preview));
    }

    private void EndPreviewDrag()
    {
        if (_pGrab is not null)
        {
            _pGrab.Opacity = 1;
        }

        PreviewOverlay.Children.Remove(_pMark);
        Preview.ReleasePointerCaptures();
        _pGrab = null;
        _pMoving = false;
        _pCaret = -1;
    }

    private WidgetPreview? PreviewUnder(Point at)
    {
        foreach (WidgetPreview child in PreviewStrip.Children.OfType<WidgetPreview>())
        {
            var rect = new Rect(
                PreviewStrip.ActualOffset.X + child.ActualOffset.X,
                PreviewStrip.ActualOffset.Y + child.ActualOffset.Y,
                child.ActualWidth,
                child.ActualHeight);

            if (rect.Contains(at))
            {
                return child;
            }
        }

        return null;
    }

    private void ShowPreviewMark(Point at)
    {
        if (Dock() is not { } dock)
        {
            return;
        }

        bool horizontal = DockMetrics.IsHorizontal(dock.Edge);
        double along = horizontal ? at.X : at.Y;

        List<WidgetPreview> drawn = [.. PreviewStrip.Children.OfType<WidgetPreview>()];

        int index = drawn.Count;

        for (int i = 0; i < drawn.Count; i++)
        {
            double start = horizontal
                ? PreviewStrip.ActualOffset.X + drawn[i].ActualOffset.X
                : PreviewStrip.ActualOffset.Y + drawn[i].ActualOffset.Y;
            double middle = start
                + ((horizontal ? drawn[i].ActualWidth : drawn[i].ActualHeight) / 2);

            if (along < middle)
            {
                index = i;
                break;
            }
        }

        _pCaret = index;

        _pMark.Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _pMark.RadiusX = 1;
        _pMark.RadiusY = 1;
        _pMark.Width = horizontal ? 2 : 20;
        _pMark.Height = horizontal ? 20 : 2;

        if (!PreviewOverlay.Children.Contains(_pMark))
        {
            PreviewOverlay.Children.Add(_pMark);
        }

        double edge;

        if (drawn.Count == 0)
        {
            edge = (horizontal ? Preview.ActualWidth : Preview.ActualHeight) / 2;
        }
        else if (index >= drawn.Count)
        {
            WidgetPreview last = drawn[^1];
            edge = horizontal
                ? PreviewStrip.ActualOffset.X + last.ActualOffset.X + last.ActualWidth + 2
                : PreviewStrip.ActualOffset.Y + last.ActualOffset.Y + last.ActualHeight + 2;
        }
        else
        {
            edge = horizontal
                ? PreviewStrip.ActualOffset.X + drawn[index].ActualOffset.X - 2
                : PreviewStrip.ActualOffset.Y + drawn[index].ActualOffset.Y - 2;
        }

        if (horizontal)
        {
            Canvas.SetLeft(_pMark, edge - (_pMark.Width / 2));
            Canvas.SetTop(_pMark, (Preview.ActualHeight - _pMark.Height) / 2);
        }
        else
        {
            Canvas.SetLeft(_pMark, (Preview.ActualWidth - _pMark.Width) / 2);
            Canvas.SetTop(_pMark, edge - (_pMark.Height / 2));
        }
    }

    private void LandPreview(WidgetPreview grabbed)
    {
        if (_pCaret < 0 || Dock() is not { } dock)
        {
            return;
        }

        List<WidgetPreview> drawn = [.. PreviewStrip.Children.OfType<WidgetPreview>()];
        string moved = grabbed.Widget.Entry.InstanceId;

        List<string> before =
        [
            .. drawn.Take(Math.Min(_pCaret, drawn.Count))
                .Where(c => !ReferenceEquals(c, grabbed))
                .Select(c => c.Widget.Entry.InstanceId)
        ];

        List<WidgetConfig> list = [.. dock.Widgets.Where(w => w.InstanceId != moved)];
        WidgetConfig entry = dock.Widgets.First(w => w.InstanceId == moved);

        int where = before.Count == 0
            ? 0
            : list.FindIndex(w => w.InstanceId == before[^1]) + 1;

        list.Insert(Math.Clamp(where, 0, list.Count), entry);

        if (list.Select(w => w.InstanceId).SequenceEqual(dock.Widgets.Select(w => w.InstanceId)))
        {
            return;
        }

        Rearrange(dock.StableId, _ => [.. list], Loc.Tr("UndoMoved", "moved"));
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

        UndoButton.Content = _undo.Count > 0
            ? string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("UndoWithLabel", "Undo - {0}"), _undo.Peek().Label)
            : Loc.Tr("Undo", "Undo");
    }

    /// <summary>Puts the last layout back.</summary>
    private void OnUndo(object sender, RoutedEventArgs e)
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
                    Backdrop = BackdropChoice.SelectedIndex switch
                    {
                        1 => "solid",
                        2 => "colour",
                        3 => "image",
                        4 => "braun",
                        _ => "acrylic",
                    },
                    Accent = AccentChoice.SelectedIndex == 1 ? "windows" : "neutral",
                },
            },
            WriteReason.UserAction);

        // The window this is being set from follows it too, or the person is
        // choosing a theme while looking at the old one.
        Root.RequestedTheme = Appearance.Of(Appearance.FromIndex(ThemeChoice.SelectedIndex));
        ShowBackdropExtras();

        _log.LogInformation(
            "settings.appearance theme={Theme} backdrop={Backdrop} accent={Accent}",
            ThemeChoice.SelectedIndex, BackdropChoice.SelectedIndex, AccentChoice.SelectedIndex);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        string language = LanguageChoice.SelectedIndex switch
        {
            1 => "en-US",
            2 => "ru-RU",
            _ => "system",
        };

        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with { App = current.App with { Language = language } },
            WriteReason.UserAction);

        _log.LogInformation("settings.language {Language}", language);
        RestartNow.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Starts the program again, for the change that only takes at a start.
    /// </summary>
    /// <remarks>
    /// Through a shell one-liner that waits a beat: a copy started while this
    /// one still holds the single-instance mutex would only signal it and
    /// leave. Not pretty, but honest about what a restart is.
    /// </remarks>
    /// <summary>Graphite body, or cream on the light theme - the Braun ground.</summary>
    private void PaintBody() =>
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 0xED, 0xEA, 0xE3)
            : Windows.UI.Color.FromArgb(255, 0x1C, 0x1F, 0x24));

    private void OnRestartNow(object sender, RoutedEventArgs e)
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

    private void OnDockPicked(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_filling)
        {
            return;
        }

        _editing = (sender.SelectedItem?.Tag) as string;
        ShowDock();
    }

    /// <summary>The extra row under Background: the chosen colour, or the picture.</summary>
    private void ShowBackdropExtras()
    {
        AppSettings app = _settings.Current.App;

        switch (app.Backdrop)
        {
            case "colour":
                BackdropExtras.Visibility = Visibility.Visible;
                BackdropSwatch.Visibility = Visibility.Visible;
                BackdropSwatch.Background = new SolidColorBrush(Swatch(app.BackdropColour));
                BackdropDetail.Text = app.BackdropColour;
                BackdropPick.Content = Loc.Tr("PickColour", "Choose a colour");
                break;

            case "image":
                BackdropExtras.Visibility = Visibility.Visible;
                BackdropSwatch.Visibility = Visibility.Collapsed;
                BackdropDetail.Text = app.BackdropImage.Length > 0
                    ? Path.GetFileName(app.BackdropImage)
                    : Loc.Tr("NoPictureYet", "No picture chosen yet");
                BackdropPick.Content = Loc.Tr("PickPicture", "Choose a picture");
                break;

            default:
                BackdropExtras.Visibility = Visibility.Collapsed;
                break;
        }
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

    private async void OnBackdropPick(object sender, RoutedEventArgs e)
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
                ShowBackdropExtras();
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
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
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

            flyout.ShowAt(BackdropPick);
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
            ShowBackdropExtras();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings.backdrop could not open the file picker");
        }
    }

    private void OnDockTopmostToggled(object sender, RoutedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        EditDock(
            dock => dock with { Topmost = DockTopmost.IsOn },
            Loc.Tr("UndoTopmost", "above other windows"));
    }

    private void OnDockEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        // A Toggled that only repeats what the settings already say writes
        // nothing - the event can arrive late, after the fill guard has lifted.
        if (Dock()?.Enabled == DockEnabled.IsOn)
        {
            return;
        }

        EditDock(dock => dock with { Enabled = DockEnabled.IsOn }, Loc.Tr("UndoShown", "shown"));
        ShowDock();
    }

    private void OnEdgeChosen(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_filling)
        {
            return;
        }

        int edge = sender.Items.IndexOf(sender.SelectedItem);
        EditDock(dock => dock with { Edge = (AppBarEdge)Math.Max(0, edge) }, Loc.Tr("UndoEdge", "edge"));
        ShowDock();
    }

    private void OnThicknessChosen(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_filling)
        {
            return;
        }

        bool compact = sender.Items.IndexOf(sender.SelectedItem) == 1;
        EditDock(
            dock => dock with { Density = compact ? DockDensity.Compact : DockDensity.Default },
            Loc.Tr("UndoThickness", "thickness"));
        ShowDock();
    }

    private void OnDockAutoHideToggled(object sender, RoutedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        EditDock(
            dock => dock with { Mode = DockAutoHide.IsOn ? AppBarMode.AutoHide : AppBarMode.Pinned },
            Loc.Tr("UndoHiding", "hiding"));
        ShowDock();
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
            ? Loc.Tr("SensorsNoneYet", "Nothing is answering yet.")
            : string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("SensorsSummary", "{0} readings from {1} sources."),
                found.Length,
                found.Select(d => d.Key.Value.Split('/', 2)[0]).Distinct().Count());

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
            return Loc.Tr("SourceOff", "Off.");
        }

        int mine = found.Count(
            d => d.Key.Value.StartsWith(providerId + "/", StringComparison.Ordinal));

        return mine > 0
            ? string.Format(
                CultureInfo.CurrentCulture, Loc.Tr("SourceReading", "Reading {0} temperatures."), mine)
            : trouble ?? Loc.Tr("SourceLooking", "Looking...");
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
    private async void OnPinDialog(object sender, RoutedEventArgs e)
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

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        AutoStart.Enabled = AutoStartToggle.IsOn;
        _log.LogInformation("settings.autostart enabled={Enabled}", AutoStartToggle.IsOn);
    }

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

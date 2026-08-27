using System.Globalization;
using Mcd.Core.Monitors;
using System.Collections.Immutable;
using Mcd.App.Widgets;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Mcd.Interop.AppBar;
using Mcd.Interop.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinRT.Interop;

namespace Mcd.App.Dock;

/// <summary>One dock, on one monitor.</summary>
/// <remarks>
/// <para>
/// Adapted from microsoft/PowerToys,
/// src/modules/cmdpal/Microsoft.CmdPal.UI/Dock/DockWindow.xaml.cs (MIT).
/// Copyright (c) Microsoft Corporation. See licenses/PowerToys-MIT.txt.
/// Changes: the window is handed the monitor it belongs to and never looks one
/// up; it owns no settings and listens for no system broadcasts.
/// </para>
/// <para>
/// The window is never moved to a different monitor. When the topology changes,
/// <see cref="DockWindowManager"/> destroys it and builds a new one on the new
/// monitor. That avoids three problems at once: the shell caches an AppBar's
/// coordinates from its original registration, a moved window races
/// WM_DPICHANGED on a mixed-DPI desktop, and any slide animation in flight is
/// left half-applied.
/// </para>
/// </remarks>
public sealed partial class DockWindow : Window
{
    private readonly ILogger _log;
    private readonly SensorHub _sensors;
    private WidgetContext _context;
    private readonly nint _hwnd;
    private readonly WindowSubclass _subclass;
    private readonly AppBarHost _appBar;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _slide;
    private readonly DispatcherQueueTimer _linger;
    private readonly DispatcherQueueTimer _watch;

    /// <summary>How much of the bar is on screen: 0 hidden, 1 fully out.</summary>
    private double _shown = 1;
    private double _target = 1;
    private double _perFrame;
    private readonly TranslateTransform _push = new();

    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    private const double MinimumDrag = 6;

    private readonly Rectangle _caret = new();
    private WidgetHost? _grabbed;
    private Point _grabbedAt;
    private bool _moving;
    private int _caretIndex = -1;

    /// <summary>Press-and-hold, in a field: a timer held only by a local is
    /// garbage, and whether it ever ticks then depends on the collector.</summary>
    private readonly DispatcherQueueTimer _hold;
    private Pointer? _pointer;
    private WidgetHost? _outlined;
    private bool _offBar;

    /// <summary>The slot grid, drawn only while something is in flight.</summary>
    private readonly List<Rectangle> _slots = [];
    private bool _hiding;
    private bool _tornDown;

    public DockWindow(ILogger log, MonitorInfo monitor, MonitorConfig config, WidgetContext context)
    {
        _log = log;
        _sensors = context.Sensors;
        _context = context;
        Monitor = monitor;
        Config = config;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _subclass = new WindowSubclass(_hwnd);
        _appBar = new AppBarHost(_hwnd, log);

        ExtendsContentIntoTitleBar = true;
        Dress(context);

        WindowFrame.MakeChromeless(_hwnd);
        WindowFrame.SetTopmost(_hwnd, topmost: config.Topmost);

        InnerEdge.BorderThickness = InnerBorder(config.Edge);

        _subclass.MessageReceived += OnMessage;
        Closed += (_, _) => TearDown();

        _appBar.Register();

        // Onto its own monitor before the shell is asked anything about that
        // monitor. The runtime creates the window wherever it likes, and a
        // request about a screen the window is not on is one the shell is
        // entitled to refuse.
        Place(DockMetrics.ThicknessPixels(config.Edge, config.Density, monitor.Scale));

        StartHiding();
        ApplyPosition();
        BuildWidgets();

        Bar.RenderTransform = _push;

        // The content island starts one physical pixel below the top of a
        // chromeless window and its last row is clipped, so everything on the
        // bar sat a pixel low - found with a ruler, and the presenter's
        // SetBorderAndTitleBar does not remove it. The margin pulls the
        // content back onto the window; the row the island cannot paint is
        // flush against the screen edge, where there is only bar background.
        Root.Margin = new Thickness(0, -1.0 / Monitor.Scale, 0, 0);

        // Watched rather than handled. These run even when a widget's own button
        // has already taken the event, because a press on a launcher button has
        // to be able to become a drag - and they mark nothing as handled, so an
        // ordinary click still reaches whatever it was aimed at.
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnGrab), true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnDrag), true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnDrop), true);

        // Losing the capture is not a drop. Taking the pointer from the button a
        // drag started on makes that button raise capture-lost at once, and
        // treating that as a release would end every drag on the frame it began.
        Root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnAbandon), true);

        _hold = DispatcherQueue.CreateTimer();
        _hold.Interval = TimeSpan.FromMilliseconds(500);
        _hold.IsRepeating = false;
        _hold.Tick += (_, _) => EnterEdit();

        _slide = DispatcherQueue.CreateTimer();
        _slide.Interval = DockMetrics.SlideFrameInterval;
        _slide.Tick += (_, _) => Step();

        _linger = DispatcherQueue.CreateTimer();
        _linger.Interval = DockMetrics.AutoHideCollapseDelay;
        _linger.IsRepeating = false;
        _linger.Tick += (_, _) => Hide();

        // The pointer is watched rather than listened for. While the bar is
        // hidden its window is two pixels tall, and a window that small is not
        // given pointer events by the interface layer at all - the events simply
        // never arrive, with nothing to say they were dropped.
        _watch = DispatcherQueue.CreateTimer();
        _watch.Interval = DockMetrics.AutoHideRevealPoll;
        _watch.Tick += (_, _) => Watch();

        if (_hiding)
        {
            _watch.Start();
        }

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        Refresh();
    }

    /// <summary>
    /// Raised on a right-click over an empty part of the bar.
    /// </summary>
    /// <remarks>
    /// This is how the program is configured and closed. There is no icon in the
    /// notification area: a bar of visible controls that hides its own settings
    /// inside someone else's bar is arguing with itself.
    /// </remarks>
    public event EventHandler<MonitorInfo>? SettingsRequested;

    /// <summary>
    /// Raised when the run of widgets on this bar has been changed by hand -
    /// something dragged somewhere else, or added from the bar's own menu.
    /// </summary>
    /// <remarks>
    /// The window says what the new run is; writing that down belongs to
    /// whoever owns the settings. A dock window that could write settings
    /// would be a second writer, and there is exactly one.
    /// </remarks>
    public event EventHandler<ImmutableArray<WidgetConfig>>? Rearranged;

    public MonitorInfo Monitor { get; }

    public MonitorConfig Config { get; private set; }

    /// <summary>
    /// Releases the shell registration. Safe to call twice, and it must happen on
    /// every exit path: a leaked AppBar shrinks the desktop work area and stays
    /// that way across reboots, because the shell remembers it.
    /// </summary>
    public void TearDown()
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        _tick.Stop();
        _slide?.Stop();
        _linger?.Stop();
        _watch?.Stop();

        if (_hiding)
        {
            // Given up before the registration goes, not after: the shell keeps
            // the edge claimed against a window that no longer exists, and the
            // next dock to ask for it is refused for the rest of the session.
            _appBar.SetAutoHide(Config.Edge, Monitor.Bounds, claim: false);
        }


        foreach (WidgetHost host in _hosts)
        {
            host.Dispose();
        }

        _hosts.Clear();
        _appBar.Dispose();
        _subclass.Dispose();
    }

    /// <summary>Re-reads the shell's idea of where this bar goes.</summary>
    public void ApplyPosition()
    {
        if (_tornDown)
        {
            return;
        }

        // Thickness comes from the snapshot's DPI, not from GetDpiForWindow: the
        // window has not reached its monitor yet the first time this runs.
        int thickness = DockMetrics.ThicknessPixels(Config.Edge, Config.Density, Monitor.Scale);

        if (_hiding)
        {
            // An auto-hiding bar reserves nothing, so there is nothing to ask
            // the shell about. Where it sits is ours to decide, and it is
            // decided by how far through the slide it currently is.
            Place(thickness);
        }
        else
        {
            _appBar.SetPosition(Config.Edge, Monitor.Bounds, thickness);
        }

        _log.LogInformation(
            "dock.applied monitor={Monitor} edge={Edge} density={Density} dpi={Dpi} thickness={Thickness}",
            Monitor.Identity.FriendlyName, Config.Edge, Config.Density, Monitor.Dpi, thickness);
    }

    /// <summary>
    /// Takes this edge as the auto-hiding bar, when the settings ask for it and
    /// the shell allows it.
    /// </summary>
    /// <remarks>
    /// Refusal is not a failure to report to the user in a dialog - it is an
    /// ordinary state of a desktop that already hides its taskbar here. The dock
    /// stays pinned, the reason goes in the log, and the settings page says the
    /// same thing next to the setting that did not take.
    /// </remarks>
    private void StartHiding()
    {
        if (Config.Mode != AppBarMode.AutoHide)
        {
            return;
        }

        if (AppBarHost.TaskbarAutoHidesOn(Config.Edge))
        {
            _log.LogWarning(
                "dock.autohide refused monitor={Monitor} edge={Edge}: the taskbar already hides there",
                Monitor.Identity.FriendlyName, Config.Edge);
            return;
        }

        // Asking the shell to reserve this edge for us is a courtesy to other
        // hiding bars, not a requirement: the dock reserves no work area and
        // places itself either way. Windows 11 refuses the request on every edge
        // of this desk for reasons it does not give, so the answer is recorded
        // and the bar hides regardless. What is lost is only the guarantee that
        // no second hiding bar takes the same edge.
        _appBar.SetAutoHide(Config.Edge, Monitor.Bounds, claim: true);

        _hiding = true;
        _shown = 0;
        _target = 0;
    }

    /// <summary>
    /// Moves the window to wherever the slide currently has it, and pushes the
    /// bar's contents through the near edge by the part that is off screen.
    /// </summary>
    private void Place(int thickness)
    {
        WindowFrame.MoveTo(
            _hwnd,
            AutoHideGeometry.At(
                Config.Edge, Monitor.Bounds, thickness, _shown, DockMetrics.RevealHitTestMargin));

        double offset = AutoHideGeometry.Offset(
            Config.Edge, thickness, _shown, DockMetrics.RevealHitTestMargin) / Monitor.Scale;

        bool horizontal = DockMetrics.IsHorizontal(Config.Edge);

        // The contents keep their full size and are clipped by the window. Left
        // to fill the window instead, every widget would be laid out again on
        // each of the twenty-odd frames of the slide.
        if (horizontal)
        {
            Bar.Height = thickness / Monitor.Scale;
            Bar.Width = double.NaN;
            Bar.VerticalAlignment = VerticalAlignment.Top;
            Bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Bar.Width = thickness / Monitor.Scale;
            Bar.Height = double.NaN;
            Bar.HorizontalAlignment = HorizontalAlignment.Left;
            Bar.VerticalAlignment = VerticalAlignment.Stretch;
        }

        _push.Y = horizontal ? -offset : 0;
        _push.X = horizontal ? 0 : -offset;
    }

    /// <summary>
    /// One look at where the pointer is, against where the bar is.
    /// </summary>
    /// <remarks>
    /// The window's own rectangle is the test, so the target grows as the bar
    /// slides out and shrinks back to the sliver when it is away. Nothing else
    /// has to be kept in step with the animation.
    /// </remarks>
    private void Watch()
    {
        // A bar that retracted while a widget was being dragged across it would
        // take the drop target with it.
        if (!_hiding || _tornDown || _moving)
        {
            return;
        }

        Windows.Win32.Foundation.RECT rc = WindowFrame.RectOf(_hwnd);
        (int x, int y) = WindowFrame.CursorAt();

        _watch.Interval = _shown > 0 ? DockMetrics.AutoHideLeavePoll : DockMetrics.AutoHideRevealPoll;

        if (x >= rc.left && x < rc.right && y >= rc.top && y < rc.bottom)
        {
            Show();
            return;
        }

        // Restarted only when it is not already counting, or it would be reset
        // on every look and never reach the end.
        if (_shown > 0 && !_linger.IsRunning)
        {
            _linger.Start();
        }
    }

    private void Show()
    {
        if (!_hiding)
        {
            return;
        }

        _linger.Stop();

        if (Math.Abs(_target - 1) < 0.0001)
        {
            return;
        }

        Glide(to: 1, over: DockMetrics.SlideReveal);
    }

    /// <summary>
    /// Starts the wait before hiding again.
    /// </summary>
    /// <remarks>
    /// Not immediate. The pointer leaves the bar for a moment whenever it
    /// crosses a gap or overshoots, and a bar that vanished each time would be
    /// impossible to aim at.
    /// </remarks>
    private void LingerThenHide()
    {
        if (!_hiding)
        {
            return;
        }

        _linger.Stop();
        _linger.Start();
    }

    private void Hide()
    {
        if (!_hiding || _tornDown)
        {
            return;
        }

        // A panel opened from the bar stands outside it, so the pointer has
        // "left" the bar the instant it moves onto what it just opened. Hiding
        // then would take the panel with it.
        if (Root.XamlRoot is { } root
            && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0)
        {
            _linger.Start();
            return;
        }

        Glide(to: 0, over: DockMetrics.SlideCollapse);
    }

    private void Glide(double to, TimeSpan over)
    {
        _target = to;
        _perFrame = DockMetrics.SlideFrameInterval.TotalMilliseconds / over.TotalMilliseconds;
        _slide.Start();
    }

    /// <summary>One frame of the slide.</summary>
    private void Step()
    {
        _shown = _shown < _target
            ? Math.Min(_target, _shown + _perFrame)
            : Math.Max(_target, _shown - _perFrame);

        Place(DockMetrics.ThicknessPixels(Config.Edge, Config.Density, Monitor.Scale));

        if (Math.Abs(_shown - _target) < 0.0001)
        {
            _slide.Stop();
        }
    }

    private void OnRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        // Widgets will mark their own right-clicks handled once they have menus
        // of their own; this only answers for the bare bar.
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;

        Point at = e.GetPosition(Bar);

        // Over a widget the menu is that widget's; the empty bar keeps the
        // adding menu. A spacer counts as a widget too - it is removable.
        if (Under(at) is { } target)
        {
            var own = new MenuFlyout { XamlRoot = Root.XamlRoot };

            var configure = new MenuFlyoutItem
            {
                Text = Loc.Tr("WidgetMenuConfigure", "Settings..."),
            };
            configure.Click += (_, _) => SettingsRequested?.Invoke(this, Monitor);
            own.Items.Add(configure);

            var remove = new MenuFlyoutItem
            {
                Text = Loc.Tr("WidgetMenuRemove", "Remove from bar"),
            };
            remove.Click += (_, _) => RemoveWidget(target);
            own.Items.Add(remove);

            own.ShowAt(Bar, at);
            return;
        }

        var menu = new MenuFlyout { XamlRoot = Root.XamlRoot };
        var add = new MenuFlyoutSubItem { Text = Loc.Tr("MenuAddWidget", "Add widget") };

        foreach (WidgetOffer offer in WidgetCatalog.Offers(_sensors))
        {
            bool free = !Config.Widgets.Any(offer.Matches);

            var item = new MenuFlyoutItem
            {
                // Said in the item itself. The media widget keeps itself off
                // the bar while nothing is playing, so "greyed out" alone
                // reads as a fault to someone who cannot see it anywhere.
                Text = free
                    ? offer.Name
                    : string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        Mcd.App.Loc.Tr("AlreadyOnBar", "{0} — already on this bar"),
                        offer.Name),
                IsEnabled = free,
            };

            ToolTipService.SetToolTip(item, offer.Description);

            WidgetOffer chosen = offer;
            item.Click += (_, _) => Insert(chosen.Make(), at);
            add.Items.Add(item);
        }

        menu.Items.Add(add);
        menu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem { Text = Loc.Tr("MenuDockSettings", "Dock settings") };

        // The monitor goes with the request. Settings that open on the primary
        // screen when the click happened on the third one are settings the user
        // has to go and find.
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, Monitor);
        menu.Items.Add(settings);

        menu.ShowAt(Bar, at);
    }

    /// <summary>Puts a new widget where the menu was opened, or the file dropped.</summary>
    private void Insert(WidgetConfig entry, Point at)
    {
        List<WidgetConfig> list = [.. Config.Widgets];

        list.Insert(ConfigIndexAt(at, list), entry);

        _log.LogInformation(
            "dock.added monitor={Monitor} widget={Widget}",
            Monitor.Identity.FriendlyName, entry.TypeId);

        Rearranged?.Invoke(this, [.. list]);
    }

    /// <summary>
    /// Where in the settings run a point on the bar falls.
    /// </summary>
    /// <remarks>
    /// The point is counted over drawn widgets; the answer goes after the last
    /// drawn widget ahead of it, so an entry this build cannot draw keeps its
    /// place in the run.
    /// </remarks>
    private int ConfigIndexAt(Point at, List<WidgetConfig> list)
    {
        int index = IndexAt(at);
        List<WidgetHost> drawn = [.. Hosts()];

        int where = index == 0 || drawn.Count == 0
            ? 0
            : list.FindIndex(
                w => w.InstanceId == drawn[Math.Min(index, drawn.Count) - 1].Entry.InstanceId) + 1;

        return Math.Clamp(where, 0, list.Count);
    }

    private readonly List<WidgetHost> _hosts = [];

    /// <summary>
    /// A file dragged from anywhere onto the bar becomes an icon widget in the
    /// slot it was dropped on.
    /// </summary>
    /// <remarks>
    /// This is the way in that needs no explaining: the same drop that puts a
    /// program on the taskbar. The settings window keeps a dialog as the way
    /// to pin an address, which has no file to drag.
    /// </remarks>
    private readonly Rectangle _dropSlot = new();

    private void OnDragOverFiles(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        if (e.DragUIOverride is { } hint)
        {
            hint.Caption = Loc.Tr("PinDropCaption", "Pin to the bar");
            hint.IsGlyphVisible = false;
        }

        // A lit empty slot under the pointer: the bar saying "this fits here",
        // instead of the system's forbidding glyph saying nothing. The slot
        // grid comes up with it, so the bar's structure is visible while
        // something is being placed.
        ShowSlots(true);

        bool horizontal = DockMetrics.IsHorizontal(Config.Edge);
        Point at = e.GetPosition(Bar);

        _dropSlot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        _dropSlot.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _dropSlot.StrokeThickness = 1;
        _dropSlot.RadiusX = 4;
        _dropSlot.RadiusY = 4;
        _dropSlot.Width = horizontal ? 26 : Bar.ActualWidth - 8;
        _dropSlot.Height = horizontal ? Bar.ActualHeight - 8 : 26;

        if (!Overlay.Children.Contains(_dropSlot))
        {
            Overlay.Children.Add(_dropSlot);
        }

        if (horizontal)
        {
            Canvas.SetLeft(_dropSlot, at.X - (_dropSlot.Width / 2));
            Canvas.SetTop(_dropSlot, 4);
        }
        else
        {
            Canvas.SetLeft(_dropSlot, 4);
            Canvas.SetTop(_dropSlot, at.Y - (_dropSlot.Height / 2));
        }
    }

    private void OnDragLeaveFiles(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        Overlay.Children.Remove(_dropSlot);
        ShowSlots(false);
    }

    private async void OnDropFiles(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        Overlay.Children.Remove(_dropSlot);
        ShowSlots(false);

        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        Point at = e.GetPosition(Bar);

        try
        {
            List<WidgetConfig> list = [.. Config.Widgets];
            int where = ConfigIndexAt(at, list);
            bool changed = false;

            foreach (Windows.Storage.IStorageItem item in await e.DataView.GetStorageItemsAsync())
            {
                if (item.Path is not { Length: > 0 } path)
                {
                    continue;
                }

                WidgetConfig pin = IconWidget.Pin(path);
                string target = Mcd.App.Widgets.WidgetOptions.Text(pin.Config, "target") ?? path;

                // Already on this bar - the same file dropped twice makes one
                // icon, the way the taskbar treats it.
                if (list.Any(w => w.TypeId == IconWidget.Type
                    && string.Equals(
                        Mcd.App.Widgets.WidgetOptions.Text(w.Config, "target"),
                        target,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _log.LogInformation("dock.pin dropped {Path}", path);
                list.Insert(Math.Min(where++, list.Count), pin);
                changed = true;
            }

            if (changed)
            {
                Rearranged?.Invoke(this, [.. list]);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dock.pin could not read what was dropped");
        }
    }

    /// <summary>
    /// The grid the sketch drew: while something is in flight, every occupied
    /// slot shows its outline, so the bar reads as a row of places.
    /// </summary>
    private void ShowSlots(bool on)
    {
        foreach (Rectangle slot in _slots)
        {
            Overlay.Children.Remove(slot);
        }

        _slots.Clear();

        if (!on)
        {
            return;
        }

        foreach (WidgetHost host in _hosts)
        {
            if (host.Widget is SpacerWidget || Where(host) is not { } rect)
            {
                // Spacers already reveal themselves as filled bars.
                continue;
            }

            var slot = new Rectangle
            {
                Stroke = new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
                    ? Windows.UI.Color.FromArgb(0x33, 0x00, 0x00, 0x00)
                    : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4,
                Width = Math.Max(0, rect.Width),
                Height = Math.Max(0, rect.Height),
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(slot, rect.X);
            Canvas.SetTop(slot, rect.Y);
            Overlay.Children.Add(slot);
            _slots.Add(slot);
        }
    }

    /// <summary>
    /// Applies the chosen theme and finish to this bar.
    /// </summary>
    /// <remarks>
    /// The theme goes on the content rather than on the application: an
    /// application's theme can only be chosen once, at startup, and a setting
    /// that needs the program restarted to take effect is a setting people
    /// think is broken.
    /// </remarks>
    private void Dress(WidgetContext context)
    {
        Root.RequestedTheme = context.Theme;

        if (context.Acrylic)
        {
            SystemBackdrop = new ThinAcrylicBackdrop();
            Root.Background = new SolidColorBrush(Colors.Transparent);
            return;
        }

        // No backdrop at all, and the bar paints itself. A window with an
        // acrylic backdrop and an opaque background on top of it pays for the
        // acrylic and shows none of it.
        SystemBackdrop = null;

        if (context.Backdrop == "braun")
        {
            // A painted instrument body, not glass: Master Audio Switcher's
            // console gradient, graphite or cream by theme.
            SystemBackdrop = null;
            bool dark = Root.ActualTheme != ElementTheme.Light;

            Root.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop
                    {
                        Color = dark ? Argb(0xFF23272D) : Argb(0xFFF5F3EE),
                        Offset = 0,
                    },
                    new GradientStop
                    {
                        Color = dark ? Argb(0xFF1C1F24) : Argb(0xFFEDEAE3),
                        Offset = 1,
                    },
                },
            };

            InnerEdge.BorderBrush = new SolidColorBrush(
                dark ? Argb(0x1FFFFFFF) : Argb(0x29000000));

            // The light catch is a dark-theme thing; on cream it reads as
            // antialiasing fuzz rather than a bevel.
            InnerBevel.BorderThickness = InnerBorder(Config.Edge);
            InnerBevel.BorderBrush = new SolidColorBrush(Argb(0x12FFFFFF));
            InnerBevel.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        InnerEdge.BorderBrush =
            (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"];
        InnerBevel.Visibility = Visibility.Collapsed;

        if (context.Backdrop == "colour")
        {
            // The alpha the person chose becomes the tint's strength: low is
            // barely-there glass in their hue, full is a solid painted bar.
            Windows.UI.Color chosen = ParseColour(context.BackdropColour);
            SystemBackdrop = new ThinAcrylicBackdrop(chosen, chosen.A / 255f);
            Root.Background = new SolidColorBrush(Colors.Transparent);
            return;
        }

        Root.Background = context.Backdrop switch
        {
            "image" when context.BackdropImage.Length > 0 && File.Exists(context.BackdropImage) =>
                new ImageBrush
                {
                    ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                        new Uri(context.BackdropImage)),
                    Stretch = Stretch.UniformToFill,
                },
            _ => (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
        };
    }

    private static Windows.UI.Color Argb(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>A colour written as #AARRGGBB or #RRGGBB.</summary>
    private static Windows.UI.Color ParseColour(string text)
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
    /// Takes the settings again and rebuilds what is on the bar.
    /// </summary>
    /// <remarks>
    /// Called whenever the settings are written, so that choosing an icon or
    /// pinning a program shows up at once. Only the contents are rebuilt; the
    /// window and its shell registration are left alone, because tearing those
    /// down and putting them back makes every dock on the desktop flinch.
    /// </remarks>
    public void RefreshWidgets(MonitorConfig config, WidgetContext context)
    {
        Config = config;
        _context = context;
        Dress(context);
        WindowFrame.SetTopmost(_hwnd, topmost: config.Topmost);

        foreach (WidgetHost host in _hosts)
        {
            host.Dispose();
        }

        _hosts.Clear();
        BuildWidgets();
    }

    /// <summary>Builds the widgets this monitor's configuration asks for.</summary>
    private void BuildWidgets()
    {
        var items = new List<(FrameworkElement Element, GridLength Length)>();

        foreach (WidgetConfig entry in Config.Widgets)
        {
            WidgetViewModel? widget = WidgetCatalog.Create(_context, entry);

            if (widget is null)
            {
                // Written by a later version of the program. Skipping it beats
                // refusing to show the dock at all.
                _log.LogWarning("widget.unknown typeId={TypeId}", entry.TypeId);
                continue;
            }

            if (Application.Current.Resources[entry.TypeId] is not DataTemplate template)
            {
                _log.LogWarning("widget.template missing for typeId={TypeId}", entry.TypeId);
                widget.Dispose();
                continue;
            }

            widget.Orientation = DockMetrics.IsHorizontal(Config.Edge)
                ? Orientation.Horizontal
                : Orientation.Vertical;

            widget.Density = Config.Density;

            var host = new WidgetHost(_log, widget, template)
            {
                Quiet = widget is SpacerWidget,
            };

            DockLayout.Dress(host, Config.Edge, widget is SpacerWidget);
            host.Attach();

            _hosts.Add(host);
            items.Add((host, DockLayout.LengthOf(entry, widget)));
        }

        DockLayout.Arrange(Config.Edge, Strip, items);
    }

    /// <summary>The widgets as drawn, in bar order.</summary>
    private IEnumerable<WidgetHost> Hosts() => Strip.Children.OfType<WidgetHost>();


    /// <summary>
    /// Moving widgets about on the bar itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Done by hand rather than with the framework's drag and drop. That one
    /// runs a modal loop of its own, which a bar that hides itself and polls the
    /// pointer cannot survive; and the launcher's buttons capture the pointer on
    /// press, so a drag begun on one would never start at all.
    /// </para>
    /// <para>
    /// Nothing moves until the drop: what follows the pointer is a marker
    /// showing where the widget will land. Dragging the widget itself would mean
    /// re-laying out the bar on every frame, and the bar is what everything else
    /// on the screen is arranged around.
    /// </para>
    /// </remarks>
    private void OnGrab(object sender, PointerRoutedEventArgs e)
    {
        if (_tornDown || !e.GetCurrentPoint(Bar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Anything left over from a drag that ended somewhere unexpected.
        LetGo();

        _grabbedAt = e.GetCurrentPoint(Bar).Position;
        _grabbed = Under(_grabbedAt);
        _pointer = e.Pointer;

        if (_grabbed is not null)
        {
            _hold.Start();
        }
    }

    /// <summary>
    /// The press has been held still long enough: the element under it is in
    /// hand. A white outline says so, and from here it can be dragged - or
    /// dragged off the bar to be removed.
    /// </summary>
    /// <remarks>
    /// Widgets drag without the hold too; the hold exists for the buttons -
    /// pinned icons, the transport keys - whose plain click is taken by what
    /// the button does. Capturing the pointer here also takes the coming
    /// release away from that button, so holding an icon never launches
    /// anything.
    /// </remarks>
    private void EnterEdit()
    {
        if (_grabbed is null || _moving || _tornDown)
        {
            return;
        }

        _outlined = _grabbed;
        _outlined.Outline(true);

        if (_pointer is not null)
        {
            Root.CapturePointer(_pointer);
        }

        _log.LogInformation(
            "dock.hold monitor={Monitor} widget={Widget}",
            Monitor.Identity.FriendlyName, _grabbed.Entry.TypeId);
    }

    private void OnDrag(object sender, PointerRoutedEventArgs e)
    {
        if (_grabbed is null || _tornDown)
        {
            return;
        }

        Point at = e.GetCurrentPoint(Bar).Position;

        if (!_moving)
        {
            // A click that wanders a pixel is still a click.
            if (Math.Abs(at.X - _grabbedAt.X) + Math.Abs(at.Y - _grabbedAt.Y) < MinimumDrag)
            {
                return;
            }

            _moving = true;
            _hold.Stop();
            _grabbed.Opacity = 0.4;
            Root.CapturePointer(e.Pointer);

            // The bar shows its structure for the length of the drag: the
            // spacers fill in, and every occupied slot draws its outline.
            RevealSpacers(true);
            ShowSlots(true);

            _log.LogInformation(
                "dock.dragging monitor={Monitor} widget={Widget}",
                Monitor.Identity.FriendlyName, _grabbed.Entry.TypeId);
        }

        // Off the bar means "remove on release": the element goes ghostly and
        // the landing marker disappears, the way macOS lets an icon go.
        _offBar = at.X < -6 || at.Y < -6
            || at.X > Bar.ActualWidth + 6 || at.Y > Bar.ActualHeight + 6;

        if (_offBar)
        {
            _grabbed.Doomed(true);
            HideCaret();
            return;
        }

        _grabbed.Doomed(false);
        ShowCaret(at);
    }

    private void OnDrop(object sender, PointerRoutedEventArgs e)
    {
        if (_moving && _grabbed is { } host)
        {
            if (_offBar)
            {
                RemoveWidget(host);
            }
            else
            {
                Land(host);
            }
        }

        LetGo();
    }

    private void OnAbandon(object sender, PointerRoutedEventArgs e) => LetGo();

    private void LetGo()
    {
        _hold.Stop();

        if (_grabbed is not null)
        {
            _grabbed.Opacity = 1;
        }

        _outlined?.Outline(false);
        _outlined = null;

        RevealSpacers(false);
        ShowSlots(false);
        HideCaret();
        Root.ReleasePointerCaptures();

        _grabbed = null;
        _pointer = null;
        _moving = false;
        _offBar = false;
    }

    /// <summary>Takes a widget off this bar.</summary>
    private void RemoveWidget(WidgetHost host)
    {
        List<WidgetConfig> list =
            [.. Config.Widgets.Where(w => w.InstanceId != host.Entry.InstanceId)];

        _log.LogInformation(
            "dock.removed monitor={Monitor} widget={Widget}",
            Monitor.Identity.FriendlyName, host.Entry.TypeId);

        Rearranged?.Invoke(this, [.. list]);
    }

    /// <summary>The widget under a point, in the bar's own coordinates.</summary>
    private WidgetHost? Under(Point at)
    {
        foreach (WidgetHost host in _hosts)
        {
            if (Where(host) is { } rect && rect.Contains(at))
            {
                return host;
            }
        }

        return null;
    }

    /// <summary>
    /// Where a widget is within the bar.
    /// </summary>
    /// <remarks>
    /// Built from each element's own offset rather than from TransformToVisual,
    /// which is banned here: it answers in the coordinates of the XAML root
    /// rather than of the window, and a dock is not at the origin of either.
    /// </remarks>
    private static Rect? Where(WidgetHost host) =>
        host.Parent is FrameworkElement band
            ? new Rect(
                band.ActualOffset.X + host.ActualOffset.X,
                band.ActualOffset.Y + host.ActualOffset.Y,
                host.ActualWidth,
                host.ActualHeight)
            : null;

    /// <summary>While a widget is in flight, every spacer shows itself.</summary>
    private void RevealSpacers(bool on)
    {
        foreach (WidgetHost host in _hosts)
        {
            if (host.Widget is SpacerWidget spacer)
            {
                spacer.HintVisible = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Where along the run a point falls: the count of drawn widgets whose
    /// middles it has passed, which is the index it would be inserted at.
    /// </summary>
    private int IndexAt(Point at)
    {
        bool horizontal = DockMetrics.IsHorizontal(Config.Edge);
        double along = horizontal ? at.X : at.Y;

        int index = 0;

        foreach (WidgetHost host in Hosts())
        {
            if (Where(host) is { } rect)
            {
                double middle = horizontal
                    ? rect.X + (rect.Width / 2)
                    : rect.Y + (rect.Height / 2);

                if (along < middle)
                {
                    return index;
                }
            }

            index++;
        }

        return index;
    }

    /// <summary>Puts the marker where the widget would land if it were dropped now.</summary>
    private void ShowCaret(Point at)
    {
        bool horizontal = DockMetrics.IsHorizontal(Config.Edge);
        int index = IndexAt(at);

        if (index != _caretIndex)
        {
            _log.LogInformation("dock.caret index={Index}", index);
        }

        _caretIndex = index;

        List<WidgetHost> drawn = [.. Hosts()];

        // The marker sits on the boundary the widget would land on: the middle
        // of the gap between neighbours, or just past the end of the run.
        double along;

        if (drawn.Count == 0)
        {
            along = (horizontal ? Bar.ActualWidth : Bar.ActualHeight) / 2;
        }
        else if (index >= drawn.Count)
        {
            Rect last = Where(drawn[^1]) ?? default;
            along = (horizontal ? last.X + last.Width : last.Y + last.Height) + 3;
        }
        else
        {
            Rect next = Where(drawn[index]) ?? default;
            double edge = horizontal ? next.X : next.Y;

            if (index == 0)
            {
                along = edge - 3;
            }
            else
            {
                Rect prev = Where(drawn[index - 1]) ?? default;
                double prevEdge = horizontal ? prev.X + prev.Width : prev.Y + prev.Height;
                along = (prevEdge + edge) / 2;
            }
        }

        DressCaret(horizontal);

        if (!Overlay.Children.Contains(_caret))
        {
            Overlay.Children.Add(_caret);
        }

        if (horizontal)
        {
            Canvas.SetLeft(_caret, along - (_caret.Width / 2));
            Canvas.SetTop(_caret, (Bar.ActualHeight - _caret.Height) / 2);
        }
        else
        {
            Canvas.SetLeft(_caret, (Bar.ActualWidth - _caret.Width) / 2);
            Canvas.SetTop(_caret, along - (_caret.Height / 2));
        }
    }

    private void DressCaret(bool horizontal)
    {
        _caret.Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _caret.RadiusX = 1;
        _caret.RadiusY = 1;
        _caret.Width = horizontal ? 2 : 24;
        _caret.Height = horizontal ? 16 : 2;
    }

    private void HideCaret()
    {
        Overlay.Children.Remove(_caret);
        _caretIndex = -1;
    }

    /// <summary>Works out the new run and says so.</summary>
    private void Land(WidgetHost host)
    {
        if (_caretIndex < 0)
        {
            return;
        }

        List<WidgetHost> drawn = [.. Hosts()];
        WidgetConfig moved = host.Entry;

        // Counted over the widgets that are actually drawn, and not counting
        // the one being moved: it is still sitting where it came from.
        List<string> before =
        [
            .. drawn.Take(Math.Min(_caretIndex, drawn.Count))
                .Where(h => h != host)
                .Select(h => h.Entry.InstanceId)
        ];

        List<WidgetConfig> list = [.. Config.Widgets.Where(w => w.InstanceId != moved.InstanceId)];

        // Placed after the last widget that was ahead of the marker. Counting
        // positions in the settings instead would put it in the wrong place on
        // a bar that also holds a widget this build cannot draw.
        int where = before.Count == 0
            ? 0
            : list.FindIndex(w => w.InstanceId == before[^1]) + 1;

        list.Insert(Math.Clamp(where, 0, list.Count), moved);

        if (list.Select(w => w.InstanceId).SequenceEqual(Config.Widgets.Select(w => w.InstanceId)))
        {
            return;
        }

        _log.LogInformation(
            "dock.rearranged monitor={Monitor} widget={Widget} at={At}",
            Monitor.Identity.FriendlyName, moved.TypeId, where);

        Rearranged?.Invoke(this, [.. list]);
    }

    private void Refresh()
    {
        SensorSnapshot snapshot = _sensors.Current;

        foreach (WidgetHost host in _hosts)
        {
            host.Tick(snapshot);
        }
    }

    private static Thickness InnerBorder(AppBarEdge edge) => edge switch
    {
        AppBarEdge.Top => new Thickness(0, 0, 0, 1),
        AppBarEdge.Bottom => new Thickness(0, 1, 0, 0),
        AppBarEdge.Left => new Thickness(0, 0, 1, 0),
        AppBarEdge.Right => new Thickness(1, 0, 0, 0),
        _ => new Thickness(0),
    };

    private void OnMessage(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message != _appBar.CallbackMessage)
        {
            return;
        }

        switch ((uint)e.WParam)
        {
            case AppBarHost.AbnPosChanged:
                ApplyPosition();
                break;

            case AppBarHost.AbnFullScreenApp:
                // lParam is non-zero while a full-screen app is in front. The dock
                // steps behind it rather than unregistering, so the work area
                // stays reserved and nothing underneath reflows.
                if (e.LParam != 0)
                {
                    WindowFrame.SendToBottom(_hwnd);
                }
                else
                {
                    WindowFrame.SetTopmost(_hwnd, topmost: Config.Topmost);
                }

                break;
        }
    }
}

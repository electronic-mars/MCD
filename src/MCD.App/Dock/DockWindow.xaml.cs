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

    /// <summary>The slots the thing in hand would land on.</summary>
    /// <summary>
    /// Where the thing in hand would land.
    /// </summary>
    /// <remarks>
    /// Put on the overlay once and shown or hidden, never taken off and put
    /// back. An element held in a field and re-parented is the shape of a bug
    /// this program has already paid for once: XAML refuses to give an
    /// element a second parent, and it refuses on the UI thread, where there
    /// is nothing to catch it.
    /// </remarks>
    /// <summary>
    /// How far past the thin edge of the bar a widget must be taken before it
    /// counts as leaving.
    /// </summary>
    private const double LeaveAcross = 44;

    private readonly Rectangle _aim = new()
    {
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        RadiusX = 5,
        RadiusY = 5,
        StrokeThickness = 1.5,
    };
    private WidgetHost? _grabbed;
    private Point _grabbedAt;
    private bool _moving;

    /// <summary>How many slots into the widget the hand took hold.</summary>
    private int _grabOffset;

    /// <summary>Where the drop would put it, or null while it fits nowhere.</summary>
    private int? _landing;

    /// <summary>How many slots this bar has, and what sits on them.</summary>
    private int _capacity;
    private List<Placement> _placed = [];

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
        Raise(config);

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

        // The widgets are not built here. Settling them onto their slots is
        // something the bar reports, and whoever owns the settings has to be
        // listening before it happens - so the first arrangement of a new bar
        // is written down rather than lost between a constructor and its
        // caller's next line.
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
        _refusing.Tick += (_, _) => ShowSlots(false);
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
    public event EventHandler<DockSettingsRequest>? SettingsRequested;

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

    /// <summary>
    /// Raised when the bar has settled widgets onto slots by itself and is
    /// only recording where they went.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Rearranged"/> because the answer differs:
    /// a rearrangement has to be drawn, and this has already been drawn. Told
    /// apart, the bar is not rebuilt one tick after it first appears - which
    /// is a rebuild that can land in the middle of somebody's first drag.
    /// </remarks>
    public event EventHandler<ImmutableArray<WidgetConfig>>? Settled;

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
        _hold.Stop();
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

        if (_hiding || Config.Mode == AppBarMode.Desktop)
        {
            // Neither of these reserves anything, so there is nothing to ask
            // the shell about. Where the bar sits is ours to decide: for a
            // hiding bar, by how far through the slide it is; for one lying
            // on the desktop, flush against its edge and stays there.
            Place(thickness);
        }
        else
        {
            _appBar.SetPosition(Config.Edge, Monitor.Bounds, thickness);
        }

        if (Config.Mode == AppBarMode.Desktop)
        {
            // Under everything. Asked for again on every position change,
            // because anything that raises a window can lift this one with it.
            WindowFrame.SendToBottom(_hwnd);
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

    /// <summary>
    /// Opens this bar's own menu and asks it for the settings, the way a
    /// right-click does.
    /// </summary>
    /// <remarks>
    /// For the unattended check, and it exists because the check did not have
    /// it: every other way of opening the settings window builds it with
    /// nothing else on screen, and the way everybody actually uses builds it
    /// from inside a menu that is still open on another window's island. That
    /// is a different thing to ask of the framework, and it was the one that
    /// ended the program.
    /// </remarks>
    public void RehearseMenu()
    {
        // The menus themselves, not stand-ins for them: the bare-slot one
        // builds an entry for everything that could be added, each with a
        // tooltip of its own, and the widget one is a different shape again.
        MenuFlyout bar = BarMenu(_capacity - 1);
        bar.ShowAt(Bar, new Point(8, 8));
        bar.Hide();

        string? widget = _hosts.Count > 0 ? _hosts[0].Entry.InstanceId : null;

        if (_hosts.Count > 0)
        {
            MenuFlyout own = WidgetMenu(_hosts[0]);
            own.ShowAt(Bar, new Point(8, 8));

            // Asked for the way the menu item asks, while the menu is still
            // up: the same call, so that whatever the click does to the
            // framework, this does too.
            AskForSettings(widget);
            own.Hide();
            return;
        }

        AskForSettings(null);
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
        int cell = CellAt(at);

        // Over a widget the menu is that widget's; an empty slot offers what
        // could be put in it.
        if (DockGrid.At(_placed, cell) is { } sitting
            && _hosts.FirstOrDefault(h => h.Entry.InstanceId == sitting.InstanceId) is { } target)
        {
            WidgetMenu(target).ShowAt(Bar, at);
            return;
        }

        BarMenu(cell).ShowAt(Bar, at);
    }

    /// <summary>
    /// Asks for the settings window, on the turn after the click.
    /// </summary>
    /// <remarks>
    /// Not inside the click itself. A menu item's handler runs in the middle
    /// of the framework's own input dispatch, with a light-dismiss popup on
    /// this window's island still open; building a whole second window and
    /// reading its XAML from in there is asking for two things at once that
    /// the framework does not reliably survive. On the next turn the menu has
    /// gone and the input event has returned, and the window is built with
    /// nothing else in hand.
    /// </remarks>
    private void AskForSettings(string? widgetId) =>
        DispatcherQueue.TryEnqueue(
            () => SettingsRequested?.Invoke(this, new DockSettingsRequest(Monitor, widgetId)));

    /// <summary>The menu for a widget: its settings, and taking it off.</summary>
    private MenuFlyout WidgetMenu(WidgetHost target)
    {
        {
            var own = new MenuFlyout { XamlRoot = Root.XamlRoot };

            var configure = new MenuFlyoutItem
            {
                Text = Loc.Tr("WidgetMenuConfigure", "Settings..."),
            };

            // The widget travels with the request, so the settings open with
            // this one's own options in front of the person who asked.
            configure.Click += (_, _) => AskForSettings(target.Entry.InstanceId);
            own.Items.Add(configure);

            var remove = new MenuFlyoutItem
            {
                Text = Loc.Tr("WidgetMenuRemove", "Remove from bar"),
            };
            remove.Click += (_, _) => RemoveWidget(target);
            own.Items.Add(remove);

            return own;
        }
    }

    /// <summary>The menu for a bare slot: what could go in it, and the settings.</summary>
    private MenuFlyout BarMenu(int cell)
    {
        var menu = new MenuFlyout { XamlRoot = Root.XamlRoot };
        var add = new MenuFlyoutSubItem { Text = Loc.Tr("MenuAddWidget", "Add widget") };

        // Every widget can be had more than once - two temperatures, three
        // copies of the same reading on different parts of the bar. Nothing is
        // greyed out here; what a slot cannot take is only ever a matter of
        // whether it is free.
        foreach (WidgetOffer offer in WidgetCatalog.Offers(_sensors))
        {
            var item = new MenuFlyoutItem { Text = offer.Name };
            ToolTipService.SetToolTip(item, offer.Description);

            WidgetOffer chosen = offer;
            item.Click += (_, _) => Insert(chosen.Make(), cell);
            add.Items.Add(item);
        }

        menu.Items.Add(add);
        menu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem { Text = Loc.Tr("MenuDockSettings", "Bar settings") };

        // The monitor goes with the request. Settings that open on the primary
        // screen when the click happened on the third one are settings the user
        // has to go and find.
        settings.Click += (_, _) => AskForSettings(null);
        menu.Items.Add(settings);

        return menu;
    }

    /// <summary>Puts a new widget on the slot it was aimed at.</summary>
    /// <remarks>
    /// How wide the new widget will be is not known until it is built, so one
    /// slot is claimed and the layout settles the rest: if it needs more than
    /// are free there, it takes the first run that fits.
    /// </remarks>
    private void Insert(WidgetConfig entry, int cell)
    {
        _log.LogInformation(
            "dock.added monitor={Monitor} widget={Widget} cell={Cell}",
            Monitor.Identity.FriendlyName, entry.TypeId, cell);

        Rearranged?.Invoke(this, [.. Config.Widgets, entry with { Cell = cell }]);
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
    private async void OnDragOverFiles(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        bool files = e.DataView.Contains(
            Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);

        bool widget = e.DataView.Contains(
            Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text);

        if (!files && !widget)
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        if (e.DragUIOverride is { } hint)
        {
            hint.Caption = files
                ? Loc.Tr("PinDropCaption", "Pin to the bar")
                : Loc.Tr("WidgetDropCaption", "Put it here");

            hint.IsGlyphVisible = false;
        }

        // The slots come up, and the one under the pointer lights: the bar
        // saying "this goes here", instead of the system's forbidding glyph
        // saying nothing.
        ShowSlots(true);

        // A file becomes an icon, which is one slot; a widget is as long as it
        // says it is. Asked once for the whole drag rather than on every frame
        // of it: the answer cannot change while the pointer moves, and reading
        // the package is not free.
        if (widget && !_measured)
        {
            _measured = true;

            Microsoft.UI.Xaml.DragOperationDeferral held = e.GetDeferral();

            try
            {
                string carried = await e.DataView.GetTextAsync();

                if (WidgetDrag.Unwrap(carried) is { } coming)
                {
                    _incoming = SpanWanted(coming);
                }
            }
            catch (Exception measuring)
            {
                // A package that will not read is a package this bar cannot
                // size. One slot is the old answer and it is not worse.
                _log.LogWarning(measuring, "dock.drag could not be measured");
            }
            finally
            {
                held.Complete();
            }
        }

        Aim(CellAt(e.GetPosition(Bar)), _incoming, ignore: null);
    }

    private void OnDragLeaveFiles(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        Forget();
        ShowSlots(false);
    }

    /// <summary>Forgets what the last drag was carrying.</summary>
    private void Forget()
    {
        _incoming = 1;
        _measured = false;
    }

    private async void OnDropFiles(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        int cell = CellAt(e.GetPosition(Bar));
        ShowSlots(false);
        Forget();

        // A widget carried over from the settings window. It lands on the slot
        // it was dropped on, which is the whole point of carrying it: a press
        // in another window puts a thing on a bar somewhere with nothing to
        // watch, and a drag ends where the thing is going.
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            string carried = await e.DataView.GetTextAsync();

            if (WidgetDrag.Unwrap(carried) is { } dropped)
            {
                Place(dropped, cell);
                return;
            }
        }

        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            List<WidgetConfig> list = [.. Config.Widgets];
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

                _log.LogInformation("dock.pin dropped {Path} cell={Cell}", path, cell);
                list.Add(pin with { Cell = cell++ });
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
    /// While something is in flight, the bar shows what it is made of: every
    /// slot outlined, so an empty one is visibly a place a thing can go.
    /// </summary>
    private void ShowSlots(bool on)
    {
        foreach (Rectangle slot in _slots)
        {
            Overlay.Children.Remove(slot);
        }

        _slots.Clear();
        _aim.Visibility = Visibility.Collapsed;

        if (!on)
        {
            return;
        }

        // Only the free ones, and the ones the widget in hand is leaving.
        foreach (int cell in DockGrid.Free(_placed, _capacity, _grabbed?.Entry.InstanceId))
        {
            Rect rect = CellRect(cell, 1);

            var slot = new Rectangle
            {
                Fill = new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
                    ? Windows.UI.Color.FromArgb(0x0A, 0x00, 0x00, 0x00)
                    : Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
                    ? Windows.UI.Color.FromArgb(0x24, 0x00, 0x00, 0x00)
                    : Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                RadiusX = 5,
                RadiusY = 5,
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
    /// Lights the slots the thing in hand would land on, or nothing at all
    /// when it will not fit anywhere near the pointer.
    /// </summary>
    /// <returns>Where it would land, or null when there is no room.</returns>
    private int? Aim(int wanted, int span, string? ignore)
    {
        int? cell = DockGrid.Nearest(_placed, _capacity, wanted, span, ignore);

        if (cell is not { } landing)
        {
            _aim.Visibility = Visibility.Collapsed;
            return null;
        }

        Rect rect = CellRect(landing, span);

        // The program's own orange, not the system accent: this marker is the
        // bar talking about itself, and it has to read the same on a desktop
        // whose accent happens to be the colour of the wallpaper behind it.
        _aim.Fill = AimFill;
        _aim.Stroke = AimEdge;
        _aim.Width = Math.Max(0, rect.Width);
        _aim.Height = Math.Max(0, rect.Height);
        _aim.Visibility = Visibility.Visible;

        Canvas.SetLeft(_aim, rect.X);
        Canvas.SetTop(_aim, rect.Y);

        return landing;
    }

    /// <summary>
    /// Marks the slots a widget is about to be taken out of, or clears the
    /// mark.
    /// </summary>
    private void Farewell(bool on)
    {
        if (!on || _grabbed is null || DockGrid.At(_placed, CellAt(_grabbedAt)) is not { } held)
        {
            _goodbye.Visibility = Visibility.Collapsed;
            return;
        }

        Rect rect = CellRect(held.Cell, held.Span);

        _goodbye.Fill = DoomFill;
        _goodbye.Stroke = DoomEdge;
        _goodbye.Width = Math.Max(0, rect.Width);
        _goodbye.Height = Math.Max(0, rect.Height);
        _goodbye.Visibility = Visibility.Visible;

        Canvas.SetLeft(_goodbye, rect.X);
        Canvas.SetTop(_goodbye, rect.Y);
    }

    /// <summary>The mark over a widget on its way off the bar.</summary>
    /// <remarks>Put on the overlay once, like <see cref="_aim"/>.</remarks>
    private readonly Rectangle _goodbye = new()
    {
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        RadiusX = 5,
        RadiusY = 5,
        StrokeThickness = 1.5,
        StrokeDashArray = [3, 2],
    };

    /// <summary>
    /// The colours the overlay is drawn in.
    /// </summary>
    /// <remarks>
    /// Held rather than made: the marks are redrawn on every pointer move,
    /// and a brush per move over a bar of sixty slots is a great deal of
    /// rubbish for a colour that never changes.
    /// </remarks>
    private static readonly SolidColorBrush AimFill =
        new(Windows.UI.Color.FromArgb(0x38, 0x4C, 0xC2, 0xFF));

    private static readonly SolidColorBrush AimEdge =
        new(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));

    private static readonly SolidColorBrush DoomFill =
        new(Windows.UI.Color.FromArgb(0x38, 0xFF, 0x6B, 0x6B));

    private static readonly SolidColorBrush DoomEdge =
        new(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0x6B, 0x6B));

    /// <summary>Which slot a point on the bar falls in.</summary>
    private int CellAt(Point at)
    {
        double along = DockMetrics.IsHorizontal(Config.Edge)
            ? at.X - DockLayout.EndInset
            : at.Y - DockLayout.EndInset;

        return Math.Clamp((int)Math.Floor(along / Pitch), 0, Math.Max(0, _capacity - 1));
    }

    /// <summary>Where a run of slots is, in the bar's own coordinates.</summary>
    private Rect CellRect(int cell, int span)
    {
        double start = DockLayout.EndInset + (cell * Pitch);
        double length = span * Pitch;

        return DockMetrics.IsHorizontal(Config.Edge)
            ? new Rect(start + 1, 3, Math.Max(0, length - 2), Math.Max(0, Bar.ActualHeight - 6))
            : new Rect(3, start + 1, Math.Max(0, Bar.ActualWidth - 6), Math.Max(0, length - 2));
    }

    /// <summary>How long one slot is meant to be, before the bar is divided up.</summary>
    private double CellSize => DockMetrics.CellDips(Config.Edge, Config.Density);

    /// <summary>
    /// How long one slot actually is: the bar's own length shared out between
    /// the slots that fit in it.
    /// </summary>
    /// <remarks>
    /// A hair wider than <see cref="CellSize"/>, because the slots that do not
    /// quite fit at the end are shared out among the rest. Drawn and hit-tested
    /// with this rather than the nominal size, or the outlines drift away from
    /// the widgets by half a slot across the width of a screen.
    /// </remarks>
    private double Pitch
    {
        get
        {
            double length = (DockMetrics.IsHorizontal(Config.Edge)
                ? Bar.ActualWidth
                : Bar.ActualHeight) - (2 * DockLayout.EndInset);

            return _capacity > 0 && length > 0 ? length / _capacity : CellSize;
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
    /// Changes only whether this bar sits above other windows.
    /// </summary>
    /// <remarks>
    /// One P/Invoke. Everything else about the bar is untouched, which is the
    /// point: this used to go through the rebuild that disposes and remakes
    /// every widget on it.
    /// </remarks>
    public void SetTopmost(MonitorConfig config)
    {
        Config = config;

        Raise(config);
    }

    /// <summary>
    /// Walks every path that takes this bar apart and puts it back, twice.
    /// </summary>
    /// <remarks>
    /// For the unattended check. A page or a bar built in code goes wrong on
    /// the second build, not the first: something it holds still belongs to
    /// the build before it, and XAML answers that on the UI thread where
    /// nothing can catch it. Every one of these paths re-parents elements, so
    /// every one of them is a place that bug can live.
    /// </remarks>
    public int Rehearse(WidgetContext context)
    {
        for (int round = 0; round < 2; round++)
        {
            RefreshWidgets(Config, context);
            RefreshContents(Config);

            // Added, moved and taken away again - the three things a person
            // does to a bar, each of which lays it out afresh.
            WidgetConfig added = DockContents.Gauge("cpu");

            RefreshContents(Config with { Widgets = [.. Config.Widgets, added] });

            RefreshContents(Config with
            {
                Widgets =
                [
                    .. Config.Widgets.Select(
                        w => w.InstanceId == added.InstanceId ? w with { Cell = _capacity - 2 } : w)
                ],
            });

            RefreshContents(Config with
            {
                Widgets = [.. Config.Widgets.Where(w => w.InstanceId != added.InstanceId)],
            });

            // And the overlay, which the drag puts up and takes down.
            ShowSlots(true);
            Aim(0, 1, null);
            ShowSlots(false);
        }

        return _hosts.Count;
    }

    /// <summary>
    /// Puts the bar where it belongs in the pile of windows.
    /// </summary>
    /// <remarks>
    /// Three answers, and only three: above everything, in with everything,
    /// or under everything. The last is the bar as a thing lying on the desk,
    /// which is what "show it on the desktop only" means - it is not hidden,
    /// it is simply covered by whatever is opened over it.
    /// </remarks>
    private void Raise(MonitorConfig config)
    {
        if (config.Mode == AppBarMode.Desktop)
        {
            WindowFrame.SendToBottom(_hwnd);
            return;
        }

        WindowFrame.SetTopmost(_hwnd, topmost: config.Topmost);
    }

    /// <summary>Fills the bar for the first time. Call once, after wiring up.</summary>
    public void Fill()
    {
        // The two marks join the overlay here and stay on it for the life of
        // the window, shown and hidden rather than added and removed.
        if (!Overlay.Children.Contains(_aim))
        {
            Overlay.Children.Add(_aim);
            Overlay.Children.Add(_goodbye);
        }

        BuildWidgets();
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
        // Whatever was in hand is let go of first: the rebuild disposes every
        // host, and a drag that carried on afterwards would be dragging an
        // element that is no longer on the bar.
        LetGo();

        Config = config;
        _context = context;
        Dress(context);
        Raise(config);

        foreach (WidgetHost host in _hosts)
        {
            host.Dispose();
        }

        _hosts.Clear();
        BuildWidgets();
    }

    /// <summary>
    /// Builds the widgets this monitor's configuration asks for and puts each
    /// on the slots it holds.
    /// </summary>
    /// <remarks>
    /// How many slots a widget needs is measured rather than declared: a
    /// reading's width depends on its label and on the figures it has had to
    /// show, and a number guessed here would be wrong on somebody's machine.
    /// </remarks>
    private void BuildWidgets()
    {
        Measure();

        var built = new List<(WidgetConfig Entry, int Span)>();
        var hosts = new Dictionary<string, WidgetHost>(StringComparer.Ordinal);

        foreach (WidgetConfig entry in Config.Widgets)
        {
            if (MakeHost(entry) is not { } made)
            {
                continue;
            }

            _hosts.Add(made.Host);
            hosts[entry.InstanceId] = made.Host;
            built.Add((entry, made.Span));
        }

        _built = built;
        _drawn = hosts;
        Settle();
        WriteBackCells();
    }

    /// <summary>
    /// How many slots a widget is asking for.
    /// </summary>
    /// <remarks>
    /// None at all when the widget says it is about nothing on this machine.
    /// A slot that draws nothing is worse than no slot: it is a gap in the bar
    /// that cannot be dropped into and cannot be explained.
    /// </remarks>
    /// <param name="held">
    /// How many it already has. A widget never gives a slot back for having a
    /// shorter number in it - only for having stopped being about anything.
    /// </param>
    private int Wants(WidgetViewModel widget, int held) =>
        widget.Matters
            ? Math.Max(held, DockLayout.SpanOf(widget.Length(), CellSize))
            : 0;

    /// <summary>How long this bar is, and how many slots that comes to.</summary>
    private void Measure()
    {
        bool horizontal = DockMetrics.IsHorizontal(Config.Edge);
        double length = horizontal ? Monitor.Width / Monitor.Scale : Monitor.Height / Monitor.Scale;

        _capacity = DockLayout.Capacity(length, CellSize);
    }

    /// <summary>
    /// How many slots this entry will want, worked out before it is built for
    /// real.
    /// </summary>
    /// <remarks>
    /// The marker under the pointer promised one slot for everything, because
    /// one was all it knew how to ask for. A clock needs three: the marker lit
    /// a single gap, the drop was allowed, and the layout then shoved
    /// everything to the right of it along. A widget can be asked its length
    /// without a template or a host, so it is asked, once, when the drag
    /// arrives.
    /// </remarks>
    private int SpanWanted(WidgetConfig entry)
    {
        WidgetViewModel? widget = WidgetCatalog.Create(_context, entry);

        if (widget is null)
        {
            return 1;
        }

        try
        {
            widget.Orientation = DockMetrics.IsHorizontal(Config.Edge)
                ? Orientation.Horizontal
                : Orientation.Vertical;

            widget.Density = Config.Density;
            widget.Attach();

            return DockLayout.SpanOf(widget.Length(), CellSize);
        }
        finally
        {
            widget.Dispose();
        }
    }

    /// <summary>How many slots the thing being dragged in from outside wants.</summary>
    private int _incoming = 1;

    /// <summary>Whether this drag has already been asked how long it is.</summary>
    private bool _measured;

    /// <summary>Builds one widget, or nothing when it cannot be built.</summary>
    private (WidgetHost Host, int Span)? MakeHost(WidgetConfig entry)
    {
        WidgetViewModel? widget = WidgetCatalog.Create(_context, entry);

        if (widget is null)
        {
            // Written by a later version of the program. Skipping it beats
            // refusing to show the dock at all.
            _log.LogWarning("widget.unknown typeId={TypeId}", entry.TypeId);
            return null;
        }

        if (Application.Current.Resources[entry.TypeId] is not DataTemplate template)
        {
            _log.LogWarning("widget.template missing for typeId={TypeId}", entry.TypeId);
            widget.Dispose();
            return null;
        }

        widget.Orientation = DockMetrics.IsHorizontal(Config.Edge)
            ? Orientation.Horizontal
            : Orientation.Vertical;

        widget.Density = Config.Density;

        var host = new WidgetHost(_log, widget, template);
        DockLayout.Dress(host, Config.Edge);
        host.Attach();

        return (host, Wants(widget, 0));
    }

    /// <summary>
    /// Takes a new list of widgets and changes only what actually differs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A widget that is still there, still the same kind and still set up the
    /// same way keeps the host it already has - which means it keeps its
    /// readings, its icon and its history rather than starting again from a
    /// dash. Only what arrived is built, and only what left is disposed.
    /// </para>
    /// <para>
    /// This is the difference between moving one widget and watching the whole
    /// bar go blank for a second: rebuilding every host on the bar costs an
    /// icon extraction and a first sensor tick each, and the bar is empty
    /// until they finish.
    /// </para>
    /// </remarks>
    public void RefreshContents(MonitorConfig config)
    {
        LetGo();
        Config = config;
        Measure();

        var wanted = new Dictionary<string, WidgetConfig>(StringComparer.Ordinal);

        foreach (WidgetConfig entry in Config.Widgets)
        {
            wanted[entry.InstanceId] = entry;
        }

        var spans = _built.ToDictionary(b => b.Entry.InstanceId, b => b.Span, StringComparer.Ordinal);

        foreach ((string id, WidgetHost host) in _drawn.ToList())
        {
            if (wanted.TryGetValue(id, out WidgetConfig? still) && Same(host.Entry, still))
            {
                continue;
            }

            Strip.Children.Remove(host);
            _hosts.Remove(host);
            _drawn.Remove(id);
            spans.Remove(id);
            host.Dispose();
        }

        var built = new List<(WidgetConfig Entry, int Span)>();
        int fresh = 0;

        foreach (WidgetConfig entry in Config.Widgets)
        {
            if (_drawn.ContainsKey(entry.InstanceId))
            {
                built.Add((entry, spans[entry.InstanceId]));
                continue;
            }

            if (MakeHost(entry) is not { } made)
            {
                continue;
            }

            _hosts.Add(made.Host);
            _drawn[entry.InstanceId] = made.Host;
            built.Add((entry, made.Span));
            fresh++;
        }

        _built = built;

        _log.LogInformation(
            "dock.refreshed monitor={Monitor} kept={Kept} built={Built}",
            Monitor.Identity.FriendlyName, built.Count - fresh, fresh);

        Settle();
        WriteBackCells();
    }

    /// <summary>
    /// Whether two entries describe the same widget, drawn the same way.
    /// </summary>
    /// <remarks>
    /// The slot is deliberately not compared: a widget that only moved is the
    /// same widget, and re-laying it out is all that a move costs.
    /// </remarks>
    private static bool Same(WidgetConfig a, WidgetConfig b) =>
        a.TypeId == b.TypeId
        && string.Equals(
            a.Config?.GetRawText() ?? string.Empty,
            b.Config?.GetRawText() ?? string.Empty,
            StringComparison.Ordinal);

    /// <summary>
    /// The widgets this bar is holding but cannot show.
    /// </summary>
    /// <remarks>
    /// A bar that has run out of room keeps what will not fit rather than
    /// throwing it away - somebody's pinned program should survive a screen
    /// being turned sideways for an afternoon. But a thing that is not drawn
    /// cannot be right-clicked, and right-clicking it was the only way to
    /// reach it: it was in the settings file, counted by the gallery, and
    /// removable from nowhere. Naming them is what makes them reachable.
    /// </remarks>
    public IReadOnlyList<WidgetConfig> Unplaced =>
    [
        .. Config.Widgets.Where(
            w => !_placed.Any(p => p.InstanceId == w.InstanceId) && !Waiting(w.InstanceId))
    ];

    /// <summary>
    /// Whether this widget is off the bar because it is about nothing, rather
    /// than because there was no room for it.
    /// </summary>
    /// <remarks>
    /// The difference matters to what is said about it. "This did not fit" is
    /// something to act on - make room, or take it off. A battery widget on a
    /// machine running from the mains is doing exactly what it was asked to,
    /// and listing it as a problem would teach somebody to ignore the list.
    /// </remarks>
    private bool Waiting(string instanceId) =>
        _drawn.TryGetValue(instanceId, out WidgetHost? host)
        && !host.Widget.Matters
        && host.Widget.Possible;

    /// <summary>
    /// The widgets on this bar that are quiet because they are about nothing.
    /// </summary>
    /// <remarks>
    /// Named for the same reason the ones that do not fit are named. Somebody
    /// who drags a Wi-Fi widget onto the bar of a machine holding a cable sees
    /// nothing happen, and nothing happening is indistinguishable from broken.
    /// It is on the bar; it is simply waiting for the cable to come out, and
    /// something has to say so.
    /// </remarks>
    public IReadOnlyList<WidgetConfig> Quiet =>
        [.. Config.Widgets.Where(w => Waiting(w.InstanceId))];

    /// <summary>What was built for this bar, and where each of it went.</summary>
    private List<(WidgetConfig Entry, int Span)> _built = [];
    private Dictionary<string, WidgetHost> _drawn = [];

    /// <summary>Works out where everything goes, and puts it there.</summary>
    private void Settle()
    {
        _placed = DockGrid.Settle(_built, _capacity);

        DockLayout.Arrange(
            Config.Edge,
            Strip,
            _capacity,
            [.. _placed.Where(p => _drawn.ContainsKey(p.InstanceId))
                .Select(p => ((FrameworkElement)_drawn[p.InstanceId], p))]);
    }

    /// <summary>
    /// Records where the layout actually put things, when that differs from
    /// what the settings said.
    /// </summary>
    /// <remarks>
    /// Only the bar knows how many slots a screen has, so a widget that has
    /// never been placed - one just added, or a whole bar arriving from an
    /// older settings file - is settled here and written down once. Without
    /// this, everything would re-settle on every start and a bar arranged by
    /// hand would not stay arranged.
    /// </remarks>
    private void WriteBackCells()
    {
        var cells = _placed.ToDictionary(p => p.InstanceId, p => p.Cell, StringComparer.Ordinal);

        if (Config.Widgets.All(w => !cells.TryGetValue(w.InstanceId, out int cell) || cell == w.Cell))
        {
            return;
        }

        _log.LogInformation(
            "dock.settled monitor={Monitor} slots={Slots}", Monitor.Identity.FriendlyName, _capacity);

        ImmutableArray<WidgetConfig> settled =
        [
            .. Config.Widgets.Select(
                w => cells.TryGetValue(w.InstanceId, out int cell) ? w with { Cell = cell } : w)
        ];

        // Kept here too, so this window's own idea of its layout matches what
        // was written - otherwise the next thing to read the settings finds a
        // difference this bar has in fact already applied.
        Config = Config with { Widgets = settled };
        Settled?.Invoke(this, settled);
    }


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

        // Where in the widget the hand took hold, so a wide one keeps its
        // grip: a player grabbed by its last button lands with that button
        // under the pointer, not its first.
        _grabOffset = _grabbed is null
            ? 0
            : CellAt(_grabbedAt) - (DockGrid.At(_placed, CellAt(_grabbedAt))?.Cell ?? 0);

        // Not over a widget's own button. The hold takes the pointer away to
        // watch for a drag, and a widget with three keys in a row cannot be
        // given that press back afterwards - there is no way to say which key
        // it was for. Those are dragged by the parts that are not keys.
        if (_grabbed is not null && !(_grabbed.Widget.OwnButtons && OnAKey(e.OriginalSource)))
        {
            _hold.Start();
        }
    }

    /// <summary>Whether the press landed on a button drawn by the widget.</summary>
    private static bool OnAKey(object? source)
    {
        for (DependencyObject? at = source as DependencyObject;
             at is not null;
             at = VisualTreeHelper.GetParent(at))
        {
            if (at is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
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

            // The bar shows its structure for the length of the drag: every
            // occupied slot draws its outline, and the free stretches show
            // as rows of empty slots.
            ShowSlots(true);

            _log.LogInformation(
                "dock.dragging monitor={Monitor} widget={Widget}",
                Monitor.Identity.FriendlyName, _grabbed.Entry.TypeId);
        }

        // Off the bar means "remove on release": the element goes ghostly and
        // the landing marker disappears, the way macOS lets an icon go.
        //
        // Not the same distance in both directions. Moving a widget means
        // travelling along the bar, and a bar is a few tens of points thick -
        // so six points across it is a slip of the hand during the ordinary
        // gesture, and six points past its end is somebody leaving. Crossing
        // the thin way has to be a deliberate journey.
        double along = DockMetrics.IsHorizontal(Config.Edge) ? 8 : LeaveAcross;
        double across = DockMetrics.IsHorizontal(Config.Edge) ? LeaveAcross : 8;

        _offBar = at.X < -along || at.Y < -across
            || at.X > Bar.ActualWidth + along || at.Y > Bar.ActualHeight + across;

        if (_offBar)
        {
            // The widget goes ghostly and the slots it holds go red: dragging
            // something into empty space and having it silently disappear is
            // indistinguishable from having broken it.
            _grabbed.Doomed(true);
            _aim.Visibility = Visibility.Collapsed;
            Farewell(true);
            _landing = null;
            return;
        }

        _grabbed.Doomed(false);
        Farewell(false);

        // The slots the widget would take, lit under the pointer. Nothing lights
        // when it will not fit: a place with no room is not a place, and saying
        // so by showing nothing is the whole of the rule.
        int span = DockGrid.At(_placed, CellAt(_grabbedAt))?.Span ?? 1;
        _landing = Aim(CellAt(at) - _grabOffset, span, _grabbed.Entry.InstanceId);
    }

    private void OnDrop(object sender, PointerRoutedEventArgs e)
    {
        WidgetHost? held = _grabbed;
        bool moved = _moving;

        if (moved && held is not null)
        {
            if (_offBar)
            {
                RemoveWidget(held);
            }
            else
            {
                Land(held);
            }
        }

        LetGo();

        // A press held half a second and released where it started is still a
        // press. Taking the pointer away from the button to watch for a drag
        // was swallowing the click of anybody with an unsteady hand or a
        // trackpad, and a launcher that sometimes does nothing is worse than
        // one that never did.
        if (!moved && held is not null)
        {
            held.Press();
        }
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

        Farewell(false);
        ShowSlots(false);
        Root.ReleasePointerCaptures();

        _grabbed = null;
        _pointer = null;
        _moving = false;
        _offBar = false;
        _landing = null;
        _grabOffset = 0;
    }

    /// <summary>
    /// Moves the widget in hand to the slots it was dropped on.
    /// </summary>
    /// <remarks>
    /// Applied here and then reported, rather than reported and waited for. A
    /// move changes nothing but which slot a widget starts at, so the bar can
    /// simply put it there; going out through the settings and back would tear
    /// down every widget on the bar to redraw one of them, which is the blink
    /// a drop used to end with.
    /// </remarks>
    private void Land(WidgetHost host)
    {
        if (_landing is not { } cell || cell == host.Entry.Cell)
        {
            return;
        }

        _log.LogInformation(
            "dock.moved monitor={Monitor} widget={Widget} cell={Cell}",
            Monitor.Identity.FriendlyName, host.Entry.TypeId, cell);

        string moved = host.Entry.InstanceId;

        Config = Config with
        {
            Widgets = [.. Config.Widgets.Select(w => w.InstanceId == moved ? w with { Cell = cell } : w)],
        };

        _built =
        [
            .. _built.Select(
                b => b.Entry.InstanceId == moved ? (b.Entry with { Cell = cell }, b.Span) : b)
        ];

        Settle();
        Settled?.Invoke(this, Config.Widgets);
    }

    /// <summary>
    /// Puts a widget on a slot, or says why it could not.
    /// </summary>
    /// <remarks>
    /// The bar is a row of places and a place can be taken. Somewhere near
    /// the one aimed at will do; nowhere at all is an answer that has to be
    /// given rather than swallowed, because a thing that does not appear
    /// looks exactly like a thing that is broken.
    /// </remarks>
    private void Place(WidgetConfig entry, int cell)
    {
        int span = SpanWanted(entry);
        int? landing = DockGrid.Nearest(_placed, _capacity, cell, span, ignore: null);

        if (landing is null)
        {
            _log.LogWarning(
                "dock.full monitor={Monitor} widget={Widget}",
                Monitor.Identity.FriendlyName, entry.TypeId);

            Refuse();
            return;
        }

        _log.LogInformation(
            "dock.dropped monitor={Monitor} widget={Widget} cell={Cell}",
            Monitor.Identity.FriendlyName, entry.TypeId, landing.Value);

        Rearranged?.Invoke(this, [.. Config.Widgets, entry with { Cell = landing.Value }]);
    }

    /// <summary>
    /// Says no, in the only two ways a bar can.
    /// </summary>
    /// <remarks>
    /// The slots flash their outlines once and the machine makes the sound it
    /// makes when something will not go. There is no room and no amount of
    /// trying will make room; what there must not be is silence.
    /// </remarks>
    private void Refuse()
    {
        Mcd.Interop.Shell.Chime.Refused();

        ShowSlots(true);

        _refusing.Stop();
        _refusing.Interval = TimeSpan.FromMilliseconds(700);
        _refusing.IsRepeating = false;
        _refusing.Start();
    }

    /// <summary>Takes the flash of slots down again after a refusal.</summary>
    private readonly DispatcherQueueTimer _refusing =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

    /// <summary>The widget covering the slot a point falls in.</summary>
    private WidgetHost? Under(Point at) =>
        DockGrid.At(_placed, CellAt(at)) is { } sitting
            ? _hosts.FirstOrDefault(h => h.Entry.InstanceId == sitting.InstanceId)
            : null;

    /// <summary>
    /// Takes a widget off this bar.
    /// </summary>
    /// <remarks>
    /// Taken off here and then reported, for the same reason a move is: the
    /// other widgets on the bar have not changed and must not flinch. The
    /// short chime is the only thing that says out loud that something is
    /// gone - a widget dragged onto the desktop simply vanishes otherwise,
    /// and vanishing is indistinguishable from a bug.
    /// </remarks>
    private void RemoveWidget(WidgetHost host)
    {
        string gone = host.Entry.InstanceId;

        _log.LogInformation(
            "dock.removed monitor={Monitor} widget={Widget}",
            Monitor.Identity.FriendlyName, host.Entry.TypeId);

        Strip.Children.Remove(host);
        _hosts.Remove(host);
        _drawn.Remove(gone);
        host.Dispose();

        Config = Config with { Widgets = [.. Config.Widgets.Where(w => w.InstanceId != gone)] };
        _built = [.. _built.Where(b => b.Entry.InstanceId != gone)];

        Settle();
        Mcd.Interop.Shell.Chime.Removed();
        Settled?.Invoke(this, Config.Widgets);
    }

    private void Refresh()
    {
        // A bar that has slid off the screen shows nothing, so there is
        // nothing to bring up to date. It is caught up in one go on the way
        // back out, which takes 200 milliseconds nobody can read a number in.
        if (_hiding && _shown <= 0)
        {
            return;
        }

        SensorSnapshot snapshot = _sensors.Current;

        foreach (WidgetHost host in _hosts)
        {
            host.Tick(snapshot);
        }

        Regrow();
    }

    /// <summary>
    /// Gives a widget another slot when what it has to show has outgrown the
    /// ones it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A network rate goes from "0 B/s" to "12.4 MB/s" within a second, and a
    /// figure clipped by its own slot is worse than a bar that shuffles once.
    /// Nothing ever shrinks back for being shorter: a bar that gave a slot up
    /// the moment the number got smaller would shuffle every second of the day.
    /// </para>
    /// <para>
    /// The one thing that does give a slot back is a widget that has stopped
    /// being about anything - the cable going in, the battery coming out. That
    /// is not a number changing, it is the subject going away, and holding a
    /// slot for it would leave a hole in the bar for as long as it lasted.
    /// </para>
    /// </remarks>
    private void Regrow()
    {
        bool grew = false;

        for (int i = 0; i < _built.Count; i++)
        {
            (WidgetConfig entry, int span) = _built[i];

            if (!_drawn.TryGetValue(entry.InstanceId, out WidgetHost? host))
            {
                continue;
            }

            int wants = Wants(host.Widget, span);

            if (wants == span)
            {
                continue;
            }

            // Worth a line when a widget stops being about anything or starts
            // again - it is the one change on the bar that nobody asked for,
            // and "my battery disappeared" needs an answer that is not a guess.
            if (wants == 0 || span == 0)
            {
                _log.LogInformation(
                    "widget.{What} monitor={Monitor} typeId={TypeId}",
                    wants == 0 ? "quiet" : "back",
                    Monitor.Identity.FriendlyName,
                    entry.TypeId);
            }

            _built[i] = (entry, wants);
            grew = true;
        }

        // Not while anything is in hand - including a press that has not yet
        // become a drag. Slots moving out from under a held pointer is the
        // one thing a bar being arranged must never do.
        if (grew && _grabbed is null)
        {
            _log.LogInformation("dock.regrown monitor={Monitor}", Monitor.Identity.FriendlyName);
            Settle();
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
                    Raise(Config);
                }

                break;
        }
    }
}

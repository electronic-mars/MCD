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
    private Band _caretBand;
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
        WindowFrame.SetTopmost(_hwnd, topmost: true);

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
    /// Raised when a widget has been dragged somewhere else on this bar.
    /// </summary>
    /// <remarks>
    /// The window rearranges what it is showing and says so; writing that down
    /// belongs to whoever owns the settings. A dock window that could write
    /// settings would be a second writer, and there is exactly one.
    /// </remarks>
    public event EventHandler<DockBands>? Rearranged;

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

        // The monitor goes with the request. Settings that open on the primary
        // screen when the click happened on the third one are settings the user
        // has to go and find.
        SettingsRequested?.Invoke(this, Monitor);
    }

    private readonly List<WidgetHost> _hosts = [];

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
        Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
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
        LayOutBands();

        Fill(StartBand, Config.Bands.Start);
        Fill(CenterBand, Config.Bands.Center);
        Fill(EndBand, Config.Bands.End);
    }

    private void LayOutBands() => DockLayout.Apply(Config.Edge, StartBand, CenterBand, EndBand);

    private void Fill(Panel band, ImmutableArray<WidgetConfig> configured)
    {
        band.Children.Clear();

        foreach (WidgetConfig entry in configured)
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

            var host = new WidgetHost(_log, widget, template);
            host.Attach();

            _hosts.Add(host);
            band.Children.Add(host);
        }
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
            _grabbed.Opacity = 0.4;
            Root.CapturePointer(e.Pointer);

            _log.LogInformation(
                "dock.dragging monitor={Monitor} widget={Widget}",
                Monitor.Identity.FriendlyName, _grabbed.Entry.TypeId);
        }

        ShowCaret(at);
    }

    private void OnDrop(object sender, PointerRoutedEventArgs e)
    {
        if (_moving && _grabbed is { } host)
        {
            Land(host, _caretBand);
        }

        LetGo();
    }

    private void OnAbandon(object sender, PointerRoutedEventArgs e) => LetGo();

    private void LetGo()
    {
        if (_grabbed is not null)
        {
            _grabbed.Opacity = 1;
        }

        HideCaret();
        Root.ReleasePointerCaptures();

        _grabbed = null;
        _moving = false;
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

    /// <summary>Puts the marker where the widget would land if it were dropped now.</summary>
    private void ShowCaret(Point at)
    {
        bool horizontal = DockMetrics.IsHorizontal(Config.Edge);
        double along = horizontal ? at.X : at.Y;
        double length = horizontal ? Bar.ActualWidth : Bar.ActualHeight;

        // By thirds of the bar rather than by which region is under the pointer:
        // an empty region has no width at all, and would otherwise be the one
        // place a widget could never be dropped.
        Band was = _caretBand;

        _caretBand = along < length / 3 ? Band.Start
            : along < length * 2 / 3 ? Band.Center
            : Band.End;

        if (was != _caretBand)
        {
            _log.LogInformation(
                "dock.caret band={Band} at={At} of={Length}", _caretBand, (int)along, (int)length);
        }

        StackPanel panel = PanelFor(_caretBand);
        int at_ = panel.Children.Count;

        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not WidgetHost host || Where(host) is not { } rect)
            {
                continue;
            }

            double middle = horizontal ? rect.X + (rect.Width / 2) : rect.Y + (rect.Height / 2);

            if (along < middle)
            {
                at_ = i;
                break;
            }
        }

        Dress(horizontal);
        HideCaret();
        panel.Children.Insert(Math.Min(at_, panel.Children.Count), _caret);
    }

    private void Dress(bool horizontal)
    {
        _caret.Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _caret.RadiusX = 1;
        _caret.RadiusY = 1;
        _caret.Width = horizontal ? 2 : 24;
        _caret.Height = horizontal ? 16 : 2;
        _caret.Margin = horizontal ? new Thickness(2, 0, 2, 0) : new Thickness(0, 2, 0, 2);
        _caret.VerticalAlignment = VerticalAlignment.Center;
        _caret.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private void HideCaret()
    {
        if (_caret.Parent is Panel owner)
        {
            owner.Children.Remove(_caret);
        }
    }

    /// <summary>Works out the new arrangement and says so.</summary>
    private void Land(WidgetHost host, Band band)
    {
        StackPanel panel = PanelFor(band);
        int caret = panel.Children.IndexOf(_caret);

        if (caret < 0)
        {
            return;
        }

        // Counted over the widgets that are actually drawn, and not counting the
        // one being moved: it is still sitting in the row it came from.
        List<string> before =
        [
            .. panel.Children.Take(caret).OfType<WidgetHost>()
                .Where(h => h != host)
                .Select(h => h.Entry.InstanceId)
        ];

        WidgetConfig moved = host.Entry;
        DockBands bands = Config.Bands;

        DockBands without = bands with
        {
            Start = [.. bands.Start.Where(w => w.InstanceId != moved.InstanceId)],
            Center = [.. bands.Center.Where(w => w.InstanceId != moved.InstanceId)],
            End = [.. bands.End.Where(w => w.InstanceId != moved.InstanceId)],
        };

        List<WidgetConfig> into = [.. Contents(without, band)];

        // Placed after the last widget that was ahead of the marker. Counting
        // positions in the settings instead would put it in the wrong place on a
        // bar that also holds a widget this build cannot draw.
        int where = before.Count == 0
            ? 0
            : into.FindIndex(w => w.InstanceId == before[^1]) + 1;

        into.Insert(Math.Clamp(where, 0, into.Count), moved);

        DockBands next = band switch
        {
            Band.Start => without with { Start = [.. into] },
            Band.Center => without with { Center = [.. into] },
            _ => without with { End = [.. into] },
        };

        if (next == Config.Bands)
        {
            return;
        }

        _log.LogInformation(
            "dock.rearranged monitor={Monitor} widget={Widget} band={Band}",
            Monitor.Identity.FriendlyName, moved.TypeId, band);

        Rearranged?.Invoke(this, next);
    }

    private StackPanel PanelFor(Band band) => band switch
    {
        Band.Start => StartBand,
        Band.Center => CenterBand,
        _ => EndBand,
    };

    private static ImmutableArray<WidgetConfig> Contents(DockBands bands, Band band) => band switch
    {
        Band.Start => bands.Start,
        Band.Center => bands.Center,
        _ => bands.End,
    };

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
                    WindowFrame.SetTopmost(_hwnd, topmost: true);
                }

                break;
        }
    }
}

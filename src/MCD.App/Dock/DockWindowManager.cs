using System.Collections.Immutable;
using Mcd.Core.Monitors;
using Mcd.Core.Settings;
using Microsoft.Extensions.Logging;
using Mcd.App.Widgets;
using Mcd.Sensors;
using Microsoft.UI.Dispatching;

namespace Mcd.App.Dock;

/// <summary>
/// Keeps one dock window per monitor in step with the display topology.
/// </summary>
/// <remarks>
/// <para>
/// All window state is owned by the UI thread. The coalescer raises its event on
/// a timer thread, so every change is marshalled through the dispatcher queue -
/// that is the serialisation, and there is no second lock to get wrong.
/// </para>
/// <para>
/// Settings are written from here in exactly one case: the reconciler says the
/// monitor registry genuinely changed. A topology reading that never settled
/// writes nothing at all. PowerToys writes on every intermediate reading, which
/// is how one bad snapshot during Win+P becomes permanent (#49604).
/// </para>
/// </remarks>
public sealed class DockWindowManager : IDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<DockWindowManager> _log;
    private readonly SettingsService _settings;
    private readonly DisplayChangeWatcher _watcher;
    private readonly TopologyChangeCoalescer _coalescer;
    private readonly SensorHub _sensors;
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<string, DockWindow> _windows = [];

    private AuthoritativeSnapshot? _lastSnapshot;
    private ImmutableArray<DockPlan> _plans = [];
    private bool _visible = true;
    private bool _disposed;

    public DockWindowManager(
        ILoggerFactory loggers,
        SettingsService settings,
        DisplayChangeWatcher watcher,
        TopologyChangeCoalescer coalescer,
        SensorHub sensors)
    {
        _loggers = loggers;
        _log = loggers.CreateLogger<DockWindowManager>();
        _settings = settings;
        _watcher = watcher;
        _coalescer = coalescer;
        _sensors = sensors;
        _ui = DispatcherQueue.GetForCurrentThread();

        // Kept, so they can be taken off again. A manager that is disposed
        // while the shell restarts would otherwise put the docks back up
        // after teardown, and the shell would keep an edge claimed against
        // windows that no longer exist.
        _onChanged = (_, _) =>
        {
            // Coalesced: a rebuild writes settled cells back for every bar,
            // and each write announced another full pass. One pending pass
            // reads the newest settings when it runs; a queue of them is the
            // same work done four times in a row.
            if (Interlocked.Exchange(ref _reapplyQueued, 1) == 0)
            {
                _ui.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref _reapplyQueued, 0);
                    ReapplySettings();
                });
            }
        };
        _onTopology = (_, trigger) => _coalescer.Poke(trigger);
        _onShell = (_, _) => _ui.TryEnqueue(RebuildEverything);
        _onWatched = (_, watched) => _ui.TryEnqueue(() => Watched(watched));

        _coalescer.Settled += OnSettled;
        _settings.Changed += _onChanged;
        _watcher.TopologyMayHaveChanged += _onTopology;
        _watcher.ShellRestarted += _onShell;
        _watcher.WatchedChanged += _onWatched;
    }

    /// <summary>
    /// Tells every bar whether anybody is looking.
    /// </summary>
    /// <remarks>
    /// Behind a lock screen a bar has an audience of nobody, and every second
    /// it spends reading sensors and laying out figures is a second the
    /// machine cannot spend asleep.
    /// </remarks>
    private void Watched(bool watched)
    {
        // Kept, so that a bar built while the screen is locked - a monitor
        // waking, a dock station plugged in - starts asleep too. It would
        // otherwise tick until the next unlock, which may be the morning.
        _watched = watched;
        _sensors.Watched = watched;

        foreach (DockWindow window in _windows.Values)
        {
            window.Watched = watched;
        }
    }

    private bool _watched = true;

    private readonly EventHandler<bool> _onWatched;

    private readonly EventHandler<SettingsModel> _onChanged;
    private readonly EventHandler<TopologyTrigger> _onTopology;
    private readonly EventHandler _onShell;

    /// <summary>
    /// Raised when someone right-clicks an empty part of a dock, with the
    /// monitor that dock is on.
    /// </summary>
    public event EventHandler<DockSettingsRequest>? SettingsRequested;

    /// <summary>A bar's menu asked for the program to stop.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>What is on screen right now, one entry per attached monitor.</summary>
    public ImmutableArray<DockPlan> Plans => _plans;

    /// <summary>Reads the topology and puts the docks up. Call once, on the UI thread.</summary>
    public void Start() => _coalescer.ReadNow(TopologyTrigger.Startup);

    /// <summary>Whether the docks are on screen at all.</summary>
    /// <remarks>
    /// Hiding tears the windows down rather than making them invisible: an
    /// invisible AppBar would still be holding its slice of the desktop work
    /// area, and maximised windows would stop short of an edge with nothing on it.
    /// </remarks>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            Rebuild(_visible ? _plans : []);
            VisibleChanged?.Invoke(this, _visible);
        }
    }

    /// <summary>The bars went away, or came back.</summary>
    public event EventHandler<bool>? VisibleChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coalescer.Settled -= OnSettled;
        _settings.Changed -= _onChanged;
        _watcher.TopologyMayHaveChanged -= _onTopology;
        _watcher.ShellRestarted -= _onShell;
        _watcher.WatchedChanged -= _onWatched;

        foreach (DockWindow window in _windows.Values)
        {
            window.TearDown();
            window.Close();
        }

        _windows.Clear();
    }

    private void OnSettled(object? sender, TopologySettled settled)
    {
        if (!_ui.TryEnqueue(() => Apply(settled)))
        {
            _log.LogWarning("dock.manager could not reach the UI thread; the docks were left as they are");
        }
    }

    private void Apply(TopologySettled settled)
    {
        if (_disposed)
        {
            return;
        }

        if (settled.Authoritative is null)
        {
            // The topology never settled. Nudge what already exists back into
            // place and decide nothing: a monitor we could not identify must not
            // be allowed to rewrite the registry.
            foreach (DockWindow window in _windows.Values)
            {
                window.ApplyPosition();
            }

            return;
        }

        _lastSnapshot = settled.Authoritative;
        SettingsModel settings = _settings.Current;

        ReconcileResult result = MonitorReconciler.Reconcile(
            settled.Authoritative, settings.Monitors, DateTimeOffset.UtcNow);

        foreach (string note in result.Diagnostics)
        {
            _log.LogInformation("monitors.note {Note}", note);
        }

        if (result.RegistryChanged)
        {
            _settings.Commit(settings with { Monitors = result.Registry }, WriteReason.TopologyReconcile);
        }

        _plans = result.Plans;

        if (_visible)
        {
            Rebuild(_plans);
        }
    }

    /// <summary>Records an arrangement a person made on the bar itself.</summary>
    /// <param name="drawn">
    /// True when the bar is only saying where it has already put things. The
    /// layout is then recorded as this window's own, so the write does not
    /// come back around as a rebuild of a bar that is already correct - a
    /// rebuild that would land one tick after the dock first appears, right
    /// where somebody's first drag is.
    /// </param>
    private void Save(
        string stableId,
        ImmutableArray<WidgetConfig> widgets,
        bool drawn = false,
        string? what = null,
        int? slots = null)
    {
        SettingsModel current = _settings.Current;

        SettingsModel next = current with
        {
            Monitors =
            [
                // Anchor pinned to Start on every write: the bar has already
                // baked the old anchor's shift into the cells it is reporting,
                // and a saved End would bake it in again on the next start.
                .. current.Monitors.Select(
                    c => c.StableId == stableId
                        ? c with { Widgets = widgets, Slots = slots ?? c.Slots, Anchor = DockAnchor.Start }
                        : c)
            ],
        };

        // A bar saying where it has already put things asked nobody, so it
        // cannot be taken back. A bar somebody rearranged with their hands is
        // the one change most worth taking back, and until now it was the one
        // change never written down - the history lived in a settings window
        // that is usually shut while somebody is dragging.
        // Before the commit, not after: the commit announces the change, and
        // whoever runs on that announcement must find these maps already
        // agreeing with what was written - or it schedules another rebuild to
        // fix a difference that does not exist.
        if (drawn
            && next.Monitors.FirstOrDefault(m => m.StableId == stableId) is { } config)
        {
            _dressed[stableId] = Look(config);
            _held[stableId] = Contents(config);
        }

        _settings.Commit(
            next,
            WriteReason.WidgetConfig,
            what ?? (drawn ? null : Loc.Tr("UndoOnTheBar", "on the bar")),
            stableId);
    }

    /// <summary>
    /// Puts every dock through every rebuild, twice. For the unattended check.
    /// </summary>
    public int Rehearse()
    {
        int hosts = 0;

        foreach (DockWindow window in _windows.Values)
        {
            hosts += window.Rehearse(Context());
        }

        return hosts;
    }

    /// <summary>Gives the keyboard to the bar on the screen the pointer is on.</summary>
    public void FocusBar()
    {
        DockWindow? target = _windows.Values.FirstOrDefault(w => w.HoldsPointer) ?? _windows.Values.FirstOrDefault();
        target?.EnterKeyboard();
    }

    /// <summary>Gives the keyboard back on every bar.</summary>
    public void LeaveBars()
    {
        foreach (DockWindow window in _windows.Values)
        {
            window.LeaveKeyboard();
        }
    }

    /// <summary>Presses every widget of one kind, the way a click on it does.</summary>
    public int Press(string typeId) => _windows.Values.Sum(w => w.Press(typeId));

    /// <summary>The view models of one kind of widget on every bar. For the self-test.</summary>
    public IEnumerable<Mcd.App.Widgets.WidgetViewModel> Widgets(string typeId) =>
        _windows.Values.SelectMany(w => w.Widgets(typeId));

    /// <summary>Opens the flyout of every widget of one kind, as a press does. For the self-test.</summary>
    public int OpenFlyouts(string typeId) => _windows.Values.Sum(w => w.OpenFlyouts(typeId));

    /// <summary>Shows the bars as a drag over them shows them. For the self-test.</summary>
    public void Pretend(int cell, int span)
    {
        foreach (DockWindow window in _windows.Values)
        {
            window.Pretend(cell, span);
        }
    }

    /// <summary>How many flyouts of one kind of widget are open right now. For the self-test.</summary>
    public int FlyoutsOpen(string typeId) => _windows.Values.Sum(w => w.FlyoutsOpen(typeId));

    /// <summary>
    /// Presses a gauge's two switches the way the settings page does, and
    /// says what each press wrote. For the unattended check: a switch that
    /// only ever wrote the state it was built with looked, to the person
    /// pressing it, like a switch that had stuck.
    /// </summary>
    public string Switches()
    {
        WidgetConfig entry = WidgetConfig.New(Mcd.App.Widgets.GaugeWidget.Type) with
        {
            Config = WidgetJson.Object(("reading", "cpu"), ("temp", 1)),
        };

        using var gauge = new Mcd.App.Widgets.GaugeWidget(Context(), entry);
        var wrote = new List<string>();

        Microsoft.UI.Xaml.FrameworkElement editor = gauge.CreateEditor(options =>
            wrote.Add(options is { } json ? json.GetRawText() : "null"));

        var pills = new List<Microsoft.UI.Xaml.Controls.Button>();

        // Down the panels the editor is made of. Not the visual tree: this
        // editor has never been shown, so it has no visual children yet.
        void Find(Microsoft.UI.Xaml.DependencyObject at)
        {
            if (at is not Microsoft.UI.Xaml.Controls.Panel panel)
            {
                return;
            }

            foreach (Microsoft.UI.Xaml.UIElement child in panel.Children)
            {
                if (child is Microsoft.UI.Xaml.Controls.Button pill)
                {
                    pills.Add(pill);
                }

                Find(child);
            }
        }

        Find(editor);

        // The temperature switch is the second of the two, after the reading
        // picker's own control; pressed twice, it has to write twice and
        // differently.
        foreach (Microsoft.UI.Xaml.Controls.Button pill in pills)
        {
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(pill);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer).Invoke();
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer).Invoke();
        }

        return string.Join(" | ", wrote);
    }

    /// <summary>Asks the first live bar for its settings, through its own menu.</summary>
    public bool RehearseMenu()
    {
        foreach (DockWindow window in _windows.Values)
        {
            window.RehearseMenu();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the system refused this combination to us.
    /// </summary>
    /// <remarks>
    /// Asked of the manager because the settings window has one of those and
    /// no reference to whatever holds the keys. Answered by whoever set it.
    /// </remarks>
    public Func<string, bool> KeyRefused { get; set; } = _ => false;

    /// <summary>What the bar on this screen is holding but cannot show.</summary>
    /// <summary>
    /// How many slots a screen's bar has, or null while it has no bar.
    /// </summary>
    /// <remarks>
    /// For refitting a layout to the screen it is being copied to. Only the
    /// bar knows - the count depends on the screen's width, its scale and the
    /// bar's density - so a screen that is away answers nothing, and the copy
    /// falls back to carrying the numbers as they are.
    /// </remarks>
    public int? Slots(string stableId) =>
        _windows.TryGetValue(stableId, out DockWindow? window) ? window.Slots : null;

    /// <summary>What the live widget calls itself, or null while its bar is away.</summary>
    public string? Called(string stableId, string instanceId) =>
        _windows.TryGetValue(stableId, out DockWindow? window) ? window.Called(instanceId) : null;

    /// <summary>Points at one widget on one bar, or at nothing anywhere.</summary>
    public void Point(string stableId, string? instanceId)
    {
        foreach ((string id, DockWindow window) in _windows)
        {
            window.Point(id == stableId ? instanceId : null);
        }
    }

    public IReadOnlyList<WidgetConfig> Unplaced(string stableId) =>
        _windows.TryGetValue(stableId, out DockWindow? window) ? window.Unplaced : [];

    /// <summary>What it is holding that is about nothing on this machine today.</summary>
    public IReadOnlyList<WidgetConfig> Quiet(string stableId) =>
        _windows.TryGetValue(stableId, out DockWindow? window) ? window.Quiet : [];

    private WidgetContext Context()
    {
        AppSettings app = _settings.Current.App;

        // Windows high-contrast: the system's own colours on a plain ground,
        // whatever finish was chosen - a translucent or pictured bar under
        // text is exactly what that mode exists to get rid of.
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast && app.Backdrop != "solid")
        {
            app = app with { Backdrop = "solid" };
        }

        return new WidgetContext(
            _sensors,
            new IconChoices(app.Icons),
            new SensorNames(_settings.Current.Sensors.Names),
            _loggers.CreateLogger<WidgetContext>())
        {
            Accent = app.Accent == "windows",
            Acrylic = app.Backdrop == "acrylic",
            Backdrop = app.Backdrop,
            BackdropColour = app.BackdropColour,
            BackdropImage = app.BackdropImage,
            Theme = Appearance.Of(app.Theme),
            Size = app.Size,
        };
    }

    private int? _screens;

    /// <summary>
    /// A layout tied to the number of screens now connected is put on the main
    /// screen's bar when that number changes while the program runs.
    /// </summary>
    private void FollowTheDesk(ImmutableArray<DockPlan> plans)
    {
        int count = plans.Length;
        int? before = _screens;
        _screens = count;

        if (before is null || before == count)
        {
            return;
        }

        BarPreset? preset = _settings.Current.App.Presets.FirstOrDefault(p => p.Screens == count);
        DockPlan? main = plans.FirstOrDefault(p => p.Monitor.IsPrimary);

        if (preset is null || main is null)
        {
            return;
        }

        string stableId = main.Config.StableId;

        // After this change has finished being announced.
        _ui.TryEnqueue(() =>
        {
            SettingsModel current = _settings.Current;
            ImmutableArray<WidgetConfig> copies = [.. preset.Widgets.Select(w => w.AsNewInstance())];

            _log.LogInformation("settings.desk screens={Count} layout={Name}", count, preset.Name);

            _settings.Commit(
                current with
                {
                    Monitors =
                    [
                        .. current.Monitors.Select(m => m.StableId == stableId
                            ? m with { Widgets = copies, Slots = preset.Slots }
                            : m)
                    ],
                },
                WriteReason.UserAction,
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc.Tr("UndoDesk", "layout {0} for {1} screens"),
                    preset.Name,
                    count),
                stableId);
        });
    }

    private void Rebuild(ImmutableArray<DockPlan> plans)
    {
        FollowTheDesk(plans);

        var wanted = plans.Where(p => p.ShouldShow).ToDictionary(p => p.Config.StableId);

        foreach ((string id, DockWindow window) in _windows.ToList())
        {
            if (wanted.TryGetValue(id, out DockPlan? plan) && IsUnchanged(window, plan))
            {
                // Kept, but not necessarily left as it was. Two questions,
                // deliberately separate: has the way the bar is drawn changed,
                // or only what is on it? The first needs every widget built
                // again in the new colours; the second needs the ones that
                // arrived built and the ones that left disposed, and nothing
                // else touched. Treating them as one question is what made
                // moving a single widget blank the whole bar for a second.
                string look = Look(plan.Config);
                string held = Contents(plan.Config);

                if (window.Config.Density != plan.Config.Density)
                {
                    // The bar changes its own thickness in place. Tearing the
                    // window down and building another put three fresh windows
                    // through appbar renegotiation at once, and the process
                    // did not reliably survive the second toggle - a crash on
                    // one machine, a hang on another run. The window already
                    // knows how to re-register its strip and rebuild its
                    // widgets at the new sizes.
                    window.RefreshWidgets(plan.Config, Context());
                    window.ApplyPosition();
                }
                else if (!_dressed.TryGetValue(id, out string? dressed) || dressed != look)
                {
                    window.RefreshWidgets(plan.Config, Context());
                }
                else if (window.Config.Topmost != plan.Config.Topmost)
                {
                    // One window style bit. Rebuilding every widget on the bar
                    // to change whether it sits above other windows is a great
                    // deal of work for a call to SetWindowPos.
                    window.SetTopmost(plan.Config);
                }
                else if (!_held.TryGetValue(id, out string? shown) || shown != held)
                {
                    window.RefreshContents(plan.Config);
                }

                _dressed[id] = look;
                _held[id] = held;
                continue;
            }

            window.TearDown();
            window.Close();
            _windows.Remove(id);
            _dressed.Remove(id);
            _held.Remove(id);
        }

        foreach ((string id, DockPlan plan) in wanted)
        {
            if (_windows.ContainsKey(id))
            {
                continue;
            }

            var window = new DockWindow(
                _loggers.CreateLogger<DockWindow>(), plan.Monitor, plan.Config, Context());

            window.SettingsRequested += (s, request) => SettingsRequested?.Invoke(s, request);
            window.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            window.UndoWhat = () => _settings.UndoWhat;
            window.UndoNow = () => _settings.Undo();

            // A bar that has been rearranged by hand says so; writing it down
            // happens here, where the one writer of settings lives.
            string stableId = plan.Config.StableId;
            window.Rearranged += (_, widgets) => Save(stableId, widgets);
            window.Watched = _watched;
            window.Settled += (_, widgets) =>
                Save(stableId, widgets, drawn: true, slots: window.Slots);

            // Drawn, because the bar is already showing it - and named,
            // because a hand did it.
            window.Moved += (_, widgets) => Save(
                stableId, widgets, drawn: true, what: Loc.Tr("UndoOnTheBar", "on the bar"));
            _windows[id] = window;
            _dressed[id] = Look(plan.Config);
            _held[id] = Contents(plan.Config);

            // Filled only now that we are listening: a bar settling its widgets
            // onto slots for the first time has something to say about it.
            window.Fill();
            window.Activate();
        }

        _log.LogInformation("dock.manager docks={Docks} monitors={Monitors}", _windows.Count, plans.Length);
    }

    /// <summary>
    /// Re-applies the current settings to the docks already on screen.
    /// </summary>
    /// <remarks>
    /// Deliberately does not write anything. The change that brought us here has
    /// already been saved by whoever made it; reconciling again only decides what
    /// to show. This is also why it cannot loop: the write is upstream of the
    /// event, never downstream of it.
    /// </remarks>
    private void ReapplySettings()
    {
        if (_disposed || _lastSnapshot is null)
        {
            return;
        }

        ReconcileResult result = MonitorReconciler.Reconcile(
            _lastSnapshot, _settings.Current.Monitors, DateTimeOffset.UtcNow);

        _plans = result.Plans;

        if (_visible)
        {
            Rebuild(_plans);
        }
    }

    /// <summary>Rebuilds every dock from scratch, for when Explorer has restarted.</summary>
    private void RebuildEverything()
    {
        if (_disposed)
        {
            return;
        }

        foreach (DockWindow window in _windows.Values)
        {
            window.TearDown();
            window.Close();
        }

        _windows.Clear();
        _coalescer.ReadNow(TopologyTrigger.ExplorerRestarted);
    }

    /// <summary>How each dock window was last dressed, by stable id.</summary>
    private int _reapplyQueued;

    private readonly Dictionary<string, string> _dressed = [];

    /// <summary>What each dock window was last holding, by stable id.</summary>
    private readonly Dictionary<string, string> _held = [];

    /// <summary>
    /// Everything that decides how a bar is drawn, flattened to one string.
    /// </summary>
    /// <remarks>
    /// A change here means every widget on that bar has to be built again:
    /// the colours, the icons and the density are baked into each one as it
    /// is made.
    /// </remarks>
    private string Look(MonitorConfig config)
    {
        AppSettings app = _settings.Current.App;

        var text = new System.Text.StringBuilder()
            .Append(app.Theme).Append('|')
            .Append(app.Backdrop).Append('|')
            .Append(app.BackdropColour).Append('|')
            .Append(app.BackdropImage).Append('|')
            .Append(app.Accent).Append('|')
            .Append(app.Size).Append('|');

        foreach (KeyValuePair<string, string> icon in app.Icons)
        {
            text.Append(icon.Key).Append('=').Append(icon.Value).Append(';');
        }

        foreach (KeyValuePair<string, string> name in _settings.Current.Sensors.Names)
        {
            text.Append(name.Key).Append('=').Append(name.Value).Append(';');
        }

        return text.ToString();
    }

    /// <summary>
    /// What is on a bar and where, flattened to one string.
    /// </summary>
    /// <remarks>
    /// The slot is part of it: a widget that only moved still needs the bar
    /// laid out again, even though nothing has to be rebuilt to do it.
    /// </remarks>
    private static string Contents(MonitorConfig config)
    {
        // The anchor belongs here rather than among the things that rebuild a
        // bar: it changes where the same widgets are drawn, not what they are.
        // Left out altogether, as it was, the setting was written down and
        // nothing on screen moved until something else - a change of edge, a
        // monitor unplugged, a restart - happened to rebuild the bar.
        var text = new System.Text.StringBuilder().Append(config.Anchor);

        foreach (WidgetConfig widget in config.Widgets)
        {
            text.Append('|').Append(widget.InstanceId)
                .Append(':').Append(widget.TypeId)
                .Append(':').Append(widget.Cell)
                .Append(':').Append(widget.Config?.GetRawText());
        }

        return text.ToString();
    }

    private static bool IsUnchanged(DockWindow window, DockPlan plan) =>
        window.Monitor.Bounds.Equals(plan.Monitor.Bounds)
        && window.Monitor.Dpi == plan.Monitor.Dpi
        && window.Config.Edge == plan.Config.Edge
        && window.Config.Mode == plan.Config.Mode;
}

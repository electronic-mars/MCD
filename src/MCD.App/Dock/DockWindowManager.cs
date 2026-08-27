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

        _coalescer.Settled += OnSettled;
        _settings.Changed += (_, _) => _ui.TryEnqueue(ReapplySettings);
        _watcher.TopologyMayHaveChanged += (_, trigger) => _coalescer.Poke(trigger);
        _watcher.ShellRestarted += (_, _) => _ui.TryEnqueue(RebuildEverything);
    }

    /// <summary>
    /// Raised when someone right-clicks an empty part of a dock, with the
    /// monitor that dock is on.
    /// </summary>
    public event EventHandler<MonitorInfo>? SettingsRequested;

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
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coalescer.Settled -= OnSettled;

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
    private void Save(string stableId, ImmutableArray<WidgetConfig> widgets)
    {
        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                Monitors =
                [
                    .. current.Monitors.Select(
                        c => c.StableId == stableId ? c with { Widgets = widgets } : c)
                ],
            },
            WriteReason.WidgetConfig);
    }

    /// <summary>Pins a dropped file to the launcher, once.</summary>
    private void Pin(string path)
    {
        SettingsModel current = _settings.Current;

        if (current.App.Launcher.Any(
            i => string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _settings.Commit(
            current with
            {
                App = current.App with
                {
                    Launcher =
                    [
                        .. current.App.Launcher,
                        LaunchItem.For(path, Settings.LauncherRow.NameFor(path)),
                    ],
                },
            },
            WriteReason.UserAction);

        _log.LogInformation("launcher.pinned by drop {Target}", path);
    }

    /// <summary>Puts the pinned items in the order a bar arranged them.</summary>
    private void ReorderLauncher(ImmutableArray<string> ids)
    {
        SettingsModel current = _settings.Current;

        var by = current.App.Launcher.ToDictionary(i => i.Id);
        List<LaunchItem> ordered = [.. ids.Where(by.ContainsKey).Select(id => by[id])];

        // Anything the bar did not mention - an item added between the drag
        // and the drop - keeps its place at the end rather than vanishing.
        ordered.AddRange(current.App.Launcher.Where(i => !ids.Contains(i.Id)));

        _settings.Commit(
            current with { App = current.App with { Launcher = [.. ordered] } },
            WriteReason.UserAction);

        _log.LogInformation("launcher.reordered");
    }

    private void Unpin(string id)
    {
        SettingsModel current = _settings.Current;

        _settings.Commit(
            current with
            {
                App = current.App with
                {
                    Launcher = [.. current.App.Launcher.Where(i => i.Id != id)],
                },
            },
            WriteReason.UserAction);

        _log.LogInformation("launcher.unpinned {Id}", id);
    }

    private WidgetContext Context()
    {
        AppSettings app = _settings.Current.App;

        return new WidgetContext(
            _sensors,
            new IconChoices(app.Icons),
            app.Launcher,
            _loggers.CreateLogger<WidgetContext>())
        {
            Accent = app.Accent == "windows",
            Acrylic = app.Backdrop == "acrylic",
            Backdrop = app.Backdrop,
            BackdropColour = app.BackdropColour,
            BackdropImage = app.BackdropImage,
            Theme = Appearance.Of(app.Theme),
        };
    }

    private void Rebuild(ImmutableArray<DockPlan> plans)
    {
        var wanted = plans.Where(p => p.ShouldShow).ToDictionary(p => p.Config.StableId);

        foreach ((string id, DockWindow window) in _windows.ToList())
        {
            if (wanted.TryGetValue(id, out DockPlan? plan) && IsUnchanged(window, plan))
            {
                // Kept, but not left as it was: the settings may have changed
                // what belongs on it. Before this, choosing an icon or pinning a
                // program did nothing until the program was restarted.
                window.RefreshWidgets(plan.Config, Context());
                continue;
            }

            window.TearDown();
            window.Close();
            _windows.Remove(id);
        }

        foreach ((string id, DockPlan plan) in wanted)
        {
            if (_windows.ContainsKey(id))
            {
                continue;
            }

            var window = new DockWindow(
                _loggers.CreateLogger<DockWindow>(), plan.Monitor, plan.Config, Context());

            window.SettingsRequested += (s, monitor) => SettingsRequested?.Invoke(s, monitor);

            // A bar that has been rearranged by hand says so; writing it down
            // happens here, where the one writer of settings lives.
            string stableId = plan.Config.StableId;
            window.Rearranged += (_, widgets) => Save(stableId, widgets);
            window.PinRequested += (_, path) => Pin(path);
            window.LauncherReordered += (_, ids) => ReorderLauncher(ids);
            window.Unpinned += (_, id) => Unpin(id);
            _windows[id] = window;
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
        foreach (DockWindow window in _windows.Values)
        {
            window.TearDown();
            window.Close();
        }

        _windows.Clear();
        _coalescer.ReadNow(TopologyTrigger.ExplorerRestarted);
    }

    private static bool IsUnchanged(DockWindow window, DockPlan plan) =>
        window.Monitor.Bounds.Equals(plan.Monitor.Bounds)
        && window.Monitor.Dpi == plan.Monitor.Dpi
        && window.Config.Edge == plan.Config.Edge
        && window.Config.Density == plan.Config.Density
        && window.Config.Mode == plan.Config.Mode;
}

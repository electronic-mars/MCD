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
    public event EventHandler<DockSettingsRequest>? SettingsRequested;

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

    private WidgetContext Context()
    {
        AppSettings app = _settings.Current.App;

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
        };
    }

    private void Rebuild(ImmutableArray<DockPlan> plans)
    {
        var wanted = plans.Where(p => p.ShouldShow).ToDictionary(p => p.Config.StableId);

        foreach ((string id, DockWindow window) in _windows.ToList())
        {
            if (wanted.TryGetValue(id, out DockPlan? plan) && IsUnchanged(window, plan))
            {
                // Kept, but not necessarily left as it was: the settings may
                // have changed what belongs on it. Rebuilt only when they
                // actually did - rearranging one bar must not make every other
                // bar tear its widgets down and put them back, which is the
                // stutter a drop used to cost.
                string signature = Signature(plan.Config);

                if (_dressed.TryGetValue(id, out string? shown) && shown == signature)
                {
                    continue;
                }

                window.RefreshWidgets(plan.Config, Context());
                _dressed[id] = signature;
                continue;
            }

            window.TearDown();
            window.Close();
            _windows.Remove(id);
            _dressed.Remove(id);
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

            // A bar that has been rearranged by hand says so; writing it down
            // happens here, where the one writer of settings lives.
            string stableId = plan.Config.StableId;
            window.Rearranged += (_, widgets) => Save(stableId, widgets);
            _windows[id] = window;
            _dressed[id] = Signature(plan.Config);

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
        foreach (DockWindow window in _windows.Values)
        {
            window.TearDown();
            window.Close();
        }

        _windows.Clear();
        _coalescer.ReadNow(TopologyTrigger.ExplorerRestarted);
    }

    /// <summary>What each dock window was last built from, by stable id.</summary>
    private readonly Dictionary<string, string> _dressed = [];

    /// <summary>
    /// Everything that decides what a dock window shows, flattened to one
    /// string. Two equal signatures mean a rebuild would change nothing.
    /// </summary>
    private string Signature(MonitorConfig config)
    {
        AppSettings app = _settings.Current.App;

        var text = new System.Text.StringBuilder()
            .Append(app.Theme).Append('|')
            .Append(app.Backdrop).Append('|')
            .Append(app.BackdropColour).Append('|')
            .Append(app.BackdropImage).Append('|')
            .Append(app.Accent).Append('|')
            .Append(config.Topmost).Append('|');

        foreach (KeyValuePair<string, string> icon in app.Icons)
        {
            text.Append(icon.Key).Append('=').Append(icon.Value).Append(';');
        }

        foreach (WidgetConfig widget in config.Widgets)
        {
            text.Append('|').Append(widget.InstanceId)
                .Append(':').Append(widget.TypeId)
                .Append(':').Append(widget.Config?.GetRawText());
        }

        return text.ToString();
    }

    private static bool IsUnchanged(DockWindow window, DockPlan plan) =>
        window.Monitor.Bounds.Equals(plan.Monitor.Bounds)
        && window.Monitor.Dpi == plan.Monitor.Dpi
        && window.Config.Edge == plan.Config.Edge
        && window.Config.Density == plan.Config.Density
        && window.Config.Mode == plan.Config.Mode;
}

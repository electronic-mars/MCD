using Mcd.App.Dock;
using Mcd.App.Settings;
using Mcd.Core.Infrastructure;
using Mcd.Core.Monitors;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Mcd.Sensors.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Mcd.App;

public partial class App : Application
{
    /// <summary>
    /// Local, not Global. A standard user is not allowed to create a Global
    /// mutex, so CreateMutex would fail with "access denied" rather than
    /// "already exists" - and every launch would think it was the first.
    /// </summary>
    private const string SingleInstanceMutex = "Local\\MasterControlDockSingleInstance";

    private Mutex? _onlyInstance;
    private readonly InstanceSignal _shutdown = InstanceSignal.Shutdown;
    private readonly InstanceSignal _showSettings = InstanceSignal.ShowSettings;
    private RegisteredWaitHandle? _shutdownWatch;
    private RegisteredWaitHandle? _settingsWatch;
    private SettingsWindow? _settingsWindow;
    private ILoggerFactory? _loggers;
    private FileLogger? _logProvider;
    private ServiceProvider? _services;
    private DockWindowManager? _docks;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private bool _shutDown;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _logProvider = new FileLogger();
        ILoggerFactory loggers = LoggerFactory.Create(builder => builder.AddProvider(_logProvider));

        ILogger start = loggers.CreateLogger("app");
        start.LogInformation(
            "=== start version={Version} packaged={Packaged} root={Root}",
            AppInfo.Version, AppPaths.IsPackaged, AppPaths.Root);

        if (WantsToStopAnotherCopy())
        {
            start.LogInformation("start.signalled stop: {Found}", _shutdown.Raise());
            _logProvider.Dispose();
            Exit();
            return;
        }

        if (!ClaimSingleInstance(start))
        {
            // Starting it again is how someone asks for its settings. Doing
            // nothing visible would look exactly like a launch that failed.
            start.LogInformation("start.signalled settings: {Found}", _showSettings.Raise());

            // Flushed by hand. These two paths return before the process-exit
            // handler is wired up, and the log's writer is a background thread -
            // so without this the only record of what a second launch did is
            // thrown away as the process ends.
            _logProvider.Dispose();
            Exit();
            return;
        }

        ListenForSignals(start);

        _loggers = loggers;
        _services = BuildServices(loggers);

        ApplyLanguage(start);

        // The AppBar registration is the one piece of state that outlives a
        // crash: the shell keeps the reserved work area until someone sends
        // ABM_REMOVE. Every exit path has to reach the same teardown.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        // The XAML handler below only sees exceptions on the UI thread. This one
        // catches what the runtime is about to die from, which is the only
        // record left of a crash that takes the log thread with it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            start.LogCritical(e.ExceptionObject as Exception, "unhandled exception, the process is ending");
            _logProvider?.Dispose();
        };
        UnhandledException += (_, e) =>
        {
            start.LogCritical(e.Exception, "unhandled exception on the UI thread");
            Shutdown();
        };

        LogTopology(start);

        _docks = _services.GetRequiredService<DockWindowManager>();
        _docks.SettingsRequested += (_, request) => ShowSettings(request.Screen, request.WidgetId);
        _docks.Start();

        if (Environment.GetEnvironmentVariable("MCD_SELFTEST") == "1")
        {
            ScheduleSelfTestExit(start);
        }
    }

    /// <summary>
    /// Applies the chosen interface language, before any window exists.
    /// </summary>
    /// <remarks>
    /// Through the Windows App SDK's own override, which is the one MRT Core
    /// reads in an unpackaged program - the Windows.Globalization one needs a
    /// package identity and does nothing here. MCD_LANG is a seam for checking
    /// the other language without touching the settings, like MCD_DATA_DIR.
    /// </remarks>
    private void ApplyLanguage(ILogger log)
    {
        string chosen = Environment.GetEnvironmentVariable("MCD_LANG")
            ?? _services!.GetRequiredService<SettingsService>().Current.App.Language;

        if (chosen is not { Length: > 0 } || chosen == "system")
        {
            return;
        }

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = chosen;
            log.LogInformation("app.language override={Language}", chosen);
        }
        catch (Exception e)
        {
            // An invalid tag. The program is no worse off in its default language.
            log.LogWarning(e, "app.language could not apply {Language}", chosen);
        }
    }

    /// <summary>
    /// False when another copy is already running.
    /// </summary>
    /// <remarks>
    /// Two copies would fight over the same edge of the same monitor: both
    /// register an AppBar, the shell gives the second one whatever the first left
    /// over, and the desktop loses twice the space it should.
    /// </remarks>
    private bool ClaimSingleInstance(ILogger log)
    {
        _onlyInstance = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool mine);

        if (mine)
        {
            return true;
        }

        log.LogInformation("start.refused another copy is already running");
        _onlyInstance.Dispose();
        _onlyInstance = null;
        return false;
    }

    /// <summary>
    /// True when this launch is only here to stop the copy that is already
    /// running, rather than to start another one.
    /// </summary>
    /// <remarks>
    /// The installer, the uninstaller and the build scripts all need to stop a
    /// running copy, and none of them can click a tray menu. Killing it would
    /// leave the AppBar registered and the desktop permanently short.
    /// </remarks>
    private static bool WantsToStopAnotherCopy() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase));

    private void ListenForSignals(ILogger log)
    {
        _shutdownWatch = ThreadPool.RegisterWaitForSingleObject(
            _shutdown.Listen(),
            (_, timedOut) =>
            {
                if (timedOut)
                {
                    return;
                }

                log.LogInformation("shutdown.requested from another process");

                // Shutdown touches windows, so it has to happen on the UI thread.
                _uiQueue?.TryEnqueue(() => { Shutdown(); Exit(); });
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _settingsWatch = ThreadPool.RegisterWaitForSingleObject(
            _showSettings.Listen(),
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    _uiQueue?.TryEnqueue(() => ShowSettings());
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Brings the settings window up, creating it the first time.</summary>
    /// <param name="screen">
    /// The monitor to open on, when the request came from a dock. Null when it
    /// came from someone starting the program again, which has no screen of its
    /// own to speak of.
    /// </param>
    private void ShowSettings(MonitorInfo? screen = null, string? widgetId = null)
    {
        if (_shutDown || _services is null || _docks is null || _loggers is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            try
            {
                _settingsWindow = new SettingsWindow(
                    _loggers.CreateLogger<SettingsWindow>(),
                    _services.GetRequiredService<SettingsService>(),
                    _docks,
                    _services.GetRequiredService<SensorHub>(),
                    onExit: () => { Shutdown(); Exit(); });
            }
            catch (Exception e)
            {
                // The docks are the program; the settings are a window onto
                // them. One that cannot be built is a bug worth a line in the
                // log, not a reason to take three bars off the desktop - and
                // without this the whole process dies with nothing written down,
                // because a failure inside XAML arrives as a stowed exception
                // that no managed handler upstream ever sees.
                _loggers.CreateLogger("app").LogError(e, "settings.failed to build the window");
                _settingsWindow = null;
                return;
            }

            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        else
        {
            _settingsWindow.Reload();
        }

        if (screen is not null)
        {
            _settingsWindow.SizeAndCentre(screen);
            _settingsWindow.Show(screen, widgetId);
        }

        _settingsWindow.Activate();
        _settingsWindow.AppWindow.MoveInZOrderAtTop();

        // Activate alone is not enough. The click that asked for this landed on
        // a dock, and a dock never takes the foreground, so Windows treats the
        // request as coming from a background process and quietly leaves the
        // window behind whatever was in front.
        Mcd.Interop.Windowing.WindowFrame.BringToFront(
            WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow));
        _loggers.CreateLogger("app").LogInformation("settings.shown");
    }

    private static ServiceProvider BuildServices(ILoggerFactory loggers)
    {
        var services = new ServiceCollection();

        services.AddSingleton(loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<MonitorService>();
        services.AddSingleton<DisplayChangeWatcher>();
        services.AddSingleton<TopologyChangeCoalescer>();
        services.AddSingleton<ISensorProvider, PdhSensorProvider>();
        services.AddSingleton<ISensorProvider, MemoryProvider>();
        services.AddSingleton<ISensorProvider, NvidiaGpuProvider>();
        services.AddSingleton<ISensorProvider, StorageTemperatureProvider>();
        services.AddSingleton<ISensorProvider, AcpiThermalProvider>();

        // Held by its own type as well, because the settings page has to be able
        // to say why it is not reading anything, and "off" and "HWiNFO is not
        // running" are different answers to that.
        services.AddSingleton(sp => new HwInfoProvider(
            sp.GetRequiredService<ILogger<HwInfoProvider>>(),
            () => sp.GetRequiredService<SettingsService>().Current
                .Sensors.EnabledProviders.GetValueOrDefault(HwInfoProvider.ProviderId, true)));
        services.AddSingleton<ISensorProvider>(sp => sp.GetRequiredService<HwInfoProvider>());

        services.AddSingleton(sp => new LhmProvider(
            sp.GetRequiredService<ILogger<LhmProvider>>(),
            () => sp.GetRequiredService<SettingsService>().Current
                .Sensors.EnabledProviders.GetValueOrDefault(LhmProvider.ProviderId, true),
            () => sp.GetRequiredService<SettingsService>().Current.Sensors.LhmHttpEndpoint));
        services.AddSingleton<ISensorProvider>(sp => sp.GetRequiredService<LhmProvider>());

        services.AddSingleton<SensorHub>();
        services.AddSingleton<DockWindowManager>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Writes the topology to the log at startup.
    /// </summary>
    /// <remarks>
    /// Kept in the release build deliberately. When someone reports "the dock is
    /// blank on my second screen", this is the only record of what the program
    /// actually saw - the thing missing from every report on the PowerToys bug.
    /// </remarks>
    private void LogTopology(ILogger log)
    {
        MonitorSnapshot snapshot = _services!.GetRequiredService<MonitorService>().Enumerate();

        log.LogInformation(
            "monitors.snapshot {Kind} count={Count}",
            snapshot is AuthoritativeSnapshot ? "authoritative" : "provisional",
            snapshot.Count);

        foreach (MonitorInfo m in snapshot.Monitors)
        {
            log.LogInformation(
                "monitors.item id={Id} name={Name} edid={Edid} gdi={Gdi} primary={Primary} "
                + "bounds={Left},{Top},{Right},{Bottom} dpi={Dpi} path={Path}",
                m.StableId, m.Identity.FriendlyName, m.Identity.EdidKey, m.Identity.GdiName, m.IsPrimary,
                m.Bounds.left, m.Bounds.top, m.Bounds.right, m.Bounds.bottom, m.Dpi, m.Identity.DevicePath);
        }
    }

    /// <summary>
    /// Ends the program the same way a person would, after a fixed run.
    /// </summary>
    /// <remarks>
    /// The CI smoke check cannot simply kill the process: taskkill skips
    /// ProcessExit, the AppBar registration survives, and the build agent's work
    /// area stays shrunk for every job that follows.
    /// </remarks>
    /// <summary>
    /// The self-test's timers, held in a field on purpose: a
    /// DispatcherQueueTimer referenced only by a local is garbage once the
    /// method returns, and whether its ticks ever fire then depends on when
    /// the collector happens to run. The eight-second exit tick was lost to
    /// exactly that.
    /// </summary>
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _selfTest = [];

    private void ScheduleSelfTestExit(ILogger log)
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MCD_SELFTEST_SECONDS"), out int s)
            ? s
            : 8;

        log.LogInformation("selftest.armed seconds={Seconds}", seconds);

        // The settings window is opened and closed as part of the run. Every
        // page of it is built from XAML, and XAML names are checked when they
        // are read rather than when they are compiled - a wrong one is a parse
        // failure that ends the process without a word in the log. Only opening
        // the window finds them, so the check that runs unattended has to.
        Microsoft.UI.Dispatching.DispatcherQueueTimer settings =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        settings.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds / 3));
        settings.IsRepeating = false;
        settings.Tick += (_, _) =>
        {
            ShowSettings();

            // Then the page is built a second time, the way a right-click on
            // a dock builds it: naming a screen, and sometimes a widget. The
            // second build is where a page made of code goes wrong - an
            // element it holds on to still belongs to the page before it -
            // and opening the window with no screen never reaches it, which
            // is how a crash on every right-click got past a run of this.
            //
            // The page is asked directly rather than through ShowSettings:
            // that one also drags the window to the front, and joining input
            // queues with whatever is in front has no business happening in
            // an unattended run.
            if (_docks?.Plans.FirstOrDefault(p => p.ShouldShow) is { } plan)
            {
                _settingsWindow?.Show(
                    plan.Monitor, plan.Config.Widgets.FirstOrDefault()?.InstanceId);

                log.LogInformation("selftest.reopened");
            }

            // Left open, and walked through, when pages were asked for: a
            // visual change is verified by looking at the window it changed,
            // and the window that gets looked at must not be the one on
            // somebody's desk while they are working. Each page is announced
            // in the log so whatever is photographing can keep in step
            // instead of guessing at a cadence.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_PAGE") is { Length: > 0 } pages)
            {
                Walk(log, [.. pages.Split(',', StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)]);

                return;
            }

            _settingsWindow?.Close();
        };
        _selfTest.Add(settings);
        settings.Start();

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            log.LogInformation("selftest.done");
            Shutdown();
            Exit();
        };
        _selfTest.Add(timer);
        timer.Start();
    }

    /// <summary>Shows each page in turn, announcing every one it reaches.</summary>
    private void Walk(ILogger log, IReadOnlyList<string> pages)
    {
        int at = 0;

        Microsoft.UI.Dispatching.DispatcherQueueTimer step =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        step.Interval = TimeSpan.FromMilliseconds(1400);

        step.Tick += (_, _) =>
        {
            if (at >= pages.Count || _settingsWindow is null)
            {
                step.Stop();
                return;
            }

            _settingsWindow.GoTo(pages[at]);
            log.LogInformation("selftest.page {Page}", pages[at]);
            at++;
        };

        _selfTest.Add(step);
        step.Start();
    }

    private void Shutdown()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;

        _settingsWindow?.Close();
        _settingsWindow = null;

        _docks?.Dispose();
        _docks = null;

        _services?.Dispose();
        _services = null;

        _shutdownWatch?.Unregister(null);
        _shutdownWatch = null;
        _settingsWatch?.Unregister(null);
        _settingsWatch = null;
        _shutdown.Dispose();
        _showSettings.Dispose();

        _logProvider?.Dispose();
        _logProvider = null;

        _onlyInstance?.ReleaseMutex();
        _onlyInstance?.Dispose();
        _onlyInstance = null;
    }
}

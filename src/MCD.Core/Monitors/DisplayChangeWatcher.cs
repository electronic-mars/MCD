using Mcd.Interop.Windowing;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Core.Monitors;

/// <summary>Why the display topology might have moved.</summary>
public enum TopologyTrigger
{
    Startup,
    DisplayChange,
    WorkAreaChange,
    MonitorArrivedOrLeft,
    ResumedFromSleep,
    SessionUnlocked,
    ExplorerRestarted,
}

/// <summary>
/// Watches the system for anything that could have changed the set of monitors.
/// </summary>
/// <remarks>
/// One instance per process, created before the first dock window and disposed
/// after the last. In PowerToys the dock window is the listener, so a user with
/// every dock switched off has a monitor list that quietly goes stale - one of
/// the four causes named in their fix for #49604.
/// </remarks>
public sealed unsafe class DisplayChangeWatcher : IDisposable
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDeviceChange = 0x0219;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmSessionChange = 0x02B1;

    private const uint SpiSetWorkArea = 0x002F;
    private const uint DbtDeviceArrival = 0x8000;
    private const uint DbtDeviceRemoveComplete = 0x8004;
    private const uint PbtApmResumeAutomatic = 0x0012;
    private const uint WtsSessionUnlock = 0x8;

    private readonly ILogger<DisplayChangeWatcher> _log;
    private readonly BroadcastListener _listener;
    private readonly uint _taskbarCreated;

    private nint _deviceNotification;
    private bool _disposed;

    public DisplayChangeWatcher(ILogger<DisplayChangeWatcher> log)
    {
        _log = log;
        _listener = new BroadcastListener();
        _listener.MessageReceived += OnMessage;

        // Explorer restarting drops every AppBar registration on the floor.
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

        Subscribe();
        _log.LogInformation("display.watcher started hwnd={Hwnd}", _listener.Handle);
    }

    /// <summary>Raised on the listener's thread. Handlers must not block.</summary>
    public event EventHandler<TopologyTrigger>? TopologyMayHaveChanged;

    /// <summary>Raised when Explorer restarted and every AppBar must re-register.</summary>
    public event EventHandler? ShellRestarted;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_deviceNotification != 0)
        {
            PInvoke.UnregisterDeviceNotification(new HDEVNOTIFY((void*)_deviceNotification));
        }

        PInvoke.WTSUnRegisterSessionNotification(new HWND(_listener.Handle));
        _listener.Dispose();
    }

    private void Subscribe()
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE_W
        {
            dbcc_size = (uint)sizeof(DEV_BROADCAST_DEVICEINTERFACE_W),
            dbcc_devicetype = 0x00000005, // DBT_DEVTYP_DEVICEINTERFACE
            dbcc_classguid = PInvoke.GUID_DEVINTERFACE_MONITOR,
        };

        HDEVNOTIFY handle = PInvoke.RegisterDeviceNotification(
            new HANDLE(_listener.Handle),
            &filter,
            REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);

        _deviceNotification = (nint)handle.Value;

        if (_deviceNotification == 0)
        {
            _log.LogWarning("display.watcher device notifications unavailable; relying on WM_DISPLAYCHANGE alone");
        }

        if (!PInvoke.WTSRegisterSessionNotification(new HWND(_listener.Handle), PInvoke.NOTIFY_FOR_THIS_SESSION))
        {
            _log.LogWarning("display.watcher session notifications unavailable");
        }
    }

    private void OnMessage(object? sender, WindowMessageEventArgs e)
    {
        TopologyTrigger? trigger = Classify(e);
        if (trigger is null)
        {
            if (e.Message == _taskbarCreated)
            {
                _log.LogInformation("display.trigger ExplorerRestarted");
                ShellRestarted?.Invoke(this, EventArgs.Empty);
                Raise(TopologyTrigger.ExplorerRestarted);
            }

            return;
        }

        Raise(trigger.Value);
    }

    private TopologyTrigger? Classify(WindowMessageEventArgs e) => e.Message switch
    {
        WmDisplayChange => TopologyTrigger.DisplayChange,

        WmSettingChange when (uint)e.WParam == SpiSetWorkArea => TopologyTrigger.WorkAreaChange,

        WmDeviceChange when (uint)e.WParam is DbtDeviceArrival or DbtDeviceRemoveComplete
            => TopologyTrigger.MonitorArrivedOrLeft,

        WmPowerBroadcast when (uint)e.WParam == PbtApmResumeAutomatic => TopologyTrigger.ResumedFromSleep,

        WmSessionChange when (uint)e.WParam == WtsSessionUnlock => TopologyTrigger.SessionUnlocked,

        _ => null,
    };

    private void Raise(TopologyTrigger trigger)
    {
        _log.LogInformation("display.trigger {Trigger}", trigger);
        TopologyMayHaveChanged?.Invoke(this, trigger);
    }
}

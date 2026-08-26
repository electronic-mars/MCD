using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Mcd.Interop.AppBar;

/// <summary>
/// One window's registration with the shell as an application desktop toolbar.
/// </summary>
/// <remarks>
/// <para>
/// Adapted from microsoft/PowerToys,
/// src/modules/cmdpal/Microsoft.CmdPal.UI/Dock/DockWindow.xaml.cs (MIT).
/// Copyright (c) Microsoft Corporation. See licenses/PowerToys-MIT.txt.
/// Changes: extracted into a standalone state machine with an explicit
/// registration counter, and no dependency on settings or monitor code.
/// </para>
/// <para>
/// The invariant this class exists to hold is one ABM_NEW per ABM_REMOVE. A
/// registration that leaks shrinks the desktop work area permanently - the
/// worst damage this program is able to do to a machine - and it survives
/// reboots because the shell persists it.
/// </para>
/// </remarks>
public sealed class AppBarHost : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbmGetState = 0x00000004;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const uint AbmGetAutoHideBarEx = 0x0000000A;
    private const uint AbmSetAutoHideBarEx = 0x0000000B;

    private const uint AbsAutoHide = 0x00000001;

    public const uint AbnStateChange = 0x00000000;
    public const uint AbnPosChanged = 0x00000001;
    public const uint AbnFullScreenApp = 0x00000002;
    public const uint AbnWindowArrange = 0x00000003;

    private static int _liveRegistrations;

    private readonly ILogger _log;
    private readonly HWND _hwnd;
    private readonly nint _id;
    private bool _registered;

    public AppBarHost(nint hwnd, ILogger log)
    {
        _hwnd = new HWND(hwnd);
        _id = hwnd;
        _log = log;

        // Per-window so two docks on two monitors never confuse each other's
        // notifications.
        CallbackMessage = PInvoke.RegisterWindowMessage($"MCD_ABM_{hwnd}");
        if (CallbackMessage == 0)
        {
            throw new InvalidOperationException("RegisterWindowMessage failed for the AppBar callback");
        }
    }

    /// <summary>The private message the shell sends this window; ABN_* arrives in wParam.</summary>
    public uint CallbackMessage { get; }

    /// <summary>How many AppBarHost instances currently hold a registration, across the process.</summary>
    public static int LiveRegistrations => Volatile.Read(ref _liveRegistrations);

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        var data = NewData();
        if (PInvoke.SHAppBarMessage(AbmNew, ref data) == 0)
        {
            throw new InvalidOperationException("SHAppBarMessage(ABM_NEW) was refused by the shell");
        }

        _registered = true;
        Interlocked.Increment(ref _liveRegistrations);
        _log.LogInformation("appbar.registered hwnd={Hwnd} live={Live}", _id, LiveRegistrations);
    }

    /// <summary>
    /// Asks the shell where a bar of this thickness fits on this edge, claims that
    /// space, and moves the window into it. All values are physical pixels.
    /// </summary>
    public void SetPosition(AppBarEdge edge, RECT monitor, int thickness)
    {
        if (!_registered)
        {
            throw new InvalidOperationException("SetPosition before Register");
        }

        var data = NewData();
        data.uEdge = (uint)edge;
        data.rc = Occupy(edge, monitor, thickness);

        // The shell rewrites rc to avoid the taskbar and any other bar already
        // holding this edge; the thickness has to be re-applied to its answer.
        PInvoke.SHAppBarMessage(AbmQueryPos, ref data);
        data.rc = Occupy(edge, data.rc, thickness);

        PInvoke.SHAppBarMessage(AbmSetPos, ref data);

        RECT rc = data.rc;
        PInvoke.MoveWindow(_hwnd, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, bRepaint: true);

        _log.LogInformation(
            "appbar.positioned edge={Edge} rect={Left},{Top},{Right},{Bottom}",
            edge, rc.left, rc.top, rc.right, rc.bottom);
    }

    /// <summary>
    /// Claims this edge of this monitor as the auto-hiding bar, or gives it up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An auto-hiding bar reserves no work area - that is the whole point of it -
    /// so this replaces the query-and-set dance rather than joining it. The
    /// window's position is then ours to decide.
    /// </para>
    /// <para>
    /// The shell allows one auto-hiding bar per edge per monitor and refuses the
    /// second. The refusal is the return value and nothing else: no error is
    /// raised and the bar simply never appears, which is why the caller has to
    /// act on it.
    /// </para>
    /// </remarks>
    public bool SetAutoHide(AppBarEdge edge, RECT monitor, bool claim)
    {
        if (!_registered)
        {
            throw new InvalidOperationException("SetAutoHide before Register");
        }

        var data = NewData();
        data.uEdge = (uint)edge;
        data.rc = monitor;
        data.lParam = new LPARAM(claim ? 1 : 0);

        bool ok = PInvoke.SHAppBarMessage(AbmSetAutoHideBarEx, ref data) != 0;

        _log.LogInformation(
            "appbar.autohide hwnd={Hwnd} edge={Edge} claim={Claim} accepted={Accepted}",
            _id, edge, claim, ok);

        return ok;
    }

    /// <summary>
    /// The window already holding this edge of this monitor as its hiding bar,
    /// or zero when the edge is free.
    /// </summary>
    /// <remarks>
    /// Only worth asking after a refusal, and only to say who. A refusal with no
    /// name attached is the kind of thing that gets diagnosed twice.
    /// </remarks>
    public static nint AutoHideOwner(AppBarEdge edge, RECT monitor)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            uEdge = (uint)edge,
            rc = monitor,
        };

        return (nint)PInvoke.SHAppBarMessage(AbmGetAutoHideBarEx, ref data);
    }

    /// <summary>
    /// True when the taskbar itself auto-hides on this edge. Two auto-hiding bars
    /// on one edge is a state the shell does not support: the second registration
    /// silently fails and the dock becomes unreachable.
    /// </summary>
    public static bool TaskbarAutoHidesOn(AppBarEdge edge)
    {
        var probe = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        uint state = (uint)PInvoke.SHAppBarMessage(AbmGetState, ref probe);
        if ((state & AbsAutoHide) == 0)
        {
            return false;
        }

        var taskbar = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return PInvoke.SHAppBarMessage(AbmGetTaskbarPos, ref taskbar) != 0
            && taskbar.uEdge == (uint)edge;
    }

    public void Remove()
    {
        if (!_registered)
        {
            return;
        }

        var data = NewData();
        PInvoke.SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
        Interlocked.Decrement(ref _liveRegistrations);
        _log.LogInformation("appbar.removed hwnd={Hwnd} live={Live}", _id, LiveRegistrations);
    }

    public void Dispose()
    {
        Remove();
        Debug.Assert(!_registered, "AppBarHost disposed while still registered");
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
        uCallbackMessage = CallbackMessage,
    };

    private static RECT Occupy(AppBarEdge edge, RECT area, int thickness) => edge switch
    {
        AppBarEdge.Top => area with { bottom = area.top + thickness },
        AppBarEdge.Bottom => area with { top = area.bottom - thickness },
        AppBarEdge.Left => area with { right = area.left + thickness },
        AppBarEdge.Right => area with { left = area.right - thickness },
        _ => area,
    };
}

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Interop.Windowing;

/// <summary>
/// Turns an ordinary top-level window into something that can sit flush against
/// the edge of a screen.
/// </summary>
/// <remarks>
/// Adapted from microsoft/PowerToys,
/// src/modules/cmdpal/Microsoft.CmdPal.UI/Dock/DockWindow.xaml.cs (MIT).
/// Copyright (c) Microsoft Corporation. See licenses/PowerToys-MIT.txt.
/// Changes: extracted to a static helper over a raw HWND, no WinUIEx dependency.
/// </remarks>
public static unsafe class WindowFrame
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    /// <summary>Windows 11 draws the border in the accent colour; this value means "none".</summary>
    private const uint BorderColorNone = 0xFFFFFFFE;

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    /// <summary>
    /// Strips the caption, the resize frame, the rounded corners and the accent
    /// border, and keeps the window out of the taskbar and Alt+Tab.
    /// </summary>
    public static void MakeChromeless(nint hwnd)
    {
        var handle = new HWND(hwnd);

        nint style = PInvoke.GetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlStyle);
        PInvoke.SetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlStyle, style & ~(nint)WsOverlappedWindow);

        nint exStyle = PInvoke.GetWindowLongPtr(handle, (WINDOW_LONG_PTR_INDEX)GwlExStyle);
        PInvoke.SetWindowLongPtr(
            handle,
            (WINDOW_LONG_PTR_INDEX)GwlExStyle,
            exStyle | (nint)WsExToolWindow | (nint)WsExNoActivate);

        // A rounded corner against the screen edge leaves a wedge of desktop
        // showing through, which reads as a rendering fault rather than a style.
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(
            handle,
            DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
            &corner,
            (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));

        uint borderColor = BorderColorNone;
        PInvoke.DwmSetWindowAttribute(
            handle,
            DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
            &borderColor,
            sizeof(uint));
    }

    public static void SetTopmost(nint hwnd, bool topmost)
    {
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            topmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Steps aside for a full-screen application without giving up the AppBar slot.</summary>
    public static void SendToBottom(nint hwnd)
    {
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            HWND.HWND_BOTTOM,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Physical pixels per DIP for the monitor this window is currently on.</summary>
    public static double GetScale(nint hwnd) => PInvoke.GetDpiForWindow(new HWND(hwnd)) / 96.0;

    /// <summary>
    /// Puts the window in front of everything else and gives it the keyboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows refuses to hand the foreground to a process that does not already
    /// have it. Ours never does: the docks carry WS_EX_NOACTIVATE, so clicking
    /// one deliberately does not activate anything - which is right for a bar,
    /// and leaves the settings window it opens appearing behind whatever the
    /// person was reading.
    /// </para>
    /// <para>
    /// Joining the foreground window's input queue for the length of the call is
    /// the documented way round it. The alternative, flashing the window topmost
    /// and back, raises it without focus, so the first keystroke goes to the
    /// wrong place.
    /// </para>
    /// </remarks>
    public static void BringToFront(nint hwnd)
    {
        var handle = new HWND(hwnd);

        if (PInvoke.IsIconic(handle))
        {
            PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        HWND front = PInvoke.GetForegroundWindow();

        if (front == handle)
        {
            return;
        }

        uint theirs = PInvoke.GetWindowThreadProcessId(front, null);
        uint ours = PInvoke.GetCurrentThreadId();
        bool joined = theirs != 0 && theirs != ours && PInvoke.AttachThreadInput(ours, theirs, true);

        try
        {
            PInvoke.BringWindowToTop(handle);
            PInvoke.SetForegroundWindow(handle);
        }
        finally
        {
            if (joined)
            {
                PInvoke.AttachThreadInput(ours, theirs, false);
            }
        }
    }

    /// <summary>Where the window is now, in physical pixels.</summary>
    public static RECT RectOf(nint hwnd)
    {
        PInvoke.GetWindowRect(new HWND(hwnd), out RECT rc);
        return rc;
    }

    /// <summary>Where the pointer is, in physical pixels across the whole desktop.</summary>
    public static (int X, int Y) CursorAt()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point at);
        return (at.X, at.Y);
    }

    /// <summary>Puts the window exactly here, in physical pixels.</summary>
    /// <remarks>
    /// For the auto-hiding bar, which decides its own position rather than
    /// asking the shell for one. Every frame of the slide comes through here.
    /// </remarks>
    public static void MoveTo(nint hwnd, RECT rc) =>
        PInvoke.MoveWindow(
            new HWND(hwnd), rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, bRepaint: true);
}

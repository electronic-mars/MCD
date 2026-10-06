using System.Runtime.InteropServices;

namespace Mcd.Interop.Windowing;

/// <summary>
/// An icon in the notification area: a press opens the settings, a right press
/// opens a small menu.
/// </summary>
/// <remarks>
/// <para>
/// Off unless asked for. The program has no icon there by default, on purpose -
/// a bar of visible controls should not hide its own behind a chevron - but
/// somebody who wants the usual place to look for it can have it.
/// </para>
/// <para>
/// Plain calls into the shell rather than a framework class: the icon needs one
/// hidden window to receive its messages, and that is the whole of it. The
/// window is an ordinary hidden one, not a message-only one: only ordinary
/// windows are told when Explorer restarts and the icon has to be put back.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    private readonly WndProcDelegate _thunk;
    private readonly string _className = "McdTray" + Guid.NewGuid().ToString("N");
    private readonly Action _open;
    private readonly Func<IReadOnlyList<(string Text, Action Do)>> _menu;
    private readonly uint _created;
    private nint _hwnd;
    private nint _icon;
    private bool _shown;
    private string _tip = string.Empty;

    public TrayIcon(Action open, Func<IReadOnlyList<(string Text, Action Do)>> menu)
    {
        _open = open;
        _menu = menu;
        _thunk = Proc;
        _created = RegisterWindowMessageW("TaskbarCreated");

        var klass = new WndClass
        {
            cbSize = (uint)Marshal.SizeOf<WndClass>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_thunk),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className,
        };

        RegisterClassExW(ref klass);
        _hwnd = CreateWindowExW(0x80, _className, "Master Control Dock tray", 0x80000000, 0, 0, 0, 0, 0, 0, klass.hInstance, 0);
    }

    /// <summary>Puts the icon up, or changes its picture and tip.</summary>
    /// <param name="iconFile">An .ico file; the size the shell wants is picked from it.</param>
    public void Show(string iconFile, string tip)
    {
        int side = GetSystemMetrics(49); // SM_CXSMICON

        nint fresh = LoadImageW(0, iconFile, ImageIcon, side, side, LrLoadFromFile);

        if (_icon != 0)
        {
            DestroyIcon(_icon);
        }

        _icon = fresh;
        _tip = tip.Length > 120 ? tip[..120] : tip;
        Notify(_shown ? NimModify : NimAdd);
        _shown = true;
    }

    /// <summary>Takes the icon away.</summary>
    public void Hide()
    {
        if (_shown)
        {
            Notify(NimDelete);
            _shown = false;
        }
    }

    private void Notify(uint what)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray,
            hIcon = _icon,
            szTip = _tip,
        };

        Shell_NotifyIconW(what, ref data);
    }

    private nint Proc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WmTray)
        {
            uint mouse = (uint)(lParam & 0xFFFF);

            if (mouse == WmLButtonUp)
            {
                _open();
            }
            else if (mouse == WmRButtonUp)
            {
                ShowMenu();
            }

            return 0;
        }

        // Explorer restarted: its notification area is empty again.
        if (_created != 0 && msg == _created && _shown)
        {
            Notify(NimAdd);
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IReadOnlyList<(string Text, Action Do)> items = _menu();
        nint menu = CreatePopupMenu();

        for (int i = 0; i < items.Count; i++)
        {
            AppendMenuW(menu, 0, (nuint)(i + 1), items[i].Text);
        }

        GetCursorPos(out Point at);

        // The window must be in front for the menu to close when it is clicked
        // away from - the documented dance for a menu from a tray icon.
        SetForegroundWindow(_hwnd);
        uint chosen = TrackPopupMenu(menu, 0x0100 | 0x0080, at.X, at.Y, 0, _hwnd, 0); // RETURNCMD | NONOTIFY
        PostMessageW(_hwnd, 0, 0, 0);
        DestroyMenu(menu);

        if (chosen is > 0 && chosen <= items.Count)
        {
            items[(int)chosen - 1].Do();
        }
    }

    public void Dispose()
    {
        Hide();

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClass klass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}

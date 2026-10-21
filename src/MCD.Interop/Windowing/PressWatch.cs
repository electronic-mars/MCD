using System.Runtime.InteropServices;

namespace Mcd.Interop.Windowing;

/// <summary>
/// Hears every mouse press on the desktop, for as long as it is kept.
/// </summary>
/// <remarks>
/// <para>
/// For a panel that has to close when somebody clicks anywhere else. A click
/// on another program takes the activation away and the panel hears of that;
/// a click on the empty desktop takes nothing from anybody, and asking the
/// buttons a few times a second missed most clicks, which are shorter than the
/// gap between two questions.
/// </para>
/// <para>
/// A low-level hook, on a thread of its own that does nothing else. Windows
/// waits for the hook on every movement of the mouse, on the whole machine:
/// set on the window's thread, it waited for the window - and while a
/// brightness slider kept that thread busy, the pointer stuttered everywhere.
/// The hook only passes the point on; whoever is told decides elsewhere.
/// </para>
/// </remarks>
public sealed class PressWatch : IDisposable
{
    private const int MouseLowLevel = 14;
    private const uint Quit = 0x0012;

    private delegate nint HookProc(int code, nint message, nint data);

    /// <summary>Held, so the collector does not take what Windows will call.</summary>
    private readonly HookProc _proc;
    private readonly Thread _thread;
    private uint _threadId;

    /// <param name="pressed">
    /// Told where a button went down, in physical pixels across the desktop -
    /// on the hook's own thread, and it must return at once.
    /// </param>
    public PressWatch(Action<int, int> pressed)
    {
        _proc = (code, message, data) =>
        {
            // Left, right, middle and the side buttons going down.
            if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                pressed(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
            }

            return CallNextHookEx(0, code, message, data);
        };

        using var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            nint hook = SetWindowsHookExW(MouseLowLevel, _proc, GetModuleHandleW(null), 0);
            ready.Set();

            // The hook is called from this loop.
            while (GetMessageW(out Msg msg, 0, 0, 0) > 0)
            {
            }

            if (hook != 0)
            {
                UnhookWindowsHookEx(hook);
            }
        })
        {
            IsBackground = true,
            Name = "press watch",
        };

        _thread.Start();
        ready.Wait();
    }

    public void Dispose() => PostThreadMessageW(_threadId, Quit, 0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(int kind, HookProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}

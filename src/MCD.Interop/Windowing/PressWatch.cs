using System.Runtime.InteropServices;

namespace Mcd.Interop.Windowing;

/// <summary>
/// Hears every mouse press on the desktop while it is switched on.
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
/// The thread is made once and kept; the hook is set only while switched on.
/// </para>
/// </remarks>
public static class PressWatch
{
    private const int MouseLowLevel = 14;
    private const uint HookOn = 0x8001;
    private const uint HookOff = 0x8002;

    private delegate nint HookProc(int code, nint message, nint data);

    private static readonly Lock Gate = new();

    /// <summary>Held, so the collector does not take what Windows will call.</summary>
    private static readonly HookProc Proc = Called;

    private static uint _thread;
    private static Action<int, int>? _pressed;

    /// <summary>
    /// Starts telling <paramref name="pressed"/> where a button went down, in
    /// physical pixels across the desktop - on the hook's own thread, and it
    /// must return at once. Replaces whoever was told before.
    /// </summary>
    public static void On(Action<int, int> pressed)
    {
        Volatile.Write(ref _pressed, pressed);
        Post(HookOn);
    }

    /// <summary>Stops listening. The hook comes off; the thread waits for the next time.</summary>
    public static void Off()
    {
        Volatile.Write(ref _pressed, null);
        Post(HookOff);
    }

    private static void Post(uint message)
    {
        lock (Gate)
        {
            if (_thread == 0)
            {
                using var ready = new ManualResetEventSlim();

                var thread = new Thread(() => Run(ready))
                {
                    IsBackground = true,
                    Name = "press watch",
                };

                thread.Start();
                ready.Wait();
            }

            PostThreadMessageW(_thread, message, 0, 0);
        }
    }

    private static void Run(ManualResetEventSlim ready)
    {
        _thread = GetCurrentThreadId();

        // The queue is made by the first call that looks at it. Until then a
        // message posted to this thread is refused, and the first "on" would
        // be lost.
        PeekMessageW(out _, 0, 0, 0, 0);
        ready.Set();

        nint hook = 0;

        while (GetMessageW(out Msg msg, 0, 0, 0) > 0)
        {
            if (msg.Message == HookOn && hook == 0)
            {
                hook = SetWindowsHookExW(MouseLowLevel, Proc, GetModuleHandleW(null), 0);
            }
            else if (msg.Message == HookOff && hook != 0)
            {
                UnhookWindowsHookEx(hook);
                hook = 0;
            }
        }
    }

    private static nint Called(int code, nint message, nint data)
    {
        // Left, right, middle and the side buttons going down.
        if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B
            && Volatile.Read(ref _pressed) is { } pressed)
        {
            pressed(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
        }

        return CallNextHookEx(0, code, message, data);
    }

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
    private static extern bool PeekMessageW(out Msg msg, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}

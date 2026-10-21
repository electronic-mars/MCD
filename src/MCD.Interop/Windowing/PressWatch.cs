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
/// A low-level hook, called on the thread that set it - which must have a
/// message loop, as a window's thread has. Kept only while a panel is open:
/// every press on the machine passes through it.
/// </para>
/// </remarks>
public sealed class PressWatch : IDisposable
{
    private const int MouseLowLevel = 14;

    private delegate nint HookProc(int code, nint message, nint data);

    /// <summary>Held, so the collector does not take what Windows will call.</summary>
    private readonly HookProc _proc;
    private nint _hook;

    /// <param name="pressed">Told where a button went down, in physical pixels across the desktop.</param>
    public PressWatch(Action<int, int> pressed)
    {
        _proc = (code, message, data) =>
        {
            // Left, right, middle and the side buttons going down.
            if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                int x = Marshal.ReadInt32(data);
                int y = Marshal.ReadInt32(data, 4);
                pressed(x, y);
            }

            return CallNextHookEx(0, code, message, data);
        };

        _hook = SetWindowsHookExW(MouseLowLevel, _proc, GetModuleHandleW(null), 0);
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(int kind, HookProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}

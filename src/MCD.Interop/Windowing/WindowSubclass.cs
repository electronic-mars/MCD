using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Interop.Windowing;

/// <summary>Arguments of one intercepted window message.</summary>
public sealed class WindowMessageEventArgs(uint message, nuint wParam, nint lParam)
{
    public uint Message { get; } = message;

    public nuint WParam { get; } = wParam;

    public nint LParam { get; } = lParam;

    /// <summary>Set to stop the message reaching the original procedure.</summary>
    public bool Handled { get; set; }

    public nint Result { get; set; }
}

/// <summary>
/// Puts our own window procedure in front of an existing HWND.
/// </summary>
/// <remarks>
/// The delegate is held in a field, not passed inline. A window procedure handed
/// to Windows as a function pointer has no managed reference keeping it alive:
/// the collector frees it, Windows calls into freed memory, and the process dies
/// with an access violation at a random later moment.
/// </remarks>
public sealed unsafe class WindowSubclass : IDisposable
{
    private const int GwlpWndProc = -4;

    private readonly HWND _hwnd;
    private readonly WndProcDelegate _thunk;
    private readonly nint _original;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(HWND hwnd, uint msg, nuint wParam, nint lParam);

    public WindowSubclass(nint hwnd)
    {
        _hwnd = new HWND(hwnd);
        _thunk = Dispatch;

        nint thunkPointer = Marshal.GetFunctionPointerForDelegate(_thunk);
        _original = PInvoke.SetWindowLongPtr(_hwnd, (WINDOW_LONG_PTR_INDEX)GwlpWndProc, thunkPointer);

        if (_original == 0)
        {
            throw new InvalidOperationException(
                $"SetWindowLongPtr(GWLP_WNDPROC) failed with {Marshal.GetLastWin32Error()}");
        }
    }

    public event EventHandler<WindowMessageEventArgs>? MessageReceived;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PInvoke.SetWindowLongPtr(_hwnd, (WINDOW_LONG_PTR_INDEX)GwlpWndProc, _original);
        GC.KeepAlive(_thunk);
    }

    private nint Dispatch(HWND hwnd, uint msg, nuint wParam, nint lParam)
    {
        var args = new WindowMessageEventArgs(msg, wParam, lParam);

        try
        {
            MessageReceived?.Invoke(this, args);
        }
        catch (Exception)
        {
            // An exception thrown across the native boundary tears the process
            // down with no usable stack. Handlers log their own failures.
        }

        return args.Handled
            ? args.Result
            : PInvoke.CallWindowProc(
                (delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)_original,
                hwnd,
                msg,
                new WPARAM(wParam),
                new LPARAM(lParam));
    }
}

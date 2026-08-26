using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Interop.Windowing;

/// <summary>
/// An invisible window whose only job is to hear system broadcasts, on a thread
/// of its own, for as long as the process lives.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a top-level window and <b>not</b> an <c>HWND_MESSAGE</c> one.
/// Message-only windows are cheaper and are the obvious choice for a listener -
/// and they never receive <c>WM_DISPLAYCHANGE</c>, <c>WM_SETTINGCHANGE</c> or
/// <c>TaskbarCreated</c>, because those are broadcast to top-level windows only.
/// A message-only listener would sit there silently seeing nothing.
/// </para>
/// <para>
/// It exists apart from the dock windows so that display changes are still
/// noticed when every dock is switched off. In PowerToys the dock window itself
/// is the listener, so with no dock alive their monitor list goes stale - one of
/// the causes named in their fix for #49604.
/// </para>
/// </remarks>
public sealed unsafe class BroadcastListener : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;

    private readonly string _className = $"MCD_Broadcast_{Environment.ProcessId}_{Guid.NewGuid():N}";
    private readonly WndProcDelegate _thunk;
    private readonly Thread _pump;
    private readonly ManualResetEventSlim _ready = new(false);

    private HWND _hwnd;
    private ushort _atom;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(HWND hwnd, uint msg, nuint wParam, nint lParam);

    public BroadcastListener()
    {
        // The delegate lives in a field for as long as the window does. Handing
        // Windows a function pointer creates no managed reference: collect it and
        // the next broadcast calls into freed memory.
        _thunk = Dispatch;

        _pump = new Thread(Run)
        {
            Name = "mcd-broadcast",
            IsBackground = true,
        };

        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Raised on the listener's own thread, never on the UI thread.</summary>
    public event EventHandler<WindowMessageEventArgs>? MessageReceived;

    public nint Handle => _hwnd;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_hwnd.IsNull)
        {
            PInvoke.PostMessage(_hwnd, WmClose, default, default);
        }

        _pump.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
        GC.KeepAlive(_thunk);
    }

    private void Run()
    {
        HMODULE handle = PInvoke.GetModuleHandle((PCWSTR)null);

        // ownsHandle: false. GetModuleHandle adds no reference, so letting the
        // wrapper free it would unload a module we never loaded.
        var module = new FreeLibrarySafeHandle(handle, ownsHandle: false);
        var instance = (HINSTANCE)handle.Value;

        fixed (char* className = _className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)
                    Marshal.GetFunctionPointerForDelegate(_thunk),
                hInstance = instance,
                lpszClassName = className,
            };

            _atom = PInvoke.RegisterClassEx(&wc);
            if (_atom == 0)
            {
                _ready.Set();
                return;
            }

            _hwnd = PInvoke.CreateWindowEx(
                (WINDOW_EX_STYLE)WsExToolWindow,
                _className,
                "MCD broadcast listener",
                (WINDOW_STYLE)WsPopup,
                0, 0, 0, 0,
                HWND.Null,
                null,
                module,
                null);
        }

        _ready.Set();

        if (_hwnd.IsNull)
        {
            return;
        }

        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }

        PInvoke.UnregisterClass(_className, module);
    }

    private nint Dispatch(HWND hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WmDestroy)
        {
            PInvoke.PostQuitMessage(0);
            return 0;
        }

        var args = new WindowMessageEventArgs(msg, wParam, lParam);

        try
        {
            MessageReceived?.Invoke(this, args);
        }
        catch (Exception)
        {
            // An exception crossing back into native code kills the process with
            // no usable stack. Handlers report their own failures.
        }

        return args.Handled
            ? args.Result
            : PInvoke.DefWindowProc(hwnd, msg, new WPARAM(wParam), new LPARAM(lParam));
    }
}

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Interop.Windowing;

/// <summary>One combination somebody typed, as it is stored and shown.</summary>
/// <param name="Key">The virtual-key code of the key itself.</param>
public readonly record struct Chord(bool Control, bool Alt, bool Shift, bool Win, uint Key)
{
    /// <summary>Nothing set: the combination that has not been chosen.</summary>
    public static Chord None => default;

    public bool Set => Key != 0;

    /// <summary>
    /// Whether this is a combination the system will accept.
    /// </summary>
    /// <remarks>
    /// A bare letter would take that letter away from every other program on
    /// the machine, which is not a thing to let somebody do by accident.
    /// </remarks>
    public bool Sane => Set && (Control || Alt || Win);

    /// <summary>How it is written down, and how it reads back.</summary>
    public override string ToString()
    {
        if (!Set)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);

        if (Control) { parts.Add("Ctrl"); }
        if (Alt) { parts.Add("Alt"); }
        if (Shift) { parts.Add("Shift"); }
        if (Win) { parts.Add("Win"); }

        parts.Add(Name(Key));
        return string.Join("+", parts);
    }

    /// <summary>Reads back what <see cref="ToString"/> wrote.</summary>
    public static Chord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var chord = None;

        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": chord = chord with { Control = true }; break;
                case "ALT": chord = chord with { Alt = true }; break;
                case "SHIFT": chord = chord with { Shift = true }; break;
                case "WIN": chord = chord with { Win = true }; break;
                default: chord = chord with { Key = Code(part) }; break;
            }
        }

        return chord;
    }

    /// <summary>
    /// What a key is called.
    /// </summary>
    /// <remarks>
    /// Only the keys somebody would sensibly put in a shortcut are named.
    /// Anything else is written as its number, which reads back exactly and
    /// is honest about being a number rather than pretending to a name.
    /// </remarks>
    private static string Name(uint key) => key switch
    {
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        0x20 => "Space",
        0x0D => "Enter",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        _ => "#" + key,
    };

    private static uint Code(string name)
    {
        if (name.StartsWith('#') && uint.TryParse(name[1..], out uint raw))
        {
            return raw;
        }

        if (name.Length == 1)
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length > 1 && name[0] is 'F' or 'f' && uint.TryParse(name[1..], out uint number))
        {
            return 0x6F + number;
        }

        return name.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => 0,
        };
    }
}

/// <summary>
/// Combinations the whole machine listens for.
/// </summary>
/// <remarks>
/// <para>
/// Registered against a message-only window of the program's own rather than
/// against a dock: a dock comes and goes with its monitor, and a shortcut that
/// stops working because somebody unplugged a screen is a shortcut nobody
/// trusts.
/// </para>
/// <para>
/// A combination another program already holds is refused by the system, and
/// that refusal is the answer - it is written down and shown beside the
/// setting, rather than swallowed so that a key somebody chose does nothing
/// and never says why.
/// </para>
/// </remarks>
public sealed unsafe class HotKeys : IDisposable
{
    private const uint WmHotKey = 0x0312;
    private const uint WmClose = 0x0010;
    private const uint WmApp = 0x8000;

    private readonly ILogger _log;
    private readonly string _className = $"MCD_HotKeys_{Environment.ProcessId}_{Guid.NewGuid():N}";
    private readonly WndProcDelegate _thunk;
    private readonly Thread _pump;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Dictionary<int, (Chord Chord, Action Do)> _held = [];
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private HWND _hwnd;
    private ushort _atom;
    private List<(Chord Chord, Action Do)> _wanted = [];
    private int _next = 1;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(HWND hwnd, uint msg, nuint wParam, nint lParam);

    public HotKeys(ILogger log)
    {
        _log = log;

        // The delegate lives in a field for as long as the window does.
        // Handing Windows a function pointer creates no managed reference.
        _thunk = Dispatch;

        _pump = new Thread(Run)
        {
            Name = "mcd-hotkeys",
            IsBackground = true,
        };

        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>The combinations the system would not give us.</summary>
    public IReadOnlySet<string> Refused => _refused;

    /// <summary>
    /// Takes a set of combinations, dropping whatever was held before.
    /// </summary>
    /// <remarks>
    /// Wholesale rather than one at a time: the settings are rewritten as a
    /// whole and working out the difference would be more code than doing it
    /// again, for a handful of keys.
    /// </remarks>
    public void Listen(IReadOnlyList<(Chord Chord, Action Do)> wanted)
    {
        lock (_gate)
        {
            _wanted = [.. wanted];
        }

        // Registered on the thread that owns the window, which is the only
        // thread allowed to: the call is asked for from wherever the settings
        // were changed and carried out over there.
        PInvoke.PostMessage(_hwnd, WmApp, default, default);
    }

    /// <summary>Takes the keys, on the thread that owns the window.</summary>
    private void Take()
    {
        Release();

        List<(Chord Chord, Action Do)> wanted;

        lock (_gate)
        {
            wanted = _wanted;
        }

        foreach ((Chord chord, Action act) in wanted)
        {
            if (!chord.Sane)
            {
                continue;
            }

            int id = _next++;

            if (PInvoke.RegisterHotKey(_hwnd, id, Modifiers(chord), chord.Key))
            {
                _held[id] = (chord, act);
                continue;
            }

            // Somebody else has it. Recorded rather than retried: there is no
            // second combination to fall back to that the person did not ask
            // for, and a key that quietly does nothing is worse than one that
            // says it is taken.
            _refused.Add(chord.ToString());
            _log.LogWarning("hotkey.refused {Chord}", chord);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != default)
        {
            PInvoke.PostMessage(_hwnd, WmClose, default, default);
        }

        _pump.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run()
    {
        HMODULE handle = PInvoke.GetModuleHandle((PCWSTR)null);

        // ownsHandle: false, for the same reason the broadcast listener says:
        // GetModuleHandle adds no reference, and letting the wrapper free it
        // would unload a module we never loaded.
        var module = new FreeLibrarySafeHandle(handle, ownsHandle: false);
        var instance = (HINSTANCE)handle.Value;

        fixed (char* name = _className)
        {
            var klass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)
                    Marshal.GetFunctionPointerForDelegate(_thunk),
                hInstance = instance,
                lpszClassName = name,
            };

            _atom = PInvoke.RegisterClassEx(klass);
        }

        // Message-only. Unlike the broadcast listener, nothing here depends on
        // seeing what the system sends to top-level windows: a hot key is
        // posted to the window that asked for it, wherever it lives.
        _hwnd = PInvoke.CreateWindowEx(
            0, _className, _className, 0, 0, 0, 0, 0,
            HWND.HWND_MESSAGE, null, module, null);

        _ready.Set();

        while (PInvoke.GetMessage(out MSG message, default, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }

        Release();

        if (_atom != 0)
        {
            PInvoke.UnregisterClass(_className, module);
        }
    }

    private nint Dispatch(HWND hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmApp:
                Take();
                return 0;

            case WmHotKey:
                Fire((int)wParam);
                return 0;

            case WmClose:
                PInvoke.DestroyWindow(hwnd);
                PInvoke.PostQuitMessage(0);
                return 0;

            default:
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void Release()
    {
        foreach (int id in _held.Keys)
        {
            PInvoke.UnregisterHotKey(_hwnd, id);
        }

        _held.Clear();
        _refused.Clear();
    }

    private static HOT_KEY_MODIFIERS Modifiers(Chord chord)
    {
        // Not repeated while the key is held down: a shortcut that shows and
        // hides a bar forty times a second because somebody rested a finger
        // on it is not a shortcut.
        HOT_KEY_MODIFIERS mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;

        if (chord.Control) { mods |= HOT_KEY_MODIFIERS.MOD_CONTROL; }
        if (chord.Alt) { mods |= HOT_KEY_MODIFIERS.MOD_ALT; }
        if (chord.Shift) { mods |= HOT_KEY_MODIFIERS.MOD_SHIFT; }
        if (chord.Win) { mods |= HOT_KEY_MODIFIERS.MOD_WIN; }

        return mods;
    }

    private void Fire(int id)
    {
        if (!_held.TryGetValue(id, out (Chord Chord, Action Do) held))
        {
            return;
        }

        try
        {
            held.Do();
        }
        catch (Exception e)
        {
            // A shortcut must not take the program with it.
            _log.LogError(e, "hotkey.failed {Chord}", held.Chord);
        }
    }
}

using System.Globalization;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Mcd.Interop.Machine;

/// <summary>What the keyboard is typing in.</summary>
/// <param name="Code">The language in two letters, "EN" or "RU".</param>
/// <param name="Name">The language as a person names it, for the tooltip.</param>
public readonly record struct KeyboardLayout(string Code, string Name);

/// <summary>
/// The keyboard layout, as the window in front has it, and whether Caps Lock is down.
/// </summary>
/// <remarks>
/// A layout belongs to a window's thread rather than to the machine, which is
/// why the taskbar's own indicator changes as the focus moves between
/// programs. So this asks the window in front, not the bar: the bar is never
/// where anybody types.
/// </remarks>
public static class KeyboardLayouts
{
    /// <summary>The layout of the window in front.</summary>
    public static unsafe KeyboardLayout Current()
    {
        Windows.Win32.Foundation.HWND front = PInvoke.GetForegroundWindow();
        uint thread = front.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(front, out _);

        return Of((ushort)((nint)PInvoke.GetKeyboardLayout(thread).Value & 0xFFFF));
    }

    /// <summary>The layout a language id stands for.</summary>
    public static KeyboardLayout Of(ushort language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);

            return new KeyboardLayout(
                culture.TwoLetterISOLanguageName.ToUpperInvariant(), culture.DisplayName);
        }
        catch (ArgumentException)
        {
            // A layout made for a language the machine has no name for - or
            // none at all: a window caught between layouts answers 0, which
            // the framework refuses with a different exception from the one
            // it uses for an unknown language, and that one took the widget's
            // tick down.
            return new KeyboardLayout("??", "0x" + language.ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Whether Caps Lock is on.</summary>
    public static bool CapsLock() => (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CAPITAL) & 1) != 0;

    /// <summary>How many layouts are installed. With one, there is nothing to switch to.</summary>
    public static unsafe int Installed() => PInvoke.GetKeyboardLayoutList(0, null);

    /// <summary>
    /// Asks the window in front to move to the next layout.
    /// </summary>
    /// <remarks>
    /// The request every layout switcher posts. Nothing is simulated: a
    /// pressed Alt+Shift could arrive in the middle of somebody's typing and
    /// mean something else to the program it lands in.
    /// </remarks>
    public static void Next()
    {
        Windows.Win32.Foundation.HWND front = PInvoke.GetForegroundWindow();

        if (!front.IsNull)
        {
            PInvoke.PostMessage(front, PInvoke.WM_INPUTLANGCHANGEREQUEST, ForwardInput, HklNext);
        }
    }

    /// <summary>INPUTLANGCHANGE_FORWARD: go to the next layout in the list.</summary>
    private const int ForwardInput = 2;

    /// <summary>HKL_NEXT.</summary>
    private const int HklNext = 1;
}

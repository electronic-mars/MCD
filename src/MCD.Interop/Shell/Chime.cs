using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mcd.Interop.Shell;

/// <summary>
/// The short sound that says something happened.
/// </summary>
/// <remarks>
/// Through the shell's own beep rather than a file of our own: it plays
/// whatever the person has that event set to in their sound scheme, and it
/// plays nothing at all when they have turned system sounds off. A program
/// that ships its own wav ignores both of those.
/// </remarks>
public static class Chime
{
    /// <summary>Something was taken away.</summary>
    public static void Removed() => Beep(MESSAGEBOX_STYLE.MB_ICONASTERISK);

    /// <summary>Something would not go where it was put.</summary>
    public static void Refused() => Beep(MESSAGEBOX_STYLE.MB_ICONEXCLAMATION);

    private static void Beep(MESSAGEBOX_STYLE sound)
    {
        try
        {
            PInvoke.MessageBeep(sound);
        }
        catch (Exception)
        {
            // A sound nobody hears is not worth a crash.
        }
    }
}

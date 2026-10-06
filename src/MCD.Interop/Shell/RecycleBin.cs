using System.Runtime.InteropServices;

namespace Mcd.Interop.Shell;

/// <summary>The Recycle Bin's one verb that Explorer's folder view does not put on a bar.</summary>
public static class RecycleBin
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(nint hwnd, string? root, uint flags);

    /// <summary>
    /// Empties the bin of every drive, with the shell's own confirmation and
    /// progress - emptying the bin is the one thing here nobody can take back,
    /// so the question is asked by the system rather than skipped.
    /// </summary>
    public static void Empty() => SHEmptyRecycleBinW(0, null, 0);
}

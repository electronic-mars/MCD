using System.Runtime.InteropServices;
using System.Text;

namespace Mcd.Interop.Shell;

/// <summary>
/// What a .lnk shortcut points at.
/// </summary>
/// <remarks>
/// Pinning the shortcut itself has two costs: the extracted icon carries the
/// little link arrow Windows stamps on shortcuts, and the pinned thing breaks
/// when the shortcut file is tidied away. Resolving to the target at the
/// moment of pinning avoids both. Declared by hand rather than through
/// CsWin32: two small interfaces are less machinery than a generator entry.
/// </remarks>
public static class ShellLinkResolver
{
    /// <summary>The shortcut's target path, or null when it has none to give.</summary>
    /// <remarks>Must be called from an STA thread; the interface refuses others.</remarks>
    public static string? Resolve(string lnkPath)
    {
        if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            Type? type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));

            if (type is null || Activator.CreateInstance(type) is not IShellLinkW link)
            {
                return null;
            }

            ((IPersistFile)link).Load(lnkPath, 0);

            var path = new StringBuilder(260);
            link.GetPath(path, path.Capacity, nint.Zero, 0);

            string target = path.ToString();
            return target.Length > 0 ? target : null;
        }
        catch (Exception)
        {
            // A link to somewhere exotic - a control panel page, an
            // installer-advertised app. The shortcut itself still works.
            return null;
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            nint pfd,
            uint fFlags);

        // Only GetPath is used; the rest fill the vtable so it lines up.
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out nint ppszFileName);
    }
}

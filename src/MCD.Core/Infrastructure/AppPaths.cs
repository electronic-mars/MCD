using System.Runtime.InteropServices;

namespace Mcd.Core.Infrastructure;

/// <summary>Every path the program writes to, decided in one place.</summary>
public static class AppPaths
{
    private const string FolderName = "MCD";

    /// <summary>
    /// APPMODEL_ERROR_NO_PACKAGE. The call succeeds with a different code when the
    /// process does run inside a package, so this is the documented way to ask.
    /// </summary>
    private const int NoPackage = 15700;

    private static readonly Lazy<bool> PackagedLazy = new(DetectPackaged);
    private static readonly Lazy<string> RootLazy = new(ResolveRoot);

    public static bool IsPackaged => PackagedLazy.Value;

    /// <summary>
    /// Inside a package this is LocalState, which Windows removes when the app is
    /// uninstalled - the Store expects that. Outside, it is %LOCALAPPDATA%\MCD,
    /// so a debug session never touches the installed copy's settings.
    /// </summary>
    public static string Root => RootLazy.Value;

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string ConfigBackupFile => Path.Combine(Root, "config.bak");
    public static string LogDirectory => EnsureDirectory(Path.Combine(Root, "logs"));
    public static string IconCacheDirectory => EnsureDirectory(Path.Combine(Root, "iconcache"));

    private static bool DetectPackaged()
    {
        uint length = 0;
        int code = GetCurrentPackageFullName(ref length, null);
        return code != NoPackage;
    }

    private static string ResolveRoot()
    {
        // A seam for the CI smoke check, which needs a profile of its own so that
        // "exactly one settings write on a fresh start" means something.
        // Overriding LOCALAPPDATA does not work: SpecialFolder.LocalApplicationData
        // asks Windows for the known folder and ignores the variable entirely.
        string? forced = Environment.GetEnvironmentVariable("MCD_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            return EnsureDirectory(forced);
        }

        string root = IsPackaged
            ? PackagedLocalState()
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName);

        return EnsureDirectory(root);
    }

    /// <summary>
    /// Built from the environment rather than through Windows.Storage.ApplicationData:
    /// MCD.Core must stay free of WinRT so its tests run in a plain test host.
    /// </summary>
    private static string PackagedLocalState()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Packages", PackageFamilyName(), "LocalState");
    }

    private static string PackageFamilyName()
    {
        uint length = 0;
        _ = GetCurrentPackageFamilyName(ref length, null);
        char[] buffer = new char[length];
        int code = GetCurrentPackageFamilyName(ref length, buffer);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"GetCurrentPackageFamilyName failed with {code} inside a packaged process");
        }

        return new string(buffer, 0, (int)length - 1);
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char[]? packageFamilyName);
}

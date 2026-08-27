using Microsoft.Win32;

namespace Mcd.App;

/// <summary>
/// Whether the program starts when the person signs in.
/// </summary>
/// <remarks>
/// Unpackaged this is the classic Run value under the user's own hive: no
/// administrator rights, no scheduled task, gone the moment it is switched
/// off. The packaged build will use the manifest's StartupTask instead, and
/// this class is where that branch will live.
/// </remarks>
public static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MasterControlDock";

    public static bool Enabled
    {
        get
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Name) is string;
        }

        set
        {
            using RegistryKey run = Registry.CurrentUser.CreateSubKey(Key);

            if (value && Environment.ProcessPath is { } path)
            {
                // Quoted: the path lives under \Program Files-like folders with
                // spaces, and an unquoted Run value is the classic way to start
                // the wrong program.
                run.SetValue(Name, $"\"{path}\"");
            }
            else
            {
                run.DeleteValue(Name, throwOnMissingValue: false);
            }
        }
    }
}

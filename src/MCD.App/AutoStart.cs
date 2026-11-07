using Mcd.Core.Infrastructure;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Mcd.App;

/// <summary>
/// Whether the program starts when the person signs in.
/// </summary>
/// <remarks>
/// Unpackaged this is the classic Run value under the user's own hive: no
/// administrator rights, no scheduled task, gone the moment it is switched
/// off. Packaged, the registry is the package's own copy and a Run value
/// written there is never read at sign-in; the manifest's StartupTask is the
/// switch Windows honours instead.
/// </remarks>
public static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MasterControlDock";

    /// <summary>The StartupTask's id in Package.appxmanifest.</summary>
    private const string TaskId = "McdStartup";

    public static bool Enabled
    {
        get
        {
            if (AppPaths.IsPackaged)
            {
                return Off(async () => (await StartupTask.GetAsync(TaskId)).State)
                    is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Name) is string;
        }

        set
        {
            if (AppPaths.IsPackaged)
            {
                Off(async () =>
                {
                    StartupTask task = await StartupTask.GetAsync(TaskId);

                    if (value)
                    {
                        // Refused without a word when the person switched it
                        // off in Task Manager: only they can switch it back on
                        // there. The switch then reads the real state and
                        // stays off.
                        await task.RequestEnableAsync();
                    }
                    else
                    {
                        task.Disable();
                    }

                    return true;
                });

                return;
            }

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

    /// <summary>
    /// Waits for an answer off the caller's thread. The caller is the UI
    /// thread; waiting there on an operation that wanted to finish there
    /// would hang the settings window for good.
    /// </summary>
    private static T Off<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
}

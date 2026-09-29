using Mcd.Core.Settings;

namespace Mcd.App.Widgets;

/// <summary>
/// The programs Windows brings with it, ready to pin.
/// </summary>
/// <remarks>
/// <para>
/// A bar that starts empty of anything to press asks a new person to know
/// where Notepad lives. These are the ones people actually reach for, each
/// already drawn in this program's own icons rather than whatever the
/// system has - and being pinned icons they change like any other: the same
/// "Change" beside the name in the settings.
/// </para>
/// <para>
/// Only what is installed is offered. Sticky Notes is a store app and a
/// machine without it would put a button on the bar that opens nothing; the
/// Windows Terminal is the same. Addresses of the <c>ms-settings:</c> and
/// <c>shell:</c> kind are the system's own and are always there.
/// </para>
/// </remarks>
public static class StandardPrograms
{
    /// <param name="Key">The name the strings are filed under.</param>
    /// <param name="Fallback">The English name.</param>
    /// <param name="Target">What is started: a program, or an address the shell understands.</param>
    /// <param name="Icon">The drawing it wears, from the icon library.</param>
    /// <param name="Probe">A file that has to exist for the program to be here, or null when there is nothing to check.</param>
    public sealed record Program(string Key, string Fallback, string Target, string Icon, string? Probe = null)
    {
        /// <summary>The name in the person's language.</summary>
        public string Name => Loc.Tr(Key, Fallback);

        /// <summary>Whether this machine has it.</summary>
        public bool Present => Probe is null || Found(Probe);

        /// <summary>The pinned icon this offers, as the bar stores it.</summary>
        public WidgetConfig Pin() => WidgetConfig.New(IconWidget.Type) with
        {
            Config = WidgetJson.Object(("target", Target), ("name", Name), ("icon", Icon)),
        };

        /// <summary>Whether a pinned icon already on a bar is this program.</summary>
        public bool Is(WidgetConfig entry) =>
            entry.TypeId == IconWidget.Type
            && string.Equals(WidgetOptions.Text(entry.Config, "target"), Target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>In the order a person would look for them: the commonest first.</summary>
    public static IReadOnlyList<Program> All { get; } =
    [
        new("ProgExplorer", "File Explorer", "explorer.exe", "Folder", "explorer.exe"),
        new("ProgNotepad", "Notepad", "notepad.exe", "Notepad", "notepad.exe"),
        new("ProgCalculator", "Calculator", "calc.exe", "Calculator", "calc.exe"),
        new("ProgTerminal", "Terminal", "wt.exe", "Terminal", "wt.exe"),
        new("ProgPowerShell", "PowerShell", "powershell.exe", "Code", @"WindowsPowerShell\v1.0\powershell.exe"),
        new("ProgCommandPrompt", "Command Prompt", "cmd.exe", "Terminal", "cmd.exe"),
        new("ProgSnipping", "Snipping Tool", "snippingtool.exe", "Scissors", "snippingtool.exe"),
        new("ProgPaint", "Paint", "mspaint.exe", "Paint", "mspaint.exe"),
        new("ProgTaskManager", "Task Manager", "taskmgr.exe", "Activity", "taskmgr.exe"),
        new("ProgSettings", "Windows Settings", "ms-settings:", "Gear"),
        new("ProgControlPanel", "Control Panel", "control.exe", "Sliders", "control.exe"),
        new("ProgDownloads", "Downloads", "shell:Downloads", "Download"),
        new("ProgRecycleBin", "Recycle Bin", "shell:RecycleBinFolder", "Delete"),
        new("ProgStickyNotes", "Sticky Notes", @"shell:AppsFolder\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe!App", "StickyNote", @"Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe"),
    ];

    /// <summary>
    /// Whether a program or a folder is on this machine.
    /// </summary>
    /// <remarks>
    /// A path relative to the system folder, the Windows folder, a folder on
    /// the search path, or the shortcuts Windows keeps for store programs.
    /// The last is where the terminal's <c>wt.exe</c> lives, and it is a
    /// stub the file system reports as an ordinary file.
    /// </remarks>
    public static bool Found(string probe)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        IEnumerable<string> folders =
        [
            Environment.SystemDirectory,
            windows,
            Path.Combine(local, "Microsoft", "WindowsApps"),
            local,
            .. (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        ];

        foreach (string folder in folders)
        {
            try
            {
                string path = Path.Combine(folder, probe);

                if (File.Exists(path) || Directory.Exists(path))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A search path with a character in it no path may have.
            }
        }

        return false;
    }
}

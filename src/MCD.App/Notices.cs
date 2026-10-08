using System.Security;
using Microsoft.Extensions.Logging;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Mcd.App;

/// <summary>
/// Windows notifications, for the two things worth interrupting somebody for:
/// a part past its critical temperature, and an update that needs a yes.
/// </summary>
/// <remarks>
/// <para>
/// Both are off unless asked for. A button on a notification names an action
/// registered here with <see cref="On"/>; the press arrives on a thread of
/// Windows' own and is handed to the interface thread. It works while the
/// program runs, which is the only time either notification is shown.
/// </para>
/// <para>
/// Windows' own toast API rather than the App SDK's: the App SDK's needs its
/// shared runtime package, and this program carries its runtime with it, so
/// registering failed with "module not found". An unpackaged program is known
/// to the notification centre by an id registered under the user's classes
/// (removed by the uninstaller), not by a package.
/// </para>
/// </remarks>
internal static class Notices
{
    private const string Id = "ElectronicMars.MasterControlDock";

    private static readonly Dictionary<string, Action> Actions = new(StringComparer.Ordinal);

    // Held while shown: a notification nobody refers to is collected, and a
    // press on its button then goes nowhere.
    private static readonly Dictionary<string, ToastNotification> Shown = new(StringComparer.Ordinal);

    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private static ILogger? _log;
    private static ToastNotifier? _notifier;

    public static void Start(Microsoft.UI.Dispatching.DispatcherQueue ui, ILogger log)
    {
        _ui = ui;
        _log = log;

        try
        {
            using (Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Id))
            {
                key.SetValue("DisplayName", "Master Control Dock");
                key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "icon-128.png"));
            }

            _notifier = ToastNotificationManager.CreateToastNotifier(Id);
        }
        catch (Exception ex)
        {
            // A Windows that cannot have them: the bar's own colours still
            // say everything.
            log.LogWarning(ex, "notices.unavailable");
        }
    }

    /// <summary>What a button carrying this name does.</summary>
    public static void On(string what, Action act) => Actions[what] = act;

    /// <summary>Shows a notification; one with the same tag replaces the last.</summary>
    public static void Show(string tag, string title, string text, params (string Label, string Do)[] buttons)
    {
        if (_notifier is null)
        {
            return;
        }

        string actions = string.Concat(buttons.Select(b =>
            $"<action content=\"{SecurityElement.Escape(b.Label)}\" arguments=\"{SecurityElement.Escape(b.Do)}\" activationType=\"foreground\"/>"));

        var xml = new XmlDocument();
        xml.LoadXml(
            "<toast><visual><binding template=\"ToastGeneric\">"
            + $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>"
            + "</binding></visual>"
            + (actions.Length > 0 ? $"<actions>{actions}</actions>" : string.Empty)
            + "</toast>");

        var toast = new ToastNotification(xml) { Tag = tag };

        toast.Activated += (_, args) =>
        {
            if (args is ToastActivatedEventArgs pressed && Actions.TryGetValue(pressed.Arguments, out Action? act))
            {
                _ui?.TryEnqueue(() => act());
            }
        };

        try
        {
            Shown[tag] = toast;
            _notifier.Show(toast);
            _log?.LogInformation("notices.shown {Tag}", tag);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "notices.failed {Tag}", tag);
        }
    }
}

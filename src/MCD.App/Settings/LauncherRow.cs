using Mcd.Core.Settings;

namespace Mcd.App.Settings;

/// <summary>One pinned item as the settings list shows it.</summary>
public sealed class LauncherRow(LaunchItem item)
{
    public string Id { get; } = item.Id;

    public string Name { get; } = item.Name;

    public string Target { get; } = item.Target;

    /// <summary>
    /// A readable name for a target the person did not name themselves.
    /// </summary>
    /// <remarks>
    /// The file's own name without its extension, or the site for an address.
    /// "notepad" and "github.com" are both better than the full string, which on
    /// the bar is only ever a tooltip anyway.
    /// </remarks>
    public static string NameFor(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            return uri.Host.Length > 0 ? uri.Host : target;
        }

        string name = Path.GetFileNameWithoutExtension(target.TrimEnd('\\', '/'));

        return name.Length > 0 ? name : target;
    }
}

using Microsoft.UI.Xaml;

namespace Mcd.App;

/// <summary>
/// How the settings name a look, and what that means to the interface layer.
/// </summary>
/// <remarks>
/// The names in config.json are words rather than numbers on purpose - a person
/// reading the file should be able to tell what it says - so something has to
/// turn them back into what the framework wants. Anything unrecognised means
/// "whatever Windows is set to", because a settings file from a later version
/// naming a look this build has never heard of should not leave the dock blank.
/// </remarks>
public static class Appearance
{
    public static ElementTheme Of(string? theme) => theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static string Name(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "light",
        ElementTheme.Dark => "dark",
        _ => "system",
    };

    /// <summary>The index this theme takes in the settings list: Windows, Light, Dark.</summary>
    public static int Index(string? theme) => theme switch
    {
        "light" => 1,
        "dark" => 2,
        _ => 0,
    };

    public static string FromIndex(int index) => index switch
    {
        1 => "light",
        2 => "dark",
        _ => "system",
    };
}

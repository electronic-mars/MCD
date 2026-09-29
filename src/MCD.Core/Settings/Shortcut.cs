namespace Mcd.Core.Settings;

/// <summary>
/// The things a combination of keys can be made to do.
/// </summary>
/// <remarks>
/// Named rather than numbered, because the name is what goes in the settings
/// file and what somebody reads there. A name that disappears from this list
/// simply stops being bound; the line stays in the file and does nothing,
/// which is better than a file that will not load.
/// </remarks>
public static class Shortcut
{
    /// <summary>Show every bar, or hide every bar.</summary>
    public const string Bars = "bars";

    /// <summary>Open the settings window.</summary>
    public const string Settings = "settings";

    /// <summary>Silence the machine, or let it speak.</summary>
    public const string Mute = "mute";

    /// <summary>Switch the microphone off, or on again.</summary>
    public const string Mic = "mic";

    /// <summary>All of them, in the order they are offered.</summary>
    public static IReadOnlyList<string> All { get; } = [Bars, Settings, Mute, Mic];
}

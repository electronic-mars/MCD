using System.Collections.Immutable;

namespace Mcd.App.Widgets;

/// <summary>Which icon a reading is drawn with, as chosen by the person using it.</summary>
/// <remarks>
/// The choice is per reading, not per widget instance: someone who prefers a
/// different processor icon means it for every dock they have, and asking them
/// to set it once per screen would be tedious to no purpose.
/// </remarks>
public sealed class IconChoices(ImmutableDictionary<string, string> chosen)
{
    public static IconChoices None { get; } = new(ImmutableDictionary<string, string>.Empty);

    /// <summary>Every reading a person can pick an icon for, in the order the settings show them.</summary>
    public static IReadOnlyList<(string Id, string Label, string Fallback)> Known { get; } =
    [
        ("cpu", Loc.Tr("LabelCpu", "CPU"), "Cpu"),
        ("ram", Loc.Tr("LabelMemory", "Memory"), "Memory"),
        ("up", Loc.Tr("LabelSend", "Send"), "ArrowUp"),
        ("down", Loc.Tr("LabelReceive", "Receive"), "ArrowDown"),
        ("gpu", Loc.Tr("LabelGpu", "GPU"), "Gpu"),
        ("temp", Loc.Tr("LabelTemperature", "Temperature"), "Temperature"),
    ];

    /// <summary>The chosen icon, or the widget's own default when there is none.</summary>
    public string For(string id, string fallback) =>
        chosen.TryGetValue(id, out string? name) && IconLibrary.Paths.ContainsKey(name)
            ? name
            : fallback;
}

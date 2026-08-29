using System.Text.Json;

namespace Mcd.Core.Settings;

/// <summary>
/// A widget carried from the settings window to a bar.
/// </summary>
/// <remarks>
/// <para>
/// Sent as plain text rather than as a format of this program's own. A custom
/// clipboard format between two windows of one process ought to be the tidier
/// answer, and it is - until a widget is dropped on something else, at which
/// point a format nobody else understands is refused silently while text at
/// least arrives somewhere legible.
/// </para>
/// <para>
/// The prefix is what makes it ours. Text dragged in from anywhere else does
/// not begin with it and is treated as text, which is how a path dropped from
/// Explorer still becomes a pinned program.
/// </para>
/// </remarks>
public static class WidgetDrag
{
    private const string Mark = "mcd-widget:";

    /// <summary>Packs a widget for the journey.</summary>
    public static string Wrap(WidgetConfig entry) =>
        Mark + JsonSerializer.Serialize(entry, WidgetDragJson.Options);

    /// <summary>
    /// Unpacks one, or nothing when this was never one of ours.
    /// </summary>
    /// <remarks>
    /// The instance id is thrown away and a new one taken: dragging the same
    /// chip twice puts two widgets on the bar, and two widgets sharing an id
    /// are one widget as far as every other part of this program is concerned.
    /// </remarks>
    public static WidgetConfig? Unwrap(string? text)
    {
        if (text is null || !text.StartsWith(Mark, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            WidgetConfig? entry =
                JsonSerializer.Deserialize<WidgetConfig>(text[Mark.Length..], WidgetDragJson.Options);

            return entry is null
                ? null
                : entry with { InstanceId = WidgetConfig.New(entry.TypeId).InstanceId };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class WidgetDragJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

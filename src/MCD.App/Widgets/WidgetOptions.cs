using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcd.App.Widgets;

/// <summary>
/// Reading and writing a widget's own settings.
/// </summary>
/// <remarks>
/// The settings of one widget instance are an opaque piece of JSON as far as
/// everything else is concerned - see <see cref="Mcd.Core.Settings.WidgetConfig"/>.
/// Only the widget that owns them knows what is in there, and the helpers here
/// exist so that each widget does not write its own defensive parsing.
/// </remarks>
public static class WidgetOptions
{
    public static string? Text(JsonElement? config, string name) =>
        Property(config, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static int? Number(JsonElement? config, string name) =>
        Property(config, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out int n)
            ? n
            : null;

    /// <summary>A list of strings, or null when the setting was never written.</summary>
    public static IReadOnlyList<string>? Strings(JsonElement? config, string name)
    {
        if (Property(config, name) is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        return [.. list.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)];
    }

    /// <summary>
    /// The settings with some values replaced, and everything else left alone.
    /// </summary>
    /// <remarks>
    /// Merged rather than rewritten. A settings file written by a later version
    /// may hold keys this build has never heard of, and a build that dropped
    /// them every time someone touched a checkbox would quietly undo the newer
    /// version's configuration - which is the settings-file shape of the bug
    /// this whole program was built against.
    /// </remarks>
    public static JsonElement Merge(JsonElement? config, params (string Name, JsonNode? Value)[] changes)
    {
        JsonObject root = config is { ValueKind: JsonValueKind.Object } existing
            ? JsonNode.Parse(existing.GetRawText())?.AsObject() ?? []
            : [];

        foreach ((string name, JsonNode? value) in changes)
        {
            root[name] = value;
        }

        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    public static JsonArray Array(IEnumerable<string> values)
    {
        var array = new JsonArray();

        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonElement? Property(JsonElement? config, string name) =>
        config is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out JsonElement value)
            ? value
            : null;
}

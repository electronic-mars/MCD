using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcd.Core.Settings;

/// <summary>Builds the little JSON objects a widget's own settings are made of.</summary>
/// <remarks>
/// Members whose value is null are simply left out, so callers can pass what
/// they have and the file never records an empty string as a choice.
/// </remarks>
public static class WidgetJson
{
    public static JsonElement Object(params (string Name, JsonNode? Value)[] members)
    {
        var root = new JsonObject();

        foreach ((string name, JsonNode? value) in members)
        {
            if (value is not null)
            {
                root[name] = value;
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Mcd.Sensors.Contracts;

namespace Mcd.Sensors.Providers;

/// <param name="Id">
/// LibreHardwareMonitor's own identifier, such as
/// <c>/amdcpu/0/temperature/0</c>. Derived from the hardware and its position
/// rather than from the order things were found in, so it means the same thing
/// after a restart - which is what makes it usable as a key in settings.
/// </param>
public sealed record LhmSensor(
    string Id,
    string Label,
    string Hardware,
    HardwareGroup Group,
    double Celsius);

/// <summary>
/// Reads the tree LibreHardwareMonitor's web server publishes.
/// </summary>
/// <remarks>
/// <para>
/// The document is a tree of nodes with a Text, a Value and some Children;
/// hardware sits a couple of levels down and its sensors are grouped under
/// headings by kind. Nothing about the shape is guaranteed by a schema, so this
/// walks it defensively and takes only what it recognises.
/// </para>
/// <para>
/// Values arrive as display strings in the running machine's language -
/// "45,0 °C" on a Russian Windows, "45.0 °C" on an English one - which is why
/// they are parsed by hand rather than handed to the framework's number reader.
/// </para>
/// </remarks>
public static class LhmReport
{
    /// <summary>Every temperature in the document, or empty if it is not one.</summary>
    public static ImmutableArray<LhmSensor> Temperatures(string json)
    {
        JsonElement root;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        ImmutableArray<LhmSensor>.Builder found = ImmutableArray.CreateBuilder<LhmSensor>();
        Walk(root, hardware: null, found);
        return found.ToImmutable();
    }

    private static void Walk(JsonElement node, string? hardware, ImmutableArray<LhmSensor>.Builder into)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string text = Text(node, "Text");
        string id = Text(node, "SensorId");

        if (id.Length > 0)
        {
            // A leaf with an identifier is a sensor. Everything above it that
            // had no identifier was a heading, and the last real piece of
            // hardware is whatever we were told on the way down.
            if (Celsius(node) is { } degrees && hardware is not null)
            {
                into.Add(new LhmSensor(id, text, hardware, Classify(id), degrees));
            }

            return;
        }

        // A node that owns an identified sensor somewhere below it is the piece
        // of hardware those sensors belong to. Headings such as "Temperatures"
        // sit in between and must not be mistaken for it.
        string? below = Owns(node) ? text : hardware;

        if (!node.TryGetProperty("Children", out JsonElement children)
            || children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement child in children.EnumerateArray())
        {
            Walk(child, below, into);
        }
    }

    /// <summary>True when this node is a piece of hardware rather than a heading.</summary>
    /// <remarks>
    /// Told apart by what is under it: hardware has headings under it, and a
    /// heading has sensors. There is nothing in the document that says which is
    /// which, and matching the heading names would only work in English.
    /// </remarks>
    private static bool Owns(JsonElement node)
    {
        if (!node.TryGetProperty("Children", out JsonElement children)
            || children.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object
                || !child.TryGetProperty("Children", out JsonElement below)
                || below.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement leaf in below.EnumerateArray())
            {
                if (leaf.ValueKind == JsonValueKind.Object && Text(leaf, "SensorId").Length > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Which part of the machine an identifier belongs to.</summary>
    /// <remarks>
    /// From the first segment of the identifier rather than from the hardware's
    /// name or its icon. The segment is a fixed word in the program's source -
    /// <c>amdcpu</c>, <c>nvidiagpu</c>, <c>lpc</c> - while names and icons vary
    /// with the machine and the language.
    /// </remarks>
    public static HardwareGroup Classify(string sensorId)
    {
        string kind = sensorId.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (kind.Contains("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareGroup.Cpu;
        }

        if (kind.Contains("gpu", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareGroup.Gpu;
        }

        return kind switch
        {
            "nvme" or "hdd" or "ssd" or "storage" => HardwareGroup.Storage,
            "ram" => HardwareGroup.Memory,
            "nic" => HardwareGroup.Network,
            _ => HardwareGroup.Motherboard,
        };
    }

    /// <summary>The number at the front of a display string, or null if it is not a temperature.</summary>
    public static double? Celsius(JsonElement node)
    {
        if (!string.Equals(Text(node, "Type"), "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            // Older builds of the web server do not label a sensor's kind, so
            // the unit on the value is the fallback test.
            if (!Text(node, "Value").Contains('°'))
            {
                return null;
            }
        }

        return Number(Text(node, "Value"));
    }

    /// <summary>
    /// The leading number of a value such as "45,0 °C".
    /// </summary>
    /// <remarks>
    /// Both separators are accepted whatever the machine is set to. Parsing this
    /// with the current culture would read "45.0" as four hundred and fifty on a
    /// Russian Windows, which is a plausible temperature and therefore the worst
    /// kind of wrong.
    /// </remarks>
    public static double? Number(string value)
    {
        int end = 0;

        while (end < value.Length
               && (char.IsAsciiDigit(value[end]) || value[end] is '.' or ',' or '-' or '+'))
        {
            end++;
        }

        string head = value[..end].Replace(',', '.');

        return double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number
            : null;
    }

    private static string Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

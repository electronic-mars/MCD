using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcd.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Mcd.Core.Settings;

/// <summary>
/// Reads and writes config.json.
/// </summary>
/// <remarks>
/// Internal on purpose. Everything outside this namespace goes through
/// <see cref="SettingsService"/>, which records why each write happened. A
/// public store would let the window-positioning code save settings, and saving
/// settings from inside a topology change is exactly the mistake behind
/// microsoft/PowerToys#49604.
/// </remarks>
internal sealed class SettingsStore(ILogger log, string? directory = null)
{
    /// <summary>The settings as the file would hold them: two models that read the same are the same settings.</summary>
    public static string Text(SettingsModel model) => JsonSerializer.Serialize(model, Json);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The default encoder escapes & < > for HTML contexts, which turns a
        // device path into a wall of &. This file is read by people when
        // something has gone wrong; it needs to stay legible.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root = directory ?? AppPaths.Root;

    private string ConfigPath => Path.Combine(_root, "config.json");

    private string BackupPath => Path.Combine(_root, "config.bak");

    private string TempPath => Path.Combine(_root, "config.json.tmp");

    /// <summary>
    /// Never throws and never returns null. A machine whose settings file went
    /// bad still gets a working dock; the broken file is set aside for a look.
    /// </summary>
    public SettingsModel Load()
    {
        if (TryRead(ConfigPath, out SettingsModel? model))
        {
            return model;
        }

        if (File.Exists(ConfigPath))
        {
            Quarantine();
        }

        if (TryRead(BackupPath, out SettingsModel? backup))
        {
            log.LogWarning("settings.load recovered from config.bak");
            return backup;
        }

        log.LogInformation("settings.load no usable settings, starting from defaults");
        return SettingsDefaults.Model;
    }

    /// <summary>
    /// Writes through a temp file so a crash mid-write cannot leave a truncated
    /// config.json - which would come back as "the dock lost all its widgets".
    /// </summary>
    public void Save(SettingsModel model)
    {
        Directory.CreateDirectory(_root);

        // The one place in the program allowed to write a settings file. The ban
        // exists so that nothing else does; this is what it is protecting.
        //
        // Written through to the disk before the swap: the swap is in the
        // file system's journal at once, and the bytes it points at were not
        // - after a power cut the new file could come back full of zeroes.
#pragma warning disable RS0030
        using (var temp = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            temp.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, Json)));
            temp.Flush(flushToDisk: true);
        }
#pragma warning restore RS0030

        if (File.Exists(ConfigPath))
        {
            // One call swaps the file and rolls the previous one into the backup.
            File.Replace(TempPath, ConfigPath, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(TempPath, ConfigPath);
        }
    }

    /// <summary>Writes the settings to a file of somebody's choosing.</summary>
    /// <remarks>
    /// Straight out, not through the temp-and-replace dance: this is a copy
    /// being made somewhere else, and losing it costs one more press of the
    /// button rather than everything the program knows.
    /// </remarks>
    public void Export(string path, SettingsModel model)
    {
#pragma warning disable RS0030
        File.WriteAllText(path, JsonSerializer.Serialize(model, Json));
#pragma warning restore RS0030
    }

    /// <summary>Reads a settings file from anywhere, or null if it is not one.</summary>
    /// <remarks>
    /// One of ours has to say so: any JSON object at all read as one, with
    /// every missing part filled from the defaults, and importing the wrong
    /// file put the theme, the keys and the layouts back to new.
    /// </remarks>
    public SettingsModel? Import(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Equals("monitors", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return TryRead(path, out SettingsModel model) ? model : null;
    }

    private bool TryRead(string path, out SettingsModel model)
    {
        model = SettingsDefaults.Model;

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            // Deserialising into the model is also how unknown keys are dropped:
            // the next save serialises the model, not whatever was on disk.
            SettingsModel? parsed = JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(path), Json);
            if (parsed is null)
            {
                return false;
            }

            model = Clean(parsed);
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "settings.load failed to read {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// The file as read, with the holes taken out.
    /// </summary>
    /// <remarks>
    /// Nullability is not checked when a file is read: <c>"widgets": [null]</c>
    /// or <c>"app": null</c> loaded without complaint and failed later, deep in
    /// the layout - and was written back as it was, so every start failed the
    /// same way and nothing ever set the file aside.
    /// </remarks>
    private static SettingsModel Clean(SettingsModel model)
    {
        AppSettings app = model.App ?? new AppSettings();
        SensorSettings sensors = model.Sensors ?? new SensorSettings();

        return model with
        {
            App = app with
            {
                Launcher = Present(app.Launcher),
                Presets = [.. Present(app.Presets).Select(p => p with { Widgets = Present(p.Widgets) })],
            },
            Sensors = sensors with { StorageBlacklist = Present(sensors.StorageBlacklist) },
            Monitors =
            [
                .. Present(model.Monitors).Select(m => m with
                {
                    Widgets = Present(m.Widgets),
                    PreviousStableIds = Present(m.PreviousStableIds),
                }),
            ],
        };
    }

    private static ImmutableArray<T> Present<T>(ImmutableArray<T> items)
        where T : class =>
        items.IsDefault ? [] : [.. items.Where(item => item is not null)];

    private void Quarantine()
    {
        string kept = Path.Combine(_root, $"config.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

        try
        {
            File.Move(ConfigPath, kept);
            log.LogError("settings.load kept the unreadable file as {Path}", kept);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "settings.load could not set aside the unreadable file");
        }
    }
}

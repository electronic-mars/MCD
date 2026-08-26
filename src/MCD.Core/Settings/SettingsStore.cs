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
#pragma warning disable RS0030
        File.WriteAllText(TempPath, JsonSerializer.Serialize(model, Json));
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

            model = parsed;
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "settings.load failed to read {Path}", path);
            return false;
        }
    }

    private void Quarantine()
    {
        string kept = Path.Combine(_root, $"config.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

        try
        {
            File.Move(ConfigPath, kept);
            log.LogError("settings.load kept the unreadable file as {Path}", kept);
        }
        catch (IOException e)
        {
            log.LogError(e, "settings.load could not set aside the unreadable file");
        }
    }
}

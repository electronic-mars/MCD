namespace Mcd.Core.Settings;

/// <summary>
/// The settings a fresh install starts from, in one place.
/// </summary>
/// <remarks>
/// Every property of <see cref="SettingsModel"/> carries its own default, so a
/// key missing from config.json takes the same value as a key that was never
/// written. There is no second list of defaults to fall out of step.
/// </remarks>
public static class SettingsDefaults
{
    public const int SchemaVersion = 4;

    public static SettingsModel Model => new();
}

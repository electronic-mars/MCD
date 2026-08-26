using System.Reflection;

namespace Mcd.Core.Infrastructure;

/// <summary>
/// The version, read from the assembly rather than written out again here.
/// Directory.Version.props is the only place the number is typed.
/// </summary>
public static class AppInfo
{
    public const string Name = "Master Control Dock";
    public const string ShortName = "MCD";

    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";
}

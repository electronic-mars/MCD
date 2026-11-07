namespace Mcd.Audio;

/// <summary>
/// The volume and the microphone while MCD_DEMO=1 is set: kept here and never
/// asked of Windows, so a recording neither shows nor changes the machine's.
/// </summary>
/// <remarks>
/// The flag is read here rather than taken from Mcd.Interop.Demo: this project
/// does not reference that one, for the marshalling reason in its project file.
/// </remarks>
internal static class Demo
{
    public static bool On { get; } = Environment.GetEnvironmentVariable("MCD_DEMO") == "1";

    public static float Volume { get; set; } = 0.21f;

    public static bool VolumeMuted { get; set; }

    public static bool MicMuted { get; set; }
}

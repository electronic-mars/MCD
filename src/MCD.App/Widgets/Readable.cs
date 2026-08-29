using System.Globalization;

namespace Mcd.App.Widgets;

/// <summary>
/// Figures written the way a person would say them.
/// </summary>
/// <remarks>
/// A reading arrives from its source in the unit the hardware happens to use,
/// which for anything counted in bytes is the byte: sixty-eight thousand
/// million of them for the memory in an ordinary laptop. Nobody has ever read
/// a number that long off a bar, so the unit is chosen per value here - the
/// one place both the bar and the settings window ask.
/// </remarks>
public static class Readable
{
    private const double Kilo = 1024;
    private const double Mega = Kilo * 1024;
    private const double Giga = Mega * 1024;
    private const double Tera = Giga * 1024;

    /// <summary>An amount of storage or memory: "63.8 GB", "512 MB".</summary>
    public static string Size(double bytes) => bytes switch
    {
        >= Tera => Trim(bytes / Tera) + " TB",
        >= Giga => Trim(bytes / Giga) + " GB",
        >= Mega => Round(bytes / Mega) + " MB",
        >= Kilo => Round(bytes / Kilo) + " kB",
        _ => Round(bytes) + " B",
    };

    /// <summary>
    /// A rate: "12.4 MB/s", "427 B/s".
    /// </summary>
    /// <param name="narrow">
    /// True on a compact bar, where the unit shrinks to a single letter. A
    /// rate is the one reading whose unit changes as you watch it, so the
    /// letter has to stay: "12.4" alone could be kilobytes or megabytes.
    /// </param>
    public static string Rate(double bytesPerSecond, bool narrow = false) => bytesPerSecond switch
    {
        >= Mega => Trim(bytesPerSecond / Mega) + (narrow ? "M" : " MB/s"),
        >= Kilo => Round(bytesPerSecond / Kilo) + (narrow ? "k" : " kB/s"),
        _ => Round(bytesPerSecond) + (narrow ? string.Empty : " B/s"),
    };

    /// <summary>Any reading at all, with the unit its source declared.</summary>
    public static string Value(double value, string unit, bool narrow = false) => unit switch
    {
        "B" => Size(value),
        "B/s" => Rate(value, narrow),
        _ => Round(value) + " " + unit,
    };

    private static string Round(double value) =>
        value.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>One decimal, unless it would be a nought.</summary>
    private static string Trim(double value)
    {
        string text = value.ToString("F1", CultureInfo.InvariantCulture);

        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}

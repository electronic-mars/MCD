using Microsoft.Windows.ApplicationModel.Resources;

namespace Mcd.App;

/// <summary>Strings the code builds at run time, from the same table the XAML reads.</summary>
public static class Loc
{
    private static readonly ResourceLoader? Loader = Make();

    private static ResourceLoader? Make()
    {
        try { return new ResourceLoader(); }
        catch (Exception) { return null; }   // test hosts have no resource map
    }

    public static string Tr(string key, string fallback)
    {
        try
        {
            string? s = Loader?.GetString(key);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }
        catch (Exception) { return fallback; }
    }
}

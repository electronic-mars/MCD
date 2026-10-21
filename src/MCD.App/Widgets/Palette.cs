using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// The brushes a widget paints with in code, in the theme the bars are in.
/// </summary>
/// <remarks>
/// <para>
/// A brush asked for from the application's resources comes in the theme
/// Windows is in. The bars can be told otherwise - light on a dark Windows, or
/// the reverse - and the template's own brushes follow the bar while one
/// fetched in code did not: near-white figures on a light bar.
/// </para>
/// <para>
/// So a bar in a theme of its own looks the brush up in that theme's own
/// dictionary. A bar that follows Windows asks the application, which follows
/// Windows too, as it changes.
/// </para>
/// </remarks>
public static class Palette
{
    private static readonly Dictionary<(string Key, ElementTheme Theme), Brush> Found = [];

    /// <summary>The theme the bars were last told to be in.</summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Default;

    public static Brush Get(string key)
    {
        ElementTheme theme = Theme;

        if (theme == ElementTheme.Default)
        {
            return (Brush)Application.Current.Resources[key];
        }

        if (!Found.TryGetValue((key, theme), out Brush? brush))
        {
            brush = Look(Application.Current.Resources, key, theme == ElementTheme.Light ? ["Light"] : ["Dark", "Default"])
                ?? (Brush)Application.Current.Resources[key];

            Found[(key, theme)] = brush;
        }

        return brush;
    }

    /// <summary>The brush in this theme's dictionary, the dictionaries merged last first, as lookup goes.</summary>
    private static Brush? Look(ResourceDictionary dictionary, string key, string[] names)
    {
        foreach (string name in names)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out object? themed)
                && themed is ResourceDictionary inTheme
                && inTheme.TryGetValue(key, out object? value)
                && value is Brush brush)
            {
                return brush;
            }
        }

        for (int i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (Look(dictionary.MergedDictionaries[i], key, names) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

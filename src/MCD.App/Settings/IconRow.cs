using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Widgets;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Settings;

/// <summary>One icon on offer in the picker.</summary>
/// <remarks>
/// Carries the drawn shape rather than the name plus a converter. A Geometry
/// belongs to exactly one element, so each choice gets its own; and x:Bind
/// cannot resolve a converter at all when the root of the XAML is a Window
/// rather than a page.
/// </remarks>
public sealed record IconChoice(string Name, Geometry Shape)
{
    public static IconChoice Of(string name) => new(name, IconRow.Draw(name));
}

/// <summary>One reading in the icon settings: what it is, and which icon it wears.</summary>
public sealed partial class IconRow : ObservableObject
{
    private readonly Action<IconRow> _onChosen;

    public IconRow(string id, string label, string current, Action<IconRow> onChosen)
    {
        Id = id;
        Label = label;
        Icon = current;
        Shape = Draw(current);
        _onChosen = onChosen;
    }

    public string Id { get; }

    public string Label { get; }

    public string Icon { get; private set; }

    /// <summary>The shape shown on this row's button.</summary>
    [ObservableProperty]
    public partial Geometry Shape { get; set; }

    /// <summary>Every icon on offer, freshly drawn. Never reuse the shapes.</summary>
    public static IReadOnlyList<IconChoice> Choices() =>
        [.. IconLibrary.Paths.Keys.Select(IconChoice.Of)];

    public static Geometry Draw(string name)
    {
        string path = IconLibrary.Paths.TryGetValue(name, out string? found)
            ? found
            : IconLibrary.Paths["Activity"];

        return (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
    }

    public void Choose(string icon)
    {
        if (Icon == icon)
        {
            return;
        }

        Icon = icon;
        Shape = Draw(icon);
        _onChosen(this);
    }
}

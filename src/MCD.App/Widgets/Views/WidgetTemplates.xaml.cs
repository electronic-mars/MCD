using Microsoft.UI.Xaml;

namespace Mcd.App.Widgets.Views;

/// <summary>
/// Code-behind for the widget templates.
/// </summary>
/// <remarks>
/// Empty, and required. A resource dictionary that uses <c>x:Bind</c> must have
/// a partial class for the generated bindings to live in; the alternative is
/// <c>Binding</c>, which resolves by reflection at run time and would turn a
/// renamed property into a chip that silently shows nothing.
/// </remarks>
public sealed partial class WidgetTemplates : ResourceDictionary
{
    public WidgetTemplates()
    {
        InitializeComponent();

        // The microphone and the cup are one drawing and one press, and
        // share a template written against their common base.
        this[MicWidget.Type] = this[AwakeWidget.Type];
    }
}

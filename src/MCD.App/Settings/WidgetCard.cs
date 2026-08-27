using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Widgets;
using Mcd.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Settings;

/// <summary>
/// One widget on a dock, as the settings list shows it.
/// </summary>
/// <remarks>
/// Built from the settings entry, never from a widget that was successfully
/// constructed. A build that does not know a widget's kind must still show it,
/// keep its place and write it back untouched - otherwise running an older
/// version once quietly deletes whatever a newer one had added.
/// </remarks>
public sealed partial class WidgetCard : ObservableObject, IDisposable
{
    private readonly Func<WidgetConfig, WidgetViewModel?> _build;
    private readonly Action<WidgetCard, JsonElement?> _changed;
    private WidgetViewModel? _widget;

    public WidgetCard(
        WidgetConfig entry,
        Func<WidgetConfig, WidgetViewModel?> build,
        Action<WidgetCard, JsonElement?> changed)
    {
        Entry = entry;
        _build = build;
        _changed = changed;

        WidgetType? type = WidgetCatalog.Find(entry.TypeId);

        Known = type is not null;
        Name = type?.Name ?? entry.TypeId;
        Shape = IconRow.Draw(type?.Icon ?? "Activity");

        Summary = Known
            ? Widget?.Summarise() ?? string.Empty
            : Loc.Tr(
                "WidgetUnknownSummary",
                "This version does not know this widget. It stays in your settings and is left alone.");
    }

    public WidgetConfig Entry { get; }

    public string Id => Entry.InstanceId;

    public string Name { get; }

    /// <summary>False for a widget added by a later version of the program.</summary>
    public bool Known { get; }

    /// <summary>The icon drawn on this row. Never shared - a Geometry has one owner.</summary>
    public Geometry Shape { get; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool Expanded { get; set; }

    /// <summary>
    /// The widget's own settings controls, built the first time they are asked for.
    /// </summary>
    /// <remarks>
    /// Late on purpose. The temperature editor lists the sensors that answered,
    /// and sources take a moment to be found after the window opens; built
    /// eagerly it would offer an empty list.
    /// </remarks>
    [ObservableProperty]
    public partial FrameworkElement? Editor { get; set; }

    private WidgetViewModel? Widget => _widget ??= _build(Entry);

    partial void OnExpandedChanged(bool value)
    {
        if (!value || Editor is not null)
        {
            return;
        }

        Editor = (Known ? Widget?.CreateEditor(config => _changed(this, config)) : null)
            ?? new TextBlock
            {
                Text = Known
                    ? Loc.Tr("WidgetNothingToSetUp", "This widget has nothing to set up.")
                    : Loc.Tr(
                        "WidgetUnknownNothing",
                        "Nothing can be set up for a widget this version does not know."),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            };
    }

    /// <summary>Re-reads the one-line summary after the widget's settings changed.</summary>
    public void Restate(WidgetConfig entry)
    {
        _widget?.Dispose();
        _widget = _build(entry);

        if (Known)
        {
            Summary = _widget?.Summarise() ?? string.Empty;
        }
    }

    public void Dispose()
    {
        _widget?.Dispose();
        _widget = null;
    }
}

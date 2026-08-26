using System.Collections.Immutable;
using Mcd.Core.Settings;

namespace Mcd.App.Widgets;

/// <summary>
/// One kind of widget: what it is called, what it does, and how to build one.
/// </summary>
/// <param name="AllowsMultiple">
/// False for a widget that would show the same thing twice. The launcher draws
/// one shared list of pinned items, so a second copy on the same bar is only a
/// duplicate.
/// </param>
public sealed record WidgetType(
    string TypeId,
    string Name,
    string Description,
    string Icon,
    bool AllowsMultiple,
    Func<WidgetContext, WidgetConfig, WidgetViewModel> Build);

/// <summary>
/// Every widget this build knows about, in the order they are offered.
/// </summary>
/// <remarks>
/// One list rather than a switch statement in the factory and a second list in
/// the settings. Adding a kind of widget is one entry here, and everything that
/// offers, names, draws or configures widgets reads it from this one place.
/// </remarks>
public static class WidgetCatalog
{
    public static ImmutableArray<WidgetType> All { get; } =
    [
        new(
            LauncherWidget.Type,
            "Launcher",
            "Programs, folders and pages you have pinned.",
            "Rocket",
            AllowsMultiple: false,
            (context, entry) => new LauncherWidget(context, entry)),

        new(
            LoadWidget.Type,
            "Load",
            "How busy the processor, graphics, memory and network are.",
            "Activity",
            AllowsMultiple: true,
            (context, entry) => new LoadWidget(context, entry)),

        new(
            TemperatureWidget.Type,
            "Temperature",
            "Temperatures of the parts that report one.",
            "Temperature",
            AllowsMultiple: true,
            (context, entry) => new TemperatureWidget(context, entry)),
    ];

    public static WidgetType? Find(string typeId) =>
        All.FirstOrDefault(w => w.TypeId == typeId);

    /// <summary>
    /// Builds the widget an entry asks for, or null if this build does not know
    /// the kind.
    /// </summary>
    /// <remarks>
    /// Null is not a failure. A settings file written by a later version may
    /// name a widget that does not exist here yet, and skipping it beats
    /// refusing to show the dock. Everything that edits the layout must work
    /// from the settings entries rather than from what was built successfully,
    /// or the first rearrangement writes the unknown entry out of existence.
    /// </remarks>
    public static WidgetViewModel? Create(WidgetContext context, WidgetConfig entry) =>
        Find(entry.TypeId)?.Build(context, entry);
}

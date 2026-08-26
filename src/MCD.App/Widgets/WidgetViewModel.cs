using System.Collections.Immutable;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>Everything a widget is given when it is built.</summary>
/// <remarks>
/// One parameter rather than five. Widgets differ in what they need - readings,
/// chosen icons, the launcher's list - and threading each of those through the
/// factory, the dock window and every widget that does not want it is how a
/// constructor grows to a line nobody can read.
/// </remarks>
public sealed record WidgetContext(
    SensorHub Sensors,
    IconChoices Icons,
    ImmutableArray<LaunchItem> Launcher,
    ILogger Log)
{
    /// <summary>True when readings are drawn in the accent colour from Windows.</summary>
    public bool Accent { get; init; }

    /// <summary>True for the translucent finish, false for a plain colour.</summary>
    public bool Acrylic { get; init; } = true;

    /// <summary>Light, dark, or whatever Windows is set to.</summary>
    public ElementTheme Theme { get; init; } = ElementTheme.Default;
}

/// <summary>
/// One thing on the bar.
/// </summary>
/// <remarks>
/// <para>
/// A widget supplies a view model and the key of a template; it does not supply
/// an icon, a title and a subtitle to be arranged by someone else. PowerToys
/// reuses the Command Palette's command model for its dock, which is why a
/// widget there can only ever be a picture with two lines of text - no graph, no
/// colour that means something, no shape of its own. The whole point of building
/// this separately is that the widget owns its appearance.
/// </para>
/// <para>
/// <see cref="Tick"/> runs on the UI thread and must not do any work worth
/// mentioning: it reads an already-built snapshot and assigns to properties.
/// Anything slower belongs in a sensor provider.
/// </para>
/// </remarks>
public abstract partial class WidgetViewModel : ObservableObject, IDisposable
{
    protected WidgetViewModel(WidgetContext context, WidgetConfig entry)
    {
        Context = context;
        Entry = entry;
    }

    /// <summary>Matches the key of a DataTemplate, and the typeId stored in config.json.</summary>
    public abstract string TypeId { get; }

    /// <summary>
    /// Which way this widget's contents run.
    /// </summary>
    /// <remarks>
    /// A dock on the left or right edge is 86 points wide and as tall as the
    /// screen; one on the top or bottom is the other way round. Everything a
    /// widget lays out has to follow, or all but the first reading falls off the
    /// end of the bar - which is exactly what it did before this existed.
    /// </remarks>
    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    /// <summary>How thick the bar is, which decides how big a reading is drawn.</summary>
    public DockDensity Density { get; set; } = DockDensity.Default;

    /// <summary>The settings entry this widget was built from.</summary>
    public WidgetConfig Entry { get; }

    public string InstanceId => Entry.InstanceId;

    /// <summary>This widget instance's own settings, as it wrote them.</summary>
    protected JsonElement? Options => Entry.Config;

    /// <summary>
    /// The controls for this widget's own settings, or null when it has none.
    /// </summary>
    /// <param name="changed">
    /// Called with the new settings whenever the user changes something. There
    /// is no OK button: what the bar does and what the settings say are never
    /// allowed to differ, not even for the length of a form.
    /// </param>
    public virtual FrameworkElement? CreateEditor(Action<JsonElement?> changed) => null;

    /// <summary>One line saying how this widget is set up, for the settings list.</summary>
    public virtual string Summarise() => string.Empty;

    /// <summary>Called on the UI thread once a second while the dock is visible.</summary>
    public abstract void Tick(SensorSnapshot snapshot);

    /// <summary>Called once, after the sensor catalogue is known to have settled.</summary>
    public virtual void Attach()
    {
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    protected WidgetContext Context { get; }

    protected SensorHub Sensors => Context.Sensors;

    protected IconChoices Icons => Context.Icons;
}

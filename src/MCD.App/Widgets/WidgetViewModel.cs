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
    SensorNames Names,
    ILogger Log)
{
    /// <summary>True when readings are drawn in the accent colour from Windows.</summary>
    public bool Accent { get; init; }

    /// <summary>True for the translucent finish, false for a painted one.</summary>
    public bool Acrylic { get; init; } = true;

    /// <summary>"acrylic", "solid", "colour" or "image".</summary>
    public string Backdrop { get; init; } = "acrylic";

    /// <summary>The bar's own colour, as #AARRGGBB, while Backdrop is "colour".</summary>
    public string BackdropColour { get; init; } = "#FF202020";

    /// <summary>The picture behind the bar, while Backdrop is "image".</summary>
    public string BackdropImage { get; init; } = string.Empty;

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

    /// <summary>
    /// What to call this one in a list of what is on the bar.
    /// </summary>
    /// <remarks>
    /// The kind, unless the widget is one of several of its kind and knows
    /// which: five readings called "Reading" is a list that cannot be used to
    /// find anything. Not the same as the summary, which says how a widget is
    /// set up rather than what it is - "Keys; track name in the tooltip" is
    /// the right answer to a different question.
    /// </remarks>
    public virtual string Called => WidgetCatalog.Find(TypeId)?.Name ?? TypeId;

    /// <summary>
    /// How long this widget is, in effective pixels, before it is drawn.
    /// </summary>
    /// <remarks>
    /// Declared rather than measured. The bar is a row of slots and has to know
    /// how many each widget takes before it lays anything out - and a control
    /// that is not yet in the tree measures as nothing, which put every reading
    /// in a single slot and drew them over one another.
    /// </remarks>
    public virtual double Length() => 30;

    /// <summary>
    /// Whether this widget is about anything on this machine, right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A battery on a desktop is about nothing. Wi-Fi on a machine holding a
    /// cable is about nothing. A widget that says so gives its slot up and the
    /// bar closes over it; when the answer changes back it takes its slot
    /// again, or the first free one if somebody has since filled it.
    /// </para>
    /// <para>
    /// This is not the same as being empty. A reading with nothing to report
    /// yet still matters - it is about the processor, and the processor is
    /// still there. What is asked here is whether the subject exists.
    /// </para>
    /// </remarks>
    public virtual bool Matters => true;

    /// <summary>
    /// Raised when the widget asks for the settings window.
    /// </summary>
    /// <remarks>
    /// The bar owns the way in, not the widget: which screen asked and which
    /// widget was pointed at are the bar's to say. The widget only knocks.
    /// </remarks>
    public event EventHandler? SettingsWanted;

    /// <summary>Knocks on the settings window.</summary>
    protected void WantSettings() => SettingsWanted?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Whether this widget draws buttons of its own.
    /// </summary>
    /// <remarks>
    /// A pinned program is one button covering the whole chip, so the bar can
    /// take the pointer away from it to watch for a drag and hand the press
    /// back afterwards. A player is three buttons in a row, and one press
    /// handed back cannot say which of the three it was for - so on those the
    /// bar keeps its hands off, and the widget is dragged by the parts that
    /// are not buttons.
    /// </remarks>
    public virtual bool OwnButtons => false;

    /// <summary>
    /// Whether this widget could ever be about something on this machine.
    /// </summary>
    /// <remarks>
    /// Different from <see cref="Matters"/> by tense. A Wi-Fi widget on a
    /// laptop holding a cable is quiet and will speak again; a battery widget
    /// on a tower is quiet for ever. The first is worth a line saying so; the
    /// second would be that line on every ordinary day, which is the shape of
    /// notice nobody reads on the day it means something.
    /// </remarks>
    public virtual bool Possible => true;

    /// <summary>Called on the UI thread once a second while the dock is visible.</summary>
    public abstract void Tick(SensorSnapshot snapshot);

    /// <summary>
    /// What a press on this widget does, for widgets where that is anything.
    /// </summary>
    /// <remarks>
    /// Nothing, for a reading: a number is not a button. A pinned program
    /// starts; the player's own keys handle themselves, because a press on
    /// one of three buttons is not a press on the widget.
    /// </remarks>
    public virtual void Press()
    {
    }

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

    /// <summary>What the readings are called, as the person calls them.</summary>
    protected SensorNames Names => Context.Names;
}

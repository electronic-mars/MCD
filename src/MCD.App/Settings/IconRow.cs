using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Widgets;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Settings;

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

    /// <summary>What this reading is, so a row is not four letters and a button.</summary>
    public string Explain => Id switch
    {
        "cpu" => Loc.Tr("SenseCpuLoad", "How busy the processor is."),
        "ram" => Loc.Tr("SenseRamLoad", "How much of the memory is in use."),
        "up" => Loc.Tr("SenseUp", "What the machine is sending."),
        "down" => Loc.Tr("SenseDown", "What the machine is receiving."),
        "gpu" => Loc.Tr("SenseGpuLoad", "How busy the graphics chip is."),
        "temp" => Loc.Tr("SenseAnyTemp", "Every temperature widget, whichever part it watches."),
        _ => string.Empty,
    };

    public string Icon { get; private set; }

    /// <summary>The shape shown on this row's button.</summary>
    [ObservableProperty]
    public partial Geometry Shape { get; set; }

    /// <summary>
    /// The few icons the markup itself needs, so that a page can draw one
    /// without the code-behind reaching in to put it there.
    /// </summary>
    /// <remarks>
    /// x:Bind resolves a static property, which is the whole reason these
    /// exist. They come from the same set as everything else - the settings
    /// window draws no glyph from the system's font.
    /// </remarks>
    public static Geometry PageDocks => Draw("Computer");

    public static Geometry PageIcons => Draw("Star");

    public static Geometry PageWidgets => Draw("Layout");

    public static Geometry PageProgram => Draw("Info");

    /// <summary>
    /// The same four drawings again, for the pane.
    /// </summary>
    /// <remarks>
    /// Separate properties rather than the page ones used twice. A compiled
    /// binding evaluates a path once and hands the result to every target
    /// bound to it, and a Geometry has one owner - so the second Path given
    /// the same drawing threw, and the settings window did not open at all.
    /// </remarks>
    public static Geometry TabDocks => Draw("Computer");

    public static Geometry TabWidgets => Draw("Layout");

    public static Geometry TabPins => Draw("Rocket");

    public static Geometry TabPresets => Draw("Star");

    public static Geometry TabHide => Draw("Layout");

    public static Geometry TabLook => Draw("Brush");

    public static Geometry TabSensors => Draw("Pulse");

    public static Geometry TabProgram => Draw("Info");

    public static Geometry TabGeneral => Draw("Sliders");

    public static Geometry PageGeneral => Draw("Sliders");

    public static Geometry PagePins => Draw("Rocket");

    public static Geometry PagePresets => Draw("Star");

    public static Geometry PageLook => Draw("Brush");

    public static Geometry PageSensors => Draw("Pulse");

    public static Geometry Readings => Draw("Activity");

    public static Geometry Pinned => Draw("Rocket");

    public static Geometry Live => Draw("Pulse");

    public static Geometry Pencil => Draw("Pen");

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

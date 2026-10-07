using System.Collections.Immutable;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;

namespace Mcd.App.Widgets;

/// <summary>
/// One kind of widget: what it is called, what it does, and how to build one.
/// </summary>
public sealed record WidgetType(
    string TypeId,
    string Name,
    string Description,
    string Icon,
    Func<WidgetContext, WidgetConfig, WidgetViewModel> Build);

/// <summary>
/// One thing that can be put on a bar: a widget already configured to show it.
/// </summary>
/// <remarks>
/// Types and offers are different lists on purpose. A type is a kind of code -
/// the gauge, the temperature. An offer is a thing a person chooses - "CPU",
/// "Memory", "the drive's temperature" - and one type serves many offers. The
/// gallery, the bar's own menu and the drag-to-add all read this list.
/// </remarks>
/// <param name="Matches">
/// Whether an entry already on the bar shows this same thing, so the offer can
/// say so instead of quietly making a duplicate. Always false for things worth
/// having twice, like spacers.
/// </param>
/// <param name="Chooses">
/// Which of the choosable icons this offer wears, or null when it draws
/// something of its own. A gauge and a temperature are drawn with an icon
/// somebody picked; the clock draws digits and the player draws its own
/// buttons, and neither has an icon to change.
/// </param>
/// <param name="Category">
/// Which part of the machine it is about, for the gallery's headings: one
/// of <see cref="WidgetCatalog.Categories"/>.
/// </param>
/// <param name="Short">
/// The name under that heading, where the part is already said. Null means
/// the full name.
/// </param>
public sealed record WidgetOffer(
    string Name,
    string Description,
    string Icon,
    Func<WidgetConfig> Make,
    Func<WidgetConfig, bool> Matches,
    string? Chooses = null,
    string Category = "other",
    string? Short = null,
    WidgetSample? Sample = null);

/// <summary>
/// A picture of what a widget looks like on the bar, drawn once with ordinary
/// figures - not read from the machine - so a gallery of widgets shows what
/// each will look like and what it says, not just its name.
/// </summary>
/// <param name="Icons">The drawings, left to right.</param>
/// <param name="Figure">What it reads, as a typical day would have it.</param>
/// <param name="Label">The small name above the figure on a full-size bar.</param>
public sealed record WidgetSample(string[] Icons, string Figure = "", string Label = "")
{
    public static WidgetSample Of(string icon, string figure = "", string label = "") => new([icon], figure, label);
}

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
            GaugeWidget.Type,
            Loc.Tr("WidgetGaugeName", "Reading"),
            Loc.Tr("WidgetGaugeDescription", "One figure: how busy a part of the machine is."),
            "Activity",
            (context, entry) => new GaugeWidget(context, entry)),

        new(
            TempWidget.Type,
            Loc.Tr("WidgetTemperatureName", "Temperature"),
            Loc.Tr("WidgetTemperatureDescription", "Temperatures of the parts that report one."),
            "Temperature",
            (context, entry) => new TempWidget(context, entry)),

        new(
            IconWidget.Type,
            Loc.Tr("WidgetIconName", "Pinned icon"),
            Loc.Tr("WidgetIconDescription", "A program, folder or page, one press away."),
            "Rocket",
            (context, entry) => new IconWidget(context, entry)),

        new(
            SoundWidget.Type,
            Loc.Tr("WidgetSoundName", "Sound"),
            Loc.Tr("WidgetSoundDescription", "The speaker opens a volume slider and the wheel turns it; the figure silences the machine."),
            "Speaker",
            (context, entry) => new SoundWidget(context, entry)),

        new(
            MicWidget.Type,
            Loc.Tr("WidgetMicName", "Microphone"),
            Loc.Tr("WidgetMicDescription", "Switches the microphone off for the whole machine, and goes red while it is off."),
            "Mic",
            (context, entry) => new MicWidget(context, entry)),

        new(
            AwakeWidget.Type,
            Loc.Tr("WidgetAwakeName", "Keep awake"),
            Loc.Tr("WidgetAwakeDescription", "A full cup keeps the machine from sleeping and chat programs from turning amber."),
            "CoffeeOn",
            (context, entry) => new AwakeWidget(context, entry)),

        new(
            LayoutWidget.Type,
            Loc.Tr("WidgetLayoutName", "Keyboard layout"),
            Loc.Tr("WidgetLayoutDescription", "The language the keyboard types in, and whether Caps Lock is on."),
            "Keyboard",
            (context, entry) => new LayoutWidget(context, entry)),

        new(
            SwitcherWidget.Type,
            Loc.Tr("WidgetSwitcherName", "Audio Switcher"),
            Loc.Tr("WidgetSwitcherDescription", "Master Audio Switcher on the bar instead of in the tray: a press moves the sound to the next device."),
            "Headset",
            (context, entry) => new SwitcherWidget(context, entry)),

        new(
            BatteryWidget.Type,
            Loc.Tr("WidgetBatteryName", "Battery"),
            Loc.Tr("WidgetBatteryDescription", "How much charge is left. Only on a machine that runs on charge."),
            "Battery",
            (context, entry) => new BatteryWidget(context, entry)),

        new(
            WifiWidget.Type,
            Loc.Tr("WidgetWifiName", "Wi-Fi"),
            Loc.Tr("WidgetWifiDescription", "How good the signal is. Only while the machine is on a wireless network."),
            "WifiHigh",
            (context, entry) => new WifiWidget(context, entry)),

        new(
            SettingsWidget.Type,
            Loc.Tr("WidgetSettingsName", "Settings"),
            Loc.Tr("WidgetSettingsDescription", "Opens this window. The way in, for anybody who has not found the right click."),
            "Gear",
            (context, entry) => new SettingsWidget(context, entry)),

        new(
            ClockWidget.Type,
            Loc.Tr("WidgetClockName", "Clock"),
            Loc.Tr("WidgetClockDescription", "The time, and the date under it on a full-size bar."),
            "Clock",
            (context, entry) => new ClockWidget(context, entry)),

        new(
            MediaWidget.Type,
            Loc.Tr("WidgetMediaName", "Media"),
            Loc.Tr("WidgetMediaDescription", "Whatever is playing, with buttons for it. Hidden while nothing is."),
            "Music",
            (context, entry) => new MediaWidget(context, entry)),

        new(
            DeviceWidget.Type,
            Loc.Tr("WidgetDeviceName", "Device"),
            Loc.Tr("WidgetDeviceDescription", "How much charge a wireless mouse, headset or keyboard has left."),
            "Bluetooth",
            (context, entry) => new DeviceWidget(context, entry)),
    ];

    /// <summary>
    /// Everything that can be added to a bar right now: the five readings, a
    /// temperature per sensor worth watching, the player, a spacer.
    /// </summary>
    /// <remarks>
    /// The temperature offers list the prominent sensors - the per-part
    /// headline readings - rather than all of them; a processor's fourteen
    /// per-core sensors would drown the list. Any sensor at all can still be
    /// chosen on the widget itself once it is on the bar.
    /// </remarks>
    /// <summary>The gallery's headings, in the order they are shown, with their names.</summary>
    public static IReadOnlyList<(string Id, string Name)> Categories =>
    [
        ("cpu", Loc.Tr("CatProcessor", "Processor")),
        ("memory", Loc.Tr("CatMemory", "Memory")),
        ("gpu", Loc.Tr("CatGraphics", "Graphics")),
        ("storage", Loc.Tr("CatStorage", "Drives")),
        ("network", Loc.Tr("CatNetwork", "Network")),
        ("mouse", Loc.Tr("CatMice", "Mice")),
        ("headset", Loc.Tr("CatHeadsets", "Headsets")),
        ("keyboard", Loc.Tr("CatKeyboards", "Keyboards")),
        ("device", Loc.Tr("CatDevices", "Other devices")),
        ("apps", Loc.Tr("CatPrograms", "Programs")),
        ("other", Loc.Tr("CatOther", "Everything else")),
    ];

    /// <summary>The heading a reading belongs under, and what to call it there.</summary>
    private static (string Category, string Name, string Short) Placed(string readingId) => readingId switch
    {
        "cpu" => ("cpu", Loc.Tr("OfferCpuLoad", "Processor - load"), Loc.Tr("ShortLoad", "Load")),
        "ram" => ("memory", Loc.Tr("OfferRamLoad", "Memory - load"), Loc.Tr("ShortLoad", "Load")),
        "gpu" => ("gpu", Loc.Tr("OfferGpuLoad", "Graphics - load"), Loc.Tr("ShortLoad", "Load")),
        "up" => ("network", Loc.Tr("OfferNetUp", "Network - sending"), Loc.Tr("LabelSend", "Send")),
        "down" => ("network", Loc.Tr("OfferNetDown", "Network - receiving"), Loc.Tr("LabelReceive", "Receive")),
        _ => ("other", readingId, readingId),
    };

    private static string CategoryOf(HardwareGroup group) => group switch
    {
        HardwareGroup.Cpu => "cpu",
        HardwareGroup.Memory => "memory",
        HardwareGroup.Gpu => "gpu",
        HardwareGroup.Storage => "storage",
        HardwareGroup.Network => "network",
        _ => "other",
    };

    public static IEnumerable<WidgetOffer> Offers(SensorHub sensors)
    {
        foreach (GaugeReading reading in GaugeWidget.Known)
        {
            (string category, string name, string shortName) = Placed(reading.Id);

            yield return new WidgetOffer(
                name,
                Loc.Tr("OfferGauge", "A live figure on the bar."),
                reading.Icon,
                () => DockContents.Gauge(reading.Id),
                entry => entry.TypeId == GaugeWidget.Type
                    && (WidgetOptions.Text(entry.Config, "reading") ?? "cpu") == reading.Id
                    && WidgetOptions.Number(entry.Config, "temp") is not 1,
                Chooses: reading.Id,
                Category: category,
                Short: shortName,
                Sample: WidgetSample.Of(reading.Icon, TypicalFigure(reading.Id), reading.Label));

            // The load and the temperature of the same part in one chip, on
            // offer by itself: it was only a switch inside the load widget's
            // own settings, and nobody looks for a widget inside a widget.
            // Offered once the part has a thermometer to read.
            HardwareGroup? part = reading.Id switch
            {
                "cpu" => HardwareGroup.Cpu,
                "ram" => HardwareGroup.Memory,
                "gpu" => HardwareGroup.Gpu,
                _ => null,
            };

            if (part is { } group
                && sensors.Catalog.Any(d => d.Kind == SensorKind.Temperature && d.Prominent && d.Group == group))
            {
                yield return new WidgetOffer(
                    reading.Id switch
                    {
                        "cpu" => Loc.Tr("OfferCpuBoth", "Processor - load and temperature"),
                        "ram" => Loc.Tr("OfferRamBoth", "Memory - load and temperature"),
                        _ => Loc.Tr("OfferGpuBoth", "Graphics - load and temperature"),
                    },
                    Loc.Tr("OfferBoth", "Both figures side by side, under one icon."),
                    reading.Icon,
                    () => DockContents.Gauge(reading.Id) with
                    {
                        Config = WidgetJson.Object(("reading", reading.Id), ("temp", 1)),
                    },
                    entry => entry.TypeId == GaugeWidget.Type
                        && (WidgetOptions.Text(entry.Config, "reading") ?? "cpu") == reading.Id
                        && WidgetOptions.Number(entry.Config, "temp") is 1,
                    Chooses: reading.Id + "+temp",
                    Category: category,
                    Short: Loc.Tr("ShortBoth", "Load and temperature"),
                    Sample: WidgetSample.Of(reading.Icon, TypicalFigure(reading.Id) + "  " + TypicalTemperature(reading.Id), reading.Label));
            }
        }

        // Named the way the sensors page names them, not the way the firmware
        // does: a chip reading "PVC10 SK hynix 1024GB" tells nobody it is the
        // drive's temperature. Where two parts end up with the same plain
        // name - two drives - the model is put back on to tell them apart,
        // because that is the only thing that does.
        List<SensorDescriptor> parts =
        [
            .. sensors.Catalog
                .Where(d => d.Kind == SensorKind.Temperature && d.Prominent)
                .OrderBy(d => d.Group)
                .ThenBy(d => d.Label, StringComparer.CurrentCulture)
        ];

        var plain = parts.ToLookup(SensorNames.Plain);

        foreach (SensorDescriptor sensor in parts)
        {
            string key = sensor.Key.Value;
            string name = SensorNames.Plain(sensor);
            string detail = SensorNames.Detail(sensor);

            // Under its part's heading the chip says "Temperature"; where
            // two parts of a kind have one each - two drives - the model
            // tells them apart.
            bool twins = plain[name].Count() > 1 && detail.Length > 0;

            yield return new WidgetOffer(
                twins ? $"{name} — {detail}" : name,
                detail.Length > 0 ? detail : Loc.Tr("OfferSensor", "This part's temperature."),
                TempWidget.IconFor(sensor.Group),
                () => WidgetConfig.New(TempWidget.Type) with
                {
                    Config = WidgetJson.Object(("sensor", key)),
                },
                entry => entry.TypeId == TempWidget.Type
                    && WidgetOptions.Text(entry.Config, "sensor") == key,
                Chooses: "temp",
                Category: CategoryOf(sensor.Group),
                Short: twins
                    ? $"{Loc.Tr("ShortTemperature", "Temperature")} — {detail}"
                    : Loc.Tr("ShortTemperature", "Temperature"),
                Sample: WidgetSample.Of(TempWidget.IconFor(sensor.Group), "48 °C"));
        }

        // One chip per wireless device that reports a charge, under the kind
        // of device it is. The name is the device's own - under "Mice" the
        // chip says "Kone Air", which is the thing a person is choosing.
        foreach (SensorDescriptor device in sensors.Catalog
            .Where(d => d.Group == HardwareGroup.Peripheral)
            .OrderBy(d => d.Hardware, StringComparer.CurrentCulture))
        {
            string key = device.Key.Value;
            string family = Mcd.Sensors.Providers.DeviceBatteryProvider.FamilyOf(device.Key);

            yield return new WidgetOffer(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc.Tr("OfferDeviceCharge", "{0} - charge"),
                    device.Hardware),
                Loc.Tr("OfferDevice", "How much charge this device has left."),
                DeviceWidget.IconFor(device.Key),
                () => WidgetConfig.New(DeviceWidget.Type) with
                {
                    Config = WidgetJson.Object(("sensor", key)),
                },
                entry => entry.TypeId == DeviceWidget.Type
                    && WidgetOptions.Text(entry.Config, "sensor") == key,
                Category: family == "other" ? "device" : family,
                Short: device.Hardware,
                Sample: WidgetSample.Of(DeviceWidget.IconFor(device.Key), "82 %"));
        }

        yield return new WidgetOffer(
            Loc.Tr("WidgetSoundName", "Sound"),
            Loc.Tr("WidgetSoundDescription", "The speaker opens a volume slider and the wheel turns it; the figure silences the machine."),
            "Speaker",
            () => WidgetConfig.New(SoundWidget.Type),
            entry => entry.TypeId == SoundWidget.Type,
            Sample: WidgetSample.Of("Speaker", "42 %"));

        yield return new WidgetOffer(
            Loc.Tr("WidgetMicName", "Microphone"),
            Loc.Tr("WidgetMicDescription", "Switches the microphone off for the whole machine, and goes red while it is off."),
            "Mic",
            () => WidgetConfig.New(MicWidget.Type),
            entry => entry.TypeId == MicWidget.Type,
            Sample: WidgetSample.Of("Mic"));

        yield return new WidgetOffer(
            Loc.Tr("WidgetAwakeName", "Keep awake"),
            Loc.Tr("WidgetAwakeDescription", "A full cup keeps the machine from sleeping and chat programs from turning amber."),
            "CoffeeOn",
            () => WidgetConfig.New(AwakeWidget.Type),
            entry => entry.TypeId == AwakeWidget.Type,
            Sample: WidgetSample.Of("CoffeeOn"));

        yield return new WidgetOffer(
            Loc.Tr("WidgetLayoutName", "Keyboard layout"),
            Loc.Tr("WidgetLayoutDescription", "The language the keyboard types in, and whether Caps Lock is on."),
            "Keyboard",
            () => WidgetConfig.New(LayoutWidget.Type),
            entry => entry.TypeId == LayoutWidget.Type,
            Sample: new WidgetSample([], "EN"));

        // What Windows brings with it, under its own heading, each drawn in
        // this program's icons and changed like any pinned icon.
        foreach (StandardPrograms.Program program in StandardPrograms.All.Where(p => p.Present))
        {
            yield return new WidgetOffer(
                program.Name,
                Loc.Tr("OfferProgram", "Starts it from the bar."),
                program.Icon,
                program.Pin,
                program.Is,
                Category: "apps",
                Sample: WidgetSample.Of(program.Icon));
        }

        // Only while the program is running: the note it leaves is the one
        // sign it is there, and a chip for a program that is not would be
        // an empty place on the bar.
        if (AudioSwitcher.Present())
        {
            yield return new WidgetOffer(
                Loc.Tr("WidgetSwitcherName", "Audio Switcher"),
                Loc.Tr("WidgetSwitcherDescription", "Master Audio Switcher on the bar instead of in the tray: a press moves the sound to the next device."),
                "Headset",
                () => WidgetConfig.New(SwitcherWidget.Type),
                entry => entry.TypeId == SwitcherWidget.Type,
                Sample: WidgetSample.Of("Headset"));
        }

        // Offered only where they are about something. A desktop has no
        // battery and is never going to grow one, and a machine with no
        // wireless card cannot join a wireless network: putting either in the
        // gallery would be offering a widget that lands on the bar, draws
        // nothing, and cannot be dragged off what it is not on.
        if (Power.Read().Present)
        {
            yield return new WidgetOffer(
                Loc.Tr("WidgetBatteryName", "Battery"),
                Loc.Tr("WidgetBatteryDescription", "How much charge is left. Only on a machine that runs on charge."),
                "Battery",
                () => WidgetConfig.New(BatteryWidget.Type),
                entry => entry.TypeId == BatteryWidget.Type,
                Sample: WidgetSample.Of("Battery", "100 %"));
        }

        if (Wireless.Fitted())
        {
            yield return new WidgetOffer(
                Loc.Tr("WidgetWifiName", "Wi-Fi"),
                Loc.Tr("WidgetWifiDescription", "How good the signal is. Only while the machine is on a wireless network."),
                "WifiHigh",
                () => WidgetConfig.New(WifiWidget.Type),
                entry => entry.TypeId == WifiWidget.Type,
                Sample: WidgetSample.Of("WifiHigh"));
        }

        yield return new WidgetOffer(
            Loc.Tr("WidgetSettingsName", "Settings"),
            Loc.Tr("WidgetSettingsDescription", "Opens this window. The way in, for anybody who has not found the right click."),
            "Gear",
            () => WidgetConfig.New(SettingsWidget.Type),
            entry => entry.TypeId == SettingsWidget.Type,
            Sample: WidgetSample.Of("Gear"));

        yield return new WidgetOffer(
            Loc.Tr("WidgetClockName", "Clock"),
            Loc.Tr("WidgetClockDescription", "The time, and the date under it on a full-size bar."),
            "Clock",
            () => WidgetConfig.New(ClockWidget.Type),
            entry => entry.TypeId == ClockWidget.Type,
            Sample: new WidgetSample([], "12:34", "Tue 6 Oct"));

        yield return new WidgetOffer(
            Loc.Tr("WidgetMediaName", "Media"),
            Loc.Tr("WidgetMediaDescription", "Whatever is playing, with buttons for it. Hidden while nothing is."),
            "Music",
            () => WidgetConfig.New(MediaWidget.Type),
            entry => entry.TypeId == MediaWidget.Type,
            Sample: new WidgetSample(["Previous", "Play", "Next"]));
    }

    /// <summary>What a reading typically says, for the gallery pictures.</summary>
    private static string TypicalFigure(string readingId) => readingId switch
    {
        "cpu" => "23 %",
        "ram" => "41 %",
        "gpu" => "18 %",
        "up" => "180 kB/s",
        _ => "2.4 MB/s",
    };

    private static string TypicalTemperature(string readingId) => readingId switch
    {
        "cpu" => "48 °C",
        "ram" => "38 °C",
        _ => "52 °C",
    };

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

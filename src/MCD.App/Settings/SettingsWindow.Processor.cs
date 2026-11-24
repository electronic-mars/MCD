using System.Globalization;
using Mcd.App.Widgets;
using Mcd.Core.Processor;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Settings;

/// <summary>
/// The Processor page: the three profiles, what the two editable ones change,
/// and the programs with a kind of core or a profile of their own.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>The frequency ceilings offered, in MHz, highest first.</summary>
    private static readonly int[] Ceilings = [5500, 5000, 4500, 4000, 3500, 3000, 2500, 2000, 1500, 1000];

    private static (string Id, string Name)[] ProfileNames() =>
    [
        (CpuProfile.Quiet, Loc.Tr("CpuProfileQuiet", "Quiet")),
        (CpuProfile.Normal, Loc.Tr("CpuProfileNormal", "Normal")),
        (CpuProfile.Max, Loc.Tr("CpuProfileMax", "Maximum")),
    ];

    private void ShowProcessor()
    {
        Braun.Theme = Root.ActualTheme;
        ProcessorBody.Children.Clear();

        ProcessorSettings chosen = _settings.Current.Processor;
        CpuControl? cpu = CpuControl.Current;
        bool hybrid = Cores.Here.Hybrid;
        (string Id, string Name)[] profiles = ProfileNames();

        // What these settings reach, said before any of them: they outlive
        // the app, and a wrong one is felt as heat, noise or a slow machine.
        ProcessorBody.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false,
            Title = Loc.Tr("CpuWarningTitle", "These settings change the Windows power plan"),
            Message = Loc.Tr(
                "CpuWarning",
                "They stay in force when the app is closed. Lifting a frequency limit lets the processor run as hot as it can: louder fans, slowing down from the heat, stutters. Turning the turbo off, or keeping a program to the E-cores, makes it slower. If something feels wrong, restore the defaults below. Choose Normal before uninstalling the app."),
        });

        ProcessorBody.Children.Add(Braun.Heading("Cpu", Loc.Tr("CpuProfilesGroup", "Profile")));

        // What is in force says why, when it is not simply what was chosen.
        string note = cpu?.Because is { } program
            ? string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("CpuProfileBecause", "While {0} runs, its own profile is in force."),
                program)
            : cpu?.Refusing == true
                ? Loc.Tr("CpuProfileRefused", "Windows did not accept every setting of this profile.")
                : Loc.Tr("CpuProfileNowHint", "Normal leaves the power plan as Windows has it. A profile stays in force when the app is closed.");

        // Following the power source is offered only where there is one.
        bool battery = Power.Read().Present;
        string[] follow = [Loc.Tr("CpuFollowNone", "Leave as it is"), .. profiles.Select(p => p.Name)];

        ProcessorBody.Children.Add(Braun.Group(
            Braun.Row(
                Loc.Tr("CpuProfileNow", "Processor profile"),
                note,
                Braun.Segs(
                    [.. profiles.Select(p => p.Name)],
                    Array.FindIndex(profiles, p => p.Id == chosen.Profile),
                    i => ChangeCpu(
                        c => c with { Profile = profiles[i].Id },
                        Loc.Tr("UndoCpuProfile", "switching the processor profile")))),
            battery
                ? Braun.Row(
                    Loc.Tr("CpuOnBattery", "On battery"),
                    Loc.Tr("CpuOnBatteryHint", "Switches to this profile when the charger is unplugged."),
                    Braun.Choice(
                        follow,
                        Array.FindIndex(profiles, p => p.Id == chosen.OnBattery) + 1,
                        i => ChangeCpu(
                            c => c with { OnBattery = i == 0 ? string.Empty : profiles[i - 1].Id },
                            Loc.Tr("UndoCpuFollow", "changing when the profile switches"))))
                : null,
            battery
                ? Braun.Row(
                    Loc.Tr("CpuOnMains", "Plugged in"),
                    Loc.Tr("CpuOnMainsHint", "Switches to this profile when the charger is plugged in."),
                    Braun.Choice(
                        follow,
                        Array.FindIndex(profiles, p => p.Id == chosen.OnMains) + 1,
                        i => ChangeCpu(
                            c => c with { OnMains = i == 0 ? string.Empty : profiles[i - 1].Id },
                            Loc.Tr("UndoCpuFollow", "changing when the profile switches"))))
                : null,
            Braun.Row(
                Loc.Tr("CpuResetRow", "Default processor settings"),
                Loc.Tr(
                    "CpuResetHint",
                    "Normal, the power plan as it was, the profiles as shipped and no program rules. Press Ctrl+Z to undo."),
                Braun.Action(
                    Loc.Tr("CpuResetButton", "Restore defaults"),
                    () => ChangeCpu(_ => new ProcessorSettings(), Loc.Tr("UndoCpuReset", "restoring the processor defaults")),
                    "Undo"))));

        ProfileEditor(
            Loc.Tr("CpuProfileQuiet", "Quiet"),
            Loc.Tr("CpuQuietHint", "Cooler and quieter, slower at its peak."),
            chosen.Quiet,
            hybrid,
            values => ChangeCpu(c => c with { Quiet = values }, Loc.Tr("UndoCpuEdit", "changing a processor profile")));

        ProfileEditor(
            Loc.Tr("CpuProfileMax", "Maximum"),
            Loc.Tr("CpuMaxSoftHint", "Windows' best performance mode, within the limits the laptop's maker set."),
            chosen.Max,
            hybrid,
            values => ChangeCpu(c => c with { Max = values }, Loc.Tr("UndoCpuEdit", "changing a processor profile")));

        ShowPrograms(chosen, cpu, hybrid, profiles);
    }

    /// <summary>What one profile changes, each line with "as Windows has it" first.</summary>
    private void ProfileEditor(string name, string hint, CpuProfile values, bool hybrid, Action<CpuProfile> set)
    {
        ProcessorBody.Children.Add(Braun.Heading("Sliders", name));

        string asIs = Loc.Tr("CpuAsWindows", "As Windows has it");

        string[] modes = [CpuProfile.Efficiency, CpuProfile.Balanced, CpuProfile.Performance];
        string[] modeNames =
        [
            asIs,
            Loc.Tr("CpuModeEfficiency", "Best power efficiency"),
            Loc.Tr("CpuModeBalanced", "Balanced"),
            Loc.Tr("CpuModePerformance", "Best performance"),
        ];

        // On is the turbo as Windows ordinarily runs it (1), not the keenest
        // setting (2), which the maker's own plan does not use either.
        int[] boosts = [0, 1];
        string[] boostNames = [asIs, Loc.Tr("CpuBoostOff", "Off"), Loc.Tr("CpuBoostOn", "On")];

        ProcessorBody.Children.Add(Braun.Group(
            Braun.Row(name, hint, null),
            Braun.Row(
                Loc.Tr("CpuMode", "Windows power mode"),
                null,
                Braun.Choice(
                    modeNames,
                    Array.IndexOf(modes, values.Mode) + 1,
                    i => set(values with { Mode = i == 0 ? string.Empty : modes[i - 1] }))),
            Braun.Row(
                Loc.Tr("CpuBoost", "Turbo boost"),
                Loc.Tr("CpuBoostHint", "Off keeps the processor at its base speed: the coolest and the slowest."),
                Braun.Choice(
                    boostNames,
                    values.Boost switch { 0 => 1, > 0 => 2, _ => 0 },
                    i => set(values with { Boost = i == 0 ? -1 : boosts[i - 1] }))),
            Ceiling(
                hybrid ? Loc.Tr("CpuMaxP", "P-cores up to") : Loc.Tr("CpuMaxAll", "Cores up to"),
                Loc.Tr("CpuCeilingHint", "No limit lifts the ceiling the laptop's maker set: hotter, louder, and slowed by the heat."),
                values.MaxP,
                v => set(values with { MaxP = v })),
            hybrid
                ? Ceiling(Loc.Tr("CpuMaxE", "E-cores up to"), null, values.MaxE, v => set(values with { MaxE = v }))
                : null));
    }

    /// <summary>A frequency ceiling: as Windows has it, none, or one of the offered speeds.</summary>
    private static Grid Ceiling(string name, string? hint, int value, Action<int> set)
    {
        // A value from the file that is not one of the offered ones is
        // offered too, rather than shown as nothing.
        int[] speeds = [.. Ceilings.Append(value).Where(v => v > 0).Distinct().OrderByDescending(v => v)];
        int[] all = [-1, 0, .. speeds];

        string[] names =
        [
            Loc.Tr("CpuAsWindows", "As Windows has it"),
            Loc.Tr("CpuNoLimit", "No limit"),
            .. speeds.Select(v => Readable.Value(v / 1000.0, "GHz")),
        ];

        return Braun.Row(name, hint, Braun.Choice(names, Array.IndexOf(all, value), i => set(all[i])));
    }

    private void ShowPrograms(ProcessorSettings chosen, CpuControl? cpu, bool hybrid, (string Id, string Name)[] profiles)
    {
        ProcessorBody.Children.Add(Braun.Heading("Rocket", Loc.Tr("CpuProgramsGroup", "Programs")));

        var rows = new List<FrameworkElement?>
        {
            Braun.Row(
                Loc.Tr("CpuProgramAdd", "Add a program"),
                hybrid
                    ? Loc.Tr("CpuProgramAddHint", "Keep a program to the P- or E-cores, or switch the profile while it runs.")
                    : Loc.Tr("CpuProgramAddHintOneKind", "Switch the profile while a program runs."),
                Braun.Action(Loc.Tr("CpuProgramAddButton", "Choose a program…"), () => _ = PickProgram(), "Plus")),
        };

        string[] kinds = ["", "p", "e"];
        string[] kindNames =
        [
            Loc.Tr("CpuCoresAny", "Any cores"),
            Loc.Tr("CpuCoresP", "P-cores only"),
            Loc.Tr("CpuCoresE", "E-cores only"),
        ];

        string[] profileNames = [Loc.Tr("CpuProgramNoProfile", "Profile unchanged"), .. profiles.Select(p => p.Name)];

        // Each list as wide as its longest choice, so the rows line up
        // whichever choice each one shows.
        double kindWidth = ChoiceWidth(kindNames);
        double profileWidth = ChoiceWidth(profileNames);

        foreach (ProgramRule rule in chosen.Programs)
        {
            ProgramRule current = rule;

            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            if (hybrid)
            {
                ComboBox cores = Braun.Choice(
                    kindNames,
                    Array.IndexOf(kinds, current.Cores),
                    i => ChangeRule(current, current with { Cores = kinds[i] }));
                cores.MinWidth = 0;
                cores.Width = kindWidth;
                keys.Children.Add(cores);
            }

            ComboBox profile = Braun.Choice(
                profileNames,
                Array.FindIndex(profiles, p => p.Id == current.Profile) + 1,
                i => ChangeRule(current, current with { Profile = i == 0 ? string.Empty : profiles[i - 1].Id }));
            profile.MinWidth = 0;
            profile.Width = profileWidth;
            keys.Children.Add(profile);

            keys.Children.Add(Braun.Action(
                Loc.Tr("CpuProgramRemove", "Remove"),
                () => ChangeCpu(
                    c => c with { Programs = c.Programs.Remove(current) },
                    string.Format(CultureInfo.CurrentCulture, Loc.Tr("UndoCpuProgramRemoved", "removing {0}"), current.Program)),
                "Delete"));

            string? refused = cpu?.Refused.Contains(current.Program) == true
                ? Loc.Tr("CpuProgramRefused", "Windows did not allow changing its cores: it may run as administrator or be protected by anti-cheat.")
                : null;

            rows.Add(Braun.Row(current.Program, refused, keys, stack: true));
        }

        ProcessorBody.Children.Add(Braun.Group([.. rows]));
    }

    /// <summary>The width of a list that holds the longest of its choices whole.</summary>
    private static double ChoiceWidth(IEnumerable<string> labels)
    {
        // A new ruler each time, as Braun's own: an element kept in a static
        // field outlives the window it was made under.
        var ruler = new TextBlock { FontSize = 14 };
        double widest = 0;

        foreach (string label in labels)
        {
            ruler.Text = label;
            ruler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, ruler.DesiredSize.Width);
        }

        // The list's padding either side, its chevron and its border:
        // 62 measured on a rendered row, rounded up.
        return Math.Ceiling(widest) + 64;
    }

    /// <summary>Asks for a program file and adds a rule for it.</summary>
    private async Task PickProgram()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            picker.FileTypeFilter.Add(".exe");

            if (await picker.PickSingleFileAsync() is not { } file)
            {
                return;
            }

            string program = file.Name;

            if (_settings.Current.Processor.Programs.Any(r => string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // The commonest wish first: a game kept off the slow cores.
            ChangeCpu(
                c => c with
                {
                    Programs = c.Programs.Add(new ProgramRule { Program = program, Cores = Cores.Here.Hybrid ? "p" : string.Empty }),
                },
                string.Format(CultureInfo.CurrentCulture, Loc.Tr("UndoCpuProgramAdded", "adding {0}"), program));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings.processor could not open the file picker");
        }
    }

    private void ChangeRule(ProgramRule was, ProgramRule now) =>
        ChangeCpu(
            c => c with { Programs = c.Programs.Replace(was, now) },
            string.Format(CultureInfo.CurrentCulture, Loc.Tr("UndoCpuProgramChanged", "changing {0}"), now.Program));

    private void ChangeCpu(Func<ProcessorSettings, ProcessorSettings> change, string what)
    {
        SettingsModel current = _settings.Current;
        ProcessorSettings next = change(current.Processor);

        if (next != current.Processor)
        {
            Write(current with { Processor = next }, WriteReason.UserAction, what);
            _log.LogInformation("settings.processor {What}", what);
        }

        ShowProcessor();
    }
}

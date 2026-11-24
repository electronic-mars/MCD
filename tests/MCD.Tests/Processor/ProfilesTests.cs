using Mcd.Core.Processor;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Mcd.Tests.Processor;

/// <summary>
/// A profile goes into the power plan and comes out again leaving the plan
/// exactly as it was found - the one promise that matters, because the plan
/// outlives the program.
/// </summary>
public sealed class ProfilesTests
{
    private const string Here = "THIS-PC";

    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>A plan in memory, set up the way an Alienware m18 R2 ships.</summary>
    private sealed class FakePlan : IPowerPlan
    {
        public Guid Scheme = Balanced;
        public Guid CurrentMode = PowerPlan.Balanced;
        public bool Modes = true;
        public bool Locked;
        public int Applied;
        public readonly Dictionary<(Guid, Guid, bool), uint> Values = [];

        public FakePlan()
        {
            foreach (bool mains in new[] { true, false })
            {
                Values[(Balanced, PowerPlan.Boost, mains)] = 4;
                Values[(Balanced, PowerPlan.MaxFrequency, mains)] = 3600;
                Values[(Balanced, PowerPlan.MaxFrequencyFast, mains)] = 3800;
                Values[(HighPerformance, PowerPlan.Boost, mains)] = 2;
                Values[(HighPerformance, PowerPlan.MaxFrequency, mains)] = 0;
                Values[(HighPerformance, PowerPlan.MaxFrequencyFast, mains)] = 0;
            }
        }

        public Guid? Active() => Scheme;

        public uint? Read(Guid scheme, Guid setting, bool mains) =>
            Values.TryGetValue((scheme, setting, mains), out uint v) ? v : null;

        public bool Write(Guid scheme, Guid setting, bool mains, uint value)
        {
            if (Locked)
            {
                return false;
            }

            Values[(scheme, setting, mains)] = value;
            return true;
        }

        public bool Apply(Guid scheme)
        {
            Applied++;
            return true;
        }

        public Guid? Mode() => Modes ? CurrentMode : null;

        public bool SetMode(Guid mode)
        {
            if (!Modes)
            {
                return false;
            }

            CurrentMode = mode;
            return true;
        }

        public Dictionary<(Guid, Guid, bool), uint> Copy() => new(Values);
    }

    private static readonly CpuProfile Quiet = new ProcessorSettings().Quiet;
    private static readonly CpuProfile Max = new ProcessorSettings().Max;

    /// <summary>A profile with every value set, the ceilings lifted: the hardest one to take out again.</summary>
    private static readonly CpuProfile Hot = new() { Mode = CpuProfile.Performance, Boost = 2, MaxP = 0, MaxE = 0 };

    [Fact]
    public void NormalAfterQuietPutsEveryValueBackAndForgetsThem()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        ProfileResult quiet = Profiles.Apply(plan, Quiet, hybrid: true, new PlanBackup(), Here);

        quiet.Done.ShouldBeTrue();
        plan.Values[(Balanced, PowerPlan.Boost, true)].ShouldBe(0u);
        plan.Values[(Balanced, PowerPlan.Boost, false)].ShouldBe(0u);
        plan.CurrentMode.ShouldBe(PowerPlan.Efficiency);
        plan.Applied.ShouldBe(1);

        ProfileResult normal = Profiles.Apply(plan, null, hybrid: true, quiet.Before, Here);

        normal.Done.ShouldBeTrue();
        plan.Values.ShouldBe(original);
        plan.CurrentMode.ShouldBe(PowerPlan.Balanced);
        normal.Before.Values.ShouldBeEmpty();
    }

    [Fact]
    public void GoingFromOneProfileToAnotherKeepsTheFirstOriginalsNotTheFirstProfile()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        PlanBackup before = Profiles.Apply(plan, Quiet, hybrid: true, new PlanBackup(), Here).Before;
        before = Profiles.Apply(plan, Hot, hybrid: true, before, Here).Before;

        plan.Values[(Balanced, PowerPlan.Boost, true)].ShouldBe(2u);
        plan.Values[(Balanced, PowerPlan.MaxFrequencyFast, true)].ShouldBe(0u);
        plan.Values[(Balanced, PowerPlan.MaxFrequency, false)].ShouldBe(0u);

        Profiles.Apply(plan, null, hybrid: true, before, Here);

        plan.Values.ShouldBe(original);
        plan.CurrentMode.ShouldBe(PowerPlan.Balanced);
    }

    [Fact]
    public void WhatTheNextProfileLeavesAloneGoesBackAtOnce()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        PlanBackup before = Profiles.Apply(plan, Hot, hybrid: true, new PlanBackup(), Here).Before;

        // Quiet sets no ceilings, so the lifted ones are taken out on the way in.
        Profiles.Apply(plan, Quiet, hybrid: true, before, Here);

        plan.Values[(Balanced, PowerPlan.MaxFrequencyFast, true)].ShouldBe(original[(Balanced, PowerPlan.MaxFrequencyFast, true)]);
        plan.Values[(Balanced, PowerPlan.MaxFrequency, false)].ShouldBe(original[(Balanced, PowerPlan.MaxFrequency, false)]);
    }

    [Fact]
    public void WithOneKindOfCoreThePerformanceCeilingIsThePlansOnlyOne()
    {
        var plan = new FakePlan();
        CpuProfile capped = Quiet with { MaxP = 2500, MaxE = 1000 };

        Profiles.Apply(plan, capped, hybrid: false, new PlanBackup(), Here);

        plan.Values[(Balanced, PowerPlan.MaxFrequency, true)].ShouldBe(2500u);
        plan.Values[(Balanced, PowerPlan.MaxFrequencyFast, true)].ShouldBe(3800u);
    }

    [Fact]
    public void ARecordFromAnotherComputerIsNeverPutBack()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        var carried = new PlanBackup
        {
            Machine = "OTHER-PC",
            Values = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add($"{Balanced:D}/{PowerPlan.MaxFrequencyFast:D}/ac", "1200"),
        };

        ProfileResult normal = Profiles.Apply(plan, null, hybrid: true, carried, Here);

        plan.Values.ShouldBe(original);
        normal.Before.Values.ShouldBeEmpty();
    }

    [Fact]
    public void APlanSwitchedInBetweenGetsItsOwnValuesBack()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        PlanBackup before = Profiles.Apply(plan, Quiet, hybrid: true, new PlanBackup(), Here).Before;

        // Somebody chose another plan in the Control Panel meanwhile.
        plan.Scheme = HighPerformance;
        Profiles.Apply(plan, null, hybrid: true, before, Here);

        plan.Values.ShouldBe(original);
    }

    [Fact]
    public void ALockedPlanIsReportedAndItsOriginalsAreKeptForLater()
    {
        var plan = new FakePlan();
        PlanBackup before = Profiles.Apply(plan, Quiet, hybrid: true, new PlanBackup(), Here).Before;

        plan.Locked = true;
        ProfileResult normal = Profiles.Apply(plan, null, hybrid: true, before, Here);

        normal.Done.ShouldBeFalse();
        normal.Before.Values.Count.ShouldBe(before.Values.Count - 1);
        normal.Before.Values.ShouldNotContainKey("mode");
    }

    [Fact]
    public void NoPowerModesIsNotAFailureOfThePlan()
    {
        var plan = new FakePlan { Modes = false };

        ProfileResult quiet = Profiles.Apply(plan, Quiet, hybrid: true, new PlanBackup(), Here);

        quiet.Done.ShouldBeTrue();
        quiet.ModeRefused.ShouldBeTrue();
        quiet.Before.Values.ShouldNotContainKey("mode");
    }

    [Fact]
    public void MaximumOnlyChoosesThePowerModeAndLeavesTheMakersLimits()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        ProfileResult max = Profiles.Apply(plan, Max, hybrid: true, new PlanBackup(), Here);

        max.Done.ShouldBeTrue();
        plan.Values.ShouldBe(original);
        plan.Applied.ShouldBe(0);
        plan.CurrentMode.ShouldBe(PowerPlan.Performance);
    }

    [Fact]
    public void TheSoftMaximumAfterTheHotOnePutsTheLimitsBackAndKeepsOnlyTheMode()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        // A plan left as 0.1.24's Maximum left it, then the soft one.
        PlanBackup before = Profiles.Apply(plan, Hot, hybrid: true, new PlanBackup(), Here).Before;
        before = Profiles.Apply(plan, Max, hybrid: true, before, Here).Before;

        plan.Values.ShouldBe(original);
        plan.CurrentMode.ShouldBe(PowerPlan.Performance);
        before.Values.Keys.ShouldBe(["mode"]);

        Profiles.Apply(plan, null, hybrid: true, before, Here);

        plan.CurrentMode.ShouldBe(PowerPlan.Balanced);
    }

    [Fact]
    public void NormalWithNothingRecordedTouchesNothing()
    {
        var plan = new FakePlan();
        var original = plan.Copy();

        ProfileResult normal = Profiles.Apply(plan, null, hybrid: true, new PlanBackup(), Here);

        normal.Done.ShouldBeTrue();
        plan.Values.ShouldBe(original);
        plan.Applied.ShouldBe(0);
    }

    [Fact]
    public void TheUninstallersPutBackLeavesThePlanAsFoundAndTheRecordEmpty()
    {
        var plan = new FakePlan();
        Dictionary<(Guid, Guid, bool), uint> original = plan.Copy();
        string dir = Path.Combine(Path.GetTempPath(), "mcd-putback-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var settings = new SettingsService(NullLogger<SettingsService>.Instance, dir))
            {
                PlanBackup before = Profiles.Apply(plan, Hot, hybrid: true, new PlanBackup(), Environment.MachineName).Before;
                settings.Commit(settings.Current with { PlanBefore = before }, WriteReason.PowerPlan);

                CpuControl.PutBack(settings, NullLogger.Instance, plan).ShouldBeTrue();
            }

            plan.Values.ShouldBe(original);
            plan.CurrentMode.ShouldBe(PowerPlan.Balanced);

            using var reread = new SettingsService(NullLogger<SettingsService>.Instance, dir);
            reread.Current.PlanBefore.Values.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

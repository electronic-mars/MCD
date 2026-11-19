using System.Collections.Immutable;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Microsoft.Extensions.Logging;

namespace Mcd.Core.Processor;

/// <summary>
/// Keeps the processor the way the settings say: the profile in force, and
/// each listed program on its kind of core.
/// </summary>
/// <remarks>
/// <para>
/// Works from what Windows offers any program: the power plan, which every
/// user may change, and a running program's CPU sets, which any program of
/// the same person may set. No driver, no service, no administrator - so the
/// same in the Store's build as in the installer's.
/// </para>
/// <para>
/// Asleep while there is nothing to watch. With programs listed or a profile
/// for each power source, it looks every two seconds: a new process is named
/// once, and nothing else is asked of it.
/// </para>
/// </remarks>
public sealed class CpuControl : IDisposable
{
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(2);

    private readonly ILogger _log;
    private readonly SettingsService _settings;
    private readonly IPowerPlan _plan;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly EventHandler<SettingsModel> _onChanged;

    /// <summary>The profile last put into the plan, and what it said then.</summary>
    private (string Id, CpuProfile? Values)? _applied;

    /// <summary>The power source last seen, while a profile depends on it.</summary>
    private bool? _mains;

    /// <summary>Processes a rule matched, in the order they were found.</summary>
    private readonly Dictionary<uint, (ProgramRule Rule, long Order)> _matched = [];

    /// <summary>Processes already looked at, matched or not.</summary>
    private readonly HashSet<uint> _seen = [];

    private ImmutableArray<ProgramRule> _rules = [];
    private long _order;
    private bool _disposed;

    public CpuControl(ILogger log, SettingsService settings, IPowerPlan? plan = null)
    {
        _log = log;
        _settings = settings;
        _plan = plan ?? new WindowsPlan();
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _onChanged = (_, _) => Wake();
        _settings.Changed += _onChanged;

        Current = this;
        Wake();
    }

    /// <summary>The one the program runs, for the bar's widgets to ask.</summary>
    public static CpuControl? Current { get; private set; }

    /// <summary>The profile or the refusals changed. Raised on a worker thread.</summary>
    public event Action? Changed;

    /// <summary>The profile chosen by hand, or by the power source.</summary>
    public string Chosen => _settings.Current.Processor.Profile;

    /// <summary>The profile in force: a running program's, or the chosen one.</summary>
    public string InForce { get; private set; } = CpuProfile.Normal;

    /// <summary>The program whose profile is in force, while one is.</summary>
    public string? Because { get; private set; }

    /// <summary>Whether Windows refused part of the last profile.</summary>
    public bool Refusing { get; private set; }

    /// <summary>Programs Windows would not keep to their cores, by file name.</summary>
    public ImmutableHashSet<string> Refused { get; private set; } =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts a profile in force, as a person choosing it.</summary>
    public void Choose(string profile, string what)
    {
        SettingsModel current = _settings.Current;

        if (current.Processor.Profile != profile)
        {
            _settings.Commit(
                current with { Processor = current.Processor with { Profile = profile } },
                WriteReason.UserAction,
                what);
        }
    }

    private void Wake()
    {
        if (!_disposed)
        {
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void Tick()
    {
        bool watching;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ProcessorSettings chosen = _settings.Current.Processor;

                FollowPower(chosen);
                WatchPrograms(chosen.Programs);
                Reconcile();
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "cpu.control a round failed");
            }

            ProcessorSettings now = _settings.Current.Processor;
            watching = now.Programs.Length > 0 || _matched.Count > 0
                || now.OnBattery.Length > 0 || now.OnMains.Length > 0;
        }

        if (watching && !_disposed)
        {
            _timer.Change(Beat, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Switches the profile when the machine is plugged in or unplugged.</summary>
    private void FollowPower(ProcessorSettings chosen)
    {
        if (chosen.OnBattery.Length == 0 && chosen.OnMains.Length == 0)
        {
            _mains = null;
            return;
        }

        bool mains = Power.Read().Plugged;

        // The first look only notes where things stand: the profile chosen
        // by hand is kept until the power source actually changes.
        if (_mains is { } was && was != mains)
        {
            string next = mains ? chosen.OnMains : chosen.OnBattery;

            if (next.Length > 0 && next != chosen.Profile)
            {
                SettingsModel current = _settings.Current;
                _settings.Commit(
                    current with { Processor = current.Processor with { Profile = next } },
                    WriteReason.PowerPlan);

                _log.LogInformation("cpu.control {Source}: profile {Profile}", mains ? "mains" : "battery", next);
            }
        }

        _mains = mains;
    }

    private void WatchPrograms(ImmutableArray<ProgramRule> rules)
    {
        if (!rules.SequenceEqual(_rules))
        {
            // Every process looked at again under the new rules - and any a
            // rule kept to some cores let go first, unless it is still kept to
            // the same ones.
            foreach ((uint id, (ProgramRule rule, _)) in _matched)
            {
                if (rule.Cores.Length > 0
                    && Find(rules, rule.Program)?.Cores != rule.Cores
                    && Cores.Keep(id, []))
                {
                    _log.LogInformation("cpu.cores {Program} ({Id}) let go", rule.Program, id);
                }
            }

            _matched.Clear();
            _seen.Clear();
            Refused = Refused.Clear();
            _rules = rules;
        }

        if (rules.IsEmpty)
        {
            return;
        }

        uint[] running = Cores.Running();
        var alive = running.ToHashSet();

        _seen.RemoveWhere(id => !alive.Contains(id));

        foreach (uint gone in _matched.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            _matched.Remove(gone);
        }

        uint self = (uint)Environment.ProcessId;

        foreach (uint id in running)
        {
            if (id == self || !_seen.Add(id) || Cores.FileOf(id) is not { } file || Find(rules, file) is not { } rule)
            {
                continue;
            }

            _matched[id] = (rule, ++_order);

            if (rule.Cores is "p" or "e" && Cores.Here.Hybrid)
            {
                uint[] sets = [.. (rule.Cores == "p" ? Cores.Here.Performance : Cores.Here.Efficient).Select(p => p.CpuSet)];

                if (Cores.Keep(id, sets))
                {
                    _log.LogInformation("cpu.cores {Program} ({Id}) kept to the {Kind} cores", file, id, rule.Cores);
                    Refused = Refused.Remove(rule.Program);
                }
                else
                {
                    _log.LogWarning("cpu.cores {Program} ({Id}) refused by Windows", file, id);
                    Refused = Refused.Add(rule.Program);
                    Changed?.Invoke();
                }
            }
        }
    }

    private static ProgramRule? Find(ImmutableArray<ProgramRule> rules, string file) =>
        rules.FirstOrDefault(r => string.Equals(r.Program, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts the profile that should be in force into the plan, when it is not already.</summary>
    private void Reconcile()
    {
        ProcessorSettings chosen = _settings.Current.Processor;

        // The newest running program with a profile of its own wins over the
        // chosen one, for as long as it runs.
        (ProgramRule Rule, long Order)? program = _matched.Values
            .Where(m => m.Rule.Profile.Length > 0)
            .OrderByDescending(m => m.Order)
            .Select(m => ((ProgramRule, long)?)m)
            .FirstOrDefault();

        string id = program?.Rule.Profile ?? chosen.Profile;
        CpuProfile? values = Values(chosen, id);

        if (_applied is { } last && last.Id == id && last.Values == values)
        {
            return;
        }

        SettingsModel current = _settings.Current;
        ProfileResult result = Profiles.Apply(_plan, values, Cores.Here.Hybrid, current.PlanBefore, Environment.MachineName);

        if (SettingsKeys(result.Before) != SettingsKeys(current.PlanBefore))
        {
            _settings.Commit(current with { PlanBefore = result.Before }, WriteReason.PowerPlan);
        }

        _applied = (id, values);
        InForce = id;
        Because = program?.Rule.Program;
        Refusing = !result.Done;

        _log.LogInformation(
            "cpu.profile {Profile}{Because} done={Done} mode-refused={Mode}",
            id,
            Because is null ? string.Empty : " for " + Because,
            result.Done,
            result.ModeRefused);

        Changed?.Invoke();
    }

    private static CpuProfile? Values(ProcessorSettings chosen, string id) => id switch
    {
        CpuProfile.Quiet => chosen.Quiet,
        CpuProfile.Max => chosen.Max,
        _ => null,
    };

    private static string SettingsKeys(PlanBackup backup) =>
        backup.Machine + "|" + string.Join(";", backup.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    /// <summary>
    /// Stops watching. A profile a running program put in force gives way to
    /// the chosen one first: the plan is not left as a game wanted it once
    /// nothing is left to take it back.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settings.Changed -= _onChanged;
            _timer.Dispose();

            if (Because is not null)
            {
                try
                {
                    _matched.Clear();
                    Reconcile();
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "cpu.control could not put the chosen profile back");
                }
            }

            if (Current == this)
            {
                Current = null;
            }
        }
    }
}

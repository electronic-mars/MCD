using System.Collections.Immutable;
using System.Globalization;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;

namespace Mcd.Core.Processor;

/// <summary>The power plan as the profiles see it: the real one, or a test's.</summary>
public interface IPowerPlan
{
    Guid? Active();

    uint? Read(Guid scheme, Guid setting, bool mains);

    bool Write(Guid scheme, Guid setting, bool mains, uint value);

    bool Apply(Guid scheme);

    Guid? Mode();

    bool SetMode(Guid mode);
}

/// <summary>The power plan Windows has.</summary>
public sealed class WindowsPlan : IPowerPlan
{
    public Guid? Active() => PowerPlan.Active();

    public uint? Read(Guid scheme, Guid setting, bool mains) => PowerPlan.Read(scheme, setting, mains);

    public bool Write(Guid scheme, Guid setting, bool mains, uint value) =>
        PowerPlan.Write(scheme, setting, mains, value);

    public bool Apply(Guid scheme) => PowerPlan.Apply(scheme);

    public Guid? Mode() => PowerPlan.Mode();

    public bool SetMode(Guid mode) => PowerPlan.SetMode(mode);
}

/// <summary>What putting a profile into the plan came to.</summary>
/// <param name="Before">What the plan had before, as it must be remembered from now on.</param>
/// <param name="Done">Whether Windows took every value of the plan.</param>
/// <param name="ModeRefused">
/// Whether the power mode could not be set - which is the ordinary state of
/// affairs under any plan but Balanced, where Windows offers no modes at all.
/// </param>
public sealed record ProfileResult(PlanBackup Before, bool Done, bool ModeRefused);

/// <summary>
/// Puts a profile into the power plan, and takes it out again.
/// </summary>
/// <remarks>
/// <para>
/// Every value is read before it is first written and kept, so that Normal
/// is the plan exactly as it was - whoever set it, the maker's utility
/// included - and not some default of Windows' own. Going from one profile to
/// another puts back what the second one leaves alone, so nothing a profile
/// once set outlives it.
/// </para>
/// <para>
/// The values go to both the mains and the battery side of the plan: a
/// profile is what the processor does now, plugged in or not. Switching by
/// the power source is the profile changing, not the plan doing it.
/// </para>
/// </remarks>
public static class Profiles
{
    private const string ModeKey = "mode";

    /// <summary>
    /// Puts <paramref name="profile"/> into the plan in force; null takes every
    /// change out again.
    /// </summary>
    /// <param name="hybrid">
    /// Whether the processor has two kinds of core. With one kind, the
    /// performance cores' ceiling is the only one, and it is the plan's
    /// ordinary maximum frequency.
    /// </param>
    /// <param name="machine">This computer's name: a record made on another one is not used.</param>
    public static ProfileResult Apply(
        IPowerPlan plan, CpuProfile? profile, bool hybrid, PlanBackup before, string machine)
    {
        ImmutableDictionary<string, string>.Builder kept =
            (before.Machine == machine ? before.Values : ImmutableDictionary<string, string>.Empty).ToBuilder();

        bool done = true;
        Guid? active = plan.Active();
        var wanted = new Dictionary<string, uint>();

        if (profile is not null)
        {
            if (active is not { } scheme)
            {
                done = false;
            }
            else
            {
                void Want(Guid setting, int value)
                {
                    if (value >= 0)
                    {
                        wanted[Key(scheme, setting, mains: true)] = (uint)value;
                        wanted[Key(scheme, setting, mains: false)] = (uint)value;
                    }
                }

                Want(PowerPlan.Boost, profile.Boost);

                if (hybrid)
                {
                    Want(PowerPlan.MaxFrequencyFast, profile.MaxP);
                    Want(PowerPlan.MaxFrequency, profile.MaxE);
                }
                else
                {
                    Want(PowerPlan.MaxFrequency, profile.MaxP);
                }
            }
        }

        var touched = new HashSet<Guid>();

        // Back first: whatever an earlier profile changed and this one leaves
        // alone - in whichever plan it was changed in, which need not be the
        // one in force any more.
        foreach ((string key, string value) in kept.Where(p => p.Key != ModeKey && !wanted.ContainsKey(p.Key)).ToList())
        {
            if (Parse(key) is not { } at || !uint.TryParse(value, CultureInfo.InvariantCulture, out uint original))
            {
                kept.Remove(key);
                continue;
            }

            if (plan.Write(at.Scheme, at.Setting, at.Mains, original))
            {
                kept.Remove(key);
                touched.Add(at.Scheme);
            }
            else
            {
                done = false;
            }
        }

        foreach ((string key, uint value) in wanted)
        {
            (Guid scheme, Guid setting, bool mains) = Parse(key)!.Value;

            if (!kept.ContainsKey(key))
            {
                // Not written if it could not be read: a value nobody can put
                // back is not a value to change.
                if (plan.Read(scheme, setting, mains) is not { } original)
                {
                    done = false;
                    continue;
                }

                kept[key] = original.ToString(CultureInfo.InvariantCulture);
            }

            if (plan.Write(scheme, setting, mains, value))
            {
                touched.Add(scheme);
            }
            else
            {
                done = false;
            }
        }

        if (active is { } now && touched.Contains(now))
        {
            done &= plan.Apply(now);
        }

        // The power mode last: applying the plan again must not be what
        // decides it.
        bool modeRefused = false;

        if (ModeOf(profile?.Mode) is { } mode)
        {
            bool fresh = !kept.ContainsKey(ModeKey);

            if (fresh && plan.Mode() is { } had)
            {
                kept[ModeKey] = had.ToString("D");
            }

            if (!plan.SetMode(mode))
            {
                modeRefused = true;

                if (fresh)
                {
                    kept.Remove(ModeKey);
                }
            }
        }
        else if (kept.TryGetValue(ModeKey, out string? had))
        {
            if (!Guid.TryParse(had, out Guid back) || plan.SetMode(back))
            {
                kept.Remove(ModeKey);
            }
            else
            {
                modeRefused = true;
            }
        }

        return new ProfileResult(
            new PlanBackup { Machine = kept.Count > 0 ? machine : string.Empty, Values = kept.ToImmutable() },
            done,
            modeRefused);
    }

    private static Guid? ModeOf(string? mode) => mode switch
    {
        CpuProfile.Efficiency => PowerPlan.Efficiency,
        CpuProfile.Balanced => PowerPlan.Balanced,
        CpuProfile.Performance => PowerPlan.Performance,
        _ => null,
    };

    private static string Key(Guid scheme, Guid setting, bool mains) =>
        $"{scheme:D}/{setting:D}/{(mains ? "ac" : "dc")}";

    private static (Guid Scheme, Guid Setting, bool Mains)? Parse(string key)
    {
        string[] parts = key.Split('/');

        return parts.Length == 3
            && Guid.TryParse(parts[0], out Guid scheme)
            && Guid.TryParse(parts[1], out Guid setting)
            && parts[2] is "ac" or "dc"
                ? (scheme, setting, parts[2] == "ac")
                : null;
    }
}

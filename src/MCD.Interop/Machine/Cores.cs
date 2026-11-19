using System.Collections.Immutable;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.System.Threading;

namespace Mcd.Interop.Machine;

/// <summary>One logical processor, as the scheduler knows it.</summary>
/// <param name="CpuSet">The id a program's CPU sets name it by.</param>
/// <param name="Group">The processor group; one on any machine with 64 threads or fewer.</param>
/// <param name="Index">Its number within the group: the "3" of "0,3" in the performance counters.</param>
/// <param name="Class">
/// How fast a kind of core it is. Higher is faster; on a processor with one
/// kind of core every one of them is 0.
/// </param>
public readonly record struct LogicalProcessor(uint CpuSet, ushort Group, byte Index, byte Class)
{
    /// <summary>The name the performance counters give it: "0,3".</summary>
    public string Instance => $"{Group},{Index}";
}

/// <summary>
/// Which logical processors are performance cores and which are efficient
/// ones, and keeping a program to one kind.
/// </summary>
/// <remarks>
/// <para>
/// Windows says which kind each core is through the efficiency class of its
/// CPU sets - the same answer the scheduler itself works from - for Intel's
/// P- and E-cores and for AMD's Zen and Zen c cores alike. Nothing here asks
/// the processor's maker.
/// </para>
/// <para>
/// A processor with three kinds (the low-power E-cores of some laptop chips)
/// is split the same way: the fastest kind is "P", everything else "E".
/// </para>
/// </remarks>
public sealed class Cores
{
    private Cores(ImmutableArray<LogicalProcessor> all)
    {
        All = all;

        byte fastest = all.Length > 0 ? all.Max(p => p.Class) : (byte)0;
        Performance = [.. all.Where(p => p.Class == fastest)];
        Efficient = [.. all.Where(p => p.Class != fastest)];
    }

    public ImmutableArray<LogicalProcessor> All { get; }

    /// <summary>The fastest kind of core; every core on a processor with one kind.</summary>
    public ImmutableArray<LogicalProcessor> Performance { get; }

    /// <summary>Every slower kind; empty on a processor with one kind.</summary>
    public ImmutableArray<LogicalProcessor> Efficient { get; }

    /// <summary>Whether this processor has more than one kind of core.</summary>
    public bool Hybrid => Performance.Length > 0 && Efficient.Length > 0;

    /// <summary>This machine's processors, asked once: they do not change while it runs.</summary>
    public static Cores Here => HereLazy.Value;

    private static readonly Lazy<Cores> HereLazy = new(Read);

    /// <summary>For tests: a machine described rather than asked.</summary>
    public static Cores Of(IEnumerable<LogicalProcessor> all) => new([.. all]);

    private static unsafe Cores Read()
    {
        uint length = 0;
        PInvoke.GetSystemCpuSetInformation(null, 0, &length, HANDLE.Null, 0);

        if (length == 0)
        {
            return new Cores([]);
        }

        byte[] buffer = new byte[length];
        var found = ImmutableArray.CreateBuilder<LogicalProcessor>();

        fixed (byte* start = buffer)
        {
            if (!PInvoke.GetSystemCpuSetInformation((SYSTEM_CPU_SET_INFORMATION*)start, length, &length, HANDLE.Null, 0))
            {
                return new Cores([]);
            }

            // Entries of their own declared size, one after another: the
            // structure has grown before and may again.
            for (uint at = 0; at + sizeof(uint) * 2 <= length;)
            {
                var entry = (SYSTEM_CPU_SET_INFORMATION*)(start + at);

                if (entry->Size == 0)
                {
                    break;
                }

                if (entry->Type == CPU_SET_INFORMATION_TYPE.CpuSetInformation)
                {
                    var set = entry->Anonymous.CpuSet;
                    found.Add(new LogicalProcessor(set.Id, set.Group, set.LogicalProcessorIndex, set.EfficiencyClass));
                }

                at += entry->Size;
            }
        }

        return new Cores(found.ToImmutable());
    }

    /// <summary>
    /// Keeps a running program to the given processors, or lets it use all of
    /// them again when the list is empty.
    /// </summary>
    /// <returns>
    /// False when Windows would not allow it: a program running as
    /// administrator, a protected one, or one whose anti-cheat guards it.
    /// </returns>
    /// <remarks>
    /// CPU sets rather than an affinity mask. They take effect at once for
    /// the threads already running - measured: a busy thread moved over in
    /// under a second and came back the moment the list was cleared - and
    /// they need only the limited "set information" right, which Windows
    /// gives for some processes that refuse the full one.
    /// </remarks>
    public static unsafe bool Keep(uint processId, ReadOnlySpan<uint> cpuSets)
    {
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_SET_LIMITED_INFORMATION, false, processId);

        if (process.IsNull)
        {
            return false;
        }

        try
        {
            fixed (uint* ids = cpuSets)
            {
                return PInvoke.SetProcessDefaultCpuSets(process, cpuSets.IsEmpty ? null : ids, (uint)cpuSets.Length);
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>Every process running now, by id.</summary>
    public static unsafe uint[] Running()
    {
        uint[] ids = new uint[1024];

        while (true)
        {
            uint used = 0;

            fixed (uint* start = ids)
            {
                if (!PInvoke.K32EnumProcesses(start, (uint)(ids.Length * sizeof(uint)), &used))
                {
                    return [];
                }
            }

            // A full buffer may have been too small; the call cannot say.
            if (used < ids.Length * sizeof(uint))
            {
                return ids[..(int)(used / sizeof(uint))];
            }

            ids = new uint[ids.Length * 2];
        }
    }

    /// <summary>
    /// The program file a process was started from, without its folder:
    /// "game.exe". Null for the ones Windows will not name, such as the
    /// system's own.
    /// </summary>
    public static unsafe string? FileOf(uint processId)
    {
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (process.IsNull)
        {
            return null;
        }

        try
        {
            char* path = stackalloc char[1024];
            uint size = 1024;

            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(path), &size)
                ? Path.GetFileName(new string(path, 0, (int)size))
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}

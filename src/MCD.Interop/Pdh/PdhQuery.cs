using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Performance;

namespace Mcd.Interop.Pdh;

/// <summary>
/// A performance-counter query.
/// </summary>
/// <remarks>
/// Counters are added by their English path through <c>PdhAddEnglishCounterW</c>.
/// <c>PdhAddCounter</c> wants the localised name instead, so on a Russian
/// Windows the path "\Processor Information(_Total)\% Processor Utility"
/// resolves to nothing - and the dock would show a dash for every user outside
/// an English locale, on a machine where nothing looks broken.
/// </remarks>
public sealed unsafe class PdhQuery : IDisposable
{
    private const uint Success = 0;
    private const uint MoreData = 0x800007D2;

    private readonly PdhCloseQuerySafeHandle _query;
    private readonly Dictionary<string, Counter> _counters = [];
    private bool _primed;

    private PdhQuery(PdhCloseQuerySafeHandle query) => _query = query;

    /// <summary>Null when the performance counter service is unavailable.</summary>
    public static PdhQuery? TryOpen() =>
        PInvoke.PdhOpenQuery(null, 0, out PdhCloseQuerySafeHandle handle) == Success
            ? new PdhQuery(handle)
            : null;

    /// <summary>
    /// Adds a counter by English path. False when this machine has no such
    /// counter - a desktop with no discrete graphics has no GPU Engine object,
    /// and that is not an error.
    /// </summary>
    /// <param name="summed">
    /// True for a wildcard path such as <c>\GPU Engine(*)\Utilization
    /// Percentage</c>, whose instances are added together. Windows reports GPU
    /// and network activity per engine and per adapter; a single figure is what
    /// a dock has room for.
    /// </param>
    public bool TryAdd(string name, string englishPath, bool summed = false)
    {
        if (PInvoke.PdhAddEnglishCounter(_query, englishPath, 0, out PDH_HCOUNTER counter) != Success)
        {
            return false;
        }

        _counters[name] = new Counter(counter, summed);
        return true;
    }

    /// <summary>
    /// Reads every counter that has a value.
    /// </summary>
    /// <remarks>
    /// Rate counters are the difference between two samples, so the first call
    /// after opening returns an empty result rather than a fabricated zero. A
    /// widget reading "0%" for its first second looks like a broken widget;
    /// reading nothing says "not yet", which is the truth.
    /// </remarks>
    public IReadOnlyDictionary<string, double> Collect()
    {
        var values = new Dictionary<string, double>(_counters.Count);

        if (PInvoke.PdhCollectQueryData(new PDH_HQUERY(_query.DangerousGetHandle())) != Success)
        {
            return values;
        }

        if (!_primed)
        {
            _primed = true;
            return values;
        }

        foreach ((string name, Counter counter) in _counters)
        {
            if (counter.Summed)
            {
                if (TrySum(counter.Handle, out double total))
                {
                    values[name] = total;
                }
            }
            else if (PInvoke.PdhGetFormattedCounterValue(
                         counter.Handle, PDH_FMT.PDH_FMT_DOUBLE, out PDH_FMT_COUNTERVALUE value) == Success)
            {
                values[name] = value.Anonymous.doubleValue;
            }
        }

        return values;
    }

    public void Dispose() => _query.Dispose();

    /// <summary>Adds up every instance a wildcard counter matched.</summary>
    private static bool TrySum(PDH_HCOUNTER counter, out double total)
    {
        total = 0;

        uint bytes = 0;
        uint items = 0;

        // The first call is only ever asked for the size; it is expected to fail
        // with PDH_MORE_DATA.
        uint status = PInvoke.PdhGetFormattedCounterArray(
            counter, PDH_FMT.PDH_FMT_DOUBLE, &bytes, &items, null);

        if (status != MoreData || bytes == 0 || items == 0)
        {
            return false;
        }

        nint buffer = Marshal.AllocHGlobal((int)bytes);

        try
        {
            status = PInvoke.PdhGetFormattedCounterArray(
                counter, PDH_FMT.PDH_FMT_DOUBLE, &bytes, &items,
                (PDH_FMT_COUNTERVALUE_ITEM_W*)buffer);

            if (status != Success)
            {
                return false;
            }

            var span = new ReadOnlySpan<PDH_FMT_COUNTERVALUE_ITEM_W>((void*)buffer, (int)items);

            foreach (PDH_FMT_COUNTERVALUE_ITEM_W item in span)
            {
                double value = item.FmtValue.Anonymous.doubleValue;

                if (!double.IsNaN(value) && !double.IsInfinity(value))
                {
                    total += value;
                }
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private readonly record struct Counter(PDH_HCOUNTER Handle, bool Summed);
}

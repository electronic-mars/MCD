namespace Mcd.Interop;

/// <summary>
/// The switch behind the recordings in the READMEs and on the site: MCD_DEMO=1.
/// </summary>
/// <remarks>
/// <para>
/// A recording must not show the machine it was made on, and must not change
/// it. So with the switch on, every reading is made up - each one rests at 21
/// and takes its turn going 75, 100, 75 - the brightness is kept here instead of being
/// asked of the screens (the volume and the microphone, in Mcd.Audio.Demo),
/// and the program sees one small screen in the corner of the primary one
/// rather than the real desk.
/// </para>
/// <para>
/// Only 21, 75 and 100, never anything between: 75 is where a reading turns
/// amber and 100 where it turns red and wears its warning, and a recording is
/// there to show both; a figure caught on its way between them shows neither.
/// </para>
/// </remarks>
public static class Demo
{
    public static bool On { get; } = Environment.GetEnvironmentVariable("MCD_DEMO") == "1";

    public const double Rest = 21;

    public const double Warm = 75;

    public const double Peak = 100;

    /// <summary>The made-up screen, in physical pixels, from the primary screen's top left.</summary>
    public const int ScreenWidth = 1600;

    public const int ScreenHeight = 900;

    /// <summary>How many groups of readings take turns at the peak: see <see cref="Group"/>.</summary>
    private const int Turns = 4;

    /// <summary>
    /// Seconds each turn lasts: a second each at 75, 100 and 75, then rest.
    /// </summary>
    /// <remarks>
    /// The rest is long on purpose. A pair of readings that reaches three
    /// digits takes a slot more of the bar, and the bar closes an open slider
    /// when it regrows; the recordings open their sliders in the quiet part.
    /// </remarks>
    private const double Slot = 8;

    /// <summary>In the hour of history: a peak this long, once in this many seconds.</summary>
    private const int PastPeak = 20;

    /// <summary>And either side of it, this long at 75.</summary>
    private const int PastShoulder = 20;

    private const int PastEvery = 600;

    /// <summary>
    /// What a reading shows at a moment: the part it measures decides its
    /// turn, so the cores of one processor rise together and the next part
    /// rises after.
    /// </summary>
    public static double Value(string key, DateTimeOffset at)
    {
        double turns = at.ToUnixTimeMilliseconds() / 1000.0 / Slot;
        long turn = (long)Math.Floor(turns) % Turns;

        if (Group(key) != turn)
        {
            return Rest;
        }

        return (int)((turns - Math.Floor(turns)) * Slot) switch
        {
            0 or 2 => Warm,
            1 => Peak,
            _ => Rest,
        };
    }

    /// <summary>
    /// What the history of a reading shows, seconds ago: flat at 21 with a
    /// peak every ten minutes, each group at its own minute, 75 either side. The live turns,
    /// one every few seconds, would draw an hour of solid colour.
    /// </summary>
    public static double Past(string key, int secondsAgo)
    {
        int offset = Group(key) * (PastEvery / Turns);
        int into = (secondsAgo + offset) % PastEvery;

        return into < PastShoulder ? Warm
            : into < PastShoulder + PastPeak ? Peak
            : into < (2 * PastShoulder) + PastPeak ? Warm
            : Rest;
    }

    /// <summary>
    /// Which turn a reading takes, by the part it measures: the processor,
    /// then the graphics, then the memory, then everything else together -
    /// the order the default bar shows them in, so a recording sees the rise
    /// travel along it.
    /// </summary>
    private static int Group(string key)
    {
        int end = key.IndexOf('/', key.IndexOf('/') + 1);
        string part = end > 0 ? key[..end] : key;

        for (int turn = 0; turn < Parts.Length; turn++)
        {
            if (Parts[turn].Any(name => part.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return turn;
            }
        }

        return Parts.Length;
    }

    private static readonly string[][] Parts = [["cpu", "msr"], ["gpu", "nvml"], ["ram", "mem", "dimm"]];

    /// <summary>The screens' brightness while the switch is on.</summary>
    public static Dictionary<string, int> Screens { get; } = new()
    {
        [@"\\.\DISPLAY1"] = (int)Rest,
        [@"\\.\DISPLAY2"] = (int)Rest,
    };

    /// <summary>
    /// A settings folder of its own and the English interface, before anything
    /// reads either. Only where the person running it has not chosen.
    /// </summary>
    public static void Prepare()
    {
        if (!On)
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MCD_DATA_DIR")))
        {
            Environment.SetEnvironmentVariable(
                "MCD_DATA_DIR",
                Path.Combine(Path.GetTempPath(), "mcd-demo-" + Guid.NewGuid().ToString("n")[..8]));
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MCD_LANG")))
        {
            Environment.SetEnvironmentVariable("MCD_LANG", "en-US");
        }
    }
}

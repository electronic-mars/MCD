namespace Mcd.Sensors.Contracts;

/// <summary>What a source needs before it can be used at all.</summary>
public enum Tier
{
    /// <summary>Windows itself. Always there, no rights needed, nothing to install.</summary>
    Platform,

    /// <summary>A user-mode library that shipped with the graphics driver.</summary>
    Vendor,

    /// <summary>
    /// Something the user installed separately, such as HWiNFO or
    /// LibreHardwareMonitor. Off until they say otherwise, and the program is
    /// complete without it.
    /// </summary>
    External,

    /// <summary>
    /// A kernel driver the person installs once - PawnIO. The program carries
    /// the modules and the arithmetic; the driver carries the rights.
    /// </summary>
    Driver,
}

/// <summary>
/// One source of readings.
/// </summary>
/// <remarks>
/// Every method runs on the hub's own thread and may block. None of them are
/// async on purpose: the hub already owns a thread for this, and making the
/// contract async would only invite a provider to be called from somewhere else.
/// Several of these sources are not thread-safe and one of them holds COM.
/// </remarks>
public interface ISensorProvider : IDisposable
{
    /// <summary>Short, stable, and the first segment of every key this provider makes.</summary>
    string Id { get; }

    Tier Tier { get; }

    /// <summary>How often this source is worth asking.</summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Cheap check for whether this source exists on this machine. Must have no
    /// side effects: it is called again periodically to notice a source that has
    /// come back.
    /// </summary>
    bool IsAvailable();

    /// <summary>What this source can measure. Called after it becomes available.</summary>
    IReadOnlyList<SensorDescriptor> Discover();

    /// <summary>
    /// Reads current values into the buffer. Returning nothing is not a failure -
    /// rate counters have no value until they have been sampled twice.
    /// </summary>
    void Poll(IDictionary<SensorKey, double> into);
}

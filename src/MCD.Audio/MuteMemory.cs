namespace Mcd.Audio;

/// <summary>
/// What the machine was last switched to, for one kind of device.
/// </summary>
/// <remarks>
/// <para>
/// The switch belongs to the machine, not to a device, so it has to outlive
/// the device it was thrown on. Headphones muted, then switched off: the
/// speakers take over, and are unmuted. Then the headphones come back, and
/// they are still muted, because nothing can reach a device that is not
/// plugged in. Taking the state of whatever is in use as the truth made the
/// stale headphones mute the speakers again, and the person who had just
/// unmuted found the sound off. The truth is what the person last did, and
/// every device is brought to it as it comes back.
/// </para>
/// <para>
/// Only a change on the device in use counts as somebody throwing the switch.
/// Moving to another device does not: its state is whatever it was left in.
/// </para>
/// </remarks>
public sealed class MuteMemory
{
    private readonly Lock _gate = new();
    private bool? _now;
    private bool? _wanted;

    /// <summary>What the machine was last switched to, or null before anything is known.</summary>
    public bool? Wanted
    {
        get
        {
            lock (_gate)
            {
                return _wanted;
            }
        }
    }

    /// <summary>
    /// The device in use has just been seen in this state. True when that is
    /// the switch being thrown, in which case it is what the machine is now
    /// switched to.
    /// </summary>
    public bool Thrown(bool muted)
    {
        lock (_gate)
        {
            bool? was = _now;
            _now = muted;

            if (was is not { } before || before == muted)
            {
                return false;
            }

            _wanted = muted;
            return true;
        }
    }

    /// <summary>
    /// A different device is in use now, in this state (null: none at all).
    /// That is not the switch being thrown; the very first state seen is the
    /// one the machine starts from.
    /// </summary>
    public void Followed(bool? muted)
    {
        lock (_gate)
        {
            _now = muted;

            if (muted is { } state)
            {
                _wanted ??= state;
            }
        }
    }
}

using Mcd.Audio;
using Shouldly;

namespace Mcd.Tests.Machine;

/// <summary>
/// The mute switch belongs to the machine and has to outlive the device it was
/// thrown on. The case that broke it: a device that was unplugged when the
/// switch was last thrown comes back with the state it had then.
/// </summary>
public sealed class MuteMemoryTests
{
    [Fact]
    public void TheFirstStateSeenIsWhereTheMachineStarts()
    {
        var memory = new MuteMemory();

        memory.Wanted.ShouldBeNull();

        memory.Followed(false);

        memory.Wanted.ShouldBe(false);
    }

    [Fact]
    public void ThrowingTheSwitchOnTheDeviceInUseIsWhatTheMachineIsSwitchedTo()
    {
        var memory = new MuteMemory();
        memory.Followed(false);

        memory.Thrown(true).ShouldBeTrue();
        memory.Wanted.ShouldBe(true);

        memory.Thrown(false).ShouldBeTrue();
        memory.Wanted.ShouldBe(false);
    }

    [Fact]
    public void BeingToldWhatItAlreadyIsIsNotThrowingTheSwitch()
    {
        var memory = new MuteMemory();
        memory.Followed(true);

        memory.Thrown(true).ShouldBeFalse();
        memory.Wanted.ShouldBe(true);
    }

    [Fact]
    public void MovingToAnotherDeviceIsNotThrowingTheSwitch()
    {
        var memory = new MuteMemory();
        memory.Followed(false);

        // A device that was left muted becomes the one in use.
        memory.Followed(true);

        memory.Wanted.ShouldBe(false);
    }

    [Fact]
    public void ADeviceThatCameBackStaleDoesNotUndoWhatThePersonDid()
    {
        var memory = new MuteMemory();

        // Headphones in use, sound on.
        memory.Followed(false);

        // Muted; the speakers are muted with them.
        memory.Thrown(true).ShouldBeTrue();

        // The headphones switch off: the speakers are in use, muted.
        memory.Followed(true);

        // The person unmutes the speakers.
        memory.Thrown(false).ShouldBeTrue();
        memory.Wanted.ShouldBe(false);

        // The headphones come back, still muted: nothing could reach them
        // while they were away. What the machine is switched to is unchanged,
        // so they are brought to it rather than muting everything again.
        memory.Followed(true);

        memory.Wanted.ShouldBe(false);
    }

    [Fact]
    public void AfterTheDeviceInUseGoesAwayNothingIsSwitched()
    {
        var memory = new MuteMemory();
        memory.Followed(true);

        memory.Followed(null);

        memory.Wanted.ShouldBe(true);

        // And the next one that appears is moved to, not switched.
        memory.Thrown(false).ShouldBeFalse();
    }
}

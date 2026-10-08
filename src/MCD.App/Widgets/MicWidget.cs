using Mcd.Audio;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.App.Widgets;

/// <summary>
/// The microphone: one press and nobody hears you, in any program.
/// </summary>
/// <remarks>
/// <para>
/// Every conferencing program has a mute button and each has its own, in a
/// window that is usually behind something else. This one is the machine's:
/// it is on the bar however the call is arranged, and it is red while the
/// microphone is off, because "why can nobody hear me" is worth answering
/// at a glance.
/// </para>
/// <para>
/// Hidden on a machine with nothing to record with, like the speaker on one
/// with nothing to play through.
/// </para>
/// </remarks>
public sealed class MicWidget(WidgetContext context, WidgetConfig entry)
    : GlyphWidget(context, entry)
{
    public const string Type = "mcd.mic";

    public override string TypeId => Type;

    private bool? _said;

    public override void Tick(SensorSnapshot snapshot)
    {
        if (Microphone.Muted() is not { } muted)
        {
            Vanish();
            return;
        }

        // Written down when it changes, so that "the bar did not show it" can
        // be laid beside Master Audio Switcher's own log of the same minute.
        if (_said != muted)
        {
            _said = muted;
            Context.Log.LogInformation("mic.muted {Muted}", muted);
        }

        Draw(
            muted ? "MicOff" : "Mic",
            faded: 1,
            alert: muted,
            muted
                ? Loc.Tr("MicOffTip", "Microphone off · a press turns it on")
                : Loc.Tr("MicOnTip", "Microphone on · a press turns it off"));
    }

    /// <summary>
    /// Read back rather than assumed: a conferencing program may have changed
    /// it since the last tick, and a button that toggles what it last saw
    /// gets out of step and stays there.
    /// </summary>
    public override void Press()
    {
        if (Microphone.Muted() is { } muted)
        {
            Microphone.Mute(!muted);
        }

        Tick(SensorSnapshot.Empty);
    }

    public override string Summarise() => Loc.Tr("WidgetMicName", "Microphone");
}

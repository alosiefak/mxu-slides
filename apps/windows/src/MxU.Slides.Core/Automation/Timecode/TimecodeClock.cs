using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// Turns a stream of timecode readings into a steady clock. It locks after three readings
/// that follow on from each other, rides out short dropouts ("freewheel": counting on by
/// itself), and holds the last position once the timecode has stopped. A jump needs two
/// readings that agree before the clock follows it, so one bad frame can't move the show.
/// Port of TimecodeClock in PresenterCore/Timeline/TimecodeFollow.swift.
/// </summary>
public sealed class TimecodeClock
{
    public enum State { NoInput, Locking, Locked, Freewheel, Stopped }

    /// <summary>The clock at an instant; <see cref="Position"/> is in seconds of timecode.</summary>
    public sealed record Reading(State State, double? Position = null, TimecodeLabel? Label = null, TimecodeRate? Rate = null, bool Reverse = false)
    {
        /// <summary>Locked and running forwards: the only state the show follows.</summary>
        public bool Live => State == State.Locked && !Reverse;
    }

    /// <summary>Readings in a row needed to lock.</summary>
    public const int LockFrames = 3;

    /// <summary>Seconds of silence before the clock calls it a dropout and freewheels.</summary>
    public const double Dropout = 0.15;

    /// <summary>Seconds it freewheels before calling the timecode stopped.</summary>
    public const double Freewheel = 0.5;

    private TimecodeHeard? anchor;

    /// <summary>A reading that broke the run: if the next one agrees with it, the clock jumps there.</summary>
    private TimecodeHeard? candidate;
    private int good;

    public void Receive(LtcFrame frame, TimecodeRate rate, double hostTime) => Receive(TimecodeHeard.FromLtc(frame, rate, hostTime));

    public void Receive(TimecodeHeard next)
    {
        var hostTime = next.HostTime;
        if (anchor is null || hostTime - anchor.HostTime > Dropout + Freewheel)
        {
            anchor = next;
            candidate = null;
            good = 1;
            return;
        }
        if (anchor.Continues(next))
        {
            anchor = next;
            candidate = null;
            good++;
        }
        else if (candidate is not null && candidate.Continues(next))
        {
            anchor = next;
            candidate = null;
            good = good >= LockFrames ? good : 2;
        }
        else
        {
            candidate = next;
        }
    }

    public Reading ReadingAt(double time)
    {
        if (anchor is null) return new Reading(State.NoInput);
        var since = time - anchor.HostTime;
        var locked = good >= LockFrames;
        State state;
        if (since <= Dropout) state = locked ? State.Locked : State.Locking;
        else if (since <= Dropout + Freewheel) state = locked ? State.Freewheel : State.Locking;
        else state = locked ? State.Stopped : State.NoInput;

        var position = state == State.Stopped ? anchor.Position : anchor.PositionAt(time);
        return new Reading(state, state == State.NoInput ? null : position, anchor.Label, anchor.Rate, anchor.Reverse);
    }
}

/// <summary>
/// "No timecode": sound on the input for a second and a half that isn't decoding as
/// timecode (the wrong channel, or music instead of LTC). Port of TimecodeSignalCheck.
/// </summary>
public sealed class TimecodeSignalCheck
{
    /// <summary>A peak level above this counts as sound.</summary>
    public const float Level = 0.01f;

    public const double After = 1.5;

    private double? since;

    /// <summary>Feed it each tick; true once there has been undecodable sound for <see cref="After"/> seconds.</summary>
    public bool Update(float level, bool decoding, double at)
    {
        if (level < Level || decoding)
        {
            since = null;
            return false;
        }
        since ??= at;
        return at - since.Value >= After;
    }

    public static bool Decoding(TimecodeClock.State state) =>
        state is TimecodeClock.State.Locking or TimecodeClock.State.Locked or TimecodeClock.State.Freewheel;
}

/// <summary>
/// The timecode settings, which belong to this computer (they name its devices), so they
/// are never part of a backup. The keys match the Mac's UserDefaults keys.
/// </summary>
public static class TimecodeStation
{
    public const string DeviceKey = "timecode.deviceUID";
    public const string ChannelKey = "timecode.channel";
    public const string OffsetKey = "timecode.offsetFrames";
    public const string FollowKey = "timecode.follow";
    public const string AudioKey = "timecode.playsAudio";
    public const string SourceKey = "timecode.source";
    public const string MidiSourceKey = "timecode.midiSourceUID";

    public static readonly IReadOnlyList<string> Keys = [DeviceKey, ChannelKey, OffsetKey, FollowKey, AudioKey, SourceKey, MidiSourceKey];

    /// <summary>The offset nudge, in frames either way.</summary>
    public static readonly (int Lower, int Upper) OffsetRange = (-30, 30);
}

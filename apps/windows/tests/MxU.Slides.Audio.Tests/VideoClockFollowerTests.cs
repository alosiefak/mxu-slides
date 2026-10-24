using MxU.Slides.Audio.Video;

namespace MxU.Slides.Audio.Tests;

/// <summary>
/// Keeping a video's sound with its picture: when to hold, land and steer. New on Windows (the
/// Mac's AVPlayer plays its own sound), so these are new tests; the steering is the
/// multitrack chase's (MultitrackChaseTests) with video-sized numbers.
/// </summary>
public class VideoClockFollowerTests
{
    private static VideoClockSample Video(double position, double host, bool playing = true, double rate = 1) => new(position, host, playing, rate);

    [Fact]
    public void HoldsUntilThePictureIsSeenMovingThenLandsWhereItIs()
    {
        var follower = new VideoClockFollower();
        Assert.Equal(VideoSoundStep.Hold, follower.Step(Video(0, 10), null).Step);
        // Still opening: "playing", but the position hasn't moved.
        Assert.Equal(VideoSoundStep.Hold, follower.Step(Video(0, 10.04), null).Step);
        var land = follower.Step(Video(0.03, 10.08), null);
        Assert.Equal(VideoSoundStep.Land, land.Step);
        Assert.Equal((0.03, 10.08, 1.0), (land.Position, land.HostTime, land.Speed));
        Assert.True(follower.IsSounding);
    }

    [Fact]
    public void APauseHoldsAndAResumeWaitsForThePictureToMove()
    {
        var follower = Running(out var generation);
        var hold = follower.Step(Video(5, 20, playing: false), Heard(5, 20, generation));
        Assert.Equal(VideoSoundStep.Hold, hold.Step);
        Assert.NotEqual(generation, hold.Generation);
        // Resumed, but the picture hasn't moved yet: keep holding (landing now would run ahead of it).
        Assert.Equal(VideoSoundStep.Hold, follower.Step(Video(5, 21), null).Step);
        var land = follower.Step(Video(5.04, 21.04), null);
        Assert.Equal(VideoSoundStep.Land, land.Step);
        Assert.Equal(5.04, land.Position);
    }

    [Fact]
    public void ASmallGapIsSteeredOutAtMostHalfAPercent()
    {
        var follower = Running(out var generation);
        // The sound is 30 ms ahead: play a little slower.
        var steer = follower.Step(Video(1.0, 11.0), Heard(1.03, 11.0, generation));
        Assert.Equal(VideoSoundStep.Steer, steer.Step);
        Assert.True(steer.Speed < 1 && steer.Speed >= 1 - VideoClockFollower.MaxDeviation, $"speed {steer.Speed}");
        Assert.Equal(0.03, follower.LastError!.Value, 6);
        // Behind: a little faster.
        var faster = Running(out var second).Step(Video(1.0, 11.0), Heard(0.98, 11.0, second));
        Assert.True(faster.Speed > 1 && faster.Speed <= 1 + VideoClockFollower.MaxDeviation);
    }

    [Fact]
    public void ABigGapLandsAtOnceAndAMiddlingOneOnTheSecondLook()
    {
        var follower = Running(out var generation);
        var jump = follower.Step(Video(1.0, 11.0), Heard(1.5, 11.0, generation));
        Assert.Equal(VideoSoundStep.Land, jump.Step);

        follower = Running(out generation);
        Assert.Equal(VideoSoundStep.Steer, follower.Step(Video(1.0, 11.0), Heard(1.2, 11.0, generation)).Step);
        Assert.Equal(VideoSoundStep.Land, follower.Step(Video(1.04, 11.04), Heard(1.24, 11.04, generation)).Step);
    }

    [Fact]
    public void AGapThatComesAndGoesNeverLands()
    {
        var follower = Running(out var generation);
        Assert.Equal(VideoSoundStep.Steer, follower.Step(Video(1.0, 11.0), Heard(1.2, 11.0, generation)).Step);
        Assert.Equal(VideoSoundStep.Steer, follower.Step(Video(1.04, 11.04), Heard(1.05, 11.04, generation)).Step);
        Assert.Equal(VideoSoundStep.Steer, follower.Step(Video(1.08, 11.08), Heard(1.25, 11.08, generation)).Step);
    }

    [Fact]
    public void APictureThatStopsMovingWhilePlayingIsAStall()
    {
        var follower = Running(out var generation);
        Assert.Equal(VideoSoundStep.Steer, follower.Step(Video(1.0, 11.0), Heard(1.0, 11.0, generation)).Step);
        Assert.Equal(VideoSoundStep.Steer, follower.Step(Video(1.0, 11.2), Heard(1.2, 11.2, generation)).Step);
        Assert.Equal(VideoSoundStep.Hold, follower.Step(Video(1.0, 11.3), Heard(1.3, 11.3, generation)).Step);
        // Moving again: land where the picture is.
        Assert.Equal(VideoSoundStep.Land, follower.Step(Video(1.04, 11.34), null).Step);
    }

    [Fact]
    public void ASeekOrALoopLandsOnceThePictureMovesFromItsNewPlace()
    {
        var follower = Running(out var generation);
        // Looped back to the start.
        Assert.Equal(VideoSoundStep.Hold, follower.Step(Video(0.0, 11.0), Heard(4.9, 11.0, generation)).Step);
        var land = follower.Step(Video(0.04, 11.04), null);
        Assert.Equal((VideoSoundStep.Land, 0.04), (land.Step, land.Position));

        // Told about a small seek: wait for two looks after it.
        generation = land.Generation;
        follower.Discontinuity();
        Assert.Equal(VideoSoundStep.Hold, follower.Step(Video(0.5, 11.1), Heard(0.1, 11.1, generation)).Step);
        Assert.Equal(VideoSoundStep.Land, follower.Step(Video(0.54, 11.14), null).Step);
    }

    [Fact]
    public void AReadingFromBeforeTheLastLandingIsIgnored()
    {
        var follower = Running(out var generation);
        var steer = follower.Step(Video(1.0, 11.0), Heard(9.0, 11.0, generation - 1));
        Assert.Equal(VideoSoundStep.Steer, steer.Step);
        Assert.Equal(1.0, steer.Speed);
        Assert.Null(follower.LastError);
    }

    [Fact]
    public void AVideoAtDoubleSpeedLandsAndSteersAroundIt()
    {
        var follower = new VideoClockFollower();
        follower.Step(Video(0, 10, rate: 2), null);
        var land = follower.Step(Video(0.08, 10.04, rate: 2), null);
        Assert.Equal((VideoSoundStep.Land, 2.0), (land.Step, land.Speed));
        var steer = follower.Step(Video(0.16, 10.08, rate: 2), Heard(0.16, 10.08, land.Generation, speed: 2));
        Assert.Equal(2.0, steer.Speed, 6);
    }

    [Fact]
    public void TwoClocksThatDriftApartStayTogetherWithoutLanding()
    {
        // The sound card runs 0.01% fast against the video's clock, for ten minutes, read 25 times a second.
        var follower = new VideoClockFollower();
        double soundPosition = 0, speed = 1, host = 100;
        int? generation = null;
        var worst = 0.0;
        follower.Step(Video(0, host), null);
        for (var look = 1; look < 25 * 600; look++)
        {
            host += 0.04;
            var picture = look * 0.04;
            SoundClockReading? heard = generation is { } g ? new SoundClockReading(soundPosition, host, speed * 1.0001, g) : null;
            var command = follower.Step(Video(picture, host), heard);
            if (command.Step == VideoSoundStep.Land)
            {
                generation = command.Generation;
                soundPosition = command.Position;
                speed = command.Speed;
            }
            else if (command.Step == VideoSoundStep.Steer)
            {
                speed = command.Speed;
            }
            if (generation is not null) soundPosition += 0.04 * speed * 1.0001;
            if (look > 50) worst = Math.Max(worst, Math.Abs(soundPosition - (picture + 0.04)));
        }
        Assert.Equal(1, follower.Landings);
        Assert.True(worst < 0.01, $"the sound wandered {worst * 1000:0.0} ms from the picture");
    }

    // A follower whose sound has landed and is playing, at 10 s host time and the picture at 0.04 s.
    private static VideoClockFollower Running(out int generation)
    {
        var follower = new VideoClockFollower();
        follower.Step(Video(0, 10), null);
        var land = follower.Step(Video(0.04, 10.04), null);
        generation = land.Generation;
        return follower;
    }

    private static SoundClockReading Heard(double position, double host, int generation, double speed = 1) => new(position, host, speed, generation);
}

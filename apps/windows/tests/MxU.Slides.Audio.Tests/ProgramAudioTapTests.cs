using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Audio.Tests;

/// <summary>Port of ProgramAudioTapTests.swift, plus a check that the app's own output really is captured.</summary>
public sealed class ProgramAudioTapTests
{
    private sealed class Sink : IProgramSink
    {
        private readonly Lock gate = new();
        private readonly List<(int Frames, double Seconds, float Peak)> received = [];

        public int Count { get { lock (gate) return received.Count; } }

        public (int Frames, double Seconds, float Peak)? First { get { lock (gate) return received.Count > 0 ? received[0] : null; } }

        public float LoudestPeak { get { lock (gate) return received.Count == 0 ? 0 : received.Max(r => r.Peak); } }

        public void WriteVideo(VideoFrame frame) { }

        public void WriteAudio(AudioBuffer buffer)
        {
            var peak = 0f;
            foreach (var sample in buffer.Samples.Span) peak = MathF.Max(peak, MathF.Abs(sample));
            lock (gate) received.Add((buffer.FrameCount, buffer.HostTime, peak));
        }
    }

    [Fact]
    public async Task ProgramTapDeliversCopiedBuffersOnTheHostClock()
    {
        if (!ProgramAudioTap.IsSupported) Assert.Skip("Process loopback needs Windows 10 build 19041");
        var token = TestContext.Current.CancellationToken;
        var sink = new Sink();
        using var tap = new ProgramAudioTap();
        tap.AddSink(sink);
        Assert.True(await TestAudio.WaitForAsync(() => sink.Count >= 3, 5, token), $"the tap never delivered ({tap.Status.Message})");
        var first = sink.First!.Value;
        Assert.True(first.Frames > 0);
        Assert.Equal(2, ProgramAudioTap.Channels);
        Assert.True(Math.Abs(first.Seconds - ShowState.HostClock()) < 3_600);
        tap.RemoveSink(sink);
        Assert.True(await TestAudio.WaitForAsync(() => tap.Status.State == ProgramAudioTapState.Stopped, 3, token));
    }

    [Fact]
    public async Task TheTapHearsWhatTheAppPlays()
    {
        if (!ProgramAudioTap.IsSupported) Assert.Skip("Process loopback needs Windows 10 build 19041");
        TestAudio.RequireOutputDevice();
        var token = TestContext.Current.CancellationToken;
        var sink = new Sink();
        using var tap = new ProgramAudioTap();
        tap.AddSink(sink);
        if (!await TestAudio.WaitForAsync(() => tap.Status.State == ProgramAudioTapState.Capturing, 5, token))
            Assert.Skip($"Process loopback isn't available here: {tap.Status.Message}");
        // About -66 dB: the capture hears it, the room doesn't.
        using var engine = new AudioEngine { MasterVolume = 0.001f };
        engine.Play(TestAudio.Tone("tap", 2, 440, amplitude: 0.5f));
        if (!engine.OutputStatus.IsAudible) Assert.Skip($"The output isn't reaching a device: {engine.OutputStatus.Message}");
        Assert.True(await TestAudio.WaitForAsync(() => sink.LoudestPeak > 0.0002, 5, token), "the app's own playback is captured");
        engine.Stop();
    }
}

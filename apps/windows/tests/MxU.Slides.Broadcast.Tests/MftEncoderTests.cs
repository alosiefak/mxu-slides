using System.Diagnostics;
using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// The asynchronous (graphics-card) encoder protocol: pictures go in only when the encoder asks
/// ("need input"), outputs come back with "have output" events, and draining waits for "drain
/// complete". A fake encoder that behaves like Intel Quick Sync makes the timing exact; the last
/// test runs two real encoders at once, as when OBS or a second stream shares the graphics card.
/// </summary>
public sealed class MftEncoderTests
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaEvent(uint met, in Guid extendedType, int status, nint value, out IMFMediaEvent mediaEvent);

    /// <summary>
    /// An async encoder that asks for <c>initialRequests</c> pictures when it starts, has each
    /// one's output ready at once, and asks for the next picture only after an output is
    /// collected (Quick Sync's pattern). It can also change its output format once. An extra
    /// "have output" with nothing ready is answered "need more input", as real encoders do.
    /// </summary>
    private sealed class FakeAsyncEncoder(int initialRequests, bool changesFormatFirst = false) : IMFTransform, IMFMediaEventGenerator
    {
        private readonly Queue<IMFMediaEvent> events = new();
        private readonly Queue<long> pending = new();
        private int credits;
        private bool formatChangePending = changesFormatFirst;

        public int Inputs;
        public int Refused;

        private void Post(uint type)
        {
            MF.Check(MFCreateMediaEvent(type, Guid.Empty, 0, 0, out var e), "MFCreateMediaEvent");
            events.Enqueue(e);
        }

        private void Ask()
        {
            credits++;
            Post(MF.METransformNeedInput);
        }

        public int GetAttributes(out IMFAttributes pAttributes)
        {
            pAttributes = MediaFoundation.CreateAttributes();
            pAttributes.SetUINT32(MF.MF_TRANSFORM_ASYNC, 1);
            return 0;
        }

        public int GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo)
        {
            pStreamInfo = new MFT_OUTPUT_STREAM_INFO { dwFlags = MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES };
            return 0;
        }

        public int ProcessMessage(uint eMessage, nint ulParam)
        {
            switch (eMessage)
            {
                case MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM:
                    for (var i = 0; i < initialRequests; i++) Ask();
                    break;
                case MF.MFT_MESSAGE_COMMAND_DRAIN:
                    foreach (var _ in pending) Post(MF.METransformHaveOutput);
                    Post(MF.METransformDrainComplete);
                    break;
            }
            return 0;
        }

        public int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags)
        {
            if (credits == 0)
            {
                Refused++;
                return MF.MF_E_NOTACCEPTING;
            }
            credits--;
            Inputs++;
            pSample.GetSampleTime(out var time);
            pending.Enqueue(time);
            Post(MF.METransformHaveOutput);
            return 0;
        }

        public int ProcessOutput(uint dwFlags, uint cOutputBufferCount, ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out uint pdwStatus)
        {
            pdwStatus = 0;
            if (formatChangePending)
            {
                // Like Quick Sync: the output waits for the new format and is announced again.
                formatChangePending = false;
                Post(MF.METransformHaveOutput);
                return MF.MF_E_TRANSFORM_STREAM_CHANGE;
            }
            if (pending.Count == 0) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
            var time = pending.Dequeue();
            var sample = MediaFoundation.CreateSample([0, 0, 0, 1, 0x65, 0x88], time, 333_333);
            pOutputSamples.pSample = Marshal.GetComInterfaceForObject<IMFSample, IMFSample>(sample);
            Ask();
            return 0;
        }

        public int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType)
        {
            ppType = MediaFoundation.CreateMediaType();
            return 0;
        }

        public int SetOutputType(uint dwOutputStreamID, IMFMediaType? pType, uint dwFlags) => 0;

        public int GetEvent(uint dwFlags, out IMFMediaEvent ppEvent)
        {
            if (events.Count == 0)
            {
                ppEvent = null!;
                return MF.MF_E_NO_EVENTS_AVAILABLE;
            }
            ppEvent = events.Dequeue();
            return 0;
        }

        // Not used by the encoder driver.
        public int GetStreamLimits(out uint a, out uint b, out uint c, out uint d) { a = b = c = d = 1; return E_NOTIMPL; }
        public int GetStreamCount(out uint a, out uint b) { a = b = 1; return 0; }
        public int GetStreamIDs(uint a, uint[] b, uint c, uint[] d) => E_NOTIMPL;
        public int GetInputStreamInfo(uint a, out MFT_INPUT_STREAM_INFO b) { b = default; return E_NOTIMPL; }
        public int GetInputStreamAttributes(uint a, out IMFAttributes b) { b = null!; return E_NOTIMPL; }
        public int GetOutputStreamAttributes(uint a, out IMFAttributes b) { b = null!; return E_NOTIMPL; }
        public int DeleteInputStream(uint a) => E_NOTIMPL;
        public int AddInputStreams(uint a, uint[] b) => E_NOTIMPL;
        public int GetInputAvailableType(uint a, uint b, out IMFMediaType c) { c = null!; return E_NOTIMPL; }
        public int SetInputType(uint a, IMFMediaType? b, uint c) => 0;
        public int GetInputCurrentType(uint a, out IMFMediaType b) { b = null!; return E_NOTIMPL; }
        public int GetOutputCurrentType(uint a, out IMFMediaType b) { b = null!; return E_NOTIMPL; }
        public int GetInputStatus(uint a, out uint b) { b = 0; return E_NOTIMPL; }
        public int GetOutputStatus(out uint a) { a = 0; return E_NOTIMPL; }
        public int SetOutputBounds(long a, long b) => E_NOTIMPL;
        public int ProcessEvent(uint a, nint b) => E_NOTIMPL;
        public int BeginGetEvent(nint a, nint b) => E_NOTIMPL;
        public int EndGetEvent(nint a, out IMFMediaEvent b) { b = null!; return E_NOTIMPL; }
        public int QueueEvent(uint a, in Guid b, int c, nint d) => E_NOTIMPL;
    }

    private static List<long> Times(List<IMFSample> samples)
    {
        var times = new List<long>();
        foreach (var sample in samples)
        {
            sample.GetSampleTime(out var time);
            times.Add(time);
        }
        return times;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void EveryPictureTheEncoderAsksForGoesInAndComesOut(int initialRequests, bool changesFormatFirst)
    {
        MediaFoundation.EnsureStarted();
        var fake = new FakeAsyncEncoder(initialRequests, changesFormatFirst);
        using var encoder = new MftEncoder(fake, "fake async", isHardware: true);
        encoder.BeginStreaming();
        var outputs = new List<IMFSample>();
        var clock = Stopwatch.StartNew();
        for (var frame = 0; frame < 30; frame++)
            outputs.AddRange(encoder.Process(MediaFoundation.CreateSample(new byte[16], frame * 333_333L, 333_333)));
        outputs.AddRange(encoder.Drain());

        Assert.Equal(0, fake.Refused);
        Assert.Equal(0, encoder.DroppedInputs);
        Assert.Equal(30, fake.Inputs);
        Assert.Equal(Enumerable.Range(0, 30).Select(f => f * 333_333L), Times(outputs));
        // A picture the encoder already asked for never waits for another event (that used to stall 250 ms a frame).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"30 pictures took {clock.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// Two real encoders at once, as when a second stream (or OBS) shares the graphics card. Every
    /// picture must come out or be counted as dropped, and nearly all must come out. (Other work on
    /// the graphics card, such as tests running side by side, can make it genuinely drop a few,
    /// so a short run is tried once more before failing.)
    /// </summary>
    [Fact]
    public async Task TwoEncodersAtOnceEachKeepEveryPicture()
    {
        if (Environment.GetEnvironmentVariable("MXU_BROADCAST_ENCODER") == "software")
            TestContext.Current.TestOutputHelper?.WriteLine("MXU_BROADCAST_ENCODER=software: both use the software encoder");
        var results = await EncodeSideBySideAsync();
        if (results.Any(r => r.Frames < 57 && r.DroppedInputs > 0))
        {
            TestContext.Current.TestOutputHelper?.WriteLine("a busy encoder dropped pictures; trying again");
            results = await EncodeSideBySideAsync();
        }
        foreach (var (name, hardware, frames, dropped) in results)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{name} hardware={hardware}: {frames} out, {dropped} dropped");
            Assert.Equal(60, frames + dropped);
            Assert.True(frames >= 57, $"{name}: only {frames} of 60 pictures came out ({dropped} dropped)");
        }
    }

    private static Task<(string Name, bool IsHardware, int Frames, int DroppedInputs)[]> EncodeSideBySideAsync() =>
        Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Factory.StartNew(() =>
        {
            using var encoder = H264Encoder.Create(new H264Settings(640, 360, 30, 1500));
            var converter = new Nv12Converter(640, 360);
            var nv12 = new byte[converter.FrameSize];
            var frames = 0;
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 60; i++)
            {
                converter.Convert(Synthetic.Frame(640, 360, i + index * 7), 640, 360, 640 * 4, nv12);
                frames += encoder.Encode(nv12, 10 + i / 30.0).Count;
                // Real time: one picture every 33 ms.
                var due = TimeSpan.FromSeconds((i + 1) / 30.0) - clock.Elapsed;
                if (due > TimeSpan.Zero) Thread.Sleep(due);
            }
            frames += encoder.Drain().Count;
            return (encoder.Name, encoder.IsHardware, frames, encoder.DroppedInputs);
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
}

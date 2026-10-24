namespace MxU.Slides.Core.Media;

/// <summary>
/// One picture of the program (the audience output): 32-bit BGRA, premultiplied
/// alpha, rows top to bottom, <see cref="Stride"/> bytes apart. The pixels are only
/// valid during the call that hands the frame over; a sink that keeps them copies them.
/// Port of the CVPixelBuffer the Mac's OutputMirror hands to its destinations.
/// </summary>
public sealed class VideoFrame(int width, int height, int stride, ReadOnlyMemory<byte> pixels, double hostTime)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public ReadOnlyMemory<byte> Pixels { get; } = pixels;

    /// <summary>When the frame was drawn, on <c>ShowState.HostClock</c> (monotonic seconds).</summary>
    public double HostTime { get; } = hostTime;
}

/// <summary>
/// A slice of program audio: interleaved 32-bit float samples (-1…1). Like
/// <see cref="VideoFrame"/>, the samples are only valid during the call.
/// Port of the AVAudioPCMBuffer the Mac's ProgramAudioTap hands over.
/// </summary>
public sealed class AudioBuffer(int sampleRate, int channels, ReadOnlyMemory<float> samples, double hostTime)
{
    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public ReadOnlyMemory<float> Samples { get; } = samples;

    /// <summary>Sample frames (one sample per channel).</summary>
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;

    /// <summary>When the first sample was heard, on <c>ShowState.HostClock</c>.</summary>
    public double HostTime { get; } = hostTime;
}

/// <summary>
/// Somewhere the program goes besides the screens: a recording, a stream, an NDI
/// source, a DeckLink card. The app calls these from its frame and audio threads,
/// never the UI thread, so implementations must be thread-safe and return quickly
/// (queue the work; drop a frame rather than block).
/// </summary>
public interface IProgramSink
{
    void WriteVideo(VideoFrame frame);

    void WriteAudio(AudioBuffer buffer);
}

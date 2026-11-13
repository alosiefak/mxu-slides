using NAudio.Wave;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Turns a recording into the sound Windows' speech recognizer reads: one channel, 16,000
/// samples a second, 16 bits. Windows' own decoders (Media Foundation, through NAudio, which the
/// app already uses for sound) read the file, so anything Windows can play works: .mp4, .mov,
/// .m4a, .mp3, .wav, .wma. The result is a temporary file on this PC; nothing is sent anywhere.
/// </summary>
public static class SermonSound
{
    public static readonly WaveFormat SpeechFormat = new(16000, 16, 1);

    /// <summary>Writes <paramref name="path"/>'s sound to <paramref name="wav"/> and returns how long it is. Run it off the UI thread.</summary>
    public static TimeSpan ToSpeechWav(string path, string wav, CancellationToken cancel)
    {
        using var reader = new MediaFoundationReader(path);
        using var resampler = new MediaFoundationResampler(reader, SpeechFormat) { ResamplerQuality = 30 };
        using var writer = new WaveFileWriter(wav, SpeechFormat);
        var buffer = new byte[SpeechFormat.AverageBytesPerSecond];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            writer.Write(buffer, 0, read);
        }
        return writer.TotalTime;
    }
}

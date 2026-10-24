using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Made-up program pictures and sound for the tests: no GPU or audio device needed.</summary>
internal static class Synthetic
{
    /// <summary>A BGRA frame: a grey background with a bright square that moves with <paramref name="index"/>.</summary>
    public static byte[] Frame(int width, int height, int index, byte shade = 0x60)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = shade;
            pixels[i + 1] = (byte)(shade + 0x10);
            pixels[i + 2] = (byte)(shade + 0x20);
            pixels[i + 3] = 0xFF;
        }
        var size = Math.Max(4, height / 4);
        var left = (index * 7) % Math.Max(1, width - size);
        var top = height / 3;
        for (var y = top; y < Math.Min(height, top + size); y++)
            for (var x = left; x < left + size; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = 0xF0;
                pixels[i + 1] = 0xF0;
                pixels[i + 2] = 0xF0;
            }
        return pixels;
    }

    public static VideoFrame VideoFrame(int width, int height, int index, double hostTime) =>
        new(width, height, width * 4, Frame(width, height, index), hostTime);

    /// <summary>A 440 Hz sine at half volume, interleaved, starting at sample <paramref name="offset"/>.</summary>
    public static float[] Tone(int sampleRate, int channels, int frames, long offset, double frequency = 440)
    {
        var samples = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var value = (float)(Math.Sin((offset + f) * 2 * Math.PI * frequency / sampleRate) * 0.5);
            for (var c = 0; c < channels; c++) samples[f * channels + c] = value;
        }
        return samples;
    }

    public static AudioBuffer AudioBuffer(int sampleRate, int channels, int frames, long offset, double hostTime) =>
        new(sampleRate, channels, Tone(sampleRate, channels, frames, offset), hostTime);
}

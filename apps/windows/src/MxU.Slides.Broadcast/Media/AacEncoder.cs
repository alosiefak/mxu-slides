using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Encodes program audio to AAC-LC with Windows' AAC encoder (a software encoder present
/// wherever Media Foundation is). Takes the program's float samples, converts them with
/// <see cref="PcmConverter"/>, and returns raw AAC frames timed by <see cref="AacPacketClock"/>.
/// The Windows encoder offers 96, 128, 160 or 192 kbps; the nearest is used. Not thread-safe.
/// Replaces the AVAudioConverter in the Mac's HLSAudioEncoder and HaishinKit.
/// </summary>
internal sealed class AacEncoder : IDisposable
{
    private readonly MftEncoder encoder;
    private readonly IMFActivate activate;
    private readonly PcmConverter converter;
    private readonly AacPacketClock clock;
    private long framesIn;
    private long packetsOut;
    private bool disposed;

    private AacEncoder(MftEncoder encoder, IMFActivate activate, PcmConverter converter, AudioSpecificConfig config, int bitrate)
    {
        this.encoder = encoder;
        this.activate = activate;
        this.converter = converter;
        Config = config;
        BitrateKbps = bitrate;
        clock = new AacPacketClock(converter.OutputSampleRate);
    }

    /// <summary>The AudioSpecificConfig of the output: the RTMP AAC sequence header and the ADTS fields.</summary>
    public AudioSpecificConfig Config { get; }

    public int BitrateKbps { get; }

    public int SampleRate => converter.OutputSampleRate;

    public int Channels => converter.OutputChannels;

    /// <summary>Bytes per second the Windows AAC encoder accepts (96/128/160/192 kbps).</summary>
    private static uint ByteRate(int kbps) => kbps switch
    {
        <= 112 => 12000,
        <= 144 => 16000,
        <= 176 => 20000,
        _ => 24000,
    };

    public static AacEncoder Create(int inputSampleRate, int inputChannels, int bitrateKbps = 128) =>
        Create(new PcmConverter(inputSampleRate, inputChannels), bitrateKbps);

    /// <summary>An encoder whose output format is fixed in advance (an MP4 audio track is declared before any audio arrives).</summary>
    public static AacEncoder Create(int inputSampleRate, int inputChannels, int outputSampleRate, int outputChannels, int bitrateKbps) =>
        Create(new PcmConverter(inputSampleRate, inputChannels, outputSampleRate, outputChannels), bitrateKbps);

    /// <summary>The format Windows' AAC encoder can make closest to the one asked for: 44.1 or 48 kHz, mono or stereo.</summary>
    public static (int SampleRate, int Channels) SupportedOutput(int sampleRate, int channels) =>
        (sampleRate is 44100 or 48000 ? sampleRate : 48000, Math.Clamp(channels, 1, 2));

    private static AacEncoder Create(PcmConverter converter, int bitrateKbps)
    {
        var config = AudioSpecificConfig.AacLowComplexity(converter.OutputSampleRate, converter.OutputChannels)
            ?? throw new MediaFoundationException($"AAC can't carry {converter.OutputSampleRate} Hz × {converter.OutputChannels}", unchecked((int)0xC00D36B4));
        var candidates = MediaFoundation.FindEncoders(
            MF.MFT_CATEGORY_AUDIO_ENCODER, MF.MFMediaType_Audio, MF.MFAudioFormat_PCM, MF.MFAudioFormat_AAC, includeHardware: false);
        var failures = new List<string>();
        try
        {
            foreach (var candidate in candidates)
            {
                MftEncoder? mft = null;
                try
                {
                    MF.Check(candidate.Activate.ActivateObject(MF.IID_IMFTransform, out var created), $"{candidate.Name}: activate");
                    mft = new MftEncoder((IMFTransform)created, candidate.Name, isHardware: false);
                    var byteRate = ByteRate(bitrateKbps);
                    Configure(mft, converter.OutputSampleRate, converter.OutputChannels, byteRate);
                    mft.BeginStreaming();
                    candidates.Remove(candidate);
                    return new AacEncoder(mft, candidate.Activate, converter, config, (int)(byteRate * 8 / 1000));
                }
                catch (Exception e) when (e is MediaFoundationException or InvalidCastException or COMException)
                {
                    failures.Add($"{candidate.Name}: {e.Message}");
                    mft?.Dispose();
                    candidate.Activate.ShutdownObject();
                }
            }
        }
        finally
        {
            MediaFoundation.ReleaseAll(candidates);
        }
        throw new MediaFoundationException(
            failures.Count == 0 ? "No AAC encoder is installed" : "No AAC encoder accepted the format (" + string.Join("; ", failures) + ")",
            unchecked((int)0xC00D5212));
    }

    private static void Configure(MftEncoder mft, int sampleRate, int channels, uint byteRate)
    {
        var input = MediaFoundation.CreateMediaType();
        var output = MediaFoundation.CreateMediaType();
        try
        {
            input.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
            input.SetGUID(MF.MF_MT_SUBTYPE, MF.MFAudioFormat_PCM);
            input.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            input.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            input.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            input.SetUINT32(MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(channels * 2));
            input.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(sampleRate * channels * 2));
            input.SetUINT32(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            output.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
            output.SetGUID(MF.MF_MT_SUBTYPE, MF.MFAudioFormat_AAC);
            output.SetUINT32(MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            output.SetUINT32(MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            output.SetUINT32(MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            output.SetUINT32(MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, byteRate);
            output.SetUINT32(MF.MF_MT_AAC_PAYLOAD_TYPE, 0);
            output.SetUINT32(MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);

            // The Microsoft AAC encoder wants the input type first; accept either order.
            if (mft.Transform.SetInputType(mft.InputStream, input, 0) >= 0)
            {
                MF.Check(mft.Transform.SetOutputType(mft.OutputStream, output, 0), $"{mft.Name}: output type");
            }
            else
            {
                MF.Check(mft.Transform.SetOutputType(mft.OutputStream, output, 0), $"{mft.Name}: output type");
                MF.Check(mft.Transform.SetInputType(mft.InputStream, input, 0), $"{mft.Name}: input type");
            }
        }
        finally
        {
            MediaFoundation.Release(input);
            MediaFoundation.Release(output);
        }
    }

    /// <summary>Encodes one buffer of program audio whose first sample plays at <paramref name="hostTime"/>.</summary>
    public List<EncodedAudioFrame> Encode(ReadOnlySpan<float> interleaved, double hostTime)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var pcm = converter.Convert(interleaved);
        if (pcm.Length == 0) return [];
        clock.Observe(hostTime, framesIn - packetsOut * (long)AacPacketClock.PacketFrames);
        var frames = pcm.Length / converter.OutputChannels;
        var bytes = MemoryMarshal.AsBytes(pcm.AsSpan());
        var start = (long)Math.Round(framesIn * 10_000_000.0 / SampleRate);
        var duration = (long)Math.Round(frames * 10_000_000.0 / SampleRate);
        framesIn += frames;
        var sample = MediaFoundation.CreateSample(bytes, start, duration);
        return Collect(encoder.Process(sample));
    }

    public List<EncodedAudioFrame> Drain() => disposed ? [] : Collect(encoder.Drain());

    private List<EncodedAudioFrame> Collect(List<IMFSample> samples)
    {
        var frames = new List<EncodedAudioFrame>(samples.Count);
        foreach (var sample in samples)
        {
            try
            {
                var data = MediaFoundation.ReadBytes(sample);
                if (data.Length == 0) continue;
                packetsOut++;
                frames.Add(new EncodedAudioFrame { Data = data, PresentationTime = clock.Next(), Config = Config });
            }
            finally
            {
                MediaFoundation.Release(sample);
            }
        }
        return frames;
    }

    /// <summary>A copy of the encoder's output media type (all its attributes), for an MP4 track that takes these frames as they are.</summary>
    internal IMFMediaType CopyOutputType()
    {
        MF.Check(encoder.Transform.GetOutputCurrentType(encoder.OutputStream, out var current), "AAC output type");
        try
        {
            var copy = MediaFoundation.CreateMediaType();
            MF.Check(current.CopyAllItems((IMFAttributes)copy), "copy AAC output type");
            return copy;
        }
        finally
        {
            MediaFoundation.Release(current);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        encoder.Dispose();
        activate.ShutdownObject();
        MediaFoundation.Release(activate);
    }
}

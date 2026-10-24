using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Media;

/// <summary>What an H.264 stream encoder is asked for.</summary>
internal sealed record H264Settings(
    int Width,
    int Height,
    int FrameRate,
    int BitrateKbps,
    double KeyframeIntervalSeconds = 2)
{
    public long FrameDuration => 10_000_000L / Math.Max(1, FrameRate);

    public int GopFrames => Math.Max(1, (int)Math.Round(KeyframeIntervalSeconds * FrameRate));
}

/// <summary>
/// Encodes NV12 pictures to H.264 with Windows' encoders: High profile, constant bitrate, a
/// keyframe at least every <see cref="H264Settings.KeyframeIntervalSeconds"/> (YouTube and most
/// ingests want ≤ 4 s), no B-frames and low-latency mode, so pictures come out in the order
/// they went in and presentation time equals decode time. A hardware encoder is tried first
/// (unless <see cref="EncoderPreference.Software"/>); if it can't be set up, or fails while
/// encoding, later encoders in this process use the software one. Output frames carry AVCC NAL
/// units and the current SPS/PPS. Not thread-safe. Replaces the VideoToolbox session the
/// Mac's HaishinKit and HLSVideoEncoder use.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    private static volatile bool hardwareDisabled = Environment.GetEnvironmentVariable("MXU_BROADCAST_ENCODER") == "software";

    private readonly MftEncoder encoder;
    private readonly IMFActivate activate;
    private readonly H264Settings settings;
    private AvcParameterSets? parameterSets;
    private bool disposed;

    private H264Encoder(MftEncoder encoder, IMFActivate activate, H264Settings settings)
    {
        this.encoder = encoder;
        this.activate = activate;
        this.settings = settings;
    }

    public string Name => encoder.Name;

    public bool IsHardware => encoder.IsHardware;

    public H264Settings Settings => settings;

    /// <summary>Pictures a busy hardware encoder never asked for, so they were left out (always 0 for software).</summary>
    public int DroppedInputs => encoder.DroppedInputs;

    /// <summary>Stops trying hardware encoders for the rest of this run (after one failed).</summary>
    public static void DisableHardware(string why)
    {
        if (hardwareDisabled) return;
        hardwareDisabled = true;
        BroadcastLog.Warning("encoder", $"hardware H.264 encoder disabled: {why}; using the software encoder");
    }

    /// <summary>True when this PC has at least one H.264 encoder Media Foundation can use.</summary>
    public static bool IsAvailable()
    {
        if (!MediaFoundation.IsAvailable) return false;
        var candidates = MediaFoundation.FindEncoders(
            MF.MFT_CATEGORY_VIDEO_ENCODER, MF.MFMediaType_Video, MF.MFVideoFormat_NV12, MF.MFVideoFormat_H264, includeHardware: false);
        MediaFoundation.ReleaseAll(candidates);
        return candidates.Count > 0;
    }

    /// <summary>Creates the best encoder that accepts these settings. Throws when none does.</summary>
    public static H264Encoder Create(H264Settings settings, EncoderPreference preference = EncoderPreference.Automatic)
    {
        var useHardware = preference == EncoderPreference.Automatic && !hardwareDisabled;
        var candidates = MediaFoundation.FindEncoders(
            MF.MFT_CATEGORY_VIDEO_ENCODER, MF.MFMediaType_Video, MF.MFVideoFormat_NV12, MF.MFVideoFormat_H264, useHardware);
        var failures = new List<string>();
        try
        {
            foreach (var candidate in candidates)
            {
                MftEncoder? mft = null;
                try
                {
                    MF.Check(candidate.Activate.ActivateObject(MF.IID_IMFTransform, out var created), $"{candidate.Name}: activate");
                    mft = new MftEncoder((IMFTransform)created, candidate.Name, candidate.IsHardware);
                    Configure(mft, settings);
                    mft.BeginStreaming();
                    candidates.Remove(candidate);
                    BroadcastLog.Info("encoder", $"H.264 encoder: {candidate.Name}{(candidate.IsHardware ? " (hardware)" : "")} {settings.Width}x{settings.Height}@{settings.FrameRate} {settings.BitrateKbps} kbps");
                    return new H264Encoder(mft, candidate.Activate, settings);
                }
                catch (Exception e) when (e is MediaFoundationException or InvalidCastException or System.Runtime.InteropServices.COMException)
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
            failures.Count == 0 ? "No H.264 encoder is installed" : "No H.264 encoder accepted the settings (" + string.Join("; ", failures) + ")",
            unchecked((int)0xC00D5212));
    }

    private static void Configure(MftEncoder mft, H264Settings settings)
    {
        var bitrate = (uint)Math.Max(100_000, settings.BitrateKbps * 1000);
        // Encoder properties first: some encoders only honour rate control before the types are set.
        mft.TrySet(MF.CODECAPI_AVEncCommonRateControlMode, ComVariantValue.UInt32(MF.eAVEncCommonRateControlMode_CBR));
        mft.TrySet(MF.CODECAPI_AVEncCommonMeanBitRate, ComVariantValue.UInt32(bitrate));
        mft.TrySet(MF.CODECAPI_AVEncMPVGOPSize, ComVariantValue.UInt32((uint)settings.GopFrames));
        mft.TrySet(MF.CODECAPI_AVEncMPVDefaultBPictureCount, ComVariantValue.UInt32(0));
        mft.TrySet(MF.CODECAPI_AVLowLatencyMode, ComVariantValue.Bool(true));
        if (!mft.IsHardware)
            mft.TrySet(MF.CODECAPI_AVEncCommonQualityVsSpeed, ComVariantValue.UInt32(33));

        var output = MediaFoundation.CreateMediaType();
        try
        {
            output.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            output.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_H264);
            output.SetUINT32(MF.MF_MT_AVG_BITRATE, bitrate);
            output.SetUINT64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)settings.Width, (uint)settings.Height));
            output.SetUINT64(MF.MF_MT_FRAME_RATE, MF.Pack((uint)settings.FrameRate, 1));
            output.SetUINT64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
            output.SetUINT32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
            output.SetUINT32(MF.MF_MT_MPEG2_PROFILE, MF.eAVEncH264VProfile_High);
            if (mft.Transform.SetOutputType(mft.OutputStream, output, 0) < 0)
            {
                // Some encoders don't do High profile: Main is what every H.264 ingest takes too.
                output.SetUINT32(MF.MF_MT_MPEG2_PROFILE, MF.eAVEncH264VProfile_Main);
                MF.Check(mft.Transform.SetOutputType(mft.OutputStream, output, 0), $"{mft.Name}: output type");
            }
        }
        finally
        {
            MediaFoundation.Release(output);
        }

        var input = MediaFoundation.CreateMediaType();
        try
        {
            input.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            input.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_NV12);
            input.SetUINT64(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)settings.Width, (uint)settings.Height));
            input.SetUINT64(MF.MF_MT_FRAME_RATE, MF.Pack((uint)settings.FrameRate, 1));
            input.SetUINT64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
            input.SetUINT32(MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
            input.SetUINT32(MF.MF_MT_DEFAULT_STRIDE, (uint)settings.Width);
            input.SetUINT32(MF.MF_MT_VIDEO_NOMINAL_RANGE, MF.MFNominalRange_16_235);
            input.SetUINT32(MF.MF_MT_YUV_MATRIX, MF.MFVideoTransferMatrix_BT709);
            MF.Check(mft.Transform.SetInputType(mft.InputStream, input, 0), $"{mft.Name}: input type");
        }
        finally
        {
            MediaFoundation.Release(input);
        }

        // And again after the types, for encoders that reset them when the types change.
        mft.TrySet(MF.CODECAPI_AVEncCommonMeanBitRate, ComVariantValue.UInt32(bitrate));
        mft.TrySet(MF.CODECAPI_AVEncMPVGOPSize, ComVariantValue.UInt32((uint)settings.GopFrames));
    }

    /// <summary>
    /// Encodes one picture (an NV12 frame of exactly the settings' size) taken at
    /// <paramref name="presentationTime"/> seconds. Returns the frames that came out (usually
    /// this one; hardware encoders may hand it back with the next call).
    /// </summary>
    public List<EncodedVideoFrame> Encode(ReadOnlySpan<byte> nv12, double presentationTime, bool forceKeyframe = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (forceKeyframe) encoder.TrySet(MF.CODECAPI_AVEncVideoForceKeyFrame, ComVariantValue.UInt32(1));
        var sample = MediaFoundation.CreateSample(nv12, ToHns(presentationTime), settings.FrameDuration);
        return Collect(Guard(() => encoder.Process(sample)));
    }

    /// <summary>Returns the pictures the encoder still holds (call at the end of a stream).</summary>
    public List<EncodedVideoFrame> Drain() => disposed ? [] : Collect(Guard(encoder.Drain));

    private List<IMFSample> Guard(Func<List<IMFSample>> work)
    {
        try
        {
            return work();
        }
        catch (Exception e) when (encoder.IsHardware && e is MediaFoundationException or System.Runtime.InteropServices.COMException)
        {
            DisableHardware($"{encoder.Name} failed ({e.Message})");
            throw;
        }
    }

    private List<EncodedVideoFrame> Collect(List<IMFSample> samples)
    {
        var frames = new List<EncodedVideoFrame>(samples.Count);
        foreach (var sample in samples)
        {
            try
            {
                if (Parse(sample) is { } frame) frames.Add(frame);
            }
            finally
            {
                MediaFoundation.Release(sample);
            }
        }
        return frames;
    }

    private EncodedVideoFrame? Parse(IMFSample sample)
    {
        var bytes = MediaFoundation.ReadBytes(sample);
        sample.GetSampleTime(out var time);
        var nals = AvcBitstream.SplitAnnexB(bytes);
        byte[]? sps = null, pps = null;
        var picture = new List<ReadOnlyMemory<byte>>(nals.Count);
        var idr = false;
        foreach (var nal in nals)
        {
            switch (AvcBitstream.TypeOf(nal.Span))
            {
                case AvcBitstream.NalSps: sps = nal.ToArray(); break;
                case AvcBitstream.NalPps: pps = nal.ToArray(); break;
                case AvcBitstream.NalAccessUnitDelimiter: break;
                case AvcBitstream.NalIdr: idr = true; picture.Add(nal); break;
                default: picture.Add(nal); break;
            }
        }
        if (sps is not null && pps is not null) parameterSets = new AvcParameterSets(sps, pps);
        parameterSets ??= SequenceHeader();
        if (parameterSets is null || picture.Count == 0) return null;
        return new EncodedVideoFrame
        {
            Avcc = AvcBitstream.ToAvcc(picture),
            IsKeyframe = idr || MediaFoundation.IsKeyframe(sample),
            PresentationTime = time / 10_000_000.0,
            ParameterSets = parameterSets,
        };
    }

    /// <summary>For encoders that keep SPS/PPS out of the picture data: read them from the output type.</summary>
    private AvcParameterSets? SequenceHeader()
    {
        if (encoder.Transform.GetOutputCurrentType(encoder.OutputStream, out var type) < 0) return null;
        try
        {
            if (MediaFoundation.GetBlob(type, MF.MF_MT_MPEG_SEQUENCE_HEADER) is not { } header) return null;
            var units = AvcBitstream.SplitAnnexB(header);
            var sps = units.FirstOrDefault(u => AvcBitstream.TypeOf(u.Span) == AvcBitstream.NalSps);
            var pps = units.FirstOrDefault(u => AvcBitstream.TypeOf(u.Span) == AvcBitstream.NalPps);
            return sps.Length > 0 && pps.Length > 0 ? new AvcParameterSets(sps.ToArray(), pps.ToArray()) : null;
        }
        finally
        {
            MediaFoundation.Release(type);
        }
    }

    public static long ToHns(double seconds) => (long)Math.Round(seconds * 10_000_000.0);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        encoder.Dispose();
        activate.ShutdownObject();
        MediaFoundation.Release(activate);
    }
}

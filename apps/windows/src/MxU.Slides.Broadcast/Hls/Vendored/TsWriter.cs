// Ported from HaishinKit (BSD 3-Clause, Copyright (c) 2015, shogo4405); see LICENSE-HaishinKit.txt.
using MxU.Slides.Broadcast.Media;

namespace MxU.Slides.Broadcast.Hls.Vendored;

[Flags]
internal enum TsMedia { None = 0, Video = 1, Audio = 2 }

/// <summary>
/// Muxes encoded H.264 pictures and AAC frames into an MPEG transport stream and cuts it into
/// HLS segments. Bytes go to <see cref="Sink"/> as they are made; when a keyframe arrives at
/// least <c>segmentDuration</c> after the last cut, <see cref="OnSegmentCut"/> is called (with
/// the finished segment's length in seconds) before that keyframe is written, so every
/// segment opens with the PAT, the PMT and a keyframe. Not thread-safe: one caller at a time.
/// Port of the vendored TSWriter.swift with the Mac's additions (sink, cut callback,
/// <see cref="OpenSegmentSeconds"/>).
/// </summary>
internal sealed class TsWriter(double segmentDuration = TsWriter.DefaultSegmentDuration)
{
    public const ushort DefaultPatPid = 0;
    public const ushort DefaultPmtPid = 4095;
    public const ushort DefaultVideoPid = 256;
    public const ushort DefaultAudioPid = 257;
    public const double DefaultSegmentDuration = 2;

    /// <summary>Which tracks to wait for before writing anything. Empty means "write whatever comes".</summary>
    public TsMedia ExpectedMedias { get; set; }

    public Action<ReadOnlyMemory<byte>>? Sink { get; set; }

    public Action<double>? OnSegmentCut { get; set; }

    private bool hasSeededRotation;
    private double? lastMediaTimeStamp;

    /// <summary>How long the segment being written is so far (for the last, short segment).</summary>
    public double OpenSegmentSeconds =>
        !hasSeededRotation || lastMediaTimeStamp is null ? 0 : Math.Max(0, lastMediaTimeStamp.Value - rotatedTimeStamp);

    private AudioSpecificConfig? audioFormat;
    private AvcParameterSets? videoFormat;

    /// <summary>Declares the audio track (adds it to the PMT the first time, or when it changes).</summary>
    public AudioSpecificConfig? AudioFormat
    {
        get => audioFormat;
        set
        {
            if (value is null || value == audioFormat) { audioFormat = value; return; }
            audioFormat = value;
            pmt.ElementaryStreamSpecificData.Add(new EsSpecificData(EsStreamType.AdtsAac, DefaultAudioPid));
            audioContinuityCounter = 0;
            WriteProgramIfNeeded();
        }
    }

    /// <summary>Declares the H.264 track (adds it to the PMT the first time, or when its parameter sets change).</summary>
    public AvcParameterSets? VideoFormat
    {
        get => videoFormat;
        set
        {
            if (value is null || value.Equals(videoFormat)) { videoFormat = value; return; }
            videoFormat = value;
            pmt.ElementaryStreamSpecificData.Add(new EsSpecificData(EsStreamType.H264, DefaultVideoPid));
            videoContinuityCounter = 0;
            WriteProgramIfNeeded();
        }
    }

    private readonly TsProgramAssociation pat = NewPat();
    private readonly TsProgramMap pmt = new();
    private readonly ushort pcrPid = DefaultVideoPid;

    private bool CanWriteFor
    {
        get
        {
            if (ExpectedMedias == TsMedia.None) return true;
            if (ExpectedMedias.HasFlag(TsMedia.Audio) && ExpectedMedias.HasFlag(TsMedia.Video))
                return audioFormat is not null && videoFormat is not null;
            if (ExpectedMedias.HasFlag(TsMedia.Video)) return videoFormat is not null;
            if (ExpectedMedias.HasFlag(TsMedia.Audio)) return audioFormat is not null;
            return false;
        }
    }

    private double? videoTimeStamp;
    private double? audioTimeStamp;
    private double clockTimeStamp;
    private double rotatedTimeStamp;
    private byte audioContinuityCounter;
    private byte videoContinuityCounter;

    private static TsProgramAssociation NewPat()
    {
        var pat = new TsProgramAssociation();
        pat.Programs[1] = DefaultPmtPid;
        return pat;
    }

    public void Append(EncodedAudioFrame frame)
    {
        if (!CanWriteFor) return;
        if (audioTimeStamp is null)
        {
            audioTimeStamp = frame.PresentationTime;
            if (pcrPid == DefaultAudioPid) clockTimeStamp = audioTimeStamp.Value;
        }
        if (PacketizedElementaryStream.Audio(frame, audioTimeStamp.Value) is { } pes)
        {
            pes.StreamId = 192;
            WritePacketizedElementaryStream(DefaultAudioPid, pes, frame.PresentationTime, randomAccessIndicator: true);
        }
    }

    public void Append(EncodedVideoFrame frame)
    {
        if (!CanWriteFor) return;
        if (videoTimeStamp is null)
        {
            videoTimeStamp = frame.PresentationTime;
            if (pcrPid == DefaultVideoPid) clockTimeStamp = videoTimeStamp.Value;
        }
        var pes = PacketizedElementaryStream.Video(frame, videoTimeStamp.Value);
        var timestamp = frame.DecodeTime ?? frame.PresentationTime;
        pes.StreamId = 224;
        WritePacketizedElementaryStream(DefaultVideoPid, pes, timestamp, randomAccessIndicator: frame.IsKeyframe);
    }

    private void WritePacketizedElementaryStream(ushort pid, PacketizedElementaryStream pes, double timeStamp, bool randomAccessIndicator)
    {
        var packets = Split(pid, pes, timeStamp);

        // With video, segments may only be cut on a video keyframe; audio-only cuts anywhere.
        var canCut = ExpectedMedias.HasFlag(TsMedia.Video) ? pid == DefaultVideoPid && randomAccessIndicator : true;
        if (canCut) RotateFileHandle(timeStamp);
        lastMediaTimeStamp = timeStamp;

        if (packets[0].AdaptationField is { } field) field.RandomAccessIndicator = randomAccessIndicator;

        var bytes = new ByteArray(packets.Count * TsPacket.Size);
        foreach (var packet in packets)
        {
            switch (pid)
            {
                case DefaultAudioPid:
                    packet.ContinuityCounter = audioContinuityCounter;
                    audioContinuityCounter = (byte)((audioContinuityCounter + 1) & 0x0f);
                    break;
                case DefaultVideoPid:
                    packet.ContinuityCounter = videoContinuityCounter;
                    videoContinuityCounter = (byte)((videoContinuityCounter + 1) & 0x0f);
                    break;
            }
            packet.WriteTo(bytes);
        }
        Write(bytes.ToArray());
    }

    private void RotateFileHandle(double timestamp)
    {
        // The first cuttable sample opens the first segment rather than closing an empty one.
        if (!hasSeededRotation)
        {
            hasSeededRotation = true;
            rotatedTimeStamp = timestamp;
            return;
        }
        var duration = timestamp - rotatedTimeStamp;
        if (segmentDuration > duration) return;
        OnSegmentCut?.Invoke(duration);
        WriteProgramIfNeeded();
        rotatedTimeStamp = timestamp;
    }

    private void Write(ReadOnlyMemory<byte> data) => Sink?.Invoke(data);

    private void WriteProgram()
    {
        pmt.PcrPid = pcrPid;
        var bytes = new ByteArray(TsPacket.Size * 2);
        foreach (var packet in pat.ArrayOfPackets(DefaultPatPid).Concat(pmt.ArrayOfPackets(DefaultPmtPid)))
            packet.WriteTo(bytes);
        Write(bytes.ToArray());
    }

    private void WriteProgramIfNeeded()
    {
        if (ExpectedMedias == TsMedia.None) return;
        if (!CanWriteFor) return;
        WriteProgram();
    }

    private List<TsPacket> Split(ushort pid, PacketizedElementaryStream pes, double timestamp)
    {
        ulong? pcr = null;
        var duration = timestamp - clockTimeStamp;
        if (pcrPid == pid && 0.02 <= duration)
        {
            var first = (pid == DefaultVideoPid ? videoTimeStamp : audioTimeStamp) ?? timestamp;
            pcr = (ulong)Math.Max(0, (timestamp - first) * TsTimestamp.Resolution);
            clockTimeStamp = timestamp;
        }
        return pes.ArrayOfPackets(pid, pcr);
    }
}

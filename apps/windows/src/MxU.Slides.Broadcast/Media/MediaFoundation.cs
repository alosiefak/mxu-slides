using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Small helpers over the Media Foundation interop: start-up, media types, samples, and
/// finding encoders. COM objects are released as soon as we are done with them (with
/// <see cref="Release"/>) instead of waiting for the garbage collector, because each video
/// sample holds megabytes of native memory.
/// </summary>
internal static class MediaFoundation
{
    private static readonly Lazy<int> Startup = new(() =>
    {
        try { return MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_FULL); }
        catch (DllNotFoundException) { return unchecked((int)0x8007007E); }
    });

    /// <summary>Starts Media Foundation once per process (it is never shut down; the process exit does that).</summary>
    public static void EnsureStarted() => MF.Check(Startup.Value, "MFStartup (is the Media Foundation feature installed?)");

    /// <summary>False on Windows editions without Media Foundation (e.g. "N" editions without the Media Feature Pack).</summary>
    public static bool IsAvailable => Startup.Value >= 0;

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    public static IMFMediaType CreateMediaType()
    {
        EnsureStarted();
        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        return type;
    }

    public static IMFAttributes CreateAttributes(uint size = 4)
    {
        EnsureStarted();
        MF.Check(MF.MFCreateAttributes(out var attributes, size), "MFCreateAttributes");
        return attributes;
    }

    /// <summary>A sample holding a copy of <paramref name="data"/>, stamped in 100-nanosecond units.</summary>
    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time, long duration)
    {
        MF.Check(MF.MFCreateMemoryBuffer((uint)data.Length, out var buffer), "MFCreateMemoryBuffer");
        try
        {
            Write(buffer, data);
            MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
            MF.Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
            MF.Check(sample.SetSampleTime(time), "IMFSample.SetSampleTime");
            MF.Check(sample.SetSampleDuration(duration), "IMFSample.SetSampleDuration");
            return sample;
        }
        finally
        {
            Release(buffer);
        }
    }

    /// <summary>An empty sample with room for <paramref name="size"/> bytes, for encoders that want the caller to allocate output.</summary>
    public static IMFSample CreateOutputSample(int size, int alignment)
    {
        var hr = alignment > 1
            ? MF.MFCreateAlignedMemoryBuffer((uint)size, (uint)(alignment - 1), out var buffer)
            : MF.MFCreateMemoryBuffer((uint)size, out buffer);
        MF.Check(hr, "MFCreateMemoryBuffer");
        try
        {
            MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
            MF.Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
            return sample;
        }
        finally
        {
            Release(buffer);
        }
    }

    public static unsafe void Write(IMFMediaBuffer buffer, ReadOnlySpan<byte> data)
    {
        MF.Check(buffer.Lock(out var pointer, out var max, out _), "IMFMediaBuffer.Lock");
        try
        {
            if (data.Length > max) throw new InvalidOperationException("Media buffer too small");
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        }
        finally
        {
            buffer.Unlock();
        }
        MF.Check(buffer.SetCurrentLength((uint)data.Length), "IMFMediaBuffer.SetCurrentLength");
    }

    /// <summary>Copies a sample's bytes out (joining its buffers if it has several).</summary>
    public static unsafe byte[] ReadBytes(IMFSample sample)
    {
        MF.Check(sample.ConvertToContiguousBuffer(out var buffer), "IMFSample.ConvertToContiguousBuffer");
        try
        {
            MF.Check(buffer.Lock(out var pointer, out _, out var length), "IMFMediaBuffer.Lock");
            try
            {
                return new ReadOnlySpan<byte>((void*)pointer, (int)length).ToArray();
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    public static bool IsKeyframe(IMFSample sample) =>
        sample.GetUINT32(MF.MFSampleExtension_CleanPoint, out var clean) >= 0 && clean != 0;

    public static string? GetString(IMFActivate activate, Guid key)
    {
        if (activate.GetAllocatedString(key, out var text, out _) < 0 || text == 0) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    public static byte[]? GetBlob(IMFMediaType type, Guid key)
    {
        if (type.GetBlobSize(key, out var size) < 0 || size == 0) return null;
        var blob = new byte[size];
        return type.GetBlob(key, blob, size, out _) >= 0 ? blob : null;
    }

    /// <summary>An encoder Media Foundation knows about, not yet created.</summary>
    public sealed record EncoderCandidate(string Name, bool IsHardware, IMFActivate Activate);

    /// <summary>
    /// Lists encoders that take <paramref name="input"/> and produce <paramref name="output"/>:
    /// hardware ones (graphics-card encoders) first when <paramref name="includeHardware"/>,
    /// then the Microsoft software encoders that every Windows with Media Foundation has.
    /// The caller releases each candidate's <see cref="EncoderCandidate.Activate"/>.
    /// </summary>
    public static unsafe List<EncoderCandidate> FindEncoders(Guid category, Guid majorType, Guid input, Guid output, bool includeHardware)
    {
        EnsureStarted();
        var found = new List<EncoderCandidate>();
        var inputInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = majorType, guidSubtype = input };
        var outputInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = majorType, guidSubtype = output };
        uint[] passes = includeHardware
            ? [MF.MFT_ENUM_FLAG_HARDWARE | MF.MFT_ENUM_FLAG_SORTANDFILTER, MF.MFT_ENUM_FLAG_SYNCMFT | MF.MFT_ENUM_FLAG_LOCALMFT | MF.MFT_ENUM_FLAG_SORTANDFILTER]
            : [MF.MFT_ENUM_FLAG_SYNCMFT | MF.MFT_ENUM_FLAG_LOCALMFT | MF.MFT_ENUM_FLAG_SORTANDFILTER];
        foreach (var flags in passes)
        {
            var hardware = (flags & MF.MFT_ENUM_FLAG_HARDWARE) != 0;
            // Hardware encoders often register NV12 only for video, so the input filter is kept;
            // audio encoders are filtered on output alone (their input lists vary).
            var hr = MF.MFTEnumEx(category, flags, (nint)(&inputInfo), (nint)(&outputInfo), out var array, out var count);
            if (hr < 0 || array == 0) continue;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var pointer = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    if (pointer == 0) continue;
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    Marshal.Release(pointer);
                    found.Add(new EncoderCandidate(GetString(activate, MF.MFT_FRIENDLY_NAME_Attribute) ?? "encoder", hardware, activate));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(array);
            }
        }
        return found;
    }

    public static void ReleaseAll(IEnumerable<EncoderCandidate> candidates)
    {
        foreach (var candidate in candidates) Release(candidate.Activate);
    }
}

/// <summary>A Media Foundation call failed; <see cref="Code"/> carries the HRESULT.</summary>
internal sealed class MediaFoundationException(string what, int hr)
    : Exception($"{what} failed (0x{hr:X8})")
{
    public int Code { get; } = hr;
}
